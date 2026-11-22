using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Sparql_insert_data_standardizes_blank_nodes_apart_from_native_identifiers_and_replays(bool sequence)
    {
        Guid operation = Guid.NewGuid();
        var existing = new RdfBlankNode(operation.ToString("N") + (sequence ? "-operation-1" : "") + ":b");
        var sentinel = new RdfQuad(existing, new("urn:value"), new RdfLiteral("existing"));
        string update = (sequence ? "INSERT DATA { <urn:marker> <urn:p> 1 }; " : "") +
            "INSERT DATA { _:b <urn:self> _:b ; <urn:value> 'inserted' }";
        string directory = CreateDirectory();
        RdfQuad[]? committed = null;
        RdfSparqlUpdateCommit? receipt = null;
        for (int restart = 0; restart < 2; restart++)
        {
            await using var database = await OpenAsync(directory);
            if (restart == 0)
            {
                await database.CommitAsync(new(Guid.NewGuid(), [new AssertRdfQuad(sentinel)]));
            }
            var actual = await database.CommitSparqlUpdateAsync(operation, update);
            receipt ??= actual;
            Assert.AreEqual(receipt, actual);
            var quads = database.Find(new()).ToArray();
            var self = quads.Single(q => q.Predicate == new RdfIri("urn:self"));
            Assert.AreEqual(self.Subject, self.Object);
            Assert.AreNotEqual(existing, self.Subject);
            Assert.IsTrue(quads.Contains(sentinel));
            Assert.IsTrue(quads.Contains(new(self.Subject, new("urn:value"), new RdfLiteral("inserted"))));
            Assert.AreEqual(sequence ? 4 : 3, quads.Length);
            committed ??= quads;
            CollectionAssert.AreEquivalent(committed, quads);
            Assert.AreEqual(receipt, await database.CommitSparqlUpdateAsync(operation, update));
            CollectionAssert.AreEquivalent(committed, database.Find(new()).ToArray());
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Sparql_load_standardizes_blank_nodes_apart_from_native_identifiers_and_replays(bool sequence)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var server = builder.Build();
        int fetched = 0;
        server.MapGet("/graph", () =>
        {
            Interlocked.Increment(ref fetched);
            return Results.Text("_:b <urn:self> _:b ; <urn:value> 'loaded' .", "text/turtle");
        });
        await server.StartAsync();
        var source = new Uri(server.Urls.Single() + "/graph");
        Guid operation = Guid.NewGuid();
        string scope = operation.ToString("N") + (sequence ? "-operation-1" : "") + "-load:b";
        var existing = new RdfBlankNode(scope);
        var sentinel = new RdfQuad(existing, new("urn:value"), new RdfLiteral("existing"), new RdfIri("urn:g"));
        string update = (sequence ? "INSERT DATA { <urn:marker> <urn:p> 1 }; " : "") + $"LOAD <{source}> INTO GRAPH <urn:g>";
        string directory = CreateDirectory();
        RdfSparqlUpdateCommit? receipt = null;
        RdfQuad[]? committed = null;
        for (int restart = 0; restart < 2; restart++)
        {
            await using var database = await RdfDatabase.OpenLocalAsync(new(directory)
            {
                HttpResources = new([source], allowPrivateNetwork: true)
            });
            if (restart == 0)
            {
                await database.CommitAsync(new(Guid.NewGuid(), [new AssertRdfQuad(sentinel)]));
            }
            var actual = await database.CommitSparqlUpdateAsync(operation, update);
            receipt ??= actual;
            Assert.AreEqual(receipt, actual);
            var quads = database.Find(new()).ToArray();
            Assert.IsTrue(quads.Contains(sentinel));
            var self = quads.Single(q => q.Predicate == new RdfIri("urn:self"));
            Assert.AreEqual(self.Subject, self.Object);
            Assert.AreNotEqual(existing, self.Subject, "Imported blank nodes must not alias existing native identifiers.");
            Assert.IsTrue(quads.Contains(new(self.Subject, new("urn:value"), new RdfLiteral("loaded"), new RdfIri("urn:g"))));
            Assert.AreEqual(sequence ? 4 : 3, quads.Length);
            committed ??= quads;
            CollectionAssert.AreEquivalent(committed, quads);
            Assert.AreEqual(receipt, await database.CommitSparqlUpdateAsync(operation, update));
            CollectionAssert.AreEquivalent(committed, database.Find(new()).ToArray());
            Assert.AreEqual(1, fetched);
        }
        await server.StopAsync();
    }
}
