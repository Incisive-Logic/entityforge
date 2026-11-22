using EntityForge.Db.Rdf;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("ASK {} LIMIT 0", false)]
    [DataRow("ASK { VALUES ?x {} }", false)]
    [DataRow("ASK { VALUES (?x ?y) {} }", false)]
    [DataRow("ASK { VALUES () {} }", false)]
    [DataRow("ASK { VALUES () { () () } } OFFSET 1", true)]
    [DataRow("ASK { VALUES () { () () } } OFFSET 2", false)]
    [DataRow("ASK {} LIMIT 1", true)]
    [DataRow("ASK {} OFFSET 1", false)]
    [DataRow("ASK { VALUES ?x { 3 1 2 } } ORDER BY ?x LIMIT 1 OFFSET 2", true)]
    [DataRow("ASK { VALUES ?x { 3 1 2 } } ORDER BY DESC(?x) OFFSET 3 LIMIT 1", false)]
    [DataRow("ASK { VALUES ?x { 1 1 2 } } GROUP BY ?x HAVING (COUNT(*)>1)", true)]
    [DataRow("ASK { VALUES ?x { 1 1 2 } } GROUP BY ?x HAVING (COUNT(*)>2)", false)]
    [DataRow("ASK { VALUES ?x { 1 1 2 } } GROUP BY ?x ORDER BY COUNT(*) OFFSET 2", false)]
    [DataRow("ASK { VALUES ?x {} } HAVING (COUNT(*)=0)", true)]
    [DataRow("ASK { VALUES ?x { 1 2 } } HAVING (SUM(?x)=3)", true)]
    public async Task Sparql11_ask_modifiers_match_independent_reference(string query, bool expected)
    {
        using var store = new VDS.RDF.TripleStore();
        // dotNetRDF 3.5.2's ASK shortcut ignores solution modifiers. Evaluate
        // the same solution sequence through SELECT, then test its existence.
        string projection = query.Contains("COUNT(", StringComparison.Ordinal) || query.Contains("SUM(", StringComparison.Ordinal)
            ? "COUNT(*)" : "1";
        string referenceQuery = $"SELECT ({projection} AS ?result) WHERE " + query[4..];
        // Its zero-column VALUES shortcut also collapses the row bag. A dummy
        // UNDEF column preserves exactly the same empty solution occurrences.
        referenceQuery = referenceQuery.Replace("VALUES () {}", "VALUES ?unit {}", StringComparison.Ordinal)
            .Replace("VALUES () { () () }", "VALUES ?unit { UNDEF UNDEF }", StringComparison.Ordinal);
        var reference = (VDS.RDF.Query.SparqlResultSet)new VDS.RDF.Query.LeviathanQueryProcessor(store)
            .ProcessQuery(new VDS.RDF.Parsing.SparqlQueryParser().ParseFromString(referenceQuery));
        await using var database = await OpenAsync(CreateDirectory());
        Assert.AreEqual(expected, reference.Count > 0, query);
        Assert.AreEqual(expected, RdfSparql.ExecuteAsk(database, query), query);
    }

    [TestMethod]
    [DataRow("GROUP BY ?s HAVING (COUNT(*)>1)", 1)]
    [DataRow("GROUP BY ?s HAVING (COUNT(*)>2)", 0)]
    [DataRow("GROUP BY ?s ORDER BY DESC(COUNT(*)) LIMIT 1", 1)]
    [DataRow("GROUP BY ?s ORDER BY COUNT(*) OFFSET 2", 0)]
    public async Task Sparql11_construct_modifiers_match_independent_reference(string modifiers, int count)
    {
        string query = "CONSTRUCT { ?s <urn:seen> true } WHERE { VALUES ?s { <urn:a> <urn:a> <urn:b> } } " + modifiers;
        using var store = new VDS.RDF.TripleStore();
        var reference = (VDS.RDF.IGraph)new VDS.RDF.Query.LeviathanQueryProcessor(store)
            .ProcessQuery(new VDS.RDF.Parsing.SparqlQueryParser().ParseFromString(query));
        await using var database = await OpenAsync(CreateDirectory());
        var actual = RdfSparql.ExecuteConstruct(database, query);
        Assert.AreEqual(count, reference.Triples.Count);
        Assert.AreEqual(count, actual.Count);
        CollectionAssert.AreEquivalent(reference.Triples.Select(triple => ((VDS.RDF.IUriNode)triple.Subject).Uri.AbsoluteUri).ToArray(),
            actual.Select(quad => ((EntityForge.Db.Rdf.Contracts.RdfIri)quad.Subject).Value).ToArray());
    }
}
