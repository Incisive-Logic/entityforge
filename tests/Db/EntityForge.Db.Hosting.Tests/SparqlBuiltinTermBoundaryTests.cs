using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("isIRI(?input)", "true,false,false,false,false,error")]
    [DataRow("isURI(?input)", "true,false,false,false,false,error")]
    [DataRow("isBlank(?input)", "false,true,false,false,false,error")]
    [DataRow("isLiteral(?input)", "false,false,true,true,true,error")]
    [DataRow("isNumeric(?input)", "false,false,false,false,true,error")]
    [DataRow("STRSTARTS(?input,'a')", "error,error,true,true,error,error")]
    [DataRow("STRSTARTS(?input,'a'@en)", "error,error,error,true,error,error")]
    public async Task Sparql_builtin_term_boundaries_match_in_query_and_scan(string expression, string expected)
    {
        await using var database = await OpenAsync(CreateDirectory());
        RdfTerm[] values = [new RdfIri("urn:iri"), new RdfBlankNode("b"), new RdfLiteral("abc"),
            new RdfLiteral("abc", language: "en"), new RdfLiteral("7", "http://www.w3.org/2001/XMLSchema#integer"),
            new RdfLiteral("unused")];
        await database.BulkLoadAsync(Guid.NewGuid(), values.Select((value, index) =>
            new RdfQuad(new RdfIri("urn:input:" + index), new("urn:value"), value)).ToArray());
        string query = "SELECT ?s (" + expression + " AS ?answer) WHERE { ?s <urn:value> ?v " +
            "BIND(IF(?s=<urn:input:5>,?missing,?v) AS ?input) } ORDER BY ?s";
        void Verify(IReadOnlyList<IReadOnlyDictionary<string, RdfTerm>> rows)
        {
            Assert.AreEqual(6, rows.Count);
            CollectionAssert.AreEqual(expected.Split(','), rows.Select(row => row.TryGetValue("answer", out var answer)
                ? ((RdfLiteral)answer).LexicalForm : "error").ToArray());
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
