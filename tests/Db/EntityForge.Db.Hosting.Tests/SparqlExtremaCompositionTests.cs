using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("integer", "-3|-9|-7|-3", "-9", "-3")]
    [DataRow("integer", "5|5|5", "5", "5")]
    [DataRow("decimal", "10.5|2.25|-0.5|2.25", "-0.5", "10.5")]
    [DataRow("string", "zebra|alpha|middle|alpha", "alpha", "zebra")]
    [DataRow("dateTime", "2024-01-01T01:00:00+02:00|2024-01-01T00:00:00Z|2024-01-01T00:30:00Z", "2024-01-01T01:00:00+02:00", "2024-01-01T00:30:00Z")]
    [DataRow("dateTime", "2024-01-01T00:00:00-02:00|2024-01-01T01:00:00|2024-01-01T01:30:00Z", "2024-01-01T01:00:00", "2024-01-01T00:00:00-02:00")]
    [DataRow("dateTime", "2024-01-01T00:00:00.1234567892|2024-01-01T00:00:00.1234567891Z", "2024-01-01T00:00:00.1234567891Z", "2024-01-01T00:00:00.1234567892")]
    public async Task Sparql_extrema_preserve_order_and_terms_across_scan_update_and_restart(
        string datatype, string lexicalValues, string minimum, string maximum)
    {
        string directory = CreateDirectory();
        const string query = "SELECT (MIN(?v) AS ?minimum) (MAX(?v) AS ?maximum) " +
            "(MIN(DISTINCT ?v) AS ?distinctMinimum) (MAX(DISTINCT ?v) AS ?distinctMaximum) " +
            "WHERE { ?s <urn:value> ?v }";
        RdfLiteral Literal(string value) => new(value, "http://www.w3.org/2001/XMLSchema#" + datatype);
        void Verify(IReadOnlyDictionary<string, RdfTerm> row)
        {
            Assert.AreEqual(Literal(minimum), row["minimum"]);
            Assert.AreEqual(Literal(maximum), row["maximum"]);
            Assert.AreEqual(Literal(minimum), row["distinctMinimum"]);
            Assert.AreEqual(Literal(maximum), row["distinctMaximum"]);
        }
        await using (var database = await OpenAsync(directory))
        {
            await database.BulkLoadAsync(Guid.NewGuid(), lexicalValues.Split('|').Select((value, index) =>
                new RdfQuad(new RdfIri("urn:input:" + index), new("urn:value"), Literal(value))).ToArray());
            var result = (RdfSparqlSelectResult)await database.QuerySparqlAsync(query);
            Assert.AreEqual(1, result.Bindings.Count);
            Verify(result.Bindings[0]);
            int rows = 0, completions = 0;
            await foreach (var page in RdfSparql.ScanAsync(query, database.QueryPageAsync,
                database.ReleaseQueryContinuationAsync, new(PageSize: 1, MaximumSortTemporaryBytes: 65536)))
            {
                foreach (var row in page.Rows)
                {
                    Verify(row);
                    rows++;
                }
                completions += page.Complete ? 1 : 0;
            }
            Assert.AreEqual(1, rows);
            Assert.AreEqual(1, completions);
            Assert.AreEqual(0, database.RetainedQueryContinuationCount);
            await database.CommitSparqlUpdateAsync(Guid.NewGuid(),
                "INSERT { <urn:extrema> <urn:minimum> ?minimum ; <urn:maximum> ?maximum } WHERE { { " + query + " } }");
        }
        await using var reopened = await OpenAsync(directory);
        var persisted = (RdfSparqlSelectResult)await reopened.QuerySparqlAsync(
            "SELECT ?minimum ?maximum { <urn:extrema> <urn:minimum> ?minimum ; <urn:maximum> ?maximum }");
        Assert.AreEqual(1, persisted.Bindings.Count);
        Assert.AreEqual(Literal(minimum), persisted.Bindings[0]["minimum"]);
        Assert.AreEqual(Literal(maximum), persisted.Bindings[0]["maximum"]);
    }
}
