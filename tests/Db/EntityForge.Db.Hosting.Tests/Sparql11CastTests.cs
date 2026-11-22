using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("1e0 / -(0e0)", "-INF", "double")]
    [DataRow("1e0 / +('-0'^^xsd:double)", "-INF", "double")]
    [DataRow("1e0 / -('-0'^^xsd:double)", "INF", "double")]
    [DataRow("xsd:float(1) / -(xsd:float(0))", "-INF", "float")]
    [DataRow("xsd:float(1) / +('-0'^^xsd:float)", "-INF", "float")]
    [DataRow("-('INF'^^xsd:double)", "-INF", "double")]
    [DataRow("-('NaN'^^xsd:double)", "NaN", "double")]
    [DataRow("-(123456789012345678901234567890)", "-123456789012345678901234567890", "integer")]
    [DataRow("-(0.12345678901234567890123456789)", "-0.12345678901234567890123456789", "decimal")]
    [DataRow("COALESCE(+('bad'^^xsd:double),42)", "42", "integer")]
    [DataRow("COALESCE(+('text'),42)", "42", "integer")]
    [DataRow("COALESCE(-(?missing),42)", "42", "integer")]
    [DataRow("-SUM(?n)", "-3", "integer")]
    public async Task Sparql11_unary_numeric_signs_preserve_signed_zero_and_exact_values(string expression, string lexical, string datatype)
    {
        await using var database = await OpenAsync(CreateDirectory());
        var row = RdfSparql.Execute(database, "PREFIX xsd: <http://www.w3.org/2001/XMLSchema#> SELECT (" + expression + " AS ?answer) { VALUES ?n { 1 2 } }").First();
        Assert.AreEqual(new RdfLiteral(lexical, "http://www.w3.org/2001/XMLSchema#" + datatype), row["answer"]);
    }

    [TestMethod]
    public async Task Sparql11_iri_constructors_and_casts_work_in_bind_and_filters()
    {
        await using var database = await OpenAsync(CreateDirectory());
        var row = RdfSparql.Execute(database, """
            PREFIX xsd: <http://www.w3.org/2001/XMLSchema#>
            SELECT ?iri ?alias ?number ?bad ?relative {
                VALUES ?text { '12' }
                BIND(IRI(CONCAT('urn:',?text)) AS ?iri)
                BIND(URI(?iri) AS ?alias)
                BIND(<http://www.w3.org/2001/XMLSchema#integer>(?text) AS ?number)
                BIND(IRI('urn:word'@en) AS ?bad)
                BIND(IRI('relative') AS ?relative)
                FILTER(xsd:integer(?text)=12)
            }
            """).Single();
        Assert.AreEqual(new RdfIri("urn:12"), row["iri"]);
        Assert.AreEqual(row["iri"], row["alias"]);
        Assert.AreEqual(new RdfLiteral("12", "http://www.w3.org/2001/XMLSchema#integer"), row["number"]);
        Assert.IsFalse(row.ContainsKey("bad"));
        Assert.IsFalse(row.ContainsKey("relative"));
    }

    [TestMethod]
    [DataRow("integer('  +00042  ')", "42", "integer")]
    [DataRow("integer(DISTINCT '42')", "42", "integer")]
    [DataRow("string(DISTINCT 42)", "42", "string")]
    [DataRow("integer(-12.9)", "-12", "integer")]
    [DataRow("integer(1.5e2)", "150", "integer")]
    [DataRow("integer(true)", "1", "integer")]
    [DataRow("decimal('123456789012345678901234567890.123456789')", "123456789012345678901234567890.123456789", "decimal")]
    [DataRow("decimal(1.25e0)", "1.25", "decimal")]
    [DataRow("decimal(false)", "0", "decimal")]
    [DataRow("float('INF')", "INF", "float")]
    [DataRow("float('16777217')", "1.6777216E7", "float")]
    [DataRow("double('1.25e2')", "125", "double")]
    [DataRow("double(true)", "1", "double")]
    [DataRow("boolean('0')", "false", "boolean")]
    [DataRow("boolean(-0.0)", "false", "boolean")]
    [DataRow("boolean(-0.1)", "true", "boolean")]
    [DataRow("boolean(xsd:double('NaN'))", "false", "boolean")]
    [DataRow("boolean(xsd:double('INF'))", "true", "boolean")]
    [DataRow("string(<urn:example>)", "urn:example", "string")]
    [DataRow("string(true)", "true", "string")]
    [DataRow("string('+001.2300'^^xsd:decimal)", "1.23", "string")]
    [DataRow("string(1.0e6)", "1.0E6", "string")]
    [DataRow("string(1.0e-6)", "0.000001", "string")]
    [DataRow("string(1.0e-7)", "1.0E-7", "string")]
    [DataRow("string(xsd:double('-0'))", "-0", "string")]
    [DataRow("string(xsd:float('1.4'))", "1.4", "string")]
    [DataRow("decimal(xsd:double('4.9406564584124654E-324'))", null, "decimal")]
    [DataRow("string('  unchanged  ')", "  unchanged  ", "string")]
    [DataRow("dateTime('2024-12-31T24:00:00+00:00')", "2025-01-01T00:00:00Z", "dateTime")]
    [DataRow("string('2024-02-29T01:02:03.4000-05:30'^^xsd:dateTime)", "2024-02-29T01:02:03.4-05:30", "string")]
    public async Task Sparql11_constructors_preserve_values_and_numeric_precision(string expression, string lexical, string datatype)
    {
        await using var database = await OpenAsync(CreateDirectory());
        var row = RdfSparql.Execute(database, "PREFIX xsd: <http://www.w3.org/2001/XMLSchema#> SELECT (xsd:" + expression + " AS ?x) {}").Single();
        if (lexical is null)
        {
            var value = (RdfLiteral)row["x"];
            Assert.AreEqual("http://www.w3.org/2001/XMLSchema#decimal", value.Datatype);
            Assert.IsTrue(value.LexicalForm.StartsWith("0." + new string('0', 323) + "4", StringComparison.Ordinal));
            Assert.AreEqual(1076, value.LexicalForm.Length);
        }
        else
        {
            Assert.AreEqual(new RdfLiteral(lexical, "http://www.w3.org/2001/XMLSchema#" + datatype), row["x"]);
        }
    }

    [TestMethod]
    [DataRow("integer('1.1')")]
    [DataRow("integer()")]
    [DataRow("integer(1,2)")]
    [DataRow("string(DISTINCT 1,2)")]
    [DataRow("integer('1e2')")]
    [DataRow("decimal('1e2')")]
    [DataRow("integer(xsd:double('INF'))")]
    [DataRow("decimal(xsd:double('NaN'))")]
    [DataRow("boolean('TRUE')")]
    [DataRow("boolean('yes')")]
    [DataRow("integer('1'@en)")]
    [DataRow("string('word'@en)")]
    [DataRow("string('bad'^^xsd:integer)")]
    [DataRow("dateTime(123)")]
    [DataRow("dateTime('2023-02-29T00:00:00')")]
    [DataRow("double(<urn:example>)")]
    public async Task Sparql11_invalid_casts_are_expression_errors(string expression)
    {
        await using var database = await OpenAsync(CreateDirectory());
        var row = RdfSparql.Execute(database, "PREFIX xsd: <http://www.w3.org/2001/XMLSchema#> SELECT (xsd:" + expression + " AS ?x) {}").Single();
        Assert.IsFalse(row.ContainsKey("x"));
    }
}
