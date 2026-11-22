using System.Text;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("text/turtle", "default")]
    [DataRow("text/turtle", "existing")]
    [DataRow("text/turtle", "absent")]
    [DataRow("application/n-triples", "default")]
    [DataRow("application/n-triples", "existing")]
    [DataRow("application/n-triples", "absent")]
    [DataRow("application/rdf+xml", "default")]
    [DataRow("application/rdf+xml", "existing")]
    [DataRow("application/rdf+xml", "absent")]
    public async Task Sparql_load_empty_document_preserves_data_and_replays_without_refetch(string media, string target)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var server = builder.Build();
        int fetched = 0;
        server.MapGet("/graph", () =>
        {
            Interlocked.Increment(ref fetched);
            return Results.Text(media == "application/rdf+xml"
                ? "<rdf:RDF xmlns:rdf='http://www.w3.org/1999/02/22-rdf-syntax-ns#'/>" : "", media);
        });
        await server.StartAsync();
        var source = new Uri(server.Urls.Single() + "/graph");
        string directory = CreateDirectory();
        Guid operation = Guid.NewGuid();
        string update = $"LOAD <{source}>" + (target == "default" ? "" : $" INTO GRAPH <{(target == "existing" ? "urn:g" : "urn:empty")}>") +
            "; INSERT DATA { <urn:marker> <urn:p> 'after' }";
        RdfQuad[] seed =
        [
            new(new RdfIri("urn:s"), new("urn:p"), new RdfLiteral("default")),
            new(new RdfIri("urn:s"), new("urn:p"), new RdfLiteral("named"), new RdfIri("urn:g"))
        ];
        RdfQuad[] expected = [.. seed, new(new RdfIri("urn:marker"), new("urn:p"), new RdfLiteral("after"))];
        RdfSparqlUpdateCommit? receipt = null;
        for (int restart = 0; restart < 2; restart++)
        {
            await using var database = await RdfDatabase.OpenLocalAsync(new(directory)
            {
                HttpResources = new([source], allowPrivateNetwork: true)
            });
            if (restart == 0)
            {
                await database.CommitAsync(new(Guid.NewGuid(), seed.Select(q => (RdfMutation)new AssertRdfQuad(q)).ToArray()));
            }
            var actual = await database.CommitSparqlUpdateAsync(operation, update);
            receipt ??= actual;
            Assert.AreEqual(receipt, actual);
            Assert.AreEqual(1, actual.MutationCount);
            CollectionAssert.AreEquivalent(expected, database.Find(new()).ToArray());
            Assert.AreEqual(receipt, await database.CommitSparqlUpdateAsync(operation, update));
            Assert.AreEqual(1, fetched);
            CollectionAssert.AreEquivalent(new RdfTerm[] { new RdfIri("urn:g") },
                RdfSparql.Execute(database, "SELECT ?g { GRAPH ?g {} }").Select(row => row["g"]).ToArray());
        }
        await server.StopAsync();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Sparql_load_interrupted_transfer_is_atomic_and_failed_operation_can_retry(bool silent)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var server = builder.Build();
        int broken = 1, fetched = 0, flushed = 0;
        byte[] payload = Encoding.UTF8.GetBytes("<urn:loaded> <urn:p> 'remote' .");
        server.MapGet("/graph", async (HttpContext context) =>
        {
            Interlocked.Increment(ref fetched);
            bool truncate = Volatile.Read(ref broken) != 0;
            context.Response.ContentType = "text/turtle";
            context.Response.ContentLength = payload.Length + (truncate ? 100 : 0);
            await context.Response.Body.WriteAsync(payload, context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
            Interlocked.Increment(ref flushed);
            if (truncate)
            {
                context.Abort();
            }
        });
        await server.StartAsync();
        var source = new Uri(server.Urls.Single() + "/graph");
        string directory = CreateDirectory();
        Guid operation = Guid.NewGuid();
        string update = "INSERT DATA { <urn:before> <urn:p> 'before' }; LOAD " +
            (silent ? "SILENT " : "") + $"<{source}>; INSERT DATA {{ <urn:after> <urn:p> 'after' }}";
        var sentinel = new RdfQuad(new RdfIri("urn:keep"), new("urn:p"), new RdfLiteral("original"));
        RdfSparqlUpdateCommit? first = null;
        await using (var database = await RdfDatabase.OpenLocalAsync(new(directory)
        {
            HttpResources = new([source], allowPrivateNetwork: true)
        }))
        {
            await database.CommitAsync(new(Guid.NewGuid(), [new AssertRdfQuad(sentinel)]));
            var before = database.CurrentVersion;
            if (silent)
            {
                first = await database.CommitSparqlUpdateAsync(operation, update);
                Assert.AreEqual(2, first.MutationCount);
                Assert.HasCount(3, database.Find(new()));
            }
            else
            {
                await Assert.ThrowsExactlyAsync<RdfSparqlRemoteExecutionException>(() => database.CommitSparqlUpdateAsync(operation, update).AsTask());
                Assert.AreEqual(before, database.CurrentVersion);
                CollectionAssert.AreEquivalent(new[] { sentinel }, database.Find(new()).ToArray());
            }
            Assert.HasCount(0, database.Find(new(Subject: new RdfIri("urn:loaded"))));
            Assert.AreEqual(1, fetched);
            Assert.AreEqual(1, flushed);
        }
        Volatile.Write(ref broken, 0);
        await using var reopened = await RdfDatabase.OpenLocalAsync(new(directory)
        {
            HttpResources = new([source], allowPrivateNetwork: true)
        });
        var retry = await reopened.CommitSparqlUpdateAsync(operation, update);
        if (silent)
        {
            Assert.AreEqual(first, retry);
            Assert.AreEqual(1, fetched, "Successful SILENT receipt must replay, not fetch the repaired source.");
            Assert.HasCount(0, reopened.Find(new(Subject: new RdfIri("urn:loaded"))));
            await reopened.CommitSparqlUpdateAsync(Guid.NewGuid(), update);
        }
        else
        {
            Assert.AreEqual(3, retry.MutationCount);
            Assert.AreEqual(retry, await reopened.CommitSparqlUpdateAsync(operation, update));
        }
        Assert.AreEqual(2, fetched);
        CollectionAssert.AreEquivalent(new RdfQuad[]
        {
            sentinel,
            new(new RdfIri("urn:before"), new("urn:p"), new RdfLiteral("before")),
            new(new RdfIri("urn:after"), new("urn:p"), new RdfLiteral("after")),
            new(new RdfIri("urn:loaded"), new("urn:p"), new RdfLiteral("remote"))
        }, reopened.Find(new()).ToArray());
        await server.StopAsync();
    }
}
