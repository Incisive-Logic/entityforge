using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    // Function-level counterparts of QT3 cases whose surrounding XQuery
    // expressions cannot be executed by the literal-fixture adapter. These
    // are separate SPARQL regressions, not original QT3 corpus passes.
    [TestMethod]
    [DataRow("CONCAT(STR(REGEX('abracadabra','^bra')),STR(REGEX('abracadabra','^bra','')))", "falsefalse")]
    [DataRow("COUNT(REGEX('()','^bra'))", "1")]
    [DataRow("REGEX('a','a[^b]')", "false")]
    [DataRow("REGEX('a ','a[^b]')", "true")]
    [DataRow("IF(REPLACE('blah','a','e','m'),true,false)", "true")]
    [DataRow("REPLACE('blah','a','e','m')", "bleh")]
    [DataRow("REPLACE('ThiY Ybcd.','Y','Q')", "ThiQ Qbcd.")]
    [DataRow("REPLACE('ThiY iY a abYY.','Y','Q')", "ThiQ iQ a abQQ.")]
    [DataRow("REPLACE('ThiY abcdY.','Y','Q')", "ThiQ abcdQ.")]
    [DataRow("REPLACE('2,4,6,8,10','','c')", null)]
    [DataRow("REPLACE('2,4,6,8,10','','c','m')", null)]
    [DataRow("REGEX('input',?absent)", null)]
    [DataRow("REGEX('input','pattern',?absent)", null)]
    [DataRow("REPLACE('input',?absent,'replacement')", null)]
    [DataRow("REPLACE('input','pattern',?absent)", null)]
    [DataRow("REPLACE(?absent,'bra','*')", null)]
    public async Task Sparql_regex_adapted_function_cases_preserve_results_and_expression_errors(string expression, string? expected)
    {
        await using var database = await OpenAsync(CreateDirectory());
        var row = RdfSparql.Execute(database, $"SELECT ({expression} AS ?value) {{}}").Single();
        if (expected is null)
        {
            Assert.IsFalse(row.ContainsKey("value"));
        }
        else
        {
            Assert.AreEqual(expected, ((RdfLiteral)row["value"]).LexicalForm);
        }
    }

    [TestMethod]
    [DataRow("REGEX('input')")]
    [DataRow("REGEX('input','pattern','',?absent)")]
    [DataRow("REPLACE('input','pattern','replacement','',?absent)")]
    public async Task Sparql_regex_rejects_invalid_builtin_arity(string expression)
    {
        await using var database = await OpenAsync(CreateDirectory());
        Assert.ThrowsExactly<InvalidDataException>(() => RdfSparql.Execute(database, $"SELECT ({expression} AS ?value) {{}}"));
    }
}
