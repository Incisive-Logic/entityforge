using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Sparql_path_object_lists_preserve_endpoint_joins_hidden_nodes_and_update_replay(bool named)
    {
        RdfIri? graph = named ? new("urn:paths") : null;
        RdfQuad Quad(string subject, string predicate, string obj) => new(new RdfIri("urn:" + subject),
            new("urn:" + predicate), new RdfIri("urn:" + obj), graph);
        RdfQuad[] input = [Quad("s", "p", "m"), Quad("m", "q", "a"), Quad("m", "q", "b"),
            Quad("s", "r", "n"), Quad("n", "q", "c"), Quad("n", "q", "d"),
            Quad("a", "tag", "chosen"), Quad("c", "tag", "chosen")];
        string Scope(string body) => named ? "GRAPH <urn:paths> { " + body + " }" : body;
        var cases = new (string Body, string[] Expected)[]
        {
            ("<urn:s> <urn:p>/<urn:q> ?x ; <urn:r>/<urn:q> ?y", ["a/c", "a/d", "b/c", "b/d"]),
            ("<urn:s> <urn:p>/<urn:q> ?x, [ <urn:tag> <urn:chosen> ]", ["a", "b"]),
            ("<urn:s> <urn:p>/<urn:q> ?x ; <urn:r>/<urn:q> [ <urn:tag> <urn:chosen> ]", ["a", "b"]),
            ("<urn:s> <urn:p>/<urn:q> [ <urn:tag> <urn:chosen> ] ; <urn:r>/<urn:q> ?x, [ <urn:tag> <urn:chosen> ]", ["c", "d"]),
            ("<urn:s> <urn:p>/<urn:q> ?x, ?x", ["a", "b"])
        };
        string directory = CreateDirectory();
        Guid request = Guid.NewGuid();
        string update = "INSERT { <urn:report> <urn:end> ?x } WHERE { " + Scope(cases[3].Body) + " }";
        RdfSparqlUpdateCommit? receipt = null;
        for (int round = 0; round < 2; round++)
        {
            await using var database = await OpenAsync(directory);
            if (round == 0)
            {
                await database.BulkLoadAsync(Guid.NewGuid(), input);
            }
            foreach (var (body, expected) in cases)
            {
                var result = (RdfSparqlSelectResult)await database.QuerySparqlAsync("SELECT * WHERE { " + Scope(body) + " }");
                foreach (var row in result.Bindings)
                {
                    CollectionAssert.AreEquivalent(body.Contains("?y", StringComparison.Ordinal) ? new[] { "x", "y" } : ["x"], row.Keys.ToArray());
                }
                string[] actual = result.Bindings.Select(row => ((RdfIri)row["x"]).Value[4..] +
                    (row.TryGetValue("y", out var y) ? "/" + ((RdfIri)y).Value[4..] : "")).ToArray();
                CollectionAssert.AreEquivalent(expected, actual, body);
            }
            var committed = await database.CommitSparqlUpdateAsync(request, update);
            receipt ??= committed;
            Assert.AreEqual(receipt, committed);
            RdfQuad[] expectedQuads = [.. input,
                new(new RdfIri("urn:report"), new("urn:end"), new RdfIri("urn:c")),
                new(new RdfIri("urn:report"), new("urn:end"), new RdfIri("urn:d"))];
            CollectionAssert.AreEquivalent(expectedQuads, database.Find(new()).ToArray());
        }
    }
}
