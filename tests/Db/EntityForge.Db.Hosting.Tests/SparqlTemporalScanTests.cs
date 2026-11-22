using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("SELECT (TZ(?stamp) AS ?value) { ?s <urn:stamp> ?stamp } ORDER BY ?s", "Z|-05:30||missing")]
    [DataRow("SELECT (COALESCE(STR(TIMEZONE(?stamp)), 'absent') AS ?value) { ?s <urn:stamp> ?stamp } ORDER BY ?s", "PT0S|-PT5H30M|absent|absent")]
    [DataRow("SELECT (STR(HOURS(?stamp)) AS ?value) { ?s <urn:stamp> ?stamp FILTER(YEAR(?stamp)=2024) } ORDER BY ?s", "23|23|23")]
    [DataRow("SELECT (STR(SECONDS(?stamp)) AS ?value) { ?s <urn:stamp> ?stamp FILTER(BOUND(?stamp)) } ORDER BY ?s", "12.123456789123456789|12|12|missing")]
    [DataRow("PREFIX xsd:<http://www.w3.org/2001/XMLSchema#> SELECT (STR(?stamp) AS ?value) { ?s <urn:stamp> ?stamp FILTER(?stamp = '2024-02-29T23:45:12Z'^^xsd:dateTime) } ORDER BY ?s", "2024-02-29T23:45:12")]
    public async Task Sparql_temporal_components_preserve_results_across_scan_pages(string query, string expected)
    {
        await using var database = await OpenAsync(CreateDirectory());
        string[] values =
        [
            "2024-02-29T23:45:12.123456789123456789Z",
            "2024-02-29T23:45:12-05:30",
            "2024-02-29T23:45:12",
            "2023-02-29T23:45:12Z"
        ];
        await database.BulkLoadAsync(Guid.NewGuid(), values.Select((value, index) =>
            new RdfQuad(new RdfIri("urn:s:" + index), new("urn:stamp"),
                new RdfLiteral(value, "http://www.w3.org/2001/XMLSchema#dateTime"))).ToArray());
        static string Value(IReadOnlyDictionary<string, RdfTerm> row) =>
            row.TryGetValue("value", out var value) ? ((RdfLiteral)value).LexicalForm : "missing";
        var ordinary = RdfSparql.Execute(database, query).Select(Value).ToArray();
        Assert.AreEqual(expected, String.Join('|', ordinary));
        var streamed = new List<string>();
        int completions = 0;
        await foreach (var page in RdfSparql.ScanAsync(query, database.QueryPageAsync,
            database.ReleaseQueryContinuationAsync, new(PageSize: 1, MaximumSortTemporaryBytes: 65536)))
        {
            streamed.AddRange(page.Rows.Select(Value));
            completions += page.Complete ? 1 : 0;
        }
        CollectionAssert.AreEqual(ordinary, streamed.ToArray());
        Assert.AreEqual(1, completions);
        Assert.AreEqual(0, database.RetainedQueryContinuationCount);
    }
}
