using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("CONSTRUCT WHERE { ?s <urn:p> ?o }", 2)]
    [DataRow("CONSTRUCT WHERE { ?s <urn:p> ?o } ORDER BY ?s LIMIT 1", 1)]
    [DataRow("CONSTRUCT WHERE { ?s <urn:p> ?o } LIMIT 0", 0)]
    [DataRow("PREFIX ex:<urn:> CONSTRUCT WHERE { ?s ex:p ?o }", 2)]
    [DataRow("CONSTRUCT FROM <urn:g> WHERE { ?s <urn:p> ?o }", 1)]
    [DataRow("CONSTRUCT WHERE {}", 0)]
    [DataRow("CONSTRUCT WHERE { ?s <urn:p> 1 }", 1)]
    [DataRow("CONSTRUCT WHERE { <urn:a> ?p ?o }", 1)]
    public async Task Sparql11_construct_shorthand_matches_reference_graph(string query, int count)
    {
        const string seed = "INSERT DATA { <urn:a> <urn:p> 1 . <urn:b> <urn:p> 2 . GRAPH <urn:g> { <urn:c> <urn:p> 3 } }";
        await using var database = await OpenAsync(CreateDirectory());
        await database.CommitAsync(new(Guid.NewGuid(), RdfSparqlUpdate.Parse(seed, "shorthand")));
        using var reference = new VDS.RDF.TripleStore();
        new VDS.RDF.Update.LeviathanUpdateProcessor(reference).ProcessCommandSet(new VDS.RDF.Parsing.SparqlUpdateParser().ParseFromString(seed));
        var graph = (VDS.RDF.IGraph)new VDS.RDF.Query.LeviathanQueryProcessor(reference)
            .ProcessQuery(new VDS.RDF.Parsing.SparqlQueryParser().ParseFromString(query));
        var actual = RdfSparql.ExecuteConstruct(database, query);
        Assert.AreEqual(count, actual.Count);
        var expected = graph.Triples.Select(triple => new RdfQuad(
            RdfSyntax.FromNode(triple.Subject, "reference"),
            (RdfIri)RdfSyntax.FromNode(triple.Predicate, "reference"),
            RdfSyntax.FromNode(triple.Object, "reference"))).ToArray();
        CollectionAssert.AreEquivalent(expected, actual.ToArray());
    }

    [TestMethod]
    [DataRow("CONSTRUCT WHERE { ?s <urn:p> ?o FILTER(true) }")]
    [DataRow("CONSTRUCT WHERE { ?s <urn:p>+ ?o }")]
    [DataRow("CONSTRUCT WHERE { VALUES ?s { <urn:a> } }")]
    public async Task Sparql11_construct_shorthand_rejects_non_template_algebra(string query)
    {
        await using var database = await OpenAsync(CreateDirectory());
        Assert.ThrowsExactly<InvalidDataException>(() => RdfSparql.ExecuteConstruct(database, query));
    }
}
