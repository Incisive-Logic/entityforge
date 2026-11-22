using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("1.00000005960464477539062500000000000000000000000000000001", "1.00000011920928955078125", "decimal")]
    [DataRow("-1.00000005960464477539062500000000000000000000000000000001", "-1.00000011920928955078125", "decimal")]
    [DataRow("1.000000059604644775390625", "1", "decimal")]
    [DataRow("1208925891672223212634113", "1208925963729817250562048", "integer")]
    public async Task Sparql_exact_numeric_promotion_to_float_rounds_once(string lexical, string expected, string datatype)
    {
        await using var database = await OpenAsync(CreateDirectory());
        const string xsd = "http://www.w3.org/2001/XMLSchema#";
        string input = RdfSyntax.FormatTerm(new RdfLiteral(lexical, xsd + datatype));
        string target = RdfSyntax.FormatTerm(new RdfLiteral(expected, xsd + "float"));
        var row = RdfSparql.Execute(database, "PREFIX xsd:<" + xsd + "> SELECT " +
            "(xsd:float(?v)=" + target + " AS ?cast) " +
            "((?v+xsd:float('0'))=" + target + " AS ?sum) " +
            "((?v*xsd:float('1'))=" + target + " AS ?product) " +
            "(?v=" + target + " AS ?comparison) WHERE { VALUES ?v { " + input + " } }").Single();
        foreach (string column in new[] { "cast", "sum", "product", "comparison" })
        {
            Assert.AreEqual(new RdfLiteral("true", xsd + "boolean"), row[column], column);
        }
    }

    [TestMethod]
    [DataRow("16777217", "16777216", "true")]
    [DataRow("-16777217", "-16777216", "true")]
    [DataRow("1e-50", "0", "false")]
    [DataRow("-1e-50", "0", "false")]
    [DataRow("1e40", "INF", "true")]
    [DataRow("-1e40", "-INF", "true")]
    [DataRow("1.00000005960464477539062500000000000000000000000000000001", "1.0000001192092896", "true")]
    public async Task Sparql_float_value_is_rounded_before_promotion_to_double(string lexical, string expected, string truth)
    {
        await using var database = await OpenAsync(CreateDirectory());
        const string xsd = "http://www.w3.org/2001/XMLSchema#";
        string literal = RdfSyntax.FormatTerm(new RdfLiteral(lexical, xsd + "float"));
        string promoted = RdfSyntax.FormatTerm(new RdfLiteral(expected, xsd + "double"));
        var row = RdfSparql.Execute(database, "PREFIX xsd: <" + xsd + "> " +
            "SELECT (?v=" + promoted + " AS ?equal) ((?v+0e0)=" + promoted + " AS ?sum) " +
            "(IF(?v,true,false) AS ?truth) (xsd:double(xsd:float(STR(?v)))=" + promoted + " AS ?cast) " +
            "(STR(?v) AS ?text) WHERE { VALUES ?v { " + literal + " } }").Single();
        Assert.AreEqual(new RdfLiteral("true", xsd + "boolean"), row["equal"]);
        Assert.AreEqual(new RdfLiteral("true", xsd + "boolean"), row["sum"]);
        Assert.AreEqual(new RdfLiteral(truth, xsd + "boolean"), row["truth"]);
        Assert.AreEqual(new RdfLiteral("true", xsd + "boolean"), row["cast"]);
        Assert.AreEqual(lexical, ((RdfLiteral)row["text"]).LexicalForm);
    }

    [TestMethod]
    [DataRow(" true ", "true")]
    [DataRow("\t1\r\n", "true")]
    [DataRow(" false ", "false")]
    [DataRow("\t0\r\n", "false")]
    [DataRow("\u00A0true\u00A0", null)]
    [DataRow("\u2003false", null)]
    [DataRow("tr ue", null)]
    [DataRow("TRUE", null)]
    public async Task Sparql_boolean_values_collapse_XML_whitespace_without_changing_terms(string lexical, string? expected)
    {
        await using var database = await OpenAsync(CreateDirectory());
        const string xsd = "http://www.w3.org/2001/XMLSchema#";
        string literal = RdfSyntax.FormatTerm(new RdfLiteral(lexical, xsd + "boolean"));
        var row = RdfSparql.Execute(database, "PREFIX xsd: <" + xsd + "> " +
            "SELECT (IF(?v,true,false) AS ?truth) (?v=true AS ?equal) (xsd:boolean(?v) AS ?cast) (STR(?v) AS ?text) " +
            "WHERE { VALUES ?v { " + literal + " } }").Single();
        Assert.AreEqual(lexical, ((RdfLiteral)row["text"]).LexicalForm);
        Assert.AreEqual(new RdfLiteral(expected ?? "false", xsd + "boolean"), row["truth"]);
        foreach (string name in new[] { "equal", "cast" })
        {
            Assert.AreEqual(expected is not null, row.ContainsKey(name), name);
            if (expected is not null)
            {
                Assert.AreEqual(new RdfLiteral(expected, xsd + "boolean"), row[name], name);
            }
        }
    }

    [TestMethod]
    [DataRow("integer", "abc")]
    [DataRow("decimal", "1e2")]
    [DataRow("float", "\u00A01")]
    [DataRow("double", "invalid")]
    [DataRow("boolean", "yes")]
    [DataRow("byte", "999")]
    public async Task Sparql_ill_typed_numeric_and_boolean_EBV_is_false_but_casts_still_fail(string datatype, string lexical)
    {
        await using var database = await OpenAsync(CreateDirectory());
        const string xsd = "http://www.w3.org/2001/XMLSchema#";
        string literal = RdfSyntax.FormatTerm(new RdfLiteral(lexical, xsd + datatype));
        var row = RdfSparql.Execute(database, "PREFIX xsd: <" + xsd + "> " +
            "SELECT (IF(?v,1/0,7) AS ?selected) (!?v AS ?negated) (xsd:boolean(?v) AS ?cast) (?v=false AS ?equal) " +
            "WHERE { VALUES ?v { " + literal + " } }").Single();
        Assert.AreEqual(new RdfLiteral("7", xsd + "integer"), row["selected"]);
        Assert.AreEqual(new RdfLiteral("true", xsd + "boolean"), row["negated"]);
        Assert.IsFalse(row.ContainsKey("cast"));
        Assert.IsFalse(row.ContainsKey("equal"));
    }

    [TestMethod]
    [DataRow("float", " ", true)]
    [DataRow("double", "\t\r\n", true)]
    [DataRow("float", "\u00A0", false)]
    [DataRow("double", "\u00A0", false)]
    [DataRow("float", "\u2003", false)]
    [DataRow("double", "\u2003", false)]
    [DataRow("float", "\u0085", false)]
    [DataRow("double", "\u2028", false)]
    public async Task Sparql_floating_lexical_forms_accept_only_XML_whitespace(string datatype, string whitespace, bool valid)
    {
        await using var database = await OpenAsync(CreateDirectory());
        const string xsd = "http://www.w3.org/2001/XMLSchema#";
        string literal = RdfSyntax.FormatTerm(new RdfLiteral(whitespace + "1.5" + whitespace, xsd + datatype));
        var row = RdfSparql.Execute(database, "PREFIX xsd: <" + xsd + "> " +
            "SELECT (?v+1 AS ?sum) (isNumeric(?v) AS ?numeric) (xsd:double(STR(?v)) AS ?cast) " +
            "WHERE { VALUES ?v { " + literal + " } }").Single();
        Assert.AreEqual(valid, row.ContainsKey("sum"));
        Assert.AreEqual(valid, row.ContainsKey("cast"));
        Assert.AreEqual(new RdfLiteral(valid ? "true" : "false", xsd + "boolean"), row["numeric"]);
        if (valid)
        {
            Assert.AreEqual("2.5", ((RdfLiteral)row["sum"]).LexicalForm);
        }
    }
}
