using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    public async Task Sparql11_string_comparison_and_order_use_unicode_codepoints()
    {
        await using var database = await OpenAsync(CreateDirectory());
        var rows = RdfSparql.Execute(database, "SELECT ?value { VALUES ?value { '𐀀' '\uE000' 'a𐀀' 'a\uE000' 'a' } } ORDER BY ?value");
        CollectionAssert.AreEqual(new[] { "a", "a\uE000", "a𐀀", "\uE000", "𐀀" },
            rows.Select(row => ((RdfLiteral)row["value"]).LexicalForm).ToArray());
        var comparison = RdfSparql.Execute(database, "SELECT ('𐀀' > '\uE000' AS ?value) {}").Single();
        Assert.AreEqual("true", ((RdfLiteral)comparison["value"]).LexicalForm);
        var extremes = RdfSparql.Execute(database, "SELECT (MIN(?x) AS ?min) (MAX(?x) AS ?max) { VALUES ?x { '𐀀' '\uE000' } }").Single();
        Assert.AreEqual("\uE000", ((RdfLiteral)extremes["min"]).LexicalForm);
        Assert.AreEqual("𐀀", ((RdfLiteral)extremes["max"]).LexicalForm);
    }

    [TestMethod]
    [DataRow("食", "食べる", "食べる")]
    [DataRow("é", "resumé", "resumé")]
    [DataRow("𐀀", "𐀀", "𐀀")]
    [DataRow("a.b", "x:y", "x:y")]
    [DataRow("p", "a\\~b", "a~b")]
    [DataRow("p", "a\\#b", "a#b")]
    [DataRow("p", "a\\$b", "a$b")]
    [DataRow("p", "a\\'b", "a'b")]
    [DataRow("p", "%7E", "%7E")]
    public async Task Sparql11_prefixed_names_preserve_unicode_and_local_escapes(string prefix, string local, string expected)
    {
        await using var database = await OpenAsync(CreateDirectory());
        var quad = new RdfQuad(new RdfIri("urn:s"), new RdfIri("urn:unicode:" + expected), new RdfLiteral("value"));
        await database.CommitAsync(new(Guid.NewGuid(), new RdfMutation[] { new AssertRdfQuad(quad) }));
        var rows = RdfSparql.Execute(database, $"PREFIX {prefix}: <urn:unicode:> SELECT ?value {{ <urn:s> {prefix}:{local} ?value }}");
        Assert.AreEqual(1, rows.Count);
        Assert.AreEqual(new RdfLiteral("value"), rows[0]["value"]);
    }

    [TestMethod]
    [DataRow("食")]
    [DataRow("𐀀")]
    [DataRow("é")]
    [DataRow("1value")]
    [DataRow("\u200Cname")]
    [DataRow("name\u203Ftail")]
    [DataRow("name\u00B7tail")]
    [DataRow("\U000EFFFF")]
    public async Task Sparql11_variable_names_accept_unicode_and_both_sigils(string name)
    {
        await using var database = await OpenAsync(CreateDirectory());
        string query = $"SELECT * {{ VALUES ${name} {{ 1 }} FILTER(?{name}=1) }}";
        var row = RdfSparql.Execute(database, query).Single();
        Assert.AreEqual(new RdfLiteral("1", "http://www.w3.org/2001/XMLSchema#integer"), row[name]);
        CollectionAssert.AreEqual(new[] { name }, RdfSparql.ResultVariables(query).ToArray());
    }

    [TestMethod]
    [DataRow("\u00B7name")]
    [DataRow("\u0300name")]
    [DataRow("\u037Ename")]
    [DataRow("\U000F0000")]
    public void Sparql_variable_name_boundaries_reject_non_name_characters(string name)
    {
        foreach (char sigil in new[] { '?', '$' })
        {
            Assert.ThrowsExactly<InvalidDataException>(() =>
                RdfSparql.ResultVariables($"SELECT * {{ VALUES {sigil}{name} {{1}} }}"));
        }
    }
}
