using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using VDS.RDF;
using VDS.RDF.Parsing;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("indirect", 9)]
    [DataRow("direct", 4)]
    public async Task Sparql11_external_W3C_graph_store_protocol_sequences(string category, int expectedCases)
    {
        string? root = Environment.GetEnvironmentVariable("ENTITYFORGE_W3C_TEST_ROOT");
        if (String.IsNullOrWhiteSpace(root))
        {
            Assert.Inconclusive("Set ENTITYFORGE_W3C_TEST_ROOT to an upstream w3c/rdf-tests checkout.");
        }
        using var manifest = new VDS.RDF.Graph();
        FileLoader.Load(manifest, Path.Combine(root, "sparql", "sparql11", "graph-store-protocol", "manifest-" + category + ".ttl"));
        const string mf = "http://www.w3.org/2001/sw/DataAccess/tests/test-manifest#";
        const string ht = "http://www.w3.org/2011/http#";
        const string cnt = "http://www.w3.org/2011/content#";
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
        string endpoint = new Uri(client.BaseAddress!, "/sparql/graphs").AbsoluteUri;
        var entries = manifest.GetTriplesWithPredicate(manifest.CreateUriNode(new Uri(mf + "entries")))
            .SelectMany(triple => Items(triple.Object)).ToArray();
        Assert.AreEqual(expectedCases, entries.Length);
        var listed = entries.ToHashSet();
        var supplemental = manifest.GetTriplesWithPredicateObject(
                manifest.CreateUriNode(new Uri(RdfSpecsHelper.RdfType)),
                manifest.CreateUriNode(new Uri(mf + "GraphStoreProtocolTest")))
            .Select(triple => triple.Subject).Where(test => !listed.Contains(test)).ToArray();
        // The pinned direct manifest defines this case but omits it from mf:entries.
        // Execute it separately without presenting it as an upstream listed case.
        Assert.AreEqual(category == "direct" ? 1 : 0, supplemental.Length);
        if (supplemental.Length != 0)
        {
            Assert.IsTrue(supplemental[0] is IUriNode node && node.Uri.Fragment == "#head_non_existing_direct");
        }
        entries = entries.Concat(supplemental).ToArray();
        foreach (var test in entries)
        {
            using var reset = await client.PostAsync("/sparql/update", new StringContent("CLEAR ALL", Encoding.UTF8, "application/sparql-update"));
            Assert.IsTrue(reset.IsSuccessStatusCode);
            var variables = new Dictionary<string, string>(StringComparer.Ordinal);
            string Rewrite(string text)
            {
                const string original = "http://www.example/gsp";
                text = text.Replace(Uri.EscapeDataString(original), Uri.EscapeDataString(endpoint), StringComparison.Ordinal)
                    .Replace(original, endpoint, StringComparison.Ordinal);
                foreach (var pair in variables)
                {
                    text = text.Replace(pair.Key, pair.Value, StringComparison.Ordinal);
                }
                return text.StartsWith("/gsp", StringComparison.Ordinal) ? "/sparql/graphs" + text[4..] : text;
            }
            var connection = Objects(test, mf + "action").Single();
            foreach (var specification in Items(Objects(connection, ht + "requests").Single()))
            {
                var method = new HttpMethod(Text(Objects(specification, ht + "methodName").Single()));
                string path = Rewrite(Text(Objects(specification, ht + "absolutePath").Single()));
                Assert.IsTrue(new Uri(client.BaseAddress!, path).IsLoopback, "Fixtures may only address the isolated test host.");
                using var request = new HttpRequestMessage(method, path);
                if (Objects(specification, ht + "body").SingleOrDefault() is { } body)
                {
                    request.Content = new StringContent(Rewrite(Text(Objects(body, cnt + "chars").Single())), Encoding.UTF8);
                }
                foreach (var header in Objects(specification, ht + "headers").SelectMany(Items))
                {
                    string name = Text(Objects(header, ht + "fieldName").Single());
                    string value = Rewrite(Text(Objects(header, ht + "fieldValue").Single()));
                    if (name.Equals("content-type", StringComparison.OrdinalIgnoreCase))
                    {
                        request.Content ??= new ByteArrayContent([]);
                        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(value);
                    }
                    else
                    {
                        request.Headers.TryAddWithoutValidation(name, value);
                    }
                }
                using var response = await client.SendAsync(request);
                var expected = Objects(specification, ht + "resp").Single();
                string[] statuses = Objects(expected, mf + "expectedStatus").Cast<IUriNode>().Select(node => node.Uri.Fragment[1..]).ToArray();
                string actualBody = await response.Content.ReadAsStringAsync();
                Assert.IsTrue(statuses.Any(status => status == "StatusCode2xx" ? response.IsSuccessStatusCode :
                    Enum.TryParse<HttpStatusCode>(status, out var code) && code == response.StatusCode), test + " " + method + " " + path + ": " + response.StatusCode + " " + actualBody);
                if (Objects(expected, mf + "expectedLocation").SingleOrDefault() is { } location)
                {
                    Assert.IsNotNull(response.Headers.Location);
                    variables.Add(Text(location), response.Headers.Location.AbsoluteUri);
                }
                foreach (var header in Objects(expected, ht + "headers").SelectMany(Items))
                {
                    string name = Text(Objects(header, ht + "fieldName").Single());
                    if (name.Equals("content-type", StringComparison.OrdinalIgnoreCase))
                    {
                        var expectedType = MediaTypeHeaderValue.Parse(Text(Objects(header, ht + "fieldValue").Single()));
                        Assert.AreEqual(expectedType.MediaType, response.Content.Headers.ContentType?.MediaType);
                    }
                }
                if (Objects(expected, ht + "body").SingleOrDefault() is { } expectedBody)
                {
                    using var wanted = new VDS.RDF.Graph();
                    using var actual = new VDS.RDF.Graph();
                    var parser = new TurtleParser();
                    parser.Load(wanted, new StringReader(Rewrite(Text(Objects(expectedBody, cnt + "chars").Single()))));
                    parser.Load(actual, new StringReader(actualBody));
                    Assert.IsTrue(wanted.Equals(actual), test + " returned a different RDF graph.");
                }
                if (method == HttpMethod.Head)
                {
                    Assert.AreEqual(String.Empty, actualBody);
                }
            }
        }
        if (Environment.GetEnvironmentVariable("ENTITYFORGE_W3C_HTTP_REPORT") is { Length: > 0 } report)
        {
            var cases = entries.Select(test => new
            {
                name = test.ToString(),
                passed = true,
                listedInManifest = listed.Contains(test)
            }).ToArray();
            File.WriteAllText(report + ".graph-store-" + category + ".json",
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    cases
                },
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        await host.StopAsync();
    }
}
