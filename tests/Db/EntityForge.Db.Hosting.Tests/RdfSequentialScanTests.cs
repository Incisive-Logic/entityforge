using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    public async Task Multi_pattern_scan_interleaves_independent_cursors_on_one_snapshot()
    {
        await using var database = await OpenAsync(CreateDirectory());
        var quads = Enumerable.Range(0, 5).Select(index => new RdfQuad(new RdfIri("urn:multi:" + index),
            new("urn:p"), new RdfLiteral(index.ToString()))).ToArray();
        await database.CommitAsync(new(Guid.NewGuid(), quads.Select(quad => (RdfMutation)new AssertRdfQuad(quad)).ToArray()));
        await using (var source = new RdfQueryScanReader(database, multiplePatterns: true))
        {
            var pattern = new RdfQuadPattern(Predicate: new("urn:p"));
            var first = await source.ReadAsync(pattern, 1, null, CancellationToken.None);
            var second = await source.ReadAsync(pattern, 2, null, CancellationToken.None);
            Assert.IsNotNull(first.NextContinuation);
            Assert.IsNotNull(second.NextContinuation);
            Assert.AreNotEqual(first.NextContinuation, second.NextContinuation);
            await database.CommitAsync(new(Guid.NewGuid(), [new RetractRdfQuad(quads[0])]));
            Assert.IsTrue(await source.ReleaseAsync(first.NextContinuation, CancellationToken.None));
            Assert.IsFalse(await source.ReleaseAsync(first.NextContinuation, CancellationToken.None));
            var actual = second.Quads.ToList();
            while (second.NextContinuation is { } cursor)
            {
                second = await source.ReadAsync(pattern, 2, cursor, CancellationToken.None);
                Assert.AreEqual(first.SnapshotVersion, second.SnapshotVersion);
                actual.AddRange(second.Quads);
            }
            CollectionAssert.AreEquivalent(quads, actual.ToArray());
            var later = await source.ReadAsync(new(Subject: quads[0].Subject), 2, null, CancellationToken.None);
            CollectionAssert.AreEqual(new[] { quads[0] }, later.Quads.ToArray());
            Assert.AreEqual(first.SnapshotVersion, later.SnapshotVersion);
        }
        await database.CreateCheckpointAsync();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Multi_pattern_scan_disposes_all_readers_after_cancellation_or_early_exit(bool cancel)
    {
        await using var database = await OpenAsync(CreateDirectory());
        await database.CommitAsync(new(Guid.NewGuid(), Enumerable.Range(0, 4).Select(index =>
            (RdfMutation)new AssertRdfQuad(new(new RdfIri("urn:multi:" + index), new("urn:p"), new RdfLiteral("v")))).ToArray()));
        using var deadline = new CancellationTokenSource();
        await using (var source = new RdfQueryScanReader(database, multiplePatterns: true))
        {
            var first = await source.ReadAsync(new(), 1, null, deadline.Token);
            await source.ReadAsync(new(Predicate: new("urn:p")), 1, null, deadline.Token);
            if (cancel)
            {
                deadline.Cancel();
                await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
                    await source.ReadAsync(new(), 1, first.NextContinuation, deadline.Token));
            }
        }
        await database.CreateCheckpointAsync();
    }

    [TestMethod]
    public async Task Multi_pattern_scan_bounds_active_readers_and_checks_cursor_identity()
    {
        await using var database = await OpenAsync(CreateDirectory());
        await database.CommitAsync(new(Guid.NewGuid(), [new AssertRdfQuad(new(new RdfIri("urn:s"), new("urn:p"), new RdfLiteral("v")))]));
        await using (var source = new RdfQueryScanReader(database, multiplePatterns: true))
        {
            var first = await source.ReadAsync(new(), 1, null, CancellationToken.None);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
                await source.ReadAsync(new(), 2, first.NextContinuation, CancellationToken.None));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
                await source.ReadAsync(new(), 1, "another-request", CancellationToken.None));
            for (int index = 1; index < 64; index++)
            {
                await source.ReadAsync(new(), 1, null, CancellationToken.None);
            }
            await Assert.ThrowsExactlyAsync<RdfSparqlWorkLimitException>(async () =>
                await source.ReadAsync(new(), 1, null, CancellationToken.None));
        }
        await database.CreateCheckpointAsync();
    }

    [TestMethod]
    public async Task Scan_session_pattern_readers_share_snapshot_across_writes_and_completion()
    {
        await using var database = await OpenAsync(CreateDirectory());
        var subject = new RdfIri("urn:join:source");
        var route = new RdfQuad(subject, new("urn:route"), new RdfIri("urn:hub"));
        var value = new RdfQuad(subject, new("urn:key"), new RdfLiteral("old"));
        await database.CommitAsync(new(Guid.NewGuid(), [new AssertRdfQuad(route), new AssertRdfQuad(value)]));
        using (var session = await database.OpenScanSessionAsync(20, CancellationToken.None))
        {
            long? version = null;
            await foreach (var page in session.ReadAsync(new(Predicate: route.Predicate), 1))
            {
                version ??= page.SnapshotVersion;
                Assert.AreEqual(version.Value, page.SnapshotVersion);
            }
            await database.CommitAsync(new(Guid.NewGuid(), [new RetractRdfQuad(value),
                new AssertRdfQuad(new(subject, value.Predicate, new RdfLiteral("new")))]));
            Assert.IsNotNull(version);
            var actual = new List<RdfQuad>();
            await foreach (var page in session.ReadAsync(new(Subject: subject, Predicate: value.Predicate), 1))
            {
                Assert.AreEqual(version.Value, page.SnapshotVersion);
                actual.AddRange(page.Quads);
            }
            CollectionAssert.AreEqual(new[] { value }, actual.ToArray());
            await Assert.ThrowsExactlyAsync<RdfSnapshotRetentionException>(async () => await database.CreateCheckpointAsync());
        }
        await database.CreateCheckpointAsync();
    }

    [TestMethod]
    [DataRow(5, false, false)]
    [DataRow(6, true, false)]
    [DataRow(5, false, true)]
    [DataRow(6, true, true)]
    public async Task Scan_session_work_budget_is_cumulative_across_filtered_pattern_readers(int budget, bool succeeds, bool subjectIndex)
    {
        await using var database = await OpenAsync(CreateDirectory());
        var subject = new RdfIri("urn:join:source");
        await database.CommitAsync(new(Guid.NewGuid(), Enumerable.Range(0, 3).Select(index =>
            (RdfMutation)new AssertRdfQuad(new(subject, new("urn:p"), new RdfLiteral(index.ToString())))).ToArray()));
        using (var session = await database.OpenScanSessionAsync(budget, CancellationToken.None))
        {
            async Task ReadTwice()
            {
                for (int iteration = 0; iteration < 2; iteration++)
                {
                    await foreach (var page in session.ReadAsync(new(Subject: subject, Predicate: new("urn:absent")), 2,
                        usePrimaryIndexes: subjectIndex))
                    {
                        Assert.AreEqual(0, page.Quads.Count);
                    }
                }
            }
            if (succeeds)
            {
                await ReadTwice();
            }
            else
            {
                await Assert.ThrowsExactlyAsync<RdfSparqlWorkLimitException>(ReadTwice);
            }
        }
        await database.CreateCheckpointAsync();
    }

    [TestMethod]
    [DataRow(19, false)]
    [DataRow(20, true)]
    public async Task Sequential_scan_work_budget_counts_candidates_filtered_out_by_the_pattern(int budget, bool succeeds)
    {
        await using var database = await OpenAsync(CreateDirectory());
        var subject = new RdfIri("urn:shared");
        await database.CommitAsync(new(Guid.NewGuid(), Enumerable.Range(0, 20).Select(index =>
            (RdfMutation)new AssertRdfQuad(new(subject, new("urn:present"), new RdfLiteral(index.ToString())))).ToArray()));
        bool completed = false;
        await using (var source = new RdfQueryScanReader(database, budget))
        {
            async Task Read()
            {
                string? continuation = null;
                do
                {
                    // Subject access finds candidates; predicate filtering rejects every one.
                    var page = await source.ReadAsync(new(Subject: subject, Predicate: new("urn:absent")), 3, continuation, CancellationToken.None);
                    Assert.AreEqual(0, page.Quads.Count);
                    continuation = page.NextContinuation;
                } while (continuation is not null);
                completed = true;
            }
            if (succeeds)
            {
                await Read();
            }
            else
            {
                await Assert.ThrowsExactlyAsync<RdfSparqlWorkLimitException>(Read);
            }
        }
        Assert.AreEqual(succeeds, completed);
        await database.CreateCheckpointAsync();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Sequential_scan_preserves_snapshot_and_page_bounds_during_writes(bool indexed)
    {
        await using var database = await OpenAsync(CreateDirectory());
        var quads = Enumerable.Range(0, 31).Select(index => new RdfQuad(new RdfIri("urn:scan:" + index),
            new("urn:p"), new RdfLiteral(index.ToString()))).ToArray();
        await database.CommitAsync(new(Guid.NewGuid(), quads.Select(quad => (RdfMutation)new AssertRdfQuad(quad)).ToArray()));
        await using var source = new RdfQueryScanReader(database);
        var pattern = indexed ? new RdfQuadPattern(Predicate: new("urn:p")) : new RdfQuadPattern();
        var first = await source.ReadAsync(pattern, 3, null, CancellationToken.None);
        var actual = first.Quads.ToList();
        Assert.IsNotNull(first.NextContinuation);
        await Assert.ThrowsExactlyAsync<RdfSnapshotRetentionException>(async () => await database.CreateCheckpointAsync());
        await database.CommitAsync(new(Guid.NewGuid(),
            [new RetractRdfQuad(quads[0]), new AssertRdfQuad(new(new RdfIri("urn:new"), new("urn:p"), new RdfLiteral("new")))]));
        string? continuation = first.NextContinuation;
        while (continuation is not null)
        {
            var page = await source.ReadAsync(pattern, 3, continuation, CancellationToken.None);
            Assert.AreEqual(first.SnapshotVersion, page.SnapshotVersion);
            Assert.IsTrue(page.Quads.Count <= 3);
            actual.AddRange(page.Quads);
            continuation = page.NextContinuation;
        }
        CollectionAssert.AreEquivalent(quads, actual.ToArray());
        await database.CreateCheckpointAsync();
        Assert.AreEqual(0, database.RetainedQueryContinuationCount);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Sequential_scan_releases_snapshot_on_early_stop_and_cancellation(bool cancel)
    {
        await using var database = await OpenAsync(CreateDirectory());
        await database.CommitAsync(new(Guid.NewGuid(), Enumerable.Range(0, 20).Select(index =>
            (RdfMutation)new AssertRdfQuad(new(new RdfIri("urn:s:" + index), new("urn:p"), new RdfLiteral("v")))).ToArray()));
        using var deadline = new CancellationTokenSource();
        await using (var source = new RdfQueryScanReader(database))
        {
            var first = await source.ReadAsync(new(), 1, null, deadline.Token);
            if (cancel)
            {
                deadline.Cancel();
                await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
                    await source.ReadAsync(new(), 1, first.NextContinuation, deadline.Token));
            }
            else
            {
                Assert.IsTrue(await source.ReleaseAsync(first.NextContinuation!, CancellationToken.None));
                Assert.IsFalse(await source.ReleaseAsync(first.NextContinuation!, CancellationToken.None));
            }
        }
        await database.CreateCheckpointAsync();
    }

    [TestMethod]
    public async Task Sequential_scan_emits_complete_empty_page_for_unmatched_pattern()
    {
        await using var database = await OpenAsync(CreateDirectory());
        await using var source = new RdfQueryScanReader(database);
        var page = await source.ReadAsync(new(Predicate: new("urn:missing")), 7, null, CancellationToken.None);
        Assert.AreEqual(0, page.Quads.Count);
        Assert.IsNull(page.NextContinuation);
        await database.CreateCheckpointAsync();
    }
}
