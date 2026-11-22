using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("STRBEFORE('abc'@en,'b')", "a", "en")]
    [DataRow("STRAFTER('abc'@en,'b')", "c", "en")]
    [DataRow("STRBEFORE('abc'@en,'')", "", "en")]
    [DataRow("STRAFTER('abc'@en,'')", "abc", "en")]
    [DataRow("STRBEFORE('abc'@en,'z')", "", null)]
    [DataRow("STRAFTER('abc'@en,'z')", "", null)]
    [DataRow("STRBEFORE('abc'@en,'a'@EN)", "", "en")]
    [DataRow("STRAFTER('abc'@en,'c'@EN)", "", "en")]
    [DataRow("STRBEFORE('abc'@en,'b'@fr)", null, null)]
    [DataRow("STRAFTER('abc','b'@en)", null, null)]
    [DataRow("STRBEFORE('a😀b'@en,'😀')", "a", "en")]
    [DataRow("STRAFTER('a😀b'@en,'😀')", "b", "en")]
    [DataRow("CONCAT('a'@en,'b'@EN)", "ab", "en")]
    [DataRow("CONCAT('a'@en,'b'@fr)", "ab", null)]
    [DataRow("CONCAT('a'@en,'b')", "ab", null)]
    [DataRow("CONCAT('a',1)", null, null)]
    public async Task Sparql_string_results_preserve_kind_and_error_in_query_and_scan(string expression, string? lexical, string? language)
    {
        await using var database = await OpenAsync(CreateDirectory());
        await database.BulkLoadAsync(Guid.NewGuid(), Enumerable.Range(0, 3).Select(index =>
            new RdfQuad(new RdfIri("urn:string:" + index), new("urn:value"), new RdfLiteral("seed"))).ToArray());
        string query = "SELECT ?s (" + expression + " AS ?answer) WHERE { ?s <urn:value> ?v } ORDER BY ?s";
        void Verify(IReadOnlyList<IReadOnlyDictionary<string, RdfTerm>> rows)
        {
            Assert.AreEqual(3, rows.Count);
            foreach (var row in rows)
            {
                Assert.AreEqual(lexical is not null, row.TryGetValue("answer", out var answer));
                if (lexical is not null)
                {
                    Assert.AreEqual(new RdfLiteral(lexical, language: language), answer);
                }
            }
        }
        Verify(((RdfSparqlSelectResult)await database.QuerySparqlAsync(query)).Bindings);
        var scanned = new List<IReadOnlyDictionary<string, RdfTerm>>();
        int completions = 0;
        await foreach (var page in RdfSparql.ScanAsync(query, database.QueryPageAsync,
            database.ReleaseQueryContinuationAsync, new(PageSize: 2, MaximumSortTemporaryBytes: 65536)))
        {
            scanned.AddRange(page.Rows);
            completions += page.Complete ? 1 : 0;
        }
        Verify(scanned);
        Assert.AreEqual(1, completions);
        Assert.AreEqual(0, database.RetainedQueryContinuationCount);
    }
}
