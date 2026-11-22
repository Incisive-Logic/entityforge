using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    private const string ServiceRows = """
        {"head":{"vars":["s","v"]},"results":{"bindings":[
          {"s":{"type":"uri","value":"urn:a"},"v":{"type":"literal","value":"1","datatype":"http://www.w3.org/2001/XMLSchema#integer"}},
          {"s":{"type":"uri","value":"urn:b"},"v":{"type":"literal","value":"2","datatype":"http://www.w3.org/2001/XMLSchema#integer"}}
        ]}}
        """;

    [TestMethod]
    [DataRow("PREFIX p: <urn:> SELECT ?v WHERE { VALUES ?s { <urn:a> } SERVICE <ENDPOINT> { ?s p:p ?v } }", 1)]
    [DataRow("SELECT ?s ?v WHERE { SERVICE <ENDPOINT> { ?s <urn:p> ?v } } ORDER BY ?v", 2)]
    [DataRow("SELECT ?s WHERE { ?s <urn:local> ?x OPTIONAL { SERVICE <ENDPOINT> { ?s <urn:p> ?v } } }", 1)]
    [DataRow("ASK { SERVICE <ENDPOINT> { ?s <urn:p> ?v } }", 1)]
    [DataRow("CONSTRUCT { ?s <urn:copy> ?v } WHERE { SERVICE <ENDPOINT> { ?s <urn:p> ?v } }", 2)]
    [DataRow("DESCRIBE ?s WHERE { SERVICE <ENDPOINT> { ?s <urn:p> ?v } }", 1)]
    [DataRow("SELECT ?s WHERE { GRAPH <urn:g> { SERVICE <ENDPOINT> { ?s <urn:p> ?v } } }", 2)]
    [DataRow("SELECT ?s WHERE { { SELECT ?s WHERE { SERVICE <ENDPOINT> { ?s <urn:p> ?v } } } }", 2)]
    [DataRow("SELECT (SUM(?v) AS ?total) WHERE { SERVICE <ENDPOINT> { ?s <urn:p> ?v } } HAVING(SUM(?v)=3)", 1)]
    [DataRow("SELECT ?v WHERE { SERVICE <ENDPOINT> { ?s <urn:p> ?v } } GROUP BY ?v HAVING(?v>1)", 1)]
    [DataRow("SELECT ?w WHERE { SERVICE <ENDPOINT> { ?s <urn:p> ?v } BIND(?v+1 AS ?w) } ORDER BY DESC(?w) LIMIT 1", 1)]
    [DataRow("ASK { SERVICE <ENDPOINT> { ?s <urn:p> ?v } } HAVING(SUM(?v)=3)", 1)]
    [DataRow("CONSTRUCT { ?s <urn:copy> ?v } WHERE { SERVICE <ENDPOINT> { ?s <urn:p> ?v } } ORDER BY DESC(?v) LIMIT 1", 1)]
    [DataRow("SELECT ?total WHERE { { SELECT (SUM(?v) AS ?total) WHERE { SERVICE <ENDPOINT> { ?s <urn:p> ?v } } HAVING(SUM(?v)=3) } }", 1)]
    public async Task Sparql11_SERVICE_joins_native_solutions_for_each_query_form(string query, int expected)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var server = builder.Build();
        int fetched = 0;
        string? submitted = null;
        server.MapPost("/sparql", async (HttpContext context) =>
        {
            Interlocked.Increment(ref fetched);
            using var reader = new StreamReader(context.Request.Body);
            submitted = await reader.ReadToEndAsync();
            return Results.Text(ServiceRows, "application/sparql-results+json");
        });
        await server.StartAsync();
        var endpoint = new Uri(server.Urls.Single() + "/sparql");
        await using var database = await RdfDatabase.OpenLocalAsync(new(CreateDirectory())
        {
            HttpResources = new([endpoint], allowPrivateNetwork: true)
        });
        await database.CommitSparqlUpdateAsync(Guid.NewGuid(), "INSERT DATA { <urn:a> <urn:local> 9 . GRAPH <urn:g> { <urn:x> <urn:local> 3 } }");
        var result = await database.QuerySparqlAsync(query.Replace("ENDPOINT", endpoint.AbsoluteUri, StringComparison.Ordinal));
        int count = result switch
        {
            RdfSparqlSelectResult select => select.Bindings.Count,
            RdfSparqlAskResult ask => ask.Value ? 1 : 0,
            RdfSparqlGraphResult graph => graph.Quads.Count,
            _ => -1
        };
        Assert.AreEqual(expected, count);
        Assert.AreEqual(1, fetched);
        StringAssert.Contains(submitted!, "<urn:p>");
        await server.StopAsync();
    }

    [TestMethod]
    public async Task Sparql11_SERVICE_correlated_blank_node_identity_rejects_before_network()
    {
        await using var database = await RdfDatabase.OpenLocalAsync(new(CreateDirectory()));
        foreach (string keyword in new[] { "EXISTS", "NOT EXISTS" })
        {
            await Assert.ThrowsExactlyAsync<NotSupportedException>(() => database.QuerySparqlAsync(
                $"SELECT ?x WHERE {{ BIND(BNODE() AS ?x) FILTER {keyword} {{ SERVICE <https://example.test/sparql> {{ ?x <urn:p> ?v }} }} }}").AsTask());
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Sparql11_SERVICE_update_is_atomic_and_replays_without_refetch_after_restart(bool xml)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var server = builder.Build();
        int fetched = 0;
        server.MapPost("/sparql", () =>
        {
            Interlocked.Increment(ref fetched);
            return xml ? Results.Text("""
                <sparql xmlns="http://www.w3.org/2005/sparql-results#"><head><variable name="s"/><variable name="v"/></head><results>
                <result><binding name="s"><uri>urn:a</uri></binding><binding name="v"><literal datatype="http://www.w3.org/2001/XMLSchema#integer">1</literal></binding></result>
                <result><binding name="s"><uri>urn:b</uri></binding><binding name="v"><literal datatype="http://www.w3.org/2001/XMLSchema#integer">2</literal></binding></result>
                </results></sparql>
                """, "application/sparql-results+xml") : Results.Text(ServiceRows, "application/sparql-results+json");
        });
        await server.StartAsync();
        var endpoint = new Uri(server.Urls.Single() + "/sparql");
        string directory = CreateDirectory();
        var operation = Guid.NewGuid();
        string update = $"INSERT DATA {{ <urn:a> <urn:local> 9 }}; INSERT {{ ?s <urn:copy> ?v }} WHERE {{ ?s <urn:local> ?x SERVICE <{endpoint}> {{ ?s <urn:p> ?v }} }}; INSERT {{ ?s <urn:last> ?v }} WHERE {{ ?s <urn:copy> ?v }}";
        RdfSparqlUpdateCommit? original = null;
        for (int restart = 0; restart < 2; restart++)
        {
            await using var database = await RdfDatabase.OpenLocalAsync(new(directory)
            {
                HttpResources = new([endpoint], allowPrivateNetwork: true)
            });
            var result = await database.CommitSparqlUpdateAsync(operation, update);
            original ??= result;
            Assert.AreEqual(original, result);
            Assert.AreEqual(result, await database.CommitSparqlUpdateAsync(operation, update));
            Assert.AreEqual(3, database.Find(new()).Count);
            Assert.AreEqual(1, fetched);
        }
        await using (var rejected = await RdfDatabase.OpenLocalAsync(new(CreateDirectory())))
        {
            await Assert.ThrowsExactlyAsync<RdfSparqlRemoteExecutionException>(() => rejected.CommitSparqlUpdateAsync(Guid.NewGuid(), update).AsTask());
            Assert.AreEqual(0, rejected.Find(new()).Count);
        }
        await server.StopAsync();
    }

    [TestMethod]
    public async Task Sparql11_SERVICE_preserves_bags_and_scopes_remote_blank_nodes_per_call()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var server = builder.Build();
        int fetched = 0;
        server.MapPost("/sparql", () =>
        {
            Interlocked.Increment(ref fetched);
            return Results.Text("""{"head":{"vars":["b"]},"results":{"bindings":[{"b":{"type":"bnode","value":"same"}},{"b":{"type":"bnode","value":"same"}}]}}""", "application/sparql-results+json");
        });
        await server.StartAsync();
        var endpoint = new Uri(server.Urls.Single() + "/sparql");
        await using var database = await RdfDatabase.OpenLocalAsync(new(CreateDirectory())
        {
            HttpResources = new([endpoint], allowPrivateNetwork: true)
        });
        string service = $"SERVICE <{endpoint}> {{ ?b <urn:p> ?v }}";
        var result = (RdfSparqlSelectResult)await database.QuerySparqlAsync($"SELECT ?b WHERE {{ {{ {service} }} UNION {{ {service} }} }}");
        Assert.AreEqual(4, result.Bindings.Count);
        Assert.AreEqual(2, result.Bindings.Select(row => row["b"]).Distinct().Count());
        Assert.IsTrue(result.Bindings.All(row => row["b"] is RdfBlankNode));
        Assert.AreEqual(2, fetched, "Equal SERVICE text in separate clauses must not coalesce calls.");
        await server.StopAsync();
    }

    [TestMethod]
    [DataRow("\"boolean\":true", false)]
    [DataRow("\"boolean\":false", false)]
    [DataRow("\"boolean\":null", false)]
    [DataRow("\"extension\":{\"boolean\":true}", true)]
    public async Task Sparql11_SERVICE_JSON_distinguishes_ASK_results_from_extension_members(string member, bool valid)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var server = builder.Build();
        server.MapPost("/sparql", () => Results.Text("{\"head\":{\"vars\":[\"v\"]},\"results\":{\"bindings\":[]}," + member + "}", "application/sparql-results+json"));
        await server.StartAsync();
        var endpoint = new Uri(server.Urls.Single() + "/sparql");
        await using var database = await RdfDatabase.OpenLocalAsync(new(CreateDirectory())
        {
            HttpResources = new([endpoint], allowPrivateNetwork: true)
        });
        string pattern = $"<{endpoint}> {{ ?s ?p ?v }}";
        if (valid)
        {
            Assert.AreEqual(0, ((RdfSparqlSelectResult)await database.QuerySparqlAsync("SELECT * { SERVICE " + pattern + " }")).Bindings.Count);
        }
        else
        {
            await Assert.ThrowsExactlyAsync<RdfSparqlRemoteExecutionException>(() => database.QuerySparqlAsync("SELECT * { SERVICE " + pattern + " }").AsTask());
        }
        var silent = (RdfSparqlSelectResult)await database.QuerySparqlAsync("SELECT * { SERVICE SILENT " + pattern + " }");
        Assert.AreEqual(valid ? 0 : 1, silent.Bindings.Count);
        if (!valid)
        {
            Assert.AreEqual(0, silent.Bindings[0].Count);
        }
        await server.StopAsync();
    }

    [TestMethod]
    public async Task Sparql11_SERVICE_SILENT_returns_one_empty_solution_on_failure_but_not_on_empty_success()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var server = builder.Build();
        server.MapPost("/empty", () => Results.Text("""{"head":{"vars":["s"]},"results":{"bindings":[]}}""", "application/sparql-results+json"));
        server.MapPost("/bad", () => Results.Text("{}", "application/sparql-results+json"));
        await server.StartAsync();
        var empty = new Uri(server.Urls.Single() + "/empty");
        var bad = new Uri(server.Urls.Single() + "/bad");
        await using var database = await RdfDatabase.OpenLocalAsync(new(CreateDirectory())
        {
            HttpResources = new([empty, bad], allowPrivateNetwork: true)
        });
        foreach (var endpoint in new[] { new Uri(server.Urls.Single() + "/denied"), bad })
        {
            string service = $"SERVICE <{endpoint}> {{ ?s <urn:p> ?v }}";
            await Assert.ThrowsExactlyAsync<RdfSparqlRemoteExecutionException>(() => database.QuerySparqlAsync($"SELECT * {{ {service} }}").AsTask());
            var silent = (RdfSparqlSelectResult)await database.QuerySparqlAsync($"SELECT * {{ {service.Replace("SERVICE ", "SERVICE SILENT ")} }}");
            Assert.AreEqual(1, silent.Bindings.Count);
            Assert.AreEqual(0, silent.Bindings[0].Count);
        }
        var success = (RdfSparqlSelectResult)await database.QuerySparqlAsync($"SELECT * {{ SERVICE SILENT <{empty}> {{ ?s <urn:p> ?v }} }}");
        Assert.AreEqual(0, success.Bindings.Count);
        await server.StopAsync();
    }

    [TestMethod]
    [DataRow("literal", "\"datatype\":null")]
    [DataRow("literal", "\"xml:lang\":\"123\"")]
    [DataRow("literal", "\"xml:lang\":\"-en\"")]
    [DataRow("literal", "\"xml:lang\":\"en--GB\"")]
    [DataRow("literal", "\"xml:lang\":null")]
    [DataRow("literal", "\"datatype\":\"http://www.w3.org/1999/02/22-rdf-syntax-ns#langString\",\"xml:lang\":\"en\"")]
    [DataRow("uri", "\"datatype\":\"urn:datatype\"")]
    [DataRow("uri", "\"xml:lang\":\"en\"")]
    [DataRow("bnode", "\"datatype\":\"urn:datatype\"")]
    [DataRow("bnode", "\"xml:lang\":\"en\"")]
    public async Task Sparql11_SERVICE_JSON_rejects_invalid_term_metadata(string type, string metadata)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var server = builder.Build();
        string body = "{\"head\":{\"vars\":[\"v\"]},\"results\":{\"bindings\":[{\"v\":{\"type\":\"" + type + "\",\"value\":\"urn:text\"," + metadata + "}}]}}";
        server.MapPost("/sparql", () => Results.Text(body, "application/sparql-results+json"));
        await server.StartAsync();
        var endpoint = new Uri(server.Urls.Single() + "/sparql");
        await using var database = await RdfDatabase.OpenLocalAsync(new(CreateDirectory())
        {
            HttpResources = new([endpoint], allowPrivateNetwork: true)
        });
        await Assert.ThrowsExactlyAsync<RdfSparqlRemoteExecutionException>(() => database.QuerySparqlAsync($"SELECT * {{ SERVICE <{endpoint}> {{ ?s ?p ?v }} }}").AsTask());
        var silent = (RdfSparqlSelectResult)await database.QuerySparqlAsync($"SELECT * {{ SERVICE SILENT <{endpoint}> {{ ?s ?p ?v }} }}");
        Assert.AreEqual(1, silent.Bindings.Count);
        Assert.AreEqual(0, silent.Bindings[0].Count);
        await server.StopAsync();
    }

    [TestMethod]
    [DataRow("bytes")]
    [DataRow("rows")]
    [DataRow("columns")]
    [DataRow("depth")]
    public async Task Sparql11_SERVICE_hard_limits_are_not_silenced(string limit)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var server = builder.Build();
        string body = limit switch
        {
            "bytes" => new string(' ', 4 * 1024 * 1024 + 1),
            "columns" => "{\"head\":{\"vars\":[" + String.Join(',', Enumerable.Range(0, 257).Select(i => $"\"v{i}\"")) + "]},\"results\":{\"bindings\":[]}}",
            "depth" => "{\"head\":{\"vars\":[]},\"results\":{\"bindings\":[]},\"extension\":" + new string('[', 17) + "0" + new string(']', 17) + "}",
            _ => "{\"head\":{\"vars\":[]},\"results\":{\"bindings\":[" + String.Join(',', Enumerable.Repeat("{}", 10001)) + "]}}"
        };
        server.MapPost("/large", () => Results.Text(body, "application/sparql-results+json"));
        await server.StartAsync();
        var endpoint = new Uri(server.Urls.Single() + "/large");
        await using var database = await RdfDatabase.OpenLocalAsync(new(CreateDirectory())
        {
            HttpResources = new([endpoint], allowPrivateNetwork: true)
        });
        await Assert.ThrowsExactlyAsync<RdfSparqlWorkLimitException>(() => database.QuerySparqlAsync($"SELECT * {{ SERVICE SILENT <{endpoint}> {{ ?s ?p ?o }} }}").AsTask());
        var version = database.CurrentVersion;
        await Assert.ThrowsExactlyAsync<RdfSparqlWorkLimitException>(() => database.CommitSparqlUpdateAsync(Guid.NewGuid(),
            $"INSERT DATA {{ <urn:staged> <urn:p> 1 }}; INSERT {{ ?s ?p ?o }} WHERE {{ SERVICE SILENT <{endpoint}> {{ ?s ?p ?o }} }}").AsTask());
        Assert.AreEqual(version, database.CurrentVersion);
        Assert.AreEqual(0, database.Find(new()).Count);
        await server.StopAsync();
    }

    [TestMethod]
    public async Task Sparql11_SERVICE_cancellation_is_not_silenced()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var server = builder.Build();
        server.MapPost("/slow", async (HttpContext context) => await Task.Delay(Timeout.Infinite, context.RequestAborted));
        await server.StartAsync();
        var endpoint = new Uri(server.Urls.Single() + "/slow");
        await using var database = await RdfDatabase.OpenLocalAsync(new(CreateDirectory())
        {
            HttpResources = new([endpoint], allowPrivateNetwork: true)
        });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAsync<OperationCanceledException>(() => database.QuerySparqlAsync($"SELECT * {{ SERVICE SILENT <{endpoint}> {{ ?s ?p ?o }} }}", cancellation.Token).AsTask());
        await server.StopAsync();
    }
}
