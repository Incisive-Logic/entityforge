using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("true", "true")]
    [DataRow("false", "false")]
    [DataRow("0", "false")]
    [DataRow("-1", "true")]
    [DataRow("0.0", "false")]
    [DataRow("0.125", "true")]
    [DataRow("'-0'^^xsd:float", "false")]
    [DataRow("'NaN'^^xsd:float", "false")]
    [DataRow("'INF'^^xsd:float", "true")]
    [DataRow("'-0'^^xsd:double", "false")]
    [DataRow("'NaN'^^xsd:double", "false")]
    [DataRow("'-INF'^^xsd:double", "true")]
    [DataRow("'5e-324'^^xsd:double", "true")]
    [DataRow("'1'^^xsd:unsignedByte", "true")]
    [DataRow("''", "false")]
    [DataRow("' '", "true")]
    [DataRow("''@en", "false")]
    [DataRow("'😀'@en", "true")]
    [DataRow("''^^xsd:string", "false")]
    [DataRow("'false'^^xsd:string", "true")]
    [DataRow("<urn:value>", null)]
    [DataRow("BNODE()", null)]
    [DataRow("'true'^^<urn:custom>", null)]
    [DataRow("'2026-11-18T00:00:00Z'^^xsd:dateTime", null)]
    [DataRow("?missing", null)]
    [DataRow("1/0", null)]
    public async Task Sparql_effective_boolean_value_covers_term_classes_in_if_not_and_filter(string expression, string? expected)
    {
        await using var database = await OpenAsync(CreateDirectory());
        const string prefix = "PREFIX xsd: <http://www.w3.org/2001/XMLSchema#> ";
        var row = RdfSparql.Execute(database, prefix + "SELECT (IF(" + expression + ",true,false) AS ?truth) " +
            "(!(" + expression + ") AS ?negated) WHERE {}").Single();
        Assert.AreEqual(expected is null ? 0 : 2, row.Count);
        if (expected is not null)
        {
            Assert.AreEqual(new RdfLiteral(expected, "http://www.w3.org/2001/XMLSchema#boolean"), row["truth"]);
            Assert.AreEqual(new RdfLiteral(expected == "true" ? "false" : "true", "http://www.w3.org/2001/XMLSchema#boolean"), row["negated"]);
        }
        Assert.AreEqual(expected == "true" ? 1 : 0,
            RdfSparql.Execute(database, prefix + "SELECT * WHERE { FILTER(" + expression + ") }").Count);
        Assert.AreEqual(expected == "false" ? 1 : 0,
            RdfSparql.Execute(database, prefix + "SELECT * WHERE { FILTER(!(" + expression + ")) }").Count);
    }

    [TestMethod]
    [DataRow("true", "true", "true", "true")]
    [DataRow("true", "false", "true", "false")]
    [DataRow("false", "true", "true", "false")]
    [DataRow("false", "false", "false", "false")]
    [DataRow("true", "1/0", "true", null)]
    [DataRow("1/0", "true", "true", null)]
    [DataRow("false", "1/0", null, "false")]
    [DataRow("1/0", "false", null, "false")]
    [DataRow("1/0", "1/0", null, null)]
    public async Task Sparql_logical_operators_follow_complete_true_false_error_table(string left, string right, string? orExpected, string? andExpected)
    {
        await using var database = await OpenAsync(CreateDirectory());
        var row = RdfSparql.Execute(database, "SELECT ((" + left + ") || (" + right + ") AS ?either) " +
            "((" + left + ") && (" + right + ") AS ?both) WHERE {}").Single();
        foreach (var (name, expected) in new[] { ("either", orExpected), ("both", andExpected) })
        {
            Assert.AreEqual(expected is not null, row.ContainsKey(name), name);
            if (expected is not null)
            {
                Assert.AreEqual(new RdfLiteral(expected, "http://www.w3.org/2001/XMLSchema#boolean"), row[name], name);
            }
        }
    }
}
