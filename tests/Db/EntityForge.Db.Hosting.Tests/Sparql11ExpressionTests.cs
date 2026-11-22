using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("SELECT (1+2*3 AS ?x) {}", "7", "integer")]
    [DataRow("SELECT ((1+2)*3 AS ?x) {}", "9", "integer")]
    [DataRow("SELECT (10-3-2 AS ?x) {}", "5", "integer")]
    [DataRow("SELECT (7/2 AS ?x) {}", "3.5", "decimal")]
    [DataRow("SELECT (-2 * -3 AS ?x) {}", "6", "integer")]
    [DataRow("SELECT (1e2+0.5 AS ?x) {}", "100.5", "double")]
    [DataRow("SELECT (999999999999999999999999999999+1 AS ?x) {}", "1000000000000000000000000000000", "integer")]
    [DataRow("SELECT (STR('{ WHERE }') AS ?x) {}", "{ WHERE }", "string")]
    [DataRow("SELECT (SUM(?n) AS ?x) { VALUES ?n {1 2 3} }", "6", "integer")]
    [DataRow("SELECT (COUNT(?n) AS ?x) { VALUES ?n {1 UNDEF 2} }", "2", "integer")]
    [DataRow("SELECT (COUNT(*) AS ?x) { VALUES ?n {1 UNDEF 2} }", "3", "integer")]
    [DataRow("SELECT (COUNT(DISTINCT ?n) AS ?x) { VALUES ?n {1 UNDEF 1 UNDEF} }", "1", "integer")]
    [DataRow("SELECT (SUM(COALESCE(?n,0)) AS ?x) { VALUES ?n {1 UNDEF 2} }", "3", "integer")]
    [DataRow("SELECT (COUNT(?n) AS ?x) { VALUES ?n {UNDEF UNDEF} }", "0", "integer")]
    [DataRow("SELECT (SUM(?n*2) AS ?x) { VALUES ?n {1 2 3} }", "12", "integer")]
    [DataRow("SELECT (SUM(?n)/COUNT(*) AS ?x) { VALUES ?n {1 2 3} }", "2", "decimal")]
    [DataRow("SELECT (MIN(?n) AS ?x) { VALUES ?n {'b' 'a'} }", "a", "string")]
    [DataRow("SELECT (MAX(?n) AS ?x) { VALUES ?n {'b' 'a'} }", "b", "string")]
    [DataRow("SELECT (SAMPLE(?n) AS ?x) { VALUES ?n {'a'} }", "a", "string")]
    [DataRow("SELECT (GROUP_CONCAT(DISTINCT ?n; SEPARATOR=',') AS ?x) { VALUES ?n {'a' 'a'} }", "a", "string")]
    [DataRow("SELECT (SUM(?n) AS ?x) { ?s <urn:absent> ?n }", "0", "integer")]
    [DataRow("SELECT (AVG(?n) AS ?x) { ?s <urn:absent> ?n }", "0", "integer")]
    [DataRow("SELECT (GROUP_CONCAT(?n) AS ?x) { ?s <urn:absent> ?n }", "", "string")]
    [DataRow("SELECT (IF(true, 3, 1/0) AS ?x) {}", "3", "integer")]
    [DataRow("SELECT (IF(1 > 2, 1/0, 4) AS ?x) {}", "4", "integer")]
    [DataRow("SELECT (COALESCE(?missing, 1/0, 5) AS ?x) {}", "5", "integer")]
    [DataRow("SELECT (CONCAT('a', STR(1+2)) AS ?x) {}", "a3", "string")]
    [DataRow("SELECT (STRBEFORE('hello-world', '-') AS ?x) {}", "hello", "string")]
    [DataRow("SELECT (STRAFTER('hello-world', '-') AS ?x) {}", "world", "string")]
    [DataRow("SELECT (SUBSTR('a😀bc', 2, 2) AS ?x) {}", "😀b", "string")]
    [DataRow("SELECT (SUBSTR('abc', -9007199254740993, 9007199254740995) AS ?x) {}", "a", "string")]
    [DataRow("SELECT (SUBSTR('abc', -999999999999999999999999999999, 1000000000000000000000000000002) AS ?x) {}", "ab", "string")]
    [DataRow("SELECT (SUBSTR('abc', -999999999999999999999999999999) AS ?x) {}", "abc", "string")]
    [DataRow("SELECT (SUBSTR('abc', 999999999999999999999999999999) AS ?x) {}", "", "string")]
    [DataRow("SELECT (SUBSTR('abc', 0, 2) AS ?x) {}", "a", "string")]
    [DataRow("SELECT (SUBSTR('abc', 2, -1) AS ?x) {}", "", "string")]
    [DataRow("SELECT (SUBSTR('a😀bc', '2'^^<http://www.w3.org/2001/XMLSchema#positiveInteger>, '1'^^<http://www.w3.org/2001/XMLSchema#unsignedByte>) AS ?x) {}", "😀", "string")]
    [DataRow("SELECT (BOUND(?missing) AS ?x) {}", "false", "boolean")]
    [DataRow("SELECT (SUM(IF(?n > 1, ?n, 0)) AS ?x) { VALUES ?n {1 2 3} }", "5", "integer")]
    [DataRow("SELECT (STRDT('12', <http://www.w3.org/2001/XMLSchema#integer>) AS ?x) {}", "12", "integer")]
    [DataRow("SELECT (IF((1+2)*3 = 9, 1, 0) AS ?x) {}", "1", "integer")]
    [DataRow("SELECT (1=1 AS ?x) {}", "true", "boolean")]
    [DataRow("SELECT (!(1=2) && true AS ?x) {}", "true", "boolean")]
    [DataRow("SELECT (IF(false, 1/0, 1 < 2) AS ?x) {}", "true", "boolean")]
    [DataRow("SELECT (COALESCE(1 < 'text', false) AS ?x) {}", "false", "boolean")]
    [DataRow("SELECT ('1'^^<http://www.w3.org/2001/XMLSchema#boolean> = true AS ?x) {}", "true", "boolean")]
    [DataRow("SELECT ('NaN'^^<http://www.w3.org/2001/XMLSchema#double> = 'NaN'^^<http://www.w3.org/2001/XMLSchema#double> AS ?x) {}", "false", "boolean")]
    [DataRow("SELECT ('NaN'^^<http://www.w3.org/2001/XMLSchema#double> < 1 AS ?x) {}", "false", "boolean")]
    [DataRow("SELECT ('INF'^^<http://www.w3.org/2001/XMLSchema#double> > 1 AS ?x) {}", "true", "boolean")]
    [DataRow("SELECT (1 IN () AS ?x) {}", "false", "boolean")]
    [DataRow("SELECT (1 NOT IN () AS ?x) {}", "true", "boolean")]
    [DataRow("SELECT (1 IN (1/0, 1) AS ?x) {}", "true", "boolean")]
    [DataRow("SELECT ?x { BIND(1 < 2 AS ?x) FILTER(?x) }", "true", "boolean")]
    [DataRow("SELECT (STR(1 < 2) AS ?x) {}", "true", "string")]
    [DataRow("SELECT (COUNT(?n > 1) AS ?x) { VALUES ?n { 1 2 3 } }", "3", "integer")]
    public async Task Sparql11_expressions_and_aggregates_have_expected_values(string query, string expected, string datatype)
    {
        await using var database = await OpenAsync(CreateDirectory());
        var row = RdfSparql.Execute(database, query).Single();
        Assert.AreEqual(new RdfLiteral(expected, "http://www.w3.org/2001/XMLSchema#" + datatype), row["x"]);
        Assert.AreEqual(1, row.Count);
    }

    [TestMethod]
    [DataRow("SELECT (1/0 AS ?x) {}")]
    [DataRow("SELECT (SUBSTR('abc', 1.5) AS ?x) {}")]
    [DataRow("SELECT (SUBSTR('abc', 1, 2e0) AS ?x) {}")]
    [DataRow("SELECT (SUBSTR('abc', '0'^^<http://www.w3.org/2001/XMLSchema#positiveInteger>) AS ?x) {}")]
    [DataRow("SELECT (SUBSTR('abc', 1, '256'^^<http://www.w3.org/2001/XMLSchema#unsignedByte>) AS ?x) {}")]
    [DataRow("SELECT (SUM(?n) AS ?x) { VALUES ?n { 1 'bad' } }")]
    [DataRow("SELECT (MIN(?n) AS ?x) { ?s <urn:absent> ?n }")]
    [DataRow("SELECT (MAX(?n) AS ?x) { ?s <urn:absent> ?n }")]
    [DataRow("SELECT (SAMPLE(?n) AS ?x) { ?s <urn:absent> ?n }")]
    [DataRow("SELECT (IF(?missing, 1, 2) AS ?x) {}")]
    [DataRow("SELECT (COALESCE(?missing, 1/0) AS ?x) {}")]
    [DataRow("SELECT (1 IN (1/0, 2) AS ?x) {}")]
    [DataRow("SELECT (1 < 'text' AS ?x) {}")]
    [DataRow("SELECT ('bad'^^<urn:unknown> = 1 AS ?x) {}")]
    public async Task Sparql11_expression_errors_leave_projection_unbound(string query)
    {
        await using var database = await OpenAsync(CreateDirectory());
        Assert.AreEqual(0, RdfSparql.Execute(database, query).Single().Count);
    }

    [TestMethod]
    public async Task Sparql11_standard_having_and_group_projection_are_supported()
    {
        await using var database = await OpenAsync(CreateDirectory());
        var rows = RdfSparql.Execute(database,
            "SELECT ?g (SUM(?v)+1 AS ?total) { VALUES (?g ?v) { ('a' 1) ('a' 2) ('b' 1) } } GROUP BY ?g HAVING(COUNT(*) > 1) ORDER BY SUM(?v)");
        Assert.AreEqual(new RdfLiteral("a"), rows.Single()["g"]);
        Assert.AreEqual(new RdfLiteral("4", "http://www.w3.org/2001/XMLSchema#integer"), rows.Single()["total"]);
        Assert.AreEqual(2, rows.Single().Count);
        rows = RdfSparql.Execute(database,
            "SELECT ?b ?a { VALUES (?a ?b) { (1 2) (1 2) (2 3) } } GROUP BY ?a ?b ORDER BY ?a");
        Assert.AreEqual(2, rows.Count);
        Assert.AreEqual(1, RdfSparql.Execute(database, "SELECT REDUCED ?x { VALUES ?x { 1 1 } }").Count);
    }

    [TestMethod]
    public async Task Sparql11_projection_expressions_run_after_filter_and_before_order()
    {
        await using var database = await OpenAsync(CreateDirectory());
        var rows = RdfSparql.Execute(database,
            "SELECT (?n*2 AS ?x) { VALUES ?n { 3 1 2 } FILTER(?n+1 > 2) } ORDER BY DESC(?x) LIMIT 1");
        Assert.AreEqual(new RdfLiteral("6", "http://www.w3.org/2001/XMLSchema#integer"), rows.Single()["x"]);
        Assert.AreEqual(0, RdfSparql.Execute(database, "SELECT (1 AS ?x) { FILTER(BOUND(?x)) }").Count);
    }

    [TestMethod]
    [DataRow("SELECT (SUM(*) AS ?x) {}")]
    [DataRow("SELECT (SUM(COUNT(*)) AS ?x) {}")]
    [DataRow("SELECT * { BIND(COUNT(*) AS ?x) }")]
    [DataRow("SELECT * { FILTER(COUNT(*) > 1) }")]
    [DataRow("SELECT (?n+1 AS ?x) (COUNT(*) AS ?c) { VALUES ?n {1} }")]
    public async Task Sparql11_invalid_aggregate_placement_is_rejected(string query)
    {
        await using var database = await OpenAsync(CreateDirectory());
        Assert.ThrowsExactly<InvalidDataException>(() => RdfSparql.Execute(database, query));
    }
}
