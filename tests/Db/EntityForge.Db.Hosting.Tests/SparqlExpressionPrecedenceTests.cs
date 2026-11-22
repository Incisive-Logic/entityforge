using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("!1 = false", "true", "boolean")]
    [DataRow("!0 = true", "true", "boolean")]
    [DataRow("!true IN (true,false)", "true", "boolean")]
    [DataRow("!false NOT IN (true,false)", "false", "boolean")]
    [DataRow("!0 + 1", null, null)]
    [DataRow("EXISTS {} = true", "true", "boolean")]
    [DataRow("NOT EXISTS {} = false", "true", "boolean")]
    [DataRow("EXISTS {} IN (true)", "true", "boolean")]
    [DataRow("1 + EXISTS {}", null, null)]
    [DataRow("!(EXISTS {}) = false", "true", "boolean")]
    [DataRow("!(!true)", "true", "boolean")]
    [DataRow("+(-1)", "-1", "integer")]
    [DataRow("--1", "1", "integer")]
    [DataRow("-+1", "-1", "integer")]
    [DataRow("+-1", "-1", "integer")]
    [DataRow("1--2", "3", "integer")]
    public async Task Sparql_unary_and_exists_expressions_follow_primary_precedence(string expression, string? expected, string? datatype)
    {
        await using var database = await OpenAsync(CreateDirectory());
        foreach (string query in new[]
        {
            "SELECT (" + expression + " AS ?answer) WHERE {}",
            "SELECT ?answer WHERE { BIND(" + expression + " AS ?answer) }"
        })
        {
            var row = RdfSparql.Execute(database, query).Single();
            Assert.AreEqual(expected is null ? 0 : 1, row.Count, query);
            if (expected is not null)
            {
                Assert.AreEqual(new RdfLiteral(expected, "http://www.w3.org/2001/XMLSchema#" + datatype), row["answer"], query);
            }
        }
    }

    [TestMethod]
    [DataRow("!!true")]
    [DataRow("!+ABS(1)")]
    [DataRow("+-ABS(1)")]
    [DataRow("--ABS(1)")]
    [DataRow("+!true")]
    public async Task Sparql_unary_operators_require_a_primary_operand(string expression)
    {
        await using var database = await OpenAsync(CreateDirectory());
        Assert.ThrowsExactly<InvalidDataException>(() => RdfSparql.Execute(database,
            "SELECT (" + expression + " AS ?answer) WHERE {}"));
    }
}
