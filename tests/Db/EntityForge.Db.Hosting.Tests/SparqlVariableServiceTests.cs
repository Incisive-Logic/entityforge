using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("SELECT ?v WHERE { VALUES ?endpoint { <ENDPOINT> <ENDPOINT> } SERVICE ?endpoint { ?s <urn:p> ?v } }", 4)]
    [DataRow("SELECT ?v WHERE { BIND(<ENDPOINT> AS ?endpoint) SERVICE ?endpoint { ?s <urn:p> ?v } FILTER(?v=2) }", 1)]
    [DataRow("SELECT ?v WHERE { { VALUES ?endpoint { <ENDPOINT> } } SERVICE ?endpoint { ?s <urn:p> ?v } }", 2)]
    [DataRow("SELECT ?v WHERE { { SELECT ?v WHERE { VALUES ?endpoint { <ENDPOINT> } SERVICE ?endpoint { ?s <urn:p> ?v } } ORDER BY ?v LIMIT 1 } }", 1)]
    [DataRow("SELECT ?v WHERE { VALUES ?v { 1 2 3 } MINUS { VALUES ?endpoint { <ENDPOINT> } SERVICE ?endpoint { ?s <urn:p> ?v } } }", 1)]
    [DataRow("SELECT ?v WHERE { VALUES ?x { 1 } OPTIONAL { VALUES ?endpoint { <ENDPOINT> } SERVICE ?endpoint { ?s <urn:p> ?v } FILTER(?v=?x) } }", 1)]
    [DataRow("SELECT ?v WHERE { { VALUES ?endpoint { <ENDPOINT> } SERVICE ?endpoint { ?s <urn:p> ?v } } UNION { VALUES ?v { 3 } } }", 3)]
    [DataRow("ASK { VALUES ?endpoint { <ENDPOINT> } SERVICE ?endpoint { ?s <urn:p> ?v } }", 1)]
    [DataRow("CONSTRUCT { ?s <urn:copy> ?v } WHERE { VALUES ?endpoint { <ENDPOINT> } SERVICE ?endpoint { ?s <urn:p> ?v } }", 2)]
    public async Task Sparql11_variable_SERVICE_evaluates_with_bound_endpoint_and_native_algebra(string query, int expected)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var server = builder.Build();
        int calls = 0;
        server.MapPost("/sparql", () =>
        {
            Interlocked.Increment(ref calls);
            return Microsoft.AspNetCore.Http.Results.Text(ServiceRows, "application/sparql-results+json");
        });
        await server.StartAsync();
        var endpoint = new Uri(server.Urls.Single() + "/sparql");
        await using var database = await RdfDatabase.OpenLocalAsync(new(CreateDirectory())
        {
            HttpResources = new([endpoint], allowPrivateNetwork: true)
        });
        var result = await database.QuerySparqlAsync(query.Replace("ENDPOINT", endpoint.AbsoluteUri, StringComparison.Ordinal));
        Assert.AreEqual(expected, result switch
        {
            RdfSparqlSelectResult select => select.Bindings.Count,
            RdfSparqlAskResult ask => ask.Value ? 1 : 0,
            RdfSparqlGraphResult graph => graph.Quads.Count,
            _ => -1
        });
        Assert.AreEqual(1, calls, "Duplicate endpoint bindings retain bag multiplicity without repeated requests.");
        await server.StopAsync();
    }

    [TestMethod]
    [DataRow("UNDEF")]
    [DataRow("42")]
    [DataRow("<https://denied.example/sparql>")]
    public async Task Sparql11_variable_SERVICE_errors_and_silent_identity(string value)
    {
        await using var database = await RdfDatabase.OpenLocalAsync(new(CreateDirectory()));
        string body = $"VALUES ?endpoint {{ {value} }} SERVICE ?endpoint {{ ?s ?p ?o }}";
        await Assert.ThrowsExactlyAsync<RdfSparqlRemoteExecutionException>(() => database.QuerySparqlAsync("SELECT * { " + body + " }").AsTask());
        var silent = (RdfSparqlSelectResult)await database.QuerySparqlAsync("SELECT * { " + body.Replace("SERVICE ", "SERVICE SILENT ") + " }");
        Assert.AreEqual(1, silent.Bindings.Count);
        Assert.IsFalse(silent.Bindings[0].ContainsKey("s"));
    }

    [TestMethod]
    public async Task Sparql11_variable_SERVICE_call_budget_is_shared_and_not_silenced()
    {
        await using var database = await RdfDatabase.OpenLocalAsync(new(CreateDirectory()));
        string values = String.Join(' ', Enumerable.Range(0, 17).Select(index => $"<https://denied.example/{index}>"));
        await Assert.ThrowsExactlyAsync<RdfSparqlWorkLimitException>(() => database.QuerySparqlAsync(
            $"SELECT * {{ VALUES ?endpoint {{ {values} }} SERVICE SILENT ?endpoint {{ ?s ?p ?o }} }}").AsTask());
    }
}
