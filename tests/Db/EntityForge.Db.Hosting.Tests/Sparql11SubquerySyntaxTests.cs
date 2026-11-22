using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("SELECT (SUM(?amount) AS ?x) { { SELECT (1+2 AS ?amount) {} } UNION { SELECT (1+2 AS ?amount) {} } }", "6")]
    [DataRow("SELECT (SUM(?amount) AS ?x) { { SELECT DISTINCT ?amount { { BIND(1+2 AS ?amount) } UNION { BIND(1+2 AS ?amount) } } } }", "3")]
    [DataRow("SELECT (SUM(?amount) AS ?x) { VALUES ?n { 100 } { SELECT (SUM(?n) AS ?amount) { VALUES ?n { 1 2 } } } UNION { SELECT (3 AS ?amount) {} } }", "6")]
    [DataRow("SELECT (COUNT(*) AS ?x) { { SELECT (1/0 AS ?amount) {} } UNION { SELECT (3 AS ?amount) {} } }", "2")]
    public async Task Sparql11_computed_union_subqueries_preserve_aggregate_scope(string query, string expected)
    {
        await using var database = await OpenAsync(CreateDirectory());
        var rows = RdfSparql.Execute(database, query);
        Assert.AreEqual(expected, ((RdfLiteral)rows.Single()["x"]).LexicalForm);
    }

    [TestMethod]
    [DataRow("STR(1)", 1)]
    [DataRow("IF(true,1,0)", 1)]
    [DataRow("COALESCE(?missing,1)", 1)]
    [DataRow("<http://www.w3.org/2001/XMLSchema#integer>('0')", 0)]
    [DataRow("<urn:unknown>()", 0)]
    [DataRow("<http://www.w3.org/2001/XMLSchema#unknown>()", 0)]
    [DataRow("<http://www.w3.org/2001/XMLSchema#integer>(DISTINCT '1')", 1)]
    public async Task Sparql11_filter_accepts_unparenthesized_function_calls(string call, int expected)
    {
        await using var database = await OpenAsync(CreateDirectory());
        Assert.AreEqual(expected, RdfSparql.Execute(database, "SELECT * { FILTER " + call + " }").Count);
    }

    [TestMethod]
    [DataRow("SELECT * { <urn:s> <urn:p> 1 <urn:s> <urn:q> 2 }")]
    [DataRow("SELECT * { () }")]
    [DataRow("SELECT * { <urn:\\u005Cu0078> ?p ?o }")]
    [DataRow("SELECT * { <urn:\\u0020> ?p ?o }")]
    [DataRow("SELECT * { <urn:\\U00110000> ?p ?o }")]
    public async Task Sparql11_invalid_separators_collections_and_iri_escapes_are_rejected(string query)
    {
        await using var database = await OpenAsync(CreateDirectory());
        Assert.ThrowsExactly<InvalidDataException>(() => RdfSparql.Execute(database, query));
    }

    [TestMethod]
    public async Task Sparql11_iri_unicode_escapes_preserve_term_identity()
    {
        await using var database = await OpenAsync(CreateDirectory());
        var rows = RdfSparql.Execute(database, "SELECT ?x { VALUES ?x { <urn:\\u0078> <urn:\\U00010000> } }");
        CollectionAssert.AreEqual(new RdfTerm[] { new RdfIri("urn:x"), new RdfIri("urn:𐀀") }, rows.Select(row => row["x"]).ToArray());
    }

    [TestMethod]
    [DataRow("SELECT * { SELECT ?x { VALUES ?x { 2 1 } } ORDER BY ?x LIMIT 1 }", "1")]
    [DataRow("SELECT ?x { { SELECT ?x { VALUES ?x { 1 3 2 } } ORDER BY DESC(?x) OFFSET 1 LIMIT 1 } }", "2")]
    [DataRow("SELECT ?x { { SELECT (COUNT(*) AS ?x) { VALUES ?g { 'a' 'a' 'b' } } GROUP BY ?g ORDER BY DESC(?x) LIMIT 1 } }", "2")]
    [DataRow("SELECT ?x { VALUES ?g { 'outside' } { SELECT (COUNT(*) AS ?x) { VALUES ?g { 'a' 'a' 'b' } } GROUP BY ?g ORDER BY DESC(?x) LIMIT 1 } }", "2")]
    [DataRow("SELECT ?x { { SELECT DISTINCT ?x { VALUES ?x { 3 3 2 1 } } ORDER BY DESC(?x) OFFSET 1 LIMIT 1 } }", "2")]
    [DataRow("SELECT ?x { { SELECT ?x { VALUES ?x { 1 2 } } LIMIT 0 } UNION { BIND(3 AS ?x) } }", "3")]
    [DataRow("SELECT ?x { { SELECT (1 AS ?x) {} } UNION { SELECT (2 AS ?x) {} } } ORDER BY ?x", "1,2")]
    [DataRow("SELECT ?x { VALUES ?y { 10 } { SELECT (1 AS ?x) {} } UNION { SELECT (2 AS ?x) {} } FILTER(?y=10) } ORDER BY ?x", "1,2")]
    [DataRow("SELECT ?x { { SELECT ?x { VALUES ?x { 1 1 } } } UNION { SELECT (2 AS ?x) {} } } ORDER BY ?x", "1,1,2")]
    public async Task Sparql11_subquery_group_forms_preserve_scope_and_bags(string query, string expected)
    {
        await using var database = await OpenAsync(CreateDirectory());
        var rows = RdfSparql.Execute(database, query);
        Assert.AreEqual(expected, String.Join(",", rows.Select(row => ((RdfLiteral)row["x"]).LexicalForm)));
    }

    [TestMethod]
    [DataRow("<urn:unknown>()")]
    [DataRow("<urn:unknown>(1,2)")]
    [DataRow("<urn:unknown>(DISTINCT 1)")]
    [DataRow("<http://www.w3.org/2001/XMLSchema#unknown>(1)")]
    [DataRow("<http://www.w3.org/2001/XMLSchema#date>('2026-11-13')")]
    [DataRow("<http://www.w3.org/2001/XMLSchema#duration>('P1D')")]
    [DataRow("<http://www.w3.org/2001/XMLSchema#unknown>()")]
    [DataRow("<http://www.w3.org/2001/XMLSchema#unknown>(1,2)")]
    public async Task Sparql11_unknown_extension_calls_are_expression_errors(string call)
    {
        await using var database = await OpenAsync(CreateDirectory());
        var row = RdfSparql.Execute(database, $"SELECT ({call} AS ?missing) (COALESCE({call},42) AS ?fallback) {{}}").Single();
        Assert.IsFalse(row.ContainsKey("missing"));
        Assert.AreEqual(new RdfLiteral("42", "http://www.w3.org/2001/XMLSchema#integer"), row["fallback"]);
        Assert.AreEqual(0, RdfSparql.Execute(database, $"SELECT * {{ FILTER({call}) }}").Count);
    }
}
