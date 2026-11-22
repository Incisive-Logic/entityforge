using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("xsd:byte('-128')", "-128", "byte")]
    [DataRow("xsd:short('32767')", "32767", "short")]
    [DataRow("xsd:int('2147483647')", "2147483647", "int")]
    [DataRow("xsd:long('-9223372036854775808')", "-9223372036854775808", "long")]
    [DataRow("xsd:unsignedByte('255')", "255", "unsignedByte")]
    [DataRow("xsd:unsignedShort('65535')", "65535", "unsignedShort")]
    [DataRow("xsd:unsignedInt('4294967295')", "4294967295", "unsignedInt")]
    [DataRow("xsd:unsignedLong('18446744073709551615')", "18446744073709551615", "unsignedLong")]
    [DataRow("xsd:positiveInteger('99999999999999999999999999999999999')", "99999999999999999999999999999999999", "positiveInteger")]
    [DataRow("xsd:negativeInteger(-2.9)", "-2", "negativeInteger")]
    [DataRow("xsd:nonNegativeInteger(false)", "0", "nonNegativeInteger")]
    [DataRow("xsd:nonPositiveInteger('-0')", "0", "nonPositiveInteger")]
    [DataRow("'127'^^xsd:byte + 1", "128", "integer")]
    [DataRow("' +0042 '^^xsd:int + 1", "43", "integer")]
    [DataRow("ABS('-128'^^xsd:byte)", "128", "integer")]
    [DataRow("'42'^^xsd:int = 42", "true", "boolean")]
    [DataRow("isNumeric('42'^^xsd:unsignedByte)", "true", "boolean")]
    [DataRow("isNumeric('256'^^xsd:unsignedByte)", "false", "boolean")]
    [DataRow("xsd:byte(128)", null, null)]
    [DataRow("xsd:int('2147483648')", null, null)]
    [DataRow("xsd:unsignedLong('18446744073709551616')", null, null)]
    [DataRow("xsd:positiveInteger(0)", null, null)]
    [DataRow("xsd:unsignedByte(-1)", null, null)]
    [DataRow("xsd:int('1.2')", null, null)]
    [DataRow("'256'^^xsd:unsignedByte + 1e0", null, null)]
    [DataRow("'1.2'^^xsd:int + 1", null, null)]
    public async Task Sparql11_integer_subtypes_validate_ranges_and_promote_arithmetic(string expression, string? lexical, string? datatype)
    {
        await using var database = await OpenAsync(CreateDirectory());
        var row = RdfSparql.Execute(database,
            "PREFIX xsd: <http://www.w3.org/2001/XMLSchema#> SELECT (" + expression + " AS ?value) {}").Single();
        if (lexical is null)
        {
            Assert.IsFalse(row.ContainsKey("value"));
        }
        else
        {
            Assert.AreEqual(new RdfLiteral(lexical, "http://www.w3.org/2001/XMLSchema#" + datatype), row["value"]);
        }
    }

    [TestMethod]
    [DataRow("'42'^^xsd:int + 1")]
    [DataRow("'42'^^xsd:short = 42")]
    [DataRow("SUM(?n)")]
    public async Task Sparql11_integer_subtype_operators_match_reference(string expression)
    {
        string query = "PREFIX xsd: <http://www.w3.org/2001/XMLSchema#> SELECT (" + expression +
            " AS ?value) { VALUES ?n { '1'^^xsd:int '2'^^xsd:short '3'^^xsd:unsignedByte } }";
        using var store = new VDS.RDF.TripleStore();
        var reference = (VDS.RDF.Query.SparqlResultSet)new VDS.RDF.Query.LeviathanQueryProcessor(store)
            .ProcessQuery(new VDS.RDF.Parsing.SparqlQueryParser().ParseFromString(query));
        await using var database = await OpenAsync(CreateDirectory());
        var actual = RdfSparql.Execute(database, query);
        CollectionAssert.AreEqual(reference.Select(row => ((VDS.RDF.ILiteralNode)row["value"]).Value).ToArray(),
            actual.Select(row => ((RdfLiteral)row["value"]).LexicalForm).ToArray());
    }
}
