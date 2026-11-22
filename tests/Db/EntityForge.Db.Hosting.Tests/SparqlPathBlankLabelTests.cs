using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("<urn:p>/<urn:p>", false, "A:urn:c,A:urn:c")]
    [DataRow("<urn:p>/<urn:p>", true, "A:urn:c,A:urn:c")]
    [DataRow("<urn:p>+", false, "A:urn:b,A:urn:c,A:urn:d,B:urn:c,X:urn:c")]
    [DataRow("<urn:p>+", true, "A:urn:b,A:urn:c,A:urn:d,B:urn:c,X:urn:c")]
    [DataRow("<urn:p>?", false, "A:urn:a,A:urn:b,A:urn:d,B:urn:b,B:urn:c,X:urn:c,X:urn:x")]
    [DataRow("<urn:p>?", true, "A:urn:a,A:urn:b,A:urn:d,B:urn:b,B:urn:c,X:urn:c,X:urn:x")]
    public async Task Sparql_path_blank_label_joins_triples_without_losing_path_bags(string path, bool named, string expected)
    {
        await using var database = await OpenAsync(CreateDirectory());
        const string facts = "<urn:a> <urn:p> <urn:b>, <urn:d>; <urn:tag> 'A'. " +
            "<urn:b> <urn:p> <urn:c>; <urn:tag> 'B'. <urn:d> <urn:p> <urn:c>. " +
            "<urn:x> <urn:p> <urn:c>; <urn:tag> 'X'.";
        await database.CommitSparqlUpdateAsync(Guid.NewGuid(),
            "INSERT DATA { " + (named ? "GRAPH <urn:g> { " + facts + " }" : facts) + " }");
        string pattern = "_:shared " + path + " ?end . FILTER(true) _:shared <urn:tag> ?tag";
        string body = named ? "GRAPH <urn:g> { " + pattern + " }" : pattern;
        var result = (RdfSparqlSelectResult)await database.QuerySparqlAsync("SELECT * { " + body + " }");
        string[] actual = result.Bindings.Select(row =>
        {
            Assert.AreEqual(2, row.Count, "Internal blank-label variables must not escape SELECT *. ");
            return ((RdfLiteral)row["tag"]).LexicalForm + ":" + ((RdfIri)row["end"]).Value;
        }).ToArray();
        CollectionAssert.AreEquivalent(expected.Split(','), actual);
    }

    [TestMethod]
    [DataRow("_:same <urn:p>+ ?end . {} _:same <urn:tag> ?tag")]
    [DataRow("_:same <urn:p>+ ?end . OPTIONAL { _:same <urn:tag> ?tag }")]
    [DataRow("_:same <urn:p>+ ?end . BIND(1 AS ?value) _:same <urn:tag> ?tag")]
    [DataRow("{ _:same <urn:p>+ ?end } UNION { _:same <urn:tag> ?tag }")]
    public async Task Sparql_path_blank_label_reuse_across_groups_rejects_before_sequence_changes(string body)
    {
        await using var database = await OpenAsync(CreateDirectory());
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            database.QuerySparqlAsync("SELECT * { " + body + " }").AsTask());
        var before = database.CurrentVersion;
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => database.CommitSparqlUpdateAsync(Guid.NewGuid(),
            "INSERT DATA { <urn:probe> <urn:p> 1 }; INSERT { <urn:result> <urn:p> ?end } WHERE { " + body + " }").AsTask());
        Assert.AreEqual(before, database.CurrentVersion);
        Assert.AreEqual(0, database.Find(new()).Count());
    }
}
