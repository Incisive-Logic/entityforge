using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using VDS.RDF;
using VDS.RDF.Parsing;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    public async Task Sparql11_external_W3C_query_and_update_protocol_sequences()
    {
        string? root = Environment.GetEnvironmentVariable("ENTITYFORGE_W3C_TEST_ROOT");
        if (String.IsNullOrWhiteSpace(root))
        {
            Assert.Inconclusive("Set ENTITYFORGE_W3C_TEST_ROOT to an upstream w3c/rdf-tests checkout.");
        }
        string directory = Path.GetFullPath(Path.Combine(root, "sparql", "sparql11", "protocol"));
        using var manifest = new VDS.RDF.Graph();
        FileLoader.Load(manifest, Path.Combine(directory, "manifest.ttl"));
        const string mf = "http://www.w3.org/2001/sw/DataAccess/tests/test-manifest#";
        const string ht = "http://www.w3.org/2011/http#";
        const string cnt = "http://www.w3.org/2011/content#";
        const string ut = "http://www.w3.org/2009/sparql/tests/test-update#";
        IEnumerable<INode> Objects(INode subject, string predicate) => manifest.GetTriplesWithSubjectPredicate(subject,
            manifest.CreateUriNode(new Uri(predicate))).Select(triple => triple.Object);
        string Text(INode node) => ((ILiteralNode)node).Value;
        IEnumerable<INode> Items(INode list)
        {
            var visited = new HashSet<INode>();
            while (list is not IUriNode end || end.Uri.AbsoluteUri != RdfSpecsHelper.RdfListNil)
            {
                Assert.IsTrue(visited.Add(list), "Cyclic upstream RDF list.");
                yield return Objects(list, RdfSpecsHelper.RdfListFirst).Single();
                list = Objects(list, RdfSpecsHelper.RdfListRest).Single();
            }
        }
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UseRdfProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        var entries = manifest.GetTriplesWithPredicate(manifest.CreateUriNode(new Uri(mf + "entries")))
            .SelectMany(triple => Items(triple.Object)).ToArray();
        Assert.AreEqual(34, entries.Length);
        var failures = new List<string>();
        var results = new List<object>();
        foreach (var test in entries)
        {
            int previousFailures = failures.Count;
            var requests = new List<object>();
            try
            {
                using var reset = await client.PostAsync("/sparql/update", new StringContent("CLEAR ALL", Encoding.UTF8, "application/sparql-update"));
                Assert.IsTrue(reset.IsSuccessStatusCode);
                foreach (var seed in Objects(test, ut + "graphData"))
                {
                    var file = ((IUriNode)Objects(seed, ut + "graph").Single()).Uri;
                    Assert.IsTrue(file.IsFile);
                    string path = Path.GetFullPath(file.LocalPath);
                    Assert.IsTrue(path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
                    string name = Text(Objects(seed, "http://www.w3.org/2000/01/rdf-schema#label").Single());
                    using var loaded = await client.PutAsync("/sparql/graphs?graph=" + Uri.EscapeDataString(name),
                        new StringContent(await File.ReadAllTextAsync(path), Encoding.UTF8, "application/n-triples"));
                    Assert.IsTrue(loaded.IsSuccessStatusCode, await loaded.Content.ReadAsStringAsync());
                }
                var connection = Objects(test, mf + "action").Single();
                foreach (var specification in Items(Objects(connection, ht + "requests").Single()))
                {
                    var method = new HttpMethod(Text(Objects(specification, ht + "methodName").Single()));
                    string originalPath = Text(Objects(specification, ht + "absolutePath").Single());
                    Assert.IsTrue(originalPath.StartsWith("/sparql/", StringComparison.Ordinal));
                    var headers = Objects(specification, ht + "headers").SelectMany(Items).ToDictionary(
                        header => Text(Objects(header, ht + "fieldName").Single()),
                        header => Text(Objects(header, ht + "fieldValue").Single()), StringComparer.OrdinalIgnoreCase);
                    var body = Objects(specification, ht + "body").SingleOrDefault();
                    string chars = body is null ? String.Empty : Text(Objects(body, cnt + "chars").Single());
                    // Malformed update requests still target the update operation. Valid query follow-ups
                    // in an update sequence target the independently configured query operation.
                    bool update = headers.GetValueOrDefault("content-type", "").StartsWith("application/sparql-update", StringComparison.Ordinal)
                        || chars.StartsWith("update=", StringComparison.Ordinal) || originalPath.Contains("?update=", StringComparison.Ordinal)
                        || (test.ToString().Contains("#bad_update", StringComparison.Ordinal));
                    string path = (update ? "/sparql/update" : "/sparql") + originalPath[8..];
                    using var request = new HttpRequestMessage(method, path);
                    if (body is not null)
                    {
                        string encoding = Text(Objects(body, cnt + "characterEncoding").Single());
                        request.Content = new ByteArrayContent(Encoding.GetEncoding(encoding).GetBytes(chars));
                    }
                    foreach (var header in headers)
                    {
                        if (header.Key.Equals("content-type", StringComparison.OrdinalIgnoreCase))
                        {
                            request.Content ??= new ByteArrayContent([]);
                            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(header.Value);
                        }
                        else
                        {
                            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                        }
                    }
                    using var response = await client.SendAsync(request);
                    string actual = await response.Content.ReadAsStringAsync();
                    var expected = Objects(specification, ht + "resp").Single();
                    string[] statuses = Objects(expected, mf + "expectedStatus").Cast<IUriNode>().Select(node => node.Uri.Fragment[1..]).ToArray();
                    Assert.IsTrue(statuses.Length > 0 && statuses.All(status => status is
                        "StatusCode2xx" or "StatusCode3xx" or "StatusCode4xx" or "StatusCode5xx"),
                        "Unsupported or missing upstream status expectation.");
                    Assert.IsTrue(statuses.Any(status => status == "StatusCode" + ((int)response.StatusCode / 100) + "xx"),
                        method + " " + path + ": " + response.StatusCode + " " + actual);
                    string? format = Objects(expected, mf + "expectedFormat").Select(Text).SingleOrDefault();
                    Assert.IsTrue(format is null or "boolean" or "tabular" or "RDF",
                        "Unsupported upstream result format expectation: " + format);
                    bool? expectedAnswer = Objects(expected, mf + "expectedBoolean").SingleOrDefault() is { } booleanNode
                        ? Boolean.Parse(Text(booleanNode)) : null;
                    Assert.IsTrue(expectedAnswer is null || format == "boolean", "Boolean expectation without boolean result format.");
                    if (format is "boolean" or "tabular")
                    {
                        Assert.AreEqual("application/sparql-results+json", response.Content.Headers.ContentType?.MediaType);
                        using var json = JsonDocument.Parse(actual);
                        if (format == "boolean")
                        {
                            bool actualBoolean = json.RootElement.GetProperty("boolean").GetBoolean();
                            if (expectedAnswer is { } expectedBoolean)
                            {
                                Assert.AreEqual(expectedBoolean, actualBoolean);
                            }
                        }
                        else
                        {
                            Assert.AreEqual(JsonValueKind.Array, json.RootElement.GetProperty("results").GetProperty("bindings").ValueKind);
                        }
                    }
                    else if (format == "RDF")
                    {
                        Assert.AreEqual("text/turtle", response.Content.Headers.ContentType?.MediaType);
                        using var graph = new VDS.RDF.Graph();
                        new TurtleParser().Load(graph, new StringReader(actual));
                    }
                    requests.Add(new
                    {
                        method = method.Method,
                        endpoint = update ? "update" : "query",
                        expectedStatusClasses = statuses,
                        actualStatus = (int)response.StatusCode,
                        expectedFormat = format,
                        expectedBoolean = expectedAnswer,
                        assertions = format == "boolean" ? (expectedAnswer.HasValue ? "status/media/boolean value" : "status/media/boolean shape") :
                            format == "tabular" ? "status/media/bindings array shape; no expected row values supplied" :
                            format == "RDF" ? "status/media/RDF parse; no expected graph supplied" : "status class"
                    });
                }
            }
            catch (AssertFailedException error)
            {
                failures.Add(test + ": " + error.Message);
            }
            results.Add(new
            {
                name = test.ToString(),
                passed = failures.Count == previousFailures,
                requests
            });
        }
        if (Environment.GetEnvironmentVariable("ENTITYFORGE_W3C_HTTP_REPORT") is { Length: > 0 } report)
        {
            File.WriteAllText(report + ".protocol.json", JsonSerializer.Serialize(new
            {
                cases = results
            },
                new JsonSerializerOptions { WriteIndented = true }));
        }
        Assert.AreEqual(0, failures.Count, String.Join(Environment.NewLine, failures));
        await host.StopAsync();
    }
}
