using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    public async Task Sparql_scan_rejects_oversized_adapter_pages_and_releases_the_cursor()
    {
        bool released = false;
        var quad = new RdfQuad(new RdfIri("urn:s"), new("urn:p"), new RdfLiteral("o"));
        ValueTask<RdfQueryPage> Read(RdfQuadPattern pattern, int size, string? cursor, CancellationToken token) =>
            ValueTask.FromResult(new RdfQueryPage(1, [quad, quad], "oversized", null));
        ValueTask<bool> Release(string cursor, CancellationToken token)
        {
            Assert.AreEqual("oversized", cursor);
            released = true;
            return ValueTask.FromResult(true);
        }
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
        {
            await foreach (var page in RdfSparql.ScanAsync("SELECT * WHERE { ?s ?p ?o }", Read, Release, new(PageSize: 1)))
            {
                Assert.Fail("An oversized adapter page escaped the bounded scan contract.");
            }
        });
        Assert.IsTrue(released);
    }

    [TestMethod]
    [DataRow("CLEAR ALL")]
    [DataRow("DELETE WHERE { ?s ?p ?o }")]
    [DataRow("CLEAR NAMED; CLEAR DEFAULT")]
    public async Task Sparql_native_update_planning_observes_caller_cancellation(string update)
    {
        await using var database = await OpenAsync(CreateDirectory());
        await SeedRetainedQueryAsync(database);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await RdfSparqlUpdate.ParseWhereAsync(database, update, "cancelled", null, cancellation.Token));
        Assert.AreEqual(4, database.Find(new()).Count);
    }

    [TestMethod]
    public async Task Sparql_scan_slow_consumer_observes_expiry_without_false_completion()
    {
        await using var database = await OpenAsync(CreateDirectory(), new()
        {
            ContinuationIdleTimeout = TimeSpan.FromMilliseconds(50)
        });
        await SeedRetainedQueryAsync(database);
        int completed = 0;
        await Assert.ThrowsExactlyAsync<EntityForge.Db.Rdf.RdfQueryContinuationException>(async () =>
        {
            await foreach (var page in RdfSparql.ScanAsync("SELECT * WHERE { ?s ?p ?o }",
                database.QueryPageAsync, database.ReleaseQueryContinuationAsync, new(PageSize: 1)))
            {
                completed += page.Complete ? 1 : 0;
                await Task.Delay(100);
            }
        });
        Assert.AreEqual(0, completed);
        Assert.AreEqual(0, database.RetainedQueryContinuationCount);
        Assert.AreEqual(4, (await database.QueryPageAsync(new(), 10)).Quads.Count);
    }

    [TestMethod]
    public async Task Sparql_scan_rejects_a_source_snapshot_change_and_releases_its_cursor()
    {
        int calls = 0;
        string? released = null;
        ValueTask<RdfQueryPage> Read(RdfQuadPattern pattern, int size, string? continuation, CancellationToken token) =>
            ValueTask.FromResult(new RdfQueryPage(++calls, [new(new RdfIri("urn:s"), new("urn:p"), new RdfLiteral("o"))], "cursor", null));
        ValueTask<bool> Release(string cursor, CancellationToken token)
        {
            released = cursor;
            return ValueTask.FromResult(true);
        }
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
        {
            await foreach (var page in RdfSparql.ScanAsync("SELECT * WHERE { ?s ?p ?o }", Read, Release, new()))
            {
                Assert.IsFalse(page.Complete);
            }
        });
        Assert.AreEqual("cursor", released);
        Assert.AreEqual(2, calls);
    }

    [TestMethod]
    public async Task Sparql_scan_explicit_zero_limit_completes_without_returning_rows()
    {
        await using var database = await OpenAsync(CreateDirectory());
        await SeedRetainedQueryAsync(database);
        int pages = 0;
        await foreach (var page in RdfSparql.ScanAsync("SELECT ?s WHERE { ?s ?p ?o } LIMIT 0",
            database.QueryPageAsync, database.ReleaseQueryContinuationAsync, new(PageSize: 1)))
        {
            Assert.AreEqual(0, page.Rows.Count);
            Assert.IsTrue(page.Complete);
            pages++;
        }
        Assert.AreEqual(1, pages);
        Assert.AreEqual(0, database.RetainedQueryContinuationCount);
    }

    [TestMethod]
    [DataRow(0, 100_000, false)]
    [DataRow(10_000, 90_000, true)]
    public async Task Sparql_scan_returns_large_duplicate_projection_rows_at_one_snapshot_during_writes(int offset, int expected, bool explicitWindow)
    {
        await using var database = await OpenAsync(CreateDirectory());
        var quads = Enumerable.Range(0, 100_000).Select(i => new RdfQuad(new RdfIri("urn:scan:" + i),
            new("urn:p"), new RdfLiteral("same"))).ToArray();
        var version = await database.BulkLoadAsync(Guid.NewGuid(), quads);
        int rows = 0, completed = 0;
        bool wrote = false;
        string query = "SELECT ?o WHERE { ?s <urn:p> ?o }" + (explicitWindow ? $" LIMIT {expected} OFFSET {offset}" : "");
        await foreach (var page in RdfSparql.ScanAsync(query,
            database.QueryPageAsync, database.ReleaseQueryContinuationAsync, new(PageSize: 512)))
        {
            Assert.AreEqual(version.Value, page.SnapshotVersion);
            Assert.IsTrue(page.Rows.Count <= 512);
            Assert.IsTrue(page.Rows.All(row => Equals(row["o"], new RdfLiteral("same"))));
            CollectionAssert.AreEqual(new[] { "o" }, page.Variables.ToArray());
            if (!wrote)
            {
                wrote = true;
                await database.CommitAsync(new(Guid.NewGuid(), [new RetractRdfQuad(quads[^1]),
                    new AssertRdfQuad(new(new RdfIri("urn:new"), new("urn:p"), new RdfLiteral("new")))]));
            }
            rows += page.Rows.Count;
            completed += page.Complete ? 1 : 0;
        }
        Assert.AreEqual(expected, rows);
        Assert.AreEqual(1, completed);
        Assert.AreEqual(0, database.RetainedQueryContinuationCount);
    }

    [TestMethod]
    public async Task Sparql_scan_limit_offset_and_early_disposal_release_snapshots()
    {
        await using var database = await OpenAsync(CreateDirectory());
        await SeedRetainedQueryAsync(database);
        int count = 0;
        await foreach (var page in RdfSparql.ScanAsync("SELECT * WHERE { ?s ?p ?o } LIMIT 2 OFFSET 1",
            database.QueryPageAsync, database.ReleaseQueryContinuationAsync, new(PageSize: 1)))
        {
            count += page.Rows.Count;
        }
        Assert.AreEqual(2, count);
        Assert.AreEqual(0, database.RetainedQueryContinuationCount);
        await foreach (var page in RdfSparql.ScanAsync("SELECT ?s WHERE { ?s ?p ?o }",
            database.QueryPageAsync, database.ReleaseQueryContinuationAsync, new(PageSize: 1)))
        {
            Assert.IsFalse(page.Complete);
            break;
        }
        Assert.AreEqual(0, database.RetainedQueryContinuationCount);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task Sparql_scan_budget_failure_never_reports_completion_and_releases(bool rowBudget)
    {
        await using var database = await OpenAsync(CreateDirectory());
        await SeedRetainedQueryAsync(database);
        int completed = 0;
        var budget = rowBudget ? new RdfSparqlScanBudget(MaximumRows: 2, PageSize: 1) :
            new RdfSparqlScanBudget(MaximumExaminedQuads: 2, PageSize: 1);
        await Assert.ThrowsExactlyAsync<RdfSparqlWorkLimitException>(async () =>
        {
            await foreach (var page in RdfSparql.ScanAsync("SELECT ?o WHERE { ?s ?p ?o }",
                database.QueryPageAsync, database.ReleaseQueryContinuationAsync, budget))
            {
                completed += page.Complete ? 1 : 0;
            }
        });
        Assert.AreEqual(0, completed);
        Assert.AreEqual(0, database.RetainedQueryContinuationCount);
        Assert.AreEqual(4, (await database.QueryPageAsync(new(), 10)).Quads.Count);
    }

    [TestMethod]
    public async Task Sparql_scan_cancellation_releases_snapshot_after_a_page()
    {
        await using var database = await OpenAsync(CreateDirectory());
        await SeedRetainedQueryAsync(database);
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var page in RdfSparql.ScanAsync("SELECT ?s WHERE { ?s ?p ?o }",
                database.QueryPageAsync, database.ReleaseQueryContinuationAsync, new(PageSize: 1), cancellation.Token))
            {
                cancellation.Cancel();
            }
        });
        Assert.AreEqual(0, database.RetainedQueryContinuationCount);
    }

    [TestMethod]
    [DataRow("SELECT DISTINCT ?o WHERE { ?s ?p ?o }")]
    [DataRow("SELECT ?o WHERE { ?s ?p ?o } ORDER BY ?o")]
    [DataRow("SELECT ?o WHERE { ?s ?p ?o OPTIONAL { ?s <urn:q> ?v } }")]
    [DataRow("SELECT (COUNT(*) AS ?n) WHERE { ?s ?p ?o }")]
    public async Task Sparql_scan_refuses_unsupported_plans_before_reading(string query)
    {
        ValueTask<RdfQueryPage> Read(RdfQuadPattern pattern, int size, string? continuation, CancellationToken token) =>
            throw new AssertFailedException("An unsupported plan accessed storage.");
        await Assert.ThrowsExactlyAsync<NotSupportedException>(async () =>
        {
            await foreach (var page in RdfSparql.ScanAsync(query, Read, (_, _) => ValueTask.FromResult(false), new()))
            {
                Assert.Fail("An unsupported plan returned a page.");
            }
        });
    }

    [TestMethod]
    public async Task Sparql_scan_named_graph_and_repeated_variables_use_existing_binding_semantics()
    {
        await using var database = await OpenAsync(CreateDirectory());
        var same = new RdfIri("urn:same");
        await database.CommitAsync(new(Guid.NewGuid(), [
            new AssertRdfQuad(new(same, new("urn:p"), same, new RdfIri("urn:g"))),
            new AssertRdfQuad(new(same, new("urn:p"), new RdfLiteral("other"), new RdfIri("urn:g"))),
            new AssertRdfQuad(new(same, new("urn:p"), same))]));
        var rows = new List<IReadOnlyDictionary<string, RdfTerm>>();
        await foreach (var page in RdfSparql.ScanAsync("SELECT * WHERE { GRAPH ?g { ?s <urn:p> ?s } }",
            database.QueryPageAsync, database.ReleaseQueryContinuationAsync, new(PageSize: 1)))
        {
            rows.AddRange(page.Rows);
        }
        Assert.AreEqual(1, rows.Count);
        Assert.AreEqual(same, rows[0]["s"]);
        Assert.AreEqual(new RdfIri("urn:g"), rows[0]["g"]);
    }
}
