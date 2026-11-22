using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("SELECT ?key (COUNT(*) AS ?count) { VALUES ?x { 'A' 'a' 'B' } } GROUP BY (LCASE(?x) AS ?key) ORDER BY ?key")]
    [DataRow("SELECT (COUNT(*) AS ?count) { VALUES ?x { 'A' 'a' 'B' } } GROUP BY LCASE(?x) ORDER BY ?count")]
    [DataRow("SELECT ?key (COUNT(*) AS ?count) { VALUES ?x { 11 12 21 } } GROUP BY (FLOOR(?x/10) AS ?key) ORDER BY ?key")]
    [DataRow("SELECT (?bucket*2 AS ?key) (COUNT(*) AS ?count) { VALUES ?x { 11 12 21 } } GROUP BY (FLOOR(?x/10) AS ?bucket) ORDER BY ?key")]
    [DataRow("SELECT ?key (COUNT(*) AS ?count) { VALUES ?x { 1 2 3 } } GROUP BY (?x>1 AS ?key) ORDER BY ?key")]
    [DataRow("SELECT (COUNT(*) AS ?count) { VALUES ?x { 1 2 3 } } GROUP BY (?x>1) ORDER BY ?count")]
    [DataRow("SELECT ?x (COUNT(*) AS ?count) { VALUES ?x { 1 1 2 } } GROUP BY (?x) ORDER BY ?x")]
    [DataRow("SELECT ?key (COUNT(*) AS ?count) { VALUES ?x { 1 2 } } GROUP BY (?x/0 AS ?key)")]
    [DataRow("SELECT ?key (COUNT(*) AS ?count) { VALUES ?x {} } GROUP BY (?x+1 AS ?key)")]
    [DataRow("SELECT ?key (COUNT(*) AS ?count) { VALUES ?x { 'A' 'a' 'B' } } GROUP BY (LCASE(?x) AS ?key) HAVING (COUNT(*)>1) ORDER BY ?key")]
    [DataRow("SELECT ?key (COUNT(*) AS ?count) { VALUES ?x { 'AA' 'ab' 'BC' } } GROUP BY (SUBSTR(LCASE(?x),1,1) AS ?key) ORDER BY ?key")]
    public async Task Sparql11_computed_grouping_matches_independent_reference(string query)
    {
        using var store = new VDS.RDF.TripleStore();
        // Parentheses around a single variable do not alter its grouping key.
        // dotNetRDF's parenthesized-variable shortcut drops these VALUES rows.
        string referenceQuery = query.Replace("GROUP BY (?x)", "GROUP BY ?x", StringComparison.Ordinal);
        var reference = (VDS.RDF.Query.SparqlResultSet)new VDS.RDF.Query.LeviathanQueryProcessor(store)
            .ProcessQuery(new VDS.RDF.Parsing.SparqlQueryParser().ParseFromString(referenceQuery));
        await using var database = await OpenAsync(CreateDirectory());
        var actual = RdfSparql.Execute(database, query);
        string ReferenceKey(VDS.RDF.Query.ISparqlResult row) => String.Join('|', row.Variables.Where(name => row.HasValue(name) && row[name] is not null)
            .Order(StringComparer.Ordinal).Select(name => name + ":" + ((VDS.RDF.ILiteralNode)row[name]).Value));
        string ActualKey(IReadOnlyDictionary<string, RdfTerm> row) => String.Join('|', row.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => pair.Key + ":" + ((RdfLiteral)pair.Value).LexicalForm));
        CollectionAssert.AreEqual(reference.Select(ReferenceKey).ToArray(), actual.Select(ActualKey).ToArray(), query);
    }

    [TestMethod]
    [DataRow("SELECT (LCASE(?x) AS ?key) (COUNT(*) AS ?count) { VALUES ?x { 'A' 'a' } } GROUP BY LCASE(?x)")]
    [DataRow("SELECT (?x>1 AS ?key) (COUNT(*) AS ?count) { VALUES ?x { 1 2 } } GROUP BY (?x>1)")]
    [DataRow("SELECT ?x { VALUES ?x { 1 2 } } GROUP BY (?x+1 AS ?x)")]
    [DataRow("SELECT ?x { VALUES ?x { 1 2 } } GROUP BY (?x AS ?x)")]
    [DataRow("SELECT ?key { VALUES ?x { 1 2 } } GROUP BY (?x AS ?key) (?key AS ?key)")]
    [DataRow("ASK { VALUES ?x { 1 2 } } GROUP BY (?x AS ?x)")]
    [DataRow("CONSTRUCT { <urn:s> <urn:p> ?x } WHERE { VALUES ?x { 1 2 } } GROUP BY (?x AS ?x)")]
    [DataRow("SELECT ?key { VALUES ?x { 1 2 } } GROUP BY (SUM(?x) AS ?key)")]
    public async Task Sparql11_grouping_preserves_projection_and_assignment_restrictions(string query)
    {
        await using var database = await OpenAsync(CreateDirectory());
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => database.QuerySparqlAsync(query).AsTask());
    }

    [TestMethod]
    public async Task Sparql11_grouping_aliases_apply_after_where_and_across_graph_result_forms()
    {
        await using var database = await OpenAsync(CreateDirectory());
        Assert.IsTrue(RdfSparql.ExecuteAsk(database,
            "ASK { VALUES ?x { 11 12 21 } } GROUP BY (FLOOR(?x/10) AS ?key) HAVING (COUNT(*)>1)"));
        Assert.AreEqual(0, RdfSparql.Execute(database,
            "SELECT ?key { VALUES ?x { 1 2 } FILTER(BOUND(?key)) } GROUP BY (?x+1 AS ?key)").Count);
        var graph = RdfSparql.ExecuteConstruct(database,
            "CONSTRUCT { <urn:s> <urn:key> ?key } WHERE { VALUES ?x { 11 12 21 } } GROUP BY (FLOOR(?x/10) AS ?key)");
        Assert.AreEqual(2, graph.Count);
        Assert.IsTrue(RdfSparql.ExecuteAsk(database,
            "PREFIX xsd: <http://www.w3.org/2001/XMLSchema#> ASK { VALUES ?x { '1' '01' } } GROUP BY (xsd:int(?x) AS ?key) HAVING(COUNT(*)=2)"));
    }

    [TestMethod]
    public async Task Sparql_group_self_alias_with_no_prior_binding_retains_unbound_group()
    {
        await using var database = await OpenAsync(CreateDirectory());
        var result = (RdfSparqlSelectResult)await database.QuerySparqlAsync(
            "SELECT ?x (COUNT(*) AS ?count) { VALUES ?seed { 1 2 } } GROUP BY (?x AS ?x)");
        Assert.AreEqual(1, result.Bindings.Count);
        Assert.IsFalse(result.Bindings[0].ContainsKey("x"));
        Assert.AreEqual("2", ((RdfLiteral)result.Bindings[0]["count"]).LexicalForm);
    }
}
