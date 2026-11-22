using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Graph;
using EntityForge.Db.Distributed.Hosting;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [DataTestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph)]
    [DataRow(DatabaseProfileId.Rdf)]
    [DataRow(DatabaseProfileId.Semantic)]
    public async Task Logical_history_protection_has_no_physical_certificate_and_rejects_raw_reads(DatabaseProfileId profile)
    {
        var placement = new Authority();
        await using var node = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "logical-protection"), profile,
            placement.Lease, Routing, placement, Options, timestampClock: ReadClock);
        var cut = await node.AcquireHistoryProtectionAsync(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(20));
        Assert.IsTrue(cut.HistoryProtection);
        Assert.IsNull(cut.SnapshotEvidence);
        Assert.AreEqual(ReadClock.Domain, cut.ClockDomain);
        Assert.AreEqual(1, node.Kernel.TimestampProtectionCount);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => node.ReadCutAsync(cut, new("vertex", Guid.NewGuid())).AsTask());
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => node.ReadCutAsync(cut with
        {
            HistoryProtection = false
        },
            new("timestamp-project", Projection: new("vertex", Guid.NewGuid()), ReadTime: TestQueryTime(cut))).AsTask());
        await node.CompleteCutAsync(cut);
        Assert.AreEqual(0, node.Kernel.TimestampProtectionCount);
    }

    [TestMethod]
    public async Task Healthy_generation_serves_during_single_flight_protection_refresh()
    {
        var placement = new Authority();
        await using var node = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "refresh"), DatabaseProfileId.PropertyGraph,
            placement.Lease, Routing, placement, Options, timestampClock: ReadClock);
        var peer = new RefreshProtectionPeer(node);
        await using var epoch = new PartitionReadEpoch([peer], Routing, ReadClock);
        async ValueTask<bool> Read(PartitionRemoteRead read, CancellationToken token)
        {
            Assert.AreEqual("null", System.Text.Encoding.UTF8.GetString(await read.ReadAsync("a", new("vertex", Guid.NewGuid()), token)));
            return true;
        }
        await epoch.ExecuteAsync(Read, null, default);
        await Task.Delay(TimeSpan.FromMilliseconds(4100));
        try
        {
            var second = epoch.ExecuteAsync(Read, null, default).AsTask();
            await peer.RefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await second.WaitAsync(TimeSpan.FromMilliseconds(500));
            Assert.AreEqual(2, peer.Acquisitions);
            Assert.IsFalse(peer.ReleaseRefresh.Task.IsCompleted);
            Guid operation = Guid.NewGuid();
            await node.Authority.ExecuteAsync(async () =>
            {
                await node.Kernel.CommitAsync(operation, GraphTransactionCodec.Encode(new(operation,
                    [new CreateVertex(GraphVertexId.New(), [])])));
                return true;
            });
            var minimum = new DatabasePositionVector([new(node.Domain.Identity, node.Kernel.CurrentVersion)]);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
            await Assert.ThrowsAsync<OperationCanceledException>(() => epoch.ExecuteAsync(Read, minimum, cancellation.Token).AsTask());
            Assert.AreEqual(2, peer.Acquisitions, "A cancelled waiter must not create another refresh or cancel owned acquisition.");
        }
        finally
        {
            peer.ReleaseRefresh.TrySetResult();
        }
        await epoch.DisposeAsync();
        Assert.AreEqual(0, node.Kernel.TimestampProtectionCount);
    }

    private sealed class RefreshProtectionPeer(PartitionOwnedNode node) : IPartitionReadParticipant
    {
        internal int Acquisitions;

        internal TaskCompletionSource RefreshStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ReleaseRefresh { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public PartitionExecutionDomain Domain => node.Domain;

        public async ValueTask<PartitionReadCut> AcquireHistoryProtectionAsync(Guid id, DateTimeOffset expires, CancellationToken token = default)
        {
            if (Interlocked.Increment(ref Acquisitions) == 2)
            {
                RefreshStarted.TrySetResult();
                await ReleaseRefresh.Task.WaitAsync(token);
            }
            return await node.AcquireHistoryProtectionAsync(id, expires, token);
        }

        public ValueTask<PartitionReadCut> AcquireCutAsync(Guid id, DateTimeOffset expires, CancellationToken token = default) =>
            throw new AssertFailedException("Timestamp epochs must not acquire frozen cuts.");

        public ValueTask<byte[]> ReadCutAsync(PartitionReadCut cut, PartitionReadQuery query, CancellationToken token = default) => node.ReadCutAsync(cut, query, token);

        public ValueTask CompleteCutAsync(PartitionReadCut cut, CancellationToken token = default) => node.CompleteCutAsync(cut, token);

        public ValueTask AbandonCutAsync(Guid id, CancellationToken token = default) => node.AbandonCutAsync(id, token);
    }
}
