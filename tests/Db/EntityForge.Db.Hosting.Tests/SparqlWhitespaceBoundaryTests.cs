using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("\u000b")]
    [DataRow("\u000c")]
    [DataRow("\u0085")]
    [DataRow("\u00a0")]
    [DataRow("\u1680")]
    [DataRow("\u2003")]
    [DataRow("\u2028")]
    [DataRow("\u3000")]
    public async Task Sparql_only_grammar_whitespace_separates_tokens(string separator)
    {
        await using var database = await OpenAsync(CreateDirectory());
        Assert.ThrowsExactly<InvalidDataException>(() => RdfSparql.Execute(database,
            "SELECT" + separator + "(1 AS ?value) {}"));
        Assert.ThrowsExactly<InvalidDataException>(() => RdfSparql.Execute(database,
            "PREFIX" + separator + "ex:<urn:> SELECT * {}"));
        Assert.ThrowsExactly<InvalidDataException>(() => RdfSparql.ExecuteAsk(database,
            "ASK { [" + separator + "] <urn:p> 1 }"));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => database.CommitSparqlUpdateAsync(Guid.NewGuid(),
            "INSERT" + separator + "DATA {<urn:s><urn:p>1}").AsTask());
        foreach (string query in new[] { separator + "SELECT * {}", "SELECT * {}" + separator })
        {
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => database.QuerySparqlAsync(query).AsTask(), query);
        }
        foreach (string update in new[]
        {
            separator + "INSERT DATA {<urn:s><urn:p>1}",
            "INSERT DATA {<urn:s><urn:p>1}" + separator,
            "INSERT DATA {<urn:s><urn:p>1};" + separator,
            "INSERT DATA {<urn:s><urn:p>1};" + separator + "PREFIX ex:<urn:>"
        })
        {
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => database.CommitSparqlUpdateAsync(Guid.NewGuid(), update).AsTask(), update);
        }
        Assert.AreEqual(0, database.Find(new()).Count);
    }

    [TestMethod]
    public async Task Sparql_unicode_whitespace_inside_terms_and_names_is_not_a_separator()
    {
        await using var database = await OpenAsync(CreateDirectory());
        var row = RdfSparql.Execute(database,
            "PREFIX p\u1680x:<urn:> SELECT ?v\u1680x ('\u00a0' AS ?text) (<urn:\u00a0> AS ?iri) " +
            "{ VALUES ?v\u1680x {p\u1680x:\u1680name} }").Single();
        Assert.AreEqual(new RdfIri("urn:\u1680name"), row["v\u1680x"]);
        Assert.AreEqual("\u00a0", ((RdfLiteral)row["text"]).LexicalForm);
        Assert.AreEqual(new RdfIri("urn:\u00a0"), row["iri"]);
    }
}
