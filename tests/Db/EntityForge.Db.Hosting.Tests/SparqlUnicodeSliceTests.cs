using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("2,2", "😀e")]
    [DataRow("3,2", "é")]
    [DataRow("0,3", "a😀")]
    [DataRow("-1,3", "a")]
    [DataRow("2", "😀éz")]
    [DataRow("6,1", "")]
    [DataRow("1,-1", "")]
    [DataRow("1,999999999999999999999999999999", "a😀éz")]
    [DataRow("999999999999999999999999999999,2", "")]
    [DataRow("-999999999999999999999999999999,999999999999999999999999999999", "")]
    public async Task Sparql_unicode_substrings_preserve_exact_bounds_and_language(string arguments, string expected)
    {
        await using var database = await OpenAsync(CreateDirectory());
        var row = RdfSparql.Execute(database, "SELECT (SUBSTR('a😀éz'@en," + arguments + ") AS ?slice) " +
            "(STRLEN('a😀éz') AS ?length) WHERE {}").Single();
        Assert.AreEqual(new RdfLiteral(expected, language: "en"), row["slice"]);
        Assert.AreEqual(new RdfLiteral("5", "http://www.w3.org/2001/XMLSchema#integer"), row["length"]);
    }
}
