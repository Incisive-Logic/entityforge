using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow(false, false, "", "alpha,beta,gamma")]
    [DataRow(false, true, "", "alpha,beta,gamma")]
    [DataRow(true, false, "", "gamma,beta,alpha")]
    [DataRow(true, true, "", "gamma,beta,alpha")]
    [DataRow(false, false, " OFFSET 1 LIMIT 1", "beta")]
    [DataRow(false, true, " OFFSET 1 LIMIT 1", "beta")]
    [DataRow(true, false, " OFFSET 1 LIMIT 1", "beta")]
    [DataRow(true, true, " OFFSET 1 LIMIT 1", "beta")]
    [DataRow(false, false, " LIMIT 0", "")]
    [DataRow(false, true, " LIMIT 0", "")]
    [DataRow(true, false, " LIMIT 0", "")]
    [DataRow(true, true, " LIMIT 0", "")]
    public async Task Sparql_distinct_preserves_nonprojected_order_before_window(
        bool descending, bool scan, string window, string expected)
    {
        await using var database = await OpenAsync(CreateDirectory());
        // Each projected value occupies a disjoint range of ordering keys.
        // No expectation depends on choosing among interleaved duplicate rows.
        var values = new[] { "alpha", "alpha", "beta", "beta", "gamma", "gamma" };
        await database.BulkLoadAsync(Guid.NewGuid(), values.Select((value, index) =>
            new RdfQuad(new RdfIri("urn:s:" + index), new("urn:p"), new RdfLiteral(value))).Reverse().ToArray());
        string query = "SELECT DISTINCT ?o { ?s <urn:p> ?o } ORDER BY " +
            (descending ? "DESC(?s)" : "?s") + window;
        var rows = new List<IReadOnlyDictionary<string, RdfTerm>>();
        if (scan)
        {
            int completed = 0;
            await foreach (var page in RdfSparql.ScanAsync(query, database.QueryPageAsync,
                database.ReleaseQueryContinuationAsync, new(PageSize: 1, MaximumSortTemporaryBytes: 65536)))
            {
                rows.AddRange(page.Rows);
                completed += page.Complete ? 1 : 0;
            }
            Assert.AreEqual(1, completed);
            Assert.AreEqual(0, database.RetainedQueryContinuationCount);
        }
        else
        {
            rows.AddRange(((RdfSparqlSelectResult)await database.QuerySparqlAsync(query)).Bindings);
        }
        CollectionAssert.AreEqual(expected.Length == 0 ? Array.Empty<string>() : expected.Split(','),
            rows.Select(row => ((RdfLiteral)row["o"]).LexicalForm).ToArray());
    }

    [TestMethod]
    [DataRow(false, false, false)]
    [DataRow(false, false, true)]
    [DataRow(false, true, false)]
    [DataRow(false, true, true)]
    [DataRow(true, false, false)]
    [DataRow(true, false, true)]
    [DataRow(true, true, false)]
    [DataRow(true, true, true)]
    public async Task Sparql_empty_aggregate_results_distinguish_zero_empty_string_and_error(
        bool grouped, bool scan, bool distinct)
    {
        await using var database = await OpenAsync(CreateDirectory());
        string argument = (distinct ? "DISTINCT " : "") + "?v";
        string query = "SELECT (COUNT(*) AS ?rows) (COUNT(" + argument + ") AS ?count) " +
            "(SUM(" + argument + ") AS ?sum) (AVG(" + argument + ") AS ?avg) " +
            "(MIN(" + argument + ") AS ?min) (MAX(" + argument + ") AS ?max) " +
            "(SAMPLE(" + argument + ") AS ?sample) (GROUP_CONCAT(" + argument + ";SEPARATOR=',') AS ?text) " +
            "WHERE { ?s <urn:missing> ?v }" + (grouped ? " GROUP BY ?v" : "");
        var rows = new List<IReadOnlyDictionary<string, RdfTerm>>();
        if (scan)
        {
            int completed = 0;
            await foreach (var page in RdfSparql.ScanAsync(query, database.QueryPageAsync,
                database.ReleaseQueryContinuationAsync, new(PageSize: 1, MaximumSortTemporaryBytes: 65536)))
            {
                rows.AddRange(page.Rows);
                completed += page.Complete ? 1 : 0;
            }
            Assert.AreEqual(1, completed);
            Assert.AreEqual(0, database.RetainedQueryContinuationCount);
        }
        else
        {
            rows.AddRange(((RdfSparqlSelectResult)await database.QuerySparqlAsync(query)).Bindings);
        }
        Assert.AreEqual(grouped ? 0 : 1, rows.Count);
        if (!grouped)
        {
            foreach (string name in new[] { "rows", "count", "sum", "avg" })
            {
                Assert.AreEqual("0", ((RdfLiteral)rows[0][name]).LexicalForm, name);
            }
            Assert.AreEqual("", ((RdfLiteral)rows[0]["text"]).LexicalForm);
            foreach (string name in new[] { "min", "max", "sample" })
            {
                Assert.IsFalse(rows[0].ContainsKey(name), name);
            }
        }
    }
}
