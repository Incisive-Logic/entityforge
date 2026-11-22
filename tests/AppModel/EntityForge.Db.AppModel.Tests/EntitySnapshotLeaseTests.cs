using System.Net;
using System.Net.Http.Json;
using EntityForge.Db.AppModel.Queries;
using EntityForge.Db.Client;
using EntityForge.Query.Projection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EntityForge.Db.AppModel.Tests;

[TestClass]
public sealed class EntitySnapshotLeaseTests
{
    private static readonly SemanticQueryDocument Query = new(new("urn:entity", "Entity"), [], []);

    [TestMethod]
    public async Task Session_retains_snapshot_until_results_are_cleared()
    {
        var executor = new Executor();
        using var session = new EntityQuerySession();
        await session.ExecuteAsync(executor, Query);
        Assert.AreEqual(42L, executor.ExecutedSnapshot);
        Assert.IsFalse(executor.Lease.Disposed);
        session.EditDraft(Query with
        {
            First = 2
        });
        Assert.IsFalse(executor.Lease.Disposed);
        session.Clear();
        Assert.IsTrue(executor.Lease.Disposed);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Failed_or_invalidated_execution_releases_its_lease(bool invalidate)
    {
        var executor = new Executor { Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var session = new EntityQuerySession();
        var pending = session.ExecuteAsync(executor, Query).AsTask();
        if (invalidate)
        {
            session.Clear();
        }

        executor.Pending.SetException(new InvalidOperationException("Query failed"));
        await pending;
        Assert.IsTrue(executor.Lease.Disposed);
        Assert.IsNull(session.Result);
    }

    [TestMethod]
    public async Task Http_lease_acquires_snapshot_and_releases_on_disposal()
    {
        var handler = new Handler();
        using var http = new HttpClient(handler) { BaseAddress = new("https://example.test/") };
        var lease = await new HttpSemanticQuerySnapshotProvider(http).AcquireAsync();
        Assert.AreEqual(42L, lease.SnapshotVersion);
        lease.Dispose();
        lease.Dispose();
        await handler.Released.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class Handler : HttpMessageHandler
    {
        private readonly Guid _id = Guid.NewGuid();

        public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method == HttpMethod.Post)
            {
                StringAssert.Contains(await request.Content!.ReadAsStringAsync(token), "durationSeconds");
                return new(HttpStatusCode.OK)
                {
                    Content = new StringContent($$"""
                    {"leaseId":"{{_id}}","snapshotVersion":42,"expiresAtUtc":"2030-01-01T00:00:00Z","revision":1}
                    """)
                };
            }
            Assert.AreEqual(HttpMethod.Delete, request.Method);
            StringAssert.EndsWith(request.RequestUri!.AbsolutePath, _id.ToString());
            Released.TrySetResult();
            return new(HttpStatusCode.NoContent);
        }
    }

    private sealed class Lease : ISemanticQuerySnapshotLease
    {
        public long SnapshotVersion => 42;

        public bool Disposed;

        public void Dispose() => Disposed = true;
    }

    private sealed class Executor : IRetainedEntityQueryExecutor
    {
        public Lease Lease = new();
        public long ExecutedSnapshot;
        public TaskCompletionSource<EntityProjectionQueryResult>? Pending;

        public ValueTask<ISemanticQuerySnapshotLease?> AcquireSnapshotAsync(CancellationToken token = default) => ValueTask.FromResult<ISemanticQuerySnapshotLease?>(Lease);

        public ValueTask<EntityProjectionQueryResult> ExecuteAtSnapshotAsync(SemanticQueryDocument query, long snapshotVersion, CancellationToken token = default)
        {
            ExecutedSnapshot = snapshotVersion;
            return Pending is null ? ValueTask.FromResult(new EntityProjectionQueryResult(new EntityProjection(Guid.NewGuid(), []), new(42))) : new(Pending.Task);
        }

        public ValueTask<EntityProjectionQueryResult> ExecuteAsync(SemanticQueryDocument query, CancellationToken token = default) => throw new InvalidOperationException("Must use leased snapshot");
    }
}
