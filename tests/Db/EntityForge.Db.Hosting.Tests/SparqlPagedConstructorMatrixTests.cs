using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("'1'^^xsd:string", "1", "1", "1", "1", "1", null, "true")]
    [DataRow("'1.5'^^xsd:float", "1.5", "1.5", "1.5", "1.5", "1", null, "true")]
    [DataRow("'-2.5'^^xsd:double", "-2.5", "-2.5", "-2.5", "-2.5", "-2", null, "true")]
    [DataRow("'3.75'^^xsd:decimal", "3.75", "3.75", "3.75", "3.75", "3", null, "true")]
    [DataRow("4", "4", "4", "4", "4", "4", null, "true")]
    [DataRow("'2024-02-29T01:02:03Z'^^xsd:dateTime", "2024-02-29T01:02:03Z", null, null, null, null, "2024-02-29T01:02:03Z", null)]
    [DataRow("true", "true", "1", "1", "1", "1", null, "true")]
    [DataRow("<urn:constructor>", "urn:constructor", null, null, null, null, null, null)]
    [DataRow("'0'", "0", "0", "0", "0", "0", null, "false")]
    [DataRow("'invalid'", "invalid", null, null, null, null, null, null)]
    [DataRow("'2024-02-29T01:02:03Z'", "2024-02-29T01:02:03Z", null, null, null, null, "2024-02-29T01:02:03Z", null)]
    public async Task Sparql_constructor_type_matrix_preserves_values_and_errors_in_query_and_scan(
        string input, string? text, string? single, string? number, string? dec, string? integer, string? date, string? boolean)
    {
        const string xsd = "http://www.w3.org/2001/XMLSchema#";
        string[] targets = ["string", "float", "double", "decimal", "integer", "dateTime", "boolean"];
        string?[] expected = [text, single, number, dec, integer, date, boolean];
        await using var database = await OpenAsync(CreateDirectory());
        await database.CommitSparqlUpdateAsync(Guid.NewGuid(), "PREFIX xsd:<" + xsd + "> INSERT DATA { " +
            String.Join(' ', Enumerable.Range(0, 3).Select(index => "<urn:cast:" + index + "> <urn:input> " + input + " .")) + " }");
        string query = "PREFIX xsd:<" + xsd + "> SELECT ?s " + String.Join(' ', targets.Select((target, index) =>
            "(xsd:" + target + "(?v) AS ?value" + index + ")")) + " { ?s <urn:input> ?v } ORDER BY ?s";
        void Verify(IReadOnlyList<IReadOnlyDictionary<string, RdfTerm>> rows)
        {
            Assert.AreEqual(3, rows.Count);
            foreach (var row in rows)
            {
                for (int index = 0; index < targets.Length; index++)
                {
                    Assert.AreEqual(expected[index] is not null, row.TryGetValue("value" + index, out var actual), targets[index]);
                    if (expected[index] is { } lexical)
                    {
                        Assert.AreEqual(new RdfLiteral(lexical, xsd + targets[index]), actual, targets[index]);
                    }
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
