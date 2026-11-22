using System.Diagnostics;
using EntityForge.Db.Durability;
using EntityForge.Db.Kernel;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [TestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph)]
    [DataRow(DatabaseProfileId.Rdf)]
    [DataRow(DatabaseProfileId.Semantic)]
    public async Task Timestamp_native_admission_reuses_immutable_views_without_a_shared_worker(DatabaseProfileId profile)
    {
        var clock = new SafeReadClock();
        var authority = new Authority();
        await using var node = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "shard-serving-" + profile), profile,
            authority.Lease, Routing, authority, Options, timestampClock: clock);
        using var protection = await node.Kernel.PartitionTransactions.ProtectTimestampHistoryAsync();
        var time = new DurableTimestampState(clock.Domain, DateTime.UtcNow.Ticks, 0, 0, true);
        const int ReadCount = 64;
        int admissions = 0;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "EntityForge.Db.Kernel",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.OperationName == "db.timestamp.admission_service")
                {
                    Interlocked.Increment(ref admissions);
                }
            }
        };
        ActivitySource.AddActivityListener(listener);
        var reads = Enumerable.Range(0, ReadCount).Select(_ => node.Kernel.PartitionTransactions
            .CaptureTimestampProjectionAsync(protection, time, null).AsTask()).ToArray();
        var views = await Task.WhenAll(reads).WaitAsync(TimeSpan.FromSeconds(10));
        foreach (var view in views)
        {
            Assert.AreSame(views[0], view, "Native immutable indexes/view should not be reconstructed per query.");
        }

        Assert.AreEqual(ReadCount, admissions, "Every complete-owner read must enter native admission.");
        Assert.AreSame(views[0], await node.Kernel.PartitionTransactions.CaptureTimestampProjectionAsync(protection, time, null));
        Assert.AreEqual(ReadCount + 1, admissions);
        Assert.AreEqual(1, node.Kernel.TimestampProtectionCount, "The service must not create another complete-owner pin per query.");
    }

    [TestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph)]
    [DataRow(DatabaseProfileId.Rdf)]
    [DataRow(DatabaseProfileId.Semantic)]
    public async Task Timestamp_native_admission_bad_caller_does_not_affect_other_reads(DatabaseProfileId profile)
    {
        var clock = new SafeReadClock();
        var authority = new Authority();
        await using var node = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "shard-serving-invalid-" + profile), profile,
            authority.Lease, Routing, authority, Options, timestampClock: clock);
        using var protection = await node.Kernel.PartitionTransactions.ProtectTimestampHistoryAsync();
        var time = new DurableTimestampState(clock.Domain, DateTime.UtcNow.Ticks, 0, 0, true);
        var foreign = node.Kernel.PartitionTransactions.CaptureTimestampProjectionAsync(protection,
            new(Guid.NewGuid(), time.Physical, time.Logical, time.ReservedReadThrough, true), null).AsTask();
        var excessive = node.Kernel.PartitionTransactions.CaptureTimestampProjectionAsync(protection,
            new(time.Domain, time.Physical + TimeSpan.TicksPerMinute, time.Logical, time.ReservedReadThrough, true), null).AsTask();
        var valid = node.Kernel.PartitionTransactions.CaptureTimestampProjectionAsync(protection, time, null).AsTask();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => await foreign);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await excessive);
        Assert.IsNotNull(await valid.WaitAsync(TimeSpan.FromSeconds(5)));
        var disposed = await node.Kernel.PartitionTransactions.ProtectTimestampHistoryAsync();
        disposed.Dispose();
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await node.Kernel.PartitionTransactions.CaptureTimestampProjectionAsync(disposed, time, null));
        Assert.IsNotNull(await node.Kernel.PartitionTransactions.CaptureTimestampProjectionAsync(protection, time, null));
    }

    [TestMethod]
    public async Task Timestamp_native_admission_later_intent_does_not_block_earlier_read_or_survive_caller_cancellation()
    {
        var clock = new SafeReadClock();
        var authority = new Authority();
        await using var node = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "shard-serving-intent"), DatabaseProfileId.PropertyGraph,
            authority.Lease, Routing, authority, Options, timestampClock: clock);
        using var protection = await node.Kernel.PartitionTransactions.ProtectTimestampHistoryAsync();
        var earlier = new DurableTimestampState(clock.Domain, DateTime.UtcNow.Ticks, 0, 0, true);
        var operation = EntityOperationIds.New();
        string descriptor = new('E', 64);
        await node.Kernel.PartitionTransactions.PrepareAsync(operation, descriptor, node.Kernel.CurrentVersion,
            GraphTransactionCodec.Encode(new(operation, [new CreateVertex(GraphVertexId.New(), [])], ReadVersion: node.Kernel.CurrentVersion)));
        var vote = (await node.Kernel.PartitionTransactions.ReadTimestampVoteAsync(operation))!;
        var later = new DurableTimestampState(vote.Domain, vote.Physical + TimeSpan.TicksPerMillisecond, 0, vote.ReservedReadThrough, true);
        using var cancelled = new CancellationTokenSource();
        var waiting = node.Kernel.PartitionTransactions.CaptureTimestampProjectionAsync(protection, later, null, cancelled.Token).AsTask();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.IsNotNull(await node.Kernel.PartitionTransactions.CaptureTimestampProjectionAsync(protection, earlier, null, deadline.Token));
        Assert.IsFalse(waiting.IsCompleted, "The later read must still join the selected lower preparation.");
        cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await waiting.WaitAsync(deadline.Token));
        await node.Kernel.PartitionTransactions.CompleteAsync(operation, descriptor, false);
        Assert.IsNotNull(await node.Kernel.PartitionTransactions.CaptureTimestampProjectionAsync(protection, later, null, deadline.Token));
    }
}
