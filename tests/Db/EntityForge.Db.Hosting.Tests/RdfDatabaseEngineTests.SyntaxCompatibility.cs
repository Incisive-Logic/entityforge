using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    // Authored regression cases; these are not an official W3C conformance suite.
    [TestMethod]
    [DataRow("?s <urn:p> <urn:b>, <urn:c>; <urn:q> ?value")]
    [DataRow("?s <urn:p> <urn:b>, <urn:c>;; <urn:q> ?value;")]
    [DataRow("?s <urn:p> <urn:b>, <urn:c>; <urn:q> ?value; .")]
    [DataRow("?s <urn:p> <urn:b>, <urn:c>; <urn:q> ?value; FILTER(?value = 7)")]
    [DataRow("?s <urn:p> <urn:b>, <urn:c>; <urn:q> ?value; OPTIONAL { ?s <urn:missing> ?missing }")]
    public async Task Sparql_shorthand_preserves_subject_predicate_and_join(string body)
    {
        await using var database = await OpenAsync(CreateDirectory());
        var subject = new RdfIri("urn:a");
        await database.CommitAsync(new(Guid.NewGuid(), [
            new AssertRdfQuad(new(subject, new RdfIri("urn:p"), new RdfIri("urn:b"))),
            new AssertRdfQuad(new(subject, new RdfIri("urn:p"), new RdfIri("urn:c"))),
            new AssertRdfQuad(new(subject, new RdfIri("urn:q"), new RdfLiteral("7", "http://www.w3.org/2001/XMLSchema#integer"))),
            new AssertRdfQuad(new(new RdfIri("urn:decoy"), new RdfIri("urn:q"), new RdfLiteral("9")))
        ]));
        var rows = RdfSparql.Execute(database, "SELECT ?s ?value WHERE { " + body + " }");
        Assert.AreEqual(1, rows.Count);
        Assert.AreEqual(subject, rows[0]["s"]);
        Assert.AreEqual("7", ((RdfLiteral)rows[0]["value"]).LexicalForm);
    }

    [TestMethod]
    [DataRow("?s <urn:p> , ?o")]
    [DataRow("?s <urn:p> ?o,")]
    [DataRow("?s <urn:p> ?o; <urn:q>")]
    [DataRow("?s <urn:p> ?o; \"bad predicate\" ?v")]
    public async Task Sparql_shorthand_rejects_incomplete_lists(string body)
    {
        await using var database = await OpenAsync(CreateDirectory());
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RdfSparql.Execute(database, "SELECT ?s WHERE { " + body + " }"));
    }

    [TestMethod]
    [DataRow("SELECT ?s WHERE { ?s <urn:p> ?o } LIMIT 0")]
    [DataRow("SELECT ?s WHERE { ?s <urn:p> ?o } ORDER BY ?s LIMIT 0")]
    [DataRow("SELECT DISTINCT ?s WHERE { ?s <urn:p> ?o } LIMIT 0 OFFSET 1")]
    [DataRow("SELECT (COUNT(*) AS ?n) WHERE { ?s <urn:p> ?o } LIMIT 0")]
    [DataRow("SELECT ?s WHERE { { SELECT ?s WHERE { ?s <urn:p> ?o } ORDER BY ?s LIMIT 0 } }")]
    public async Task Sparql_zero_limit_returns_no_solutions(string query)
    {
        await using var database = await OpenAsync(CreateDirectory());
        await database.CommitAsync(new(Guid.NewGuid(), Enumerable.Range(0, 6)
            .Select(i => (RdfMutation)new AssertRdfQuad(new(new RdfIri("urn:s" + i), new RdfIri("urn:p"), new RdfLiteral("value")))).ToArray()));
        Assert.AreEqual(0, RdfSparql.Execute(database, query).Count);
    }

    [TestMethod]
    public async Task Sparql_zero_limit_still_validates_query_and_shorthand_pattern_bound()
    {
        await using var database = await OpenAsync(CreateDirectory());
        Assert.ThrowsExactly<NotSupportedException>(() => RdfSparql.Execute(database,
            "SELECT ?o WHERE { SERVICE <urn:remote> { <urn:a> <urn:p> ?o } } LIMIT 0"));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => RdfSparql.Execute(database,
            "SELECT ?o WHERE { <urn:a> <urn:p> " + String.Join(", ", Enumerable.Repeat("?o", 17)) + " } LIMIT 0"));
    }
}
