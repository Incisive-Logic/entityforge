using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("_:graph")]
    [DataRow("[]")]
    [DataRow("'urn:graph'")]
    [DataRow("1")]
    public async Task Sparql11_graph_name_grammar_rejects_non_iri_terms_without_partial_updates(string graph)
    {
        string directory = CreateDirectory();
        Guid seed = Guid.NewGuid();
        RdfQuad[] expected = [new(new RdfBlankNode("seed"), new("urn:p"), new RdfLiteral("keep"), new RdfIri("urn:graph"))];
        for (int restart = 0; restart < 2; restart++)
        {
            await using var database = await OpenAsync(directory);
            await database.CommitAsync(new(seed, expected.Select(quad => (RdfMutation)new AssertRdfQuad(quad)).ToArray()));
            var version = database.CurrentVersion;
            string pattern = "GRAPH " + graph + " { ?s <urn:p> ?o }";
            foreach (string query in new[]
            {
                "SELECT * WHERE { " + pattern + " }",
                "ASK { " + pattern + " }",
                "CONSTRUCT { ?s <urn:p> ?o } WHERE { " + pattern + " }"
            })
            {
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() => database.QuerySparqlAsync(query).AsTask());
            }
            foreach (string update in new[]
            {
                "INSERT DATA { GRAPH " + graph + " { <urn:s> <urn:p> 1 } }",
                "INSERT { GRAPH " + graph + " { <urn:s> <urn:p> ?o } } WHERE { VALUES ?o { 1 } }",
                "DELETE { ?s <urn:p> ?o } WHERE { " + pattern + " }",
                "DELETE WHERE { " + pattern + " }"
            })
            {
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() => database.CommitSparqlUpdateAsync(Guid.NewGuid(),
                    "INSERT DATA { <urn:must-not-commit> <urn:p> 1 }; " + update).AsTask());
                Assert.AreEqual(version, database.CurrentVersion);
                CollectionAssert.AreEquivalent(expected, database.Find(new()).ToArray());
            }
            var valid = (RdfSparqlSelectResult)await database.QuerySparqlAsync("SELECT ?g ?s WHERE { GRAPH ?g { ?s <urn:p> 'keep' } }");
            Assert.AreEqual(1, valid.Bindings.Count);
            Assert.AreEqual(new RdfIri("urn:graph"), valid.Bindings[0]["g"]);
            Assert.AreEqual(new RdfBlankNode("seed"), valid.Bindings[0]["s"]);
        }
    }
}
