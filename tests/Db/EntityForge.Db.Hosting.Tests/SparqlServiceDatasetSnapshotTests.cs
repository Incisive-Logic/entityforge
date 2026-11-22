using EntityForge.Db.Rdf;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("SELECT", false)]
    [DataRow("ASK", false)]
    [DataRow("CONSTRUCT", false)]
    [DataRow("DESCRIBE", false)]
    [DataRow("SELECT", true)]
    [DataRow("ASK", true)]
    [DataRow("CONSTRUCT", true)]
    [DataRow("DESCRIBE", true)]
    public async Task Sparql11_SERVICE_selected_dataset_keeps_original_snapshot_during_remote_await(string form, bool named)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var server = builder.Build();
        Func<Task> advance = () => Task.CompletedTask;
        int fetched = 0;
        server.MapPost("/sparql", async () =>
        {
            Interlocked.Increment(ref fetched);
            await advance();
            return Results.Text(ServiceRows, "application/sparql-results+json");
        });
        await server.StartAsync();
        var endpoint = new Uri(server.Urls.Single() + "/sparql");
        await using var database = await RdfDatabase.OpenLocalAsync(new(CreateDirectory())
        {
            HttpResources = new([endpoint], allowPrivateNetwork: true)
        });
        await database.CommitSparqlUpdateAsync(Guid.NewGuid(), "INSERT DATA { GRAPH <urn:g> { <urn:a> <urn:local> 9 } }");
        var originalVersion = database.CurrentVersion;
        Assert.AreNotEqual(default(DatabaseVersion), originalVersion);
        advance = () => database.CommitSparqlUpdateAsync(Guid.NewGuid(),
            "INSERT DATA { GRAPH <urn:g> { <urn:b> <urn:local> 10 } }").AsTask();
        string head = form switch
        {
            "SELECT" => "SELECT ?s",
            "CONSTRUCT" => "CONSTRUCT { ?s <urn:copy> ?v }",
            "DESCRIBE" => "DESCRIBE ?s",
            _ => "ASK"
        };
        string dataset = named ? "FROM NAMED <urn:g>" : "FROM <urn:g>";
        string local = named ? "GRAPH <urn:g> { ?s <urn:local> ?local }" : "?s <urn:local> ?local";
        var result = await database.QuerySparqlAsync($"{head} {dataset} WHERE {{ {local} SERVICE <{endpoint}> {{ ?s <urn:p> ?v }} }}");
        Assert.AreEqual(originalVersion, result.SnapshotVersion);
        Assert.AreNotEqual(originalVersion, database.CurrentVersion);
        int count = result switch
        {
            RdfSparqlSelectResult select => select.Bindings.Count,
            RdfSparqlAskResult ask => ask.Value ? 1 : 0,
            RdfSparqlGraphResult graph => graph.Quads.Count,
            _ => -1
        };
        Assert.AreEqual(1, count, "A write during SERVICE must not add a local solution to the captured dataset.");
        Assert.AreEqual(1, fetched);
        await server.StopAsync();
    }
}
