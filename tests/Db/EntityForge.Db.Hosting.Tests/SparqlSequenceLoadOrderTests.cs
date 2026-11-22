using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task Sparql_remote_sequence_dataset_limit_stops_later_fetches_and_rolls_back(bool overflowBeforeLoad, bool silent)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var server = builder.Build();
        int fetched = 0;
        server.MapGet("/graph", () =>
        {
            Interlocked.Increment(ref fetched);
            return Results.Text("<urn:loaded> <urn:p> <urn:value> .", "text/turtle");
        });
        await server.StartAsync();
        var source = new Uri(server.Urls.Single() + "/graph");
        string directory = CreateDirectory();
        var input = Enumerable.Range(0, 100_000).Select(index => new RdfQuad(new RdfIri("urn:boundary:" + index),
            new("urn:p"), new RdfIri("urn:value"))).ToArray();
        string load = $"LOAD {(silent ? "SILENT " : "")}<{source}>";
        string update = (overflowBeforeLoad ? "INSERT DATA { <urn:marker> <urn:p> <urn:value> };" : "") +
            load + "; DELETE WHERE { <urn:boundary:0> <urn:p> <urn:value> };" + load;
        Guid request = Guid.NewGuid();
        for (int round = 0; round < 2; round++)
        {
            await using var database = await RdfDatabase.OpenLocalAsync(new(directory)
            {
                HttpResources = new([source], allowPrivateNetwork: true)
            });
            if (round == 0)
            {
                await database.BulkLoadAsync(Guid.NewGuid(), input);
            }
            var before = database.CurrentVersion;
            await Assert.ThrowsExactlyAsync<RdfSparqlWorkLimitException>(() =>
                database.CommitSparqlUpdateAsync(request, update).AsTask());
            Assert.AreEqual(before, database.CurrentVersion);
            CollectionAssert.AreEquivalent(input, database.Find(new()).ToArray());
            Assert.AreEqual(overflowBeforeLoad ? 0 : round + 1, fetched,
                "Resource exhaustion must stop the sequence before later LOADs, including SILENT LOADs.");
        }
        await server.StopAsync();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Sparql_sequence_does_not_fetch_load_after_unsilenced_failure(bool silent)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var server = builder.Build();
        int fetched = 0;
        server.MapGet("/graph", () =>
        {
            Interlocked.Increment(ref fetched);
            return Results.Text("<urn:loaded> <urn:p> <urn:value> .", "text/turtle");
        });
        await server.StartAsync();
        var source = new Uri(server.Urls.Single() + "/graph");
        string directory = CreateDirectory();
        await using var database = await RdfDatabase.OpenLocalAsync(new(directory)
        {
            HttpResources = new([source], allowPrivateNetwork: true)
        });
        string update = "INSERT DATA { <urn:marker> <urn:p> <urn:value> }; COPY " + (silent ? "SILENT " : "") +
            $"GRAPH <urn:missing> TO DEFAULT; LOAD <{source}>";
        var before = database.CurrentVersion;
        if (silent)
        {
            await database.CommitSparqlUpdateAsync(Guid.NewGuid(), update);
            CollectionAssert.AreEquivalent(new[]
            {
                new RdfQuad(new RdfIri("urn:marker"), new("urn:p"), new RdfIri("urn:value")),
                new RdfQuad(new RdfIri("urn:loaded"), new("urn:p"), new RdfIri("urn:value"))
            }, database.Find(new()).ToArray());
        }
        else
        {
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => database.CommitSparqlUpdateAsync(Guid.NewGuid(), update).AsTask());
            Assert.AreEqual(before, database.CurrentVersion);
            Assert.AreEqual(0, database.Find(new()).Count);
        }
        Assert.AreEqual(silent ? 1 : 0, fetched, "A failed operation must prevent later LOAD execution.");
        await server.StopAsync();
    }
}
