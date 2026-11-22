using EntityForge.Db.Durability;
using EntityForge.Db.Kernel;
using EntityForge.Db.Distributed.Hosting;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    private sealed class SafeReadClock : IDatabaseBoundedClock
    {
        public Guid Domain { get; } = Guid.NewGuid();

        internal DatabaseClockInterval? Held;

        public DatabaseClockInterval Read()
        {
            if (Held is { } held)
            {
                return held;
            }

            long now = DateTime.UtcNow.Ticks;
            return new(now, now);
        }
    }

    [TestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph)]
    [DataRow(DatabaseProfileId.Rdf)]
    [DataRow(DatabaseProfileId.Semantic)]
    public async Task All_profiles_admit_future_projection_without_clock_expiry(DatabaseProfileId profile)
    {
        var clock = new SafeReadClock();
        var placement = new Authority();
        await using var node = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "safe-" + profile), profile,
            placement.Lease, Routing, placement, Options, timestampClock: clock);
        using var snapshot = await node.Kernel.PartitionTransactions.CaptureSnapshotAsync();
        long now = DateTime.UtcNow.Ticks;
        clock.Held = new(now, now + TimeSpan.TicksPerSecond / 2);
        var time = new DurableTimestampState(clock.Domain, now + TimeSpan.TicksPerSecond / 4, 0, 0, true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        object view = await node.Kernel.PartitionTransactions.CaptureTimestampProjectionAsync(snapshot, time, null, deadline.Token);
        Assert.IsNotNull(view);
        Assert.IsTrue(clock.Read().EarliestUtcTicks < time.Physical);
        Assert.IsTrue(node.Kernel.TimestampCostState!.ReservedReadThrough > time.Physical);
    }

    [TestMethod]
    public async Task Warm_read_epoch_uses_safe_admission_for_fresh_uncertain_query_time()
    {
        var clock = new SafeReadClock();
        var placement = new Authority();
        await using var node = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "safe-epoch"), DatabaseProfileId.PropertyGraph,
            placement.Lease, Routing, placement, Options, timestampClock: clock);
        await using var epoch = new PartitionReadEpoch([node], Routing, clock);
        async ValueTask<string> Read(PartitionRemoteRead read, CancellationToken token) =>
            System.Text.Encoding.UTF8.GetString(await read.ReadAsync("a", new("vertex", Guid.NewGuid()), token));
        Assert.AreEqual("null", (await epoch.ExecuteAsync(Read, null, default)).Value);
        long now = DateTime.UtcNow.Ticks;
        clock.Held = new(now, now + TimeSpan.TicksPerSecond / 4);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.AreEqual("null", (await epoch.ExecuteAsync(Read, null, deadline.Token)).Value);
        Assert.IsTrue(node.Kernel.TimestampCostState!.Physical >= clock.Held.Value.LatestUtcTicks);
        Assert.AreEqual(now, clock.Read().EarliestUtcTicks);
    }
}
