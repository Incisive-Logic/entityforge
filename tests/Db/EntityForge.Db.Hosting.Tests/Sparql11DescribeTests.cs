using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("DESCRIBE <urn:a>")]
    [DataRow("DESCRIBE <urn:a> FROM NAMED <urn:g> FROM NAMED <urn:h>")]
    [DataRow("DESCRIBE ?s WHERE { GRAPH ?g { ?s <urn:p> <urn:o> } }")]
    [DataRow("DESCRIBE * WHERE { GRAPH <urn:g> { ?s <urn:p> <urn:o> } }")]
    public async Task Sparql11_describe_merges_selected_subject_triples_into_one_result_graph(string query)
    {
        await using var database = await OpenAsync(CreateDirectory());
        await database.CommitAsync(new(Guid.NewGuid(), RdfSparqlUpdate.Parse("""
            INSERT DATA {
                <urn:a> <urn:p> <urn:o> .
                GRAPH <urn:g> { <urn:a> <urn:p> <urn:o> ; <urn:detail> "detail" }
                GRAPH <urn:h> { <urn:a> <urn:p> <urn:o> }
            }
            """, "describe-graph")));
        RdfQuad[] expected =
        [
            new(new RdfIri("urn:a"), new RdfIri("urn:p"), new RdfIri("urn:o")),
            new(new RdfIri("urn:a"), new RdfIri("urn:detail"), new RdfLiteral("detail"))
        ];
        var actual = RdfSparql.ExecuteDescribe(database, query);
        Assert.IsTrue(actual.All(quad => quad.Graph is null), "DESCRIBE returns an RDF graph, not its source dataset.");
        CollectionAssert.AreEquivalent(expected, actual.ToArray());
    }

    [TestMethod]
    [DataRow("DESCRIBE <urn:a>", 2)]
    [DataRow("PREFIX ex:<urn:> DESCRIBE ex:a", 2)]
    [DataRow("BASE <urn:> DESCRIBE <urn:a>", 2)]
    [DataRow("DESCRIBE <urn:a> LIMIT 0", 2)]
    [DataRow("DESCRIBE <urn:a> WHERE { FILTER(false) }", 2)]
    [DataRow("DESCRIBE ?s { ?s <urn:p> 1 }", 2)]
    [DataRow("DESCRIBE ?s WHERE { ?s <urn:p> 1 } LIMIT 0", 0)]
    [DataRow("DESCRIBE * { ?s <urn:p> 1 }", 2)]
    [DataRow("DESCRIBE * { [ <urn:p> 1 ] }", 0)]
    [DataRow("DESCRIBE *", 0)]
    [DataRow("DESCRIBE ?unbound", 0)]
    [DataRow("DESCRIBE <urn:a> FROM <urn:g>", 1)]
    [DataRow("DESCRIBE <urn:a> FROM <urn:g> LIMIT 0", 1)]
    [DataRow("DESCRIBE <urn:a> FROM NAMED <urn:g>", 1)]
    public async Task Sparql11_describe_resolves_explicit_and_wildcard_targets(string query, int count)
    {
        await using var database = await OpenAsync(CreateDirectory());
        await database.CommitAsync(new(Guid.NewGuid(), RdfSparqlUpdate.Parse(
            "INSERT DATA { <urn:a> <urn:p> 1 . GRAPH <urn:g> { <urn:a> <urn:p> 2 } }", "describe")));
        var actual = RdfSparql.ExecuteDescribe(database, query);
        Assert.AreEqual(count, actual.Count);
        Assert.IsTrue(actual.All(quad => quad.Subject == new RdfIri("urn:a")));
    }

    [TestMethod]
    [DataRow("DESCRIBE")]
    [DataRow("DESCRIBE 'literal'")]
    [DataRow("DESCRIBE <urn:a> WHERE")]
    [DataRow("DESCRIBE * <urn:a>")]
    public async Task Sparql11_describe_rejects_invalid_target_lists(string query)
    {
        await using var database = await OpenAsync(CreateDirectory());
        Assert.ThrowsExactly<InvalidDataException>(() => RdfSparql.ExecuteDescribe(database, query));
    }
}
