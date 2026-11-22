using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    public async Task RDF_paging_rejects_excess_retention_and_recovers_after_explicit_release()
    {
        await using var database = await OpenAsync(CreateDirectory(), new()
        {
            MaximumActiveContinuations = 2
        });
        await SeedRetainedQueryAsync(database);
        var first = await database.QueryPageAsync(new(), 1);
        var second = await database.QueryPageAsync(new(), 1);
        Assert.AreEqual(2, database.RetainedQueryContinuationCount);
        await Assert.ThrowsExactlyAsync<RdfQueryCapacityException>(async () => await database.QueryPageAsync(new(), 1));
        Assert.IsTrue(await database.ReleaseQueryContinuationAsync(first.NextContinuation!));
        Assert.IsFalse(await database.ReleaseQueryContinuationAsync(first.NextContinuation!));
        var replacement = await database.QueryPageAsync(new(), 1);
        Assert.AreEqual(2, database.RetainedQueryContinuationCount);
        await Assert.ThrowsExactlyAsync<RdfQueryContinuationException>(async () =>
            await database.QueryPageAsync(new(), 1, first.NextContinuation));
        Assert.IsTrue(await database.ReleaseQueryContinuationAsync(second.NextContinuation!));
        Assert.IsTrue(await database.ReleaseQueryContinuationAsync(replacement.NextContinuation!));
        Assert.AreEqual(0, database.RetainedQueryContinuationCount);
    }

    [TestMethod]
    public async Task RDF_paging_concurrent_admission_cannot_oversubscribe_snapshot_capacity()
    {
        await using var database = await OpenAsync(CreateDirectory(), new()
        {
            MaximumActiveContinuations = 3
        });
        await SeedRetainedQueryAsync(database);
        async Task<string?> Start()
        {
            try
            {
                return (await database.QueryPageAsync(new(), 1)).NextContinuation;
            }
            catch (RdfQueryCapacityException)
            {
                return null;
            }
        }
        var attempts = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Start()));
        Assert.AreEqual(3, attempts.Count(token => token is not null));
        Assert.AreEqual(3, database.RetainedQueryContinuationCount);
        foreach (string token in attempts.OfType<string>())
        {
            Assert.IsTrue(await database.ReleaseQueryContinuationAsync(token));
        }
        Assert.AreEqual(0, database.RetainedQueryContinuationCount);
    }

    [TestMethod]
    public async Task RDF_paging_expiry_releases_abandoned_reads_without_a_followup_query()
    {
        await using var database = await OpenAsync(CreateDirectory(), new()
        {
            ContinuationIdleTimeout = TimeSpan.FromMilliseconds(50)
        });
        await SeedRetainedQueryAsync(database);
        var page = await database.QueryPageAsync(new(), 1);
        Assert.IsNotNull(page.NextContinuation);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (database.RetainedQueryContinuationCount != 0 && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
        Assert.AreEqual(0, database.RetainedQueryContinuationCount, "The maintenance loop must expire an idle lease without another request.");
        await Assert.ThrowsExactlyAsync<RdfQueryContinuationException>(async () =>
            await database.QueryPageAsync(new(), 1, page.NextContinuation));
    }

    [TestMethod]
    public async Task RDF_paging_completion_and_shutdown_release_retained_snapshots()
    {
        var database = await OpenAsync(CreateDirectory(), new()
        {
            MaximumActiveContinuations = 1
        });
        try
        {
            await SeedRetainedQueryAsync(database);
            var page = await database.QueryPageAsync(new(), 1);
            while (page.NextContinuation is { } token)
            {
                page = await database.QueryPageAsync(new(), 1, token);
            }
            Assert.AreEqual(0, database.RetainedQueryContinuationCount);
            var abandoned = await database.QueryPageAsync(new(), 1);
            await database.DisposeAsync();
            Assert.AreEqual(0, database.RetainedQueryContinuationCount);
            Assert.IsFalse(await database.ReleaseQueryContinuationAsync(abandoned.NextContinuation!));
            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(async () => await database.QueryPageAsync(new(), 1));
        }
        finally
        {
            await database.DisposeAsync();
        }
    }

    private static async Task SeedRetainedQueryAsync(RdfDatabaseEngine database)
    {
        await database.CommitAsync(new(Guid.NewGuid(), Enumerable.Range(0, 4).Select(index =>
            (RdfMutation)new AssertRdfQuad(new(new RdfIri("urn:retention:" + index), new("urn:p"), new RdfLiteral("value")))).ToArray()));
    }
}
