using System.Globalization;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("SELECT (NOW() AS ?a) (NOW() AS ?b) { VALUES ?v { 1 2 3 } }")]
    [DataRow("SELECT ?a ?b { BIND(NOW() AS ?a) { SELECT (NOW() AS ?b) {} } FILTER(?a = ?b) }")]
    [DataRow("SELECT ?a ?b { BIND(NOW() AS ?a) OPTIONAL { BIND(NOW() AS ?b) FILTER(?a = NOW()) } FILTER EXISTS { FILTER(?a = NOW()) } }")]
    [DataRow("SELECT (MIN(NOW()) AS ?a) (MAX(NOW()) AS ?b) { VALUES ?v { 1 2 } } HAVING(MIN(NOW()) = MAX(NOW())) ORDER BY NOW()")]
    [DataRow("SELECT ?a (NOW() AS ?b) { { BIND(NOW() AS ?a) } UNION { BIND(NOW() AS ?a) } }")]
    public async Task Sparql11_now_is_one_instant_across_the_query_algebra(string query)
    {
        await using var database = await OpenAsync(CreateDirectory());
        DateTimeOffset before = DateTimeOffset.UtcNow;
        var rows = RdfSparql.Execute(database, query);
        DateTimeOffset after = DateTimeOffset.UtcNow;
        Assert.IsTrue(rows.Count > 0);
        var first = (RdfLiteral)rows[0]["a"];
        Assert.AreEqual("http://www.w3.org/2001/XMLSchema#dateTime", first.Datatype);
        var time = DateTimeOffset.Parse(first.LexicalForm, CultureInfo.InvariantCulture);
        Assert.IsTrue(time >= before && time <= after);
        foreach (var row in rows)
        {
            Assert.AreEqual(first, row["a"]);
            Assert.AreEqual(first, row["b"]);
        }
    }

    [TestMethod]
    public async Task Sparql11_now_is_not_retained_in_the_cached_plan()
    {
        await using var database = await OpenAsync(CreateDirectory());
        const string query = "SELECT (NOW() AS ?a) {}";
        var first = RdfSparql.Execute(database, query).Single()["a"];
        await Task.Delay(20);
        var second = RdfSparql.Execute(database, query).Single()["a"];
        Assert.AreNotEqual(first, second);
        Assert.IsTrue(RdfSparql.ExecuteAsk(database, "ASK { BIND(NOW() AS ?a) FILTER(?a=NOW()) }"));
    }

    [TestMethod]
    public async Task Sparql11_now_works_in_construct_and_keeps_literals_unchanged()
    {
        await using var database = await OpenAsync(CreateDirectory());
        var quads = RdfSparql.ExecuteConstruct(database,
            "CONSTRUCT { <urn:s> <urn:time> ?a } WHERE { BIND(NOW() AS ?a) FILTER(?a=NOW()) } ORDER BY NOW()");
        Assert.AreEqual(1, quads.Count);
        Assert.AreEqual("http://www.w3.org/2001/XMLSchema#dateTime", ((RdfLiteral)quads[0].Object).Datatype);
        var row = RdfSparql.Execute(database, "SELECT ('NOW()' AS ?text) {}").Single();
        Assert.AreEqual(new RdfLiteral("NOW()"), row["text"]);
        Assert.ThrowsExactly<InvalidDataException>(() => RdfSparql.Execute(database, "SELECT (NOW(1) AS ?a) {}"));
    }
}
