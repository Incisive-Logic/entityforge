using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    public async Task Sparql11_construct_omits_invalid_triples_individually_and_deduplicates_valid_triples()
    {
        await using var database = await OpenAsync(CreateDirectory());
        var actual = RdfSparql.ExecuteConstruct(database, """
            CONSTRUCT { ?s ?p ?o . <urn:constant> <urn:p> <urn:o> }
            WHERE {
                VALUES (?s ?p ?o) {
                    (<urn:a> <urn:p> <urn:x>)
                    (UNDEF <urn:p> <urn:x>)
                    ("literal" <urn:p> <urn:x>)
                    (<urn:a> "bad" <urn:x>)
                    (<urn:a> UNDEF <urn:x>)
                    (<urn:a> <urn:p> UNDEF)
                    (<urn:a> <urn:p> <urn:x>)
                }
            }
            """);
        RdfQuad[] expected =
        [
            new(new RdfIri("urn:a"), new RdfIri("urn:p"), new RdfIri("urn:x")),
            new(new RdfIri("urn:constant"), new RdfIri("urn:p"), new RdfIri("urn:o"))
        ];
        CollectionAssert.AreEquivalent(expected, actual.ToArray());
    }

    [TestMethod]
    [DataRow("{}", 1)]
    [DataRow("{ FILTER(false) }", 0)]
    [DataRow("{ VALUES ?unused { 1 1 2 } }", 1)]
    public async Task Sparql11_construct_ground_template_obeys_solution_existence_and_set_semantics(string pattern, int count)
    {
        await using var database = await OpenAsync(CreateDirectory());
        var actual = RdfSparql.ExecuteConstruct(database,
            "CONSTRUCT { <urn:s> <urn:p> <urn:o> } WHERE " + pattern);
        Assert.AreEqual(count, actual.Count);
        if (count != 0)
        {
            Assert.AreEqual(new RdfQuad(new RdfIri("urn:s"), new RdfIri("urn:p"), new RdfIri("urn:o")), actual.Single());
        }
    }

    [TestMethod]
    public async Task Sparql11_construct_blank_node_predicate_does_not_discard_valid_sibling_triple()
    {
        await using var database = await OpenAsync(CreateDirectory());
        var actual = RdfSparql.ExecuteConstruct(database, """
            CONSTRUCT { <urn:s> ?p <urn:o> . <urn:s> <urn:p> ?p }
            WHERE { BIND(BNODE() AS ?p) }
            """);
        Assert.AreEqual(1, actual.Count);
        var triple = actual.Single();
        Assert.AreEqual(new RdfIri("urn:s"), triple.Subject);
        Assert.AreEqual(new RdfIri("urn:p"), triple.Predicate);
        Assert.IsInstanceOfType<RdfBlankNode>(triple.Object);
        Assert.IsNull(triple.Graph);
    }
}
