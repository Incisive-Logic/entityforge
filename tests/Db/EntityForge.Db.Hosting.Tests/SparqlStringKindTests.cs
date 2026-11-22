using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("'a😀éz'", null)]
    [DataRow("'a😀éz'^^<http://www.w3.org/2001/XMLSchema#string>", null)]
    [DataRow("'a😀éz'@en", "en")]
    public async Task Sparql_string_slicing_preserves_literal_kind_and_counts_scalars(string source, string? language)
    {
        await using var database = await OpenAsync(CreateDirectory());
        var row = RdfSparql.Execute(database, "SELECT (STRLEN(" + source + ") AS ?length) " +
            "(SUBSTR(" + source + ",2,2) AS ?slice) (SUBSTR(" + source + ",99) AS ?empty) WHERE {}").Single();
        Assert.AreEqual(new RdfLiteral("5", "http://www.w3.org/2001/XMLSchema#integer"), row["length"]);
        Assert.AreEqual(new RdfLiteral("😀e", language: language), row["slice"]);
        Assert.AreEqual(new RdfLiteral(String.Empty, language: language), row["empty"]);
    }

    [TestMethod]
    [DataRow("42")]
    [DataRow("true")]
    [DataRow("<urn:input>")]
    [DataRow("BNODE()")]
    [DataRow("'abc'^^<urn:custom>")]
    [DataRow("?missing")]
    [DataRow("1/0")]
    public async Task Sparql_string_slicing_errors_follow_projection_bind_filter_and_coalesce_rules(string source)
    {
        await using var database = await OpenAsync(CreateDirectory());
        foreach (string function in new[] { "STRLEN", "SUBSTR" })
        {
            string expression = function + "(" + source + (function == "SUBSTR" ? ",1,1" : "") + ")";
            const string seed = "VALUES ?seed { 7 } ";
            foreach (string query in new[]
            {
                "SELECT ?seed (" + expression + " AS ?answer) WHERE { " + seed + "}",
                "SELECT ?seed ?answer WHERE { " + seed + "BIND(" + expression + " AS ?answer) }"
            })
            {
                var row = RdfSparql.Execute(database, query).Single();
                Assert.AreEqual(1, row.Count, query);
                Assert.AreEqual(new RdfLiteral("7", "http://www.w3.org/2001/XMLSchema#integer"), row["seed"], query);
            }
            Assert.AreEqual(0, RdfSparql.Execute(database,
                "SELECT ?seed WHERE { " + seed + "FILTER(" + expression + " = 'anything') }").Count, expression);
            var recovered = RdfSparql.Execute(database,
                "SELECT (COALESCE(" + expression + ",'fallback') AS ?answer) WHERE { " + seed + "}").Single();
            Assert.AreEqual(new RdfLiteral("fallback"), recovered["answer"], expression);
        }
    }
}
