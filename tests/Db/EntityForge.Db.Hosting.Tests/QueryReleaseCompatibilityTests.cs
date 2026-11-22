using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Xml.Linq;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using EntityForge.Db.Storage.Local;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace EntityForge.Db.Hosting.Tests;

internal static class QueryCompatibilityFixtures
{
    internal static JsonElement[] Load(string language)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "QueryCompatibility", language + "-v1.json")));
        return document.RootElement.EnumerateArray().Select(item => item.Clone()).ToArray();
    }
}

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    public async Task Release_SPARQL_selected_upstream_regressions_match_original_results()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "QueryCompatibility", "upstream");
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "cases.json")));
        XNamespace ns = "http://www.w3.org/2005/sparql-results#";
        foreach (var test in manifest.RootElement.EnumerateArray())
        {
            await using var database = await OpenAsync(CreateDirectory());
            using var input = File.OpenRead(Path.Combine(directory, test.GetProperty("data").GetString()!));
            var quads = RdfSyntax.ParseTurtle(input, "upstream");
            await database.CommitAsync(new(Guid.NewGuid(), quads.Select(quad => (RdfMutation)new AssertRdfQuad(quad)).ToArray()));
            var rows = RdfSparql.Execute(database, File.ReadAllText(Path.Combine(directory, test.GetProperty("query").GetString()!)));
            var expected = XDocument.Load(Path.Combine(directory, test.GetProperty("result").GetString()!))
                .Descendants(ns + "result").Select(result => result.Elements(ns + "binding").ToDictionary(
                    binding => binding.Attribute("name")!.Value, binding =>
                    {
                        var term = binding.Elements().Single();
                        return term.Name.LocalName switch
                        {
                            "uri" => (RdfTerm)new RdfIri(term.Value),
                            "literal" => new RdfLiteral(term.Value, term.Attribute("datatype")?.Value,
                                term.Attribute(XNamespace.Xml + "lang")?.Value),
                            _ => throw new InvalidDataException("Unsupported expected term in selected regression fixture.")
                        };
                    })).ToArray();
            string[] Canonical(IEnumerable<IReadOnlyDictionary<string, RdfTerm>> bindings) => bindings
                .Select(row => JsonSerializer.Serialize(row.OrderBy(pair => pair.Key).ToArray())).Order().ToArray();
            CollectionAssert.AreEqual(Canonical(expected), Canonical(rows), test.GetProperty("id").GetString());
        }
    }

    [TestMethod]
    public async Task Release_SPARQL_compatibility_corpus_matches_expected_results()
    {
        await using var database = await OpenAsync(CreateDirectory());
        var version = await database.CommitAsync(new(Guid.NewGuid(), [
            new AssertRdfQuad(new(new RdfIri("urn:a"), new RdfIri("urn:name"), new RdfLiteral("Alice"))),
            new AssertRdfQuad(new(new RdfIri("urn:b"), new RdfIri("urn:name"), new RdfLiteral("Bob"))),
            new AssertRdfQuad(new(new RdfIri("urn:a"), new RdfIri("urn:age"), new RdfLiteral("10", "http://www.w3.org/2001/XMLSchema#integer"))),
            new AssertRdfQuad(new(new RdfIri("urn:a"), new RdfIri("urn:name"), new RdfLiteral("Alice"), new RdfIri("urn:g")))
        ]));
        using var snapshot = await database.AcquireSnapshotAsync(version);
        foreach (var item in QueryCompatibilityFixtures.Load("sparql"))
        {
            string id = item.GetProperty("id").GetString()!;
            var rows = RdfSparql.Execute(database, item.GetProperty("query").GetString()!);
            Assert.AreEqual(item.GetProperty("count").GetInt32(), rows.Count, id);
            string query = item.GetProperty("query").GetString()!;
            if (RdfSparql.SupportsBindingFragments(query))
            {
                var fragments = RdfSparql.CaptureBindingFragments(database, snapshot, query, 100, default);
                var remote = (RdfSparqlSelectResult)RdfSparql.ExecuteBindingFragments(query, fragments, default);
                string[] Canonical(IEnumerable<IReadOnlyDictionary<string, RdfTerm>> bindings) => bindings
                    .Select(row => JsonSerializer.Serialize(row.OrderBy(pair => pair.Key).ToArray())).Order().ToArray();
                CollectionAssert.AreEqual(Canonical(rows), Canonical(remote.Bindings), id + " distributed");
            }
            if (item.TryGetProperty("variable", out var variable))
            {
                Assert.AreEqual(item.GetProperty("literal").GetString(),
                    ((RdfLiteral)rows.Single()[variable.GetString()!]).LexicalForm, id);
            }
        }
    }
}

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    public async Task Release_maintenance_backpressure_keeps_readiness()
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory)
            .ConfigureStorage(options => options.CheckpointInterval = TimeSpan.FromMilliseconds(10))
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        var coordinator = host.Application.Services.GetRequiredService<EntityForgeDbAdministrativeJobCoordinator>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var occupied = coordinator.ExecuteAsync(async cancellation =>
        {
            await release.Task.WaitAsync(cancellation);
            return true;
        }, CancellationToken.None).AsTask();
        try
        {
            await host.StartAsync();
            var runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
            await Task.Delay(200);
            Assert.IsNull(runtime.LastMaintenanceFailure, runtime.LastMaintenanceFailure?.ToString());
            Assert.IsTrue(runtime.GetReadiness().Ready);
        }
        finally
        {
            release.TrySetResult();
            await occupied;
        }
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task Release_query_rejections_are_structured_client_errors(bool rdf)
    {
        _directory = NewDirectory();
        var builder = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory)
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0"));
        await using var host = (rdf ? builder.UseRdfProfile() : builder.UsePropertyGraphProfile()).Build();
        await host.StartAsync();
        using var client = Client(host);
        string[] queries = rdf ?
        [
            "SELECT ?s {} LIMIT 999999999999999999999999999999999",
            "SELECT ?s FROM ?graph WHERE { ?s ?p ?o }",
            "SELECT ?s { SERVICE { ?s ?p ?o } }",
            "SELECT ?s { BIND('bad\\q' AS ?s) }",
            "SELECT ?s { ?s ?p }",
            "SELECT ?s { " + new string('{', 20) + "?s ?p ?o" + new string('}', 20) + " }"
        ] :
        [
            "MATCH (n) RETURN n LIMIT 999999999999999999999999999999999",
            "MATCH (n)-[r]->{10001}(m) RETURN n",
            "MATCH (n) WHERE unknown IS NULL RETURN n",
            "MATCH () RETURN *",
            "MATCH (n) WHERE n.name = 'unclosed RETURN n",
            "MATCH (n) RETURN n; MATCH (m) RETURN m",
            "MATCH (n) WHERE " + String.Concat(Enumerable.Repeat("NOT ", 300)) + "n.age = 1 RETURN n"
        ];
        foreach (string query in queries)
        {
            using var response = await client.PostAsJsonAsync(rdf ? "/v1/rdf/sparql" : "/v1/graph/gql/query", new
            {
                query
            });
            Assert.IsTrue(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity,
                query + ": " + response.StatusCode);
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.IsFalse(String.IsNullOrWhiteSpace(problem.GetProperty("code").GetString()), query);
            Assert.IsFalse(problem.GetProperty("retryable").GetBoolean(), query);
        }
    }

    [TestMethod]
    [DataRow("/v1/rdf/sparql")]
    [DataRow("/sparql")]
    public async Task Release_unavailable_SERVICE_is_a_structured_execution_failure(string route)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UseRdfProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        const string query = "SELECT ?s { SERVICE <urn:remote> { ?s ?p ?o } }";
        using var response = route == "/sparql"
            ? await client.PostAsync(route, new StringContent(query, System.Text.Encoding.UTF8, "application/sparql-query"))
            : await client.PostAsJsonAsync(route, new
            {
                query
            });
        string body = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode, body);
        using var problem = JsonDocument.Parse(body);
        Assert.AreEqual("rdf_sparql_remote_failure", problem.RootElement.GetProperty("code").GetString());
        Assert.IsFalse(problem.RootElement.GetProperty("retryable").GetBoolean());
        Assert.IsFalse(body.Contains("urn:remote", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Release_GQL_HTTP_compatibility_corpus_matches_expected_results()
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory)
            .UsePropertyGraphProfile().ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        string first = Guid.NewGuid().ToString("D"), second = Guid.NewGuid().ToString("D"), third = Guid.NewGuid().ToString("D");
        string seed = $"INSERT (a:Person {{ id: '{first}', name: 'RETURN WHERE SET, O''Brien', age: 10 }}), " +
            $"(b:Person {{ id: '{second}', name: 'Bob', age: 20 }}), (c:Person {{ id: '{third}', name: 'Carol' }}), " +
            $"(a)-[r:knows {{ id: '{Guid.NewGuid():D}' }}]->(b), (b)-[s:knows {{ id: '{Guid.NewGuid():D}' }}]->(c)";
        using var seeded = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = seed
        });
        Assert.AreEqual(HttpStatusCode.OK, seeded.StatusCode, await seeded.Content.ReadAsStringAsync());
        foreach (var item in QueryCompatibilityFixtures.Load("gql"))
        {
            string id = item.GetProperty("id").GetString()!;
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = item.GetProperty("query").GetString()
            });
            if (item.TryGetProperty("error", out var expectedError) && expectedError.GetBoolean())
            {
                Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, id + ": " + await response.Content.ReadAsStringAsync());
                continue;
            }
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, id + ": " + await response.Content.ReadAsStringAsync());
            var result = await response.Content.ReadFromJsonAsync<JsonElement>();
            var matches = result.GetProperty("matches");
            Assert.AreEqual(item.GetProperty("count").GetInt32(), matches.GetArrayLength(), id);
            foreach (var match in matches.EnumerateArray())
            {
                Assert.IsFalse(match.GetProperty("bindings").EnumerateObject().Any(binding => binding.Name.StartsWith('$')), id);
                if (item.TryGetProperty("variable", out var variable))
                {
                    Assert.IsTrue(match.GetProperty("bindings").TryGetProperty(variable.GetString()!, out _), id + ": " + result);
                    var expectedValue = item.GetProperty("value");
                    var actualValue = match.GetProperty("bindings").GetProperty(variable.GetString()!);
                    if (expectedValue.ValueKind == JsonValueKind.Number && actualValue.ValueKind == JsonValueKind.Number)
                    {
                        Assert.AreEqual(expectedValue.GetDecimal(), actualValue.GetDecimal(), id);
                    }
                    else
                    {
                        Assert.AreEqual(expectedValue.ToString(), actualValue.ToString(), id);
                    }
                }
            }
        }

        foreach (var parameter in new[] { ("RETURN WHERE SET, O'Brien", 1), ("' OR n.age = 10 RETURN n", 0) })
        {
            using var parameterized = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = "MATCH\n(n:Person) WHERE n.name = $name RETURN n",
                parameters = new
                {
                    name = parameter.Item1
                }
            });
            Assert.AreEqual(HttpStatusCode.OK, parameterized.StatusCode, await parameterized.Content.ReadAsStringAsync());
            Assert.AreEqual(parameter.Item2,
                (await parameterized.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches").GetArrayLength());
        }
        using var missingParameter = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (n:Person) WHERE n.name = $missing RETURN n"
        });
        Assert.AreEqual(HttpStatusCode.BadRequest, missingParameter.StatusCode);
        using var objectParameter = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (n:Person) WHERE n.name = $name RETURN n",
            parameters = new
            {
                name = new
                {
                    nested = true
                }
            }
        });
        Assert.AreEqual(HttpStatusCode.OK, objectParameter.StatusCode, await objectParameter.Content.ReadAsStringAsync());
        var objectResult = await objectParameter.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual(0, objectResult.GetProperty("matches").GetArrayLength());

        // Clause words inside a value must not select a different mutation action.
        using var update = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "MATCH (n:Person) WHERE n.name = 'RETURN WHERE SET, O''Brien' SET n.age = 11"
        });
        Assert.AreEqual(HttpStatusCode.OK, update.StatusCode, await update.Content.ReadAsStringAsync());
        using var rejected = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "MATCH (n:Person) SET n.age = 99, n.invalid = nonsense"
        });
        Assert.AreEqual(HttpStatusCode.BadRequest, rejected.StatusCode);
        using var count = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (n:Person) WHERE n.age = 99 RETURN n"
        });
        Assert.AreEqual(0, (await count.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches").GetArrayLength());
    }
}
