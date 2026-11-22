using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    public async Task Sparql11_dollar_variables_and_signed_integer_terms_preserve_identity()
    {
        await using var database = await OpenAsync(CreateDirectory());
        await database.CommitAsync(new(Guid.NewGuid(), new RdfMutation[]
        {
            new AssertRdfQuad(new(new RdfIri("urn:s"), new RdfIri("urn:p"), new RdfLiteral("+5", "http://www.w3.org/2001/XMLSchema#integer")))
        }));
        const string query = "SELECT * { $s ?p +5 . ?s $p $value }";
        var rows = RdfSparql.Execute(database, query);
        Assert.AreEqual(1, rows.Count);
        Assert.AreEqual("+5", ((RdfLiteral)rows[0]["value"]).LexicalForm);
        CollectionAssert.AreEquivalent(new[] { "s", "p", "value" }, RdfSparql.ResultVariables(query).ToArray());
        Assert.AreEqual(0, RdfSparql.Execute(database, "SELECT * { ?s ?p 5 }").Count);
        var literal = RdfSparql.Execute(database, "SELECT ('$value' AS $text) (<urn:$value> AS $iri) {}").Single();
        Assert.AreEqual(new RdfLiteral("$value"), literal["text"]);
        Assert.AreEqual(new RdfIri("urn:$value"), literal["iri"]);
    }

    [TestMethod]
    [DataRow("2024-01-01T01:00:00+01:00", "=", "2024-01-01T00:00:00Z", "true")]
    [DataRow("-0001-12-31T23:00:00-01:00", "=", "0001-01-01T00:00:00Z", "true")]
    [DataRow("-0001-02-29T23:00:00-01:00", "=", "-0001-03-01T00:00:00Z", "true")]
    [DataRow("2024-01-01T00:00:00.1234567891Z", "<", "2024-01-01T00:00:00.1234567892Z", "true")]
    [DataRow("2024-01-01T00:00:00", "<", "2024-01-02T00:00:00Z", "true")]
    [DataRow("2024-01-01T00:00:00", "=", "2024-01-01T00:00:00Z", "true")]
    [DataRow("2024-01-01T00:00:00", "<", "2024-01-01T14:00:00Z", "true")]
    [DataRow("2024-01-01T01:00:00+01:00", "=", "2024-01-01T00:00:00", "true")]
    [DataRow("2024-01-01T00:00:00", "!=", "2024-01-01T00:00:00Z", "false")]
    [DataRow("2024-01-01T00:00:00", "<=", "2024-01-01T00:00:00Z", "true")]
    [DataRow("2024-01-01T00:00:00Z", ">=", "2024-01-01T00:00:00", "true")]
    [DataRow("2024-01-01T00:00:00.1234567892", ">", "2024-01-01T00:00:00.1234567891Z", "true")]
    [DataRow("2024-01-01T00:00:00", "=", "2024-01-01T00:00:00", "true")]
    [DataRow("123456-12-31T24:00:00Z", "=", "123457-01-01T00:00:00Z", "true")]
    [DataRow("2023-02-29T00:00:00Z", "<", "2024-01-01T00:00:00Z", "error")]
    public async Task Sparql11_date_comparison_preserves_precision_with_implicit_UTC(string left, string op, string right, string expected)
    {
        await using var database = await OpenAsync(CreateDirectory());
        var row = RdfSparql.Execute(database, $"PREFIX xsd: <http://www.w3.org/2001/XMLSchema#> SELECT ('{left}'^^xsd:dateTime {op} '{right}'^^xsd:dateTime AS ?value) {{}}").Single();
        Assert.AreEqual(expected, row.TryGetValue("value", out var value) ? ((RdfLiteral)value).LexicalForm : "error");
    }

    [TestMethod]
    public async Task Sparql11_date_order_and_extrema_use_instants_instead_of_lexical_offsets()
    {
        await using var database = await OpenAsync(CreateDirectory());
        const string prolog = "PREFIX xsd: <http://www.w3.org/2001/XMLSchema#> ";
        const string body = "{ VALUES ?v { '2024-01-01T00:00:00-02:00'^^xsd:dateTime '2024-01-01T01:00:00Z'^^xsd:dateTime } }";
        var ordered = RdfSparql.Execute(database, prolog + "SELECT ?v " + body + " ORDER BY ?v");
        Assert.AreEqual("2024-01-01T01:00:00Z", ((RdfLiteral)ordered[0]["v"]).LexicalForm);
        var extrema = RdfSparql.Execute(database, prolog + "SELECT (MIN(?v) AS ?min) (MAX(?v) AS ?max) " + body).Single();
        Assert.AreEqual(ordered[0]["v"], extrema["min"]);
        Assert.AreEqual(ordered[1]["v"], extrema["max"]);
    }

    [TestMethod]
    public async Task Sparql_date_implicit_timezone_is_consistent_across_filter_order_and_extrema()
    {
        await using var database = await OpenAsync(CreateDirectory());
        const string prolog = "PREFIX xsd: <http://www.w3.org/2001/XMLSchema#> ";
        const string body = "{ VALUES ?v { '2024-01-01T00:00:00-02:00'^^xsd:dateTime '2024-01-01T01:00:00'^^xsd:dateTime } }";
        var ordered = RdfSparql.Execute(database, prolog + "SELECT ?v " + body + " ORDER BY ?v");
        Assert.AreEqual("2024-01-01T01:00:00", ((RdfLiteral)ordered[0]["v"]).LexicalForm);
        var extrema = RdfSparql.Execute(database, prolog + "SELECT (MIN(?v) AS ?min) (MAX(?v) AS ?max) " + body).Single();
        Assert.AreEqual(ordered[0]["v"], extrema["min"]);
        Assert.AreEqual(ordered[1]["v"], extrema["max"]);
        var filtered = RdfSparql.Execute(database, prolog + "SELECT ?v { VALUES ?v { '2024-01-01T01:00:00'^^xsd:dateTime } FILTER(?v = '2024-01-01T01:00:00Z'^^xsd:dateTime) }").Single();
        Assert.AreEqual(ordered[0]["v"], filtered["v"]);
        var lexical = RdfSparql.Execute(database, prolog + "SELECT (TZ(?v) AS ?tz) (TIMEZONE(?v) AS ?zone) (sameTerm(?v, '2024-01-01T01:00:00Z'^^xsd:dateTime) AS ?same) { VALUES ?v { '2024-01-01T01:00:00'^^xsd:dateTime } }").Single();
        Assert.AreEqual("", ((RdfLiteral)lexical["tz"]).LexicalForm);
        Assert.IsFalse(lexical.ContainsKey("zone"));
        Assert.AreEqual("false", ((RdfLiteral)lexical["same"]).LexicalForm);
    }

    [TestMethod]
    [DataRow("q")]
    [DataRow("iq")]
    [DataRow("qx")]
    public async Task Sparql11_regex_rejects_XPath3_quote_flag(string flags)
    {
        await using var database = await OpenAsync(CreateDirectory());
        var row = RdfSparql.Execute(database, $"SELECT (REGEX('a?+*.{{}}()[] c', 'a?+*.{{}}()[] c', '{flags}') AS ?answer) {{}}").Single();
        Assert.IsFalse(row.ContainsKey("answer"));
    }
}
