using System.Globalization;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Sparql_count_scan_preserves_term_identity_with_unbound_group_columns(bool unbound)
    {
        RdfTerm[] terms = [new RdfLiteral("01", "http://www.w3.org/2001/XMLSchema#integer"),
            new RdfLiteral("1", "http://www.w3.org/2001/XMLSchema#integer"),
            new RdfLiteral("1.0", "http://www.w3.org/2001/XMLSchema#decimal"), new RdfLiteral("1"),
            new RdfBlankNode("one"), new RdfIri("urn:one"), new RdfLiteral("one", language: "EN"),
            new RdfLiteral("one"), new RdfTripleTerm(new RdfIri("urn:s"), new("urn:p"), new RdfLiteral("one"))];
        int position = 0;
        ValueTask<RdfQueryPage> Read(RdfQuadPattern pattern, int size, string? cursor, CancellationToken token)
        {
            int count = Math.Min(size, terms.Length * 3 - position);
            var quads = Enumerable.Range(position, count).Select(index => new RdfQuad(new RdfIri("urn:row:" + index),
                new("urn:p"), terms[index % terms.Length])).ToArray();
            position += count;
            return ValueTask.FromResult(new RdfQueryPage(1, quads, position < terms.Length * 3 ? "next" : null, null));
        }
        var seen = new HashSet<RdfTerm>();
        int completions = 0;
        await foreach (var page in RdfSparql.ScanAsync(
            "SELECT ?o (COUNT(*) AS ?c) WHERE { ?s <urn:p> ?o } GROUP BY ?o" + (unbound ? " ?missing" : ""),
            Read, (_, _) => ValueTask.FromResult(true), new(PageSize: 2, MaximumSortTemporaryBytes: 65536)))
        {
            foreach (var row in page.Rows)
            {
                Assert.IsTrue(seen.Add(row["o"]));
                Assert.AreEqual("3", ((RdfLiteral)row["c"]).LexicalForm);
            }
            completions += page.Complete ? 1 : 0;
        }
        CollectionAssert.AreEquivalent(terms, seen.ToArray());
        Assert.AreEqual(1, completions);
    }

    [TestMethod]
    public async Task Sparql_distinct_aggregate_scan_preserves_first_occurrence_across_runs()
    {
        int position = 0;
        string[] values = ["b", "a", "c"];
        ValueTask<RdfQueryPage> Read(RdfQuadPattern pattern, int size, string? cursor, CancellationToken token)
        {
            int count = Math.Min(size, 5000 - position);
            var quads = Enumerable.Range(position, count).Select(i => new RdfQuad(new RdfIri("urn:s:" + i),
                new("urn:p"), new RdfLiteral(values[i % 3]))).ToArray();
            position += count;
            return ValueTask.FromResult(new RdfQueryPage(1, quads, position < 5000 ? "next" : null, null));
        }
        int rows = 0;
        await foreach (var page in RdfSparql.ScanAsync(
            "SELECT (SAMPLE(DISTINCT ?o) AS ?sample) (GROUP_CONCAT(DISTINCT ?o; SEPARATOR=\",\") AS ?text) (COUNT(*) AS ?all) { ?s <urn:p> ?o }",
            Read, (cursor, token) => ValueTask.FromResult(true), new(MaximumSortTemporaryBytes: 32 * 1024 * 1024)))
        {
            foreach (var row in page.Rows)
            {
                // This deterministic adapter checks the implementation's stable-input policy,
                // rather than requiring an order from SPARQL across different query plans.
                Assert.AreEqual("b", ((RdfLiteral)row["sample"]).LexicalForm);
                Assert.AreEqual("b,a,c", ((RdfLiteral)row["text"]).LexicalForm);
                Assert.AreEqual("5000", ((RdfLiteral)row["all"]).LexicalForm);
                rows++;
            }
        }
        Assert.AreEqual(1, rows);
    }

    [TestMethod]
    public async Task Sparql_distinct_aggregate_scan_uses_term_identity_and_visible_solutions()
    {
        await using var database = await OpenAsync(CreateDirectory());
        RdfTerm[] terms = [new RdfLiteral("01", "http://www.w3.org/2001/XMLSchema#integer"),
            new RdfLiteral("1", "http://www.w3.org/2001/XMLSchema#integer"),
            new RdfLiteral("1.0", "http://www.w3.org/2001/XMLSchema#decimal"), new RdfLiteral("1")];
        await database.BulkLoadAsync(Guid.NewGuid(), Enumerable.Range(0, 8).Select(i =>
            new RdfQuad(new RdfIri("urn:s:" + i), new("urn:p"), terms[i % 4])).ToArray());
        int rows = 0;
        await foreach (var page in RdfSparql.ScanAsync(
            "SELECT (COUNT(DISTINCT ?o) AS ?terms) (COUNT(DISTINCT *) AS ?solutions) (COUNT(DISTINCT ?missing) AS ?missingCount) { ?s <urn:p> ?o }",
            database.QueryPageAsync, database.ReleaseQueryContinuationAsync, new(MaximumSortTemporaryBytes: 1024 * 1024)))
        {
            foreach (var row in page.Rows)
            {
                Assert.AreEqual("4", ((RdfLiteral)row["terms"]).LexicalForm);
                Assert.AreEqual("8", ((RdfLiteral)row["solutions"]).LexicalForm);
                Assert.AreEqual("0", ((RdfLiteral)row["missingCount"]).LexicalForm);
                rows++;
            }
        }
        Assert.AreEqual(1, rows);
    }

    [TestMethod]
    public async Task Sparql_distinct_aggregate_scan_preserves_mixed_bags_at_scale()
    {
        await using var database = await OpenAsync(CreateDirectory());
        await database.BulkLoadAsync(Guid.NewGuid(), Enumerable.Range(0, 100_000).Select(i =>
            new RdfQuad(new RdfIri("urn:s:" + i), new("urn:p"),
                new RdfLiteral((i % 20000).ToString(CultureInfo.InvariantCulture), "http://www.w3.org/2001/XMLSchema#integer"))).ToArray());
        int rows = 0, completed = 0;
        await foreach (var page in RdfSparql.ScanAsync(
            "SELECT (COUNT(*) AS ?all) (COUNT(DISTINCT ?o) AS ?unique) (SUM(?o) AS ?total) (SUM(DISTINCT ?o) AS ?distinctTotal) { ?s <urn:p> ?o }",
            database.QueryPageAsync, database.ReleaseQueryContinuationAsync, new(MaximumSortTemporaryBytes: 512 * 1024 * 1024)))
        {
            foreach (var row in page.Rows)
            {
                Assert.AreEqual("100000", ((RdfLiteral)row["all"]).LexicalForm);
                Assert.AreEqual("20000", ((RdfLiteral)row["unique"]).LexicalForm);
                Assert.AreEqual("999950000", ((RdfLiteral)row["total"]).LexicalForm);
                Assert.AreEqual("199990000", ((RdfLiteral)row["distinctTotal"]).LexicalForm);
                rows++;
            }
            completed += page.Complete ? 1 : 0;
        }
        Assert.AreEqual(1, rows);
        Assert.AreEqual(1, completed);
        Assert.AreEqual(0, database.RetainedQueryContinuationCount);
    }

    [TestMethod]
    [DataRow("SELECT (SUM(IF(EXISTS { ?s <urn:q> ?x }, 1, 0)) AS ?c) { ?s <urn:p> ?o }")]
    public async Task Sparql_aggregate_scan_rejects_unbounded_or_nonlocal_arguments_before_storage(string query)
    {
        ValueTask<RdfQueryPage> Read(RdfQuadPattern pattern, int size, string? cursor, CancellationToken cancellation) =>
            throw new AssertFailedException("Unsupported aggregate accessed storage.");
        await Assert.ThrowsExactlyAsync<NotSupportedException>(async () =>
        {
            await foreach (var page in RdfSparql.ScanAsync(query, Read, (cursor, token) => ValueTask.FromResult(true),
                new(MaximumSortTemporaryBytes: 65536)))
            {
                Assert.Fail("Unsupported aggregate returned a page.");
            }
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Sparql_aggregate_scan_groups_100000_rows_before_having_order_and_window(bool countOnly)
    {
        await using var database = await OpenAsync(CreateDirectory());
        await database.BulkLoadAsync(Guid.NewGuid(), Enumerable.Range(0, 100_000).Select(i =>
            new RdfQuad(new RdfIri("urn:s:" + i), new("urn:p"),
                new RdfLiteral((i % 20000).ToString(CultureInfo.InvariantCulture), "http://www.w3.org/2001/XMLSchema#integer"))).ToArray());
        int count = 0, completed = 0;
        await foreach (var page in RdfSparql.ScanAsync(
            "SELECT ?o (COUNT(*) AS ?count)" + (countOnly ? "" : " (SUM(?o) AS ?sum)") +
            " { ?s <urn:p> ?o } GROUP BY ?o HAVING(COUNT(*) = 5) ORDER BY DESC(?o) OFFSET 1000 LIMIT 18000",
            database.QueryPageAsync, database.ReleaseQueryContinuationAsync, new(MaximumSortTemporaryBytes: 256 * 1024 * 1024)))
        {
            foreach (var row in page.Rows)
            {
                int expected = 18999 - count++;
                Assert.AreEqual(expected.ToString(CultureInfo.InvariantCulture), ((RdfLiteral)row["o"]).LexicalForm);
                Assert.AreEqual("5", ((RdfLiteral)row["count"]).LexicalForm);
                if (!countOnly)
                {
                    Assert.AreEqual((5 * expected).ToString(CultureInfo.InvariantCulture), ((RdfLiteral)row["sum"]).LexicalForm);
                }
            }
            completed += page.Complete ? 1 : 0;
        }
        Assert.AreEqual(18000, count);
        Assert.AreEqual(1, completed);
        Assert.AreEqual(0, database.RetainedQueryContinuationCount);
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task Sparql_aggregate_scan_handles_empty_implicit_and_explicit_groups(bool grouped, bool countOnly)
    {
        await using var database = await OpenAsync(CreateDirectory());
        int rows = 0, completed = 0;
        await foreach (var page in RdfSparql.ScanAsync("SELECT (COUNT(*) AS ?count)" +
            (countOnly ? "" : " (SUM(?o) AS ?sum)") + " { ?s <urn:p> ?o }" +
            (grouped ? " GROUP BY ?o" : ""), database.QueryPageAsync, database.ReleaseQueryContinuationAsync,
            new(MaximumSortTemporaryBytes: 65536)))
        {
            foreach (var row in page.Rows)
            {
                Assert.AreEqual("0", ((RdfLiteral)row["count"]).LexicalForm);
                if (!countOnly)
                {
                    Assert.AreEqual("0", ((RdfLiteral)row["sum"]).LexicalForm);
                }
                rows++;
            }
            completed += page.Complete ? 1 : 0;
        }
        Assert.AreEqual(grouped ? 0 : 1, rows);
        Assert.AreEqual(1, completed);
    }

    [TestMethod]
    [DataRow("SELECT (COUNT(?o) AS ?c) (SUM(?o) AS ?sum) (AVG(?o) AS ?avg) (MIN(?o) AS ?min) (MAX(?o) AS ?max) { ?s <urn:p> ?o }")]
    [DataRow("SELECT (COUNT(DISTINCT *) AS ?c) (SUM(DISTINCT ?o) AS ?sum) (AVG(DISTINCT ?o) AS ?avg) { ?s <urn:p> ?o }")]
    [DataRow("SELECT ?o (COUNT(DISTINCT ?s) AS ?c) (SUM(DISTINCT ?o) AS ?sum) { ?s <urn:p> ?o } GROUP BY ?o ORDER BY ?o")]
    [DataRow("SELECT ?g (COUNT(*) AS ?c) { ?s <urn:p> ?o FILTER(?o > 0) } GROUP BY (?o + 1 AS ?g) ORDER BY ?g")]
    [DataRow("SELECT DISTINCT (COUNT(*) + 1 AS ?c) { ?s <urn:p> ?o } GROUP BY ?o HAVING(COUNT(*) > 0) ORDER BY ?c")]
    [DataRow("SELECT (GROUP_CONCAT(STR(?o); SEPARATOR=\",\") AS ?text) { ?s <urn:p> ?o }")]
    public async Task Sparql_aggregate_scan_reuses_ordinary_aggregate_semantics(string query)
    {
        await using var database = await OpenAsync(CreateDirectory());
        var quads = Enumerable.Range(0, 15).Select(i => new RdfQuad(new RdfIri("urn:s:" + i), new("urn:p"),
            new RdfLiteral((i % 3).ToString(CultureInfo.InvariantCulture), "http://www.w3.org/2001/XMLSchema#integer"))).ToArray();
        await database.BulkLoadAsync(Guid.NewGuid(), quads);
        var expected = (RdfSparqlSelectResult)RdfSparql.ExecuteRelation(quads, query, default);
        var actual = new List<IReadOnlyDictionary<string, RdfTerm>>();
        await foreach (var page in RdfSparql.ScanAsync(query, database.QueryPageAsync, database.ReleaseQueryContinuationAsync,
            new(PageSize: 1, MaximumSortTemporaryBytes: 65536)))
        {
            actual.AddRange(page.Rows);
        }
        Assert.AreEqual(expected.Bindings.Count, actual.Count);
        for (int index = 0; index < actual.Count; index++)
        {
            if (actual[index].TryGetValue("text", out var text))
            {
                // GROUP_CONCAT does not promise the same input order across different plans.
                CollectionAssert.AreEquivalent(Enumerable.Range(0, 15).Select(i => (i % 3).ToString()).ToArray(),
                    ((RdfLiteral)text).LexicalForm.Split(','));
            }
            else
            {
                CollectionAssert.AreEquivalent(expected.Bindings[index].ToArray(), actual[index].ToArray());
            }
        }
    }
}
