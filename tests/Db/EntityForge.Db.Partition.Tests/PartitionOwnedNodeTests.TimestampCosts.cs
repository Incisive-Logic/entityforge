using System.Diagnostics;
using EntityForge.Db.Kernel;
using EntityForge.Db.Distributed.Hosting;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [TestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph)]
    [DataRow(DatabaseProfileId.Rdf)]
    [DataRow(DatabaseProfileId.Semantic)]
    public async Task Timestamp_cost_transfer_drains_actual_profile_protection(DatabaseProfileId profile)
    {
        DatabaseTimestampCostOptions.QualificationOverride.Value = new(Guid.NewGuid(),
            () => Stopwatch.GetElapsedTime(0).Ticks, () => true, TimeSpan.FromMilliseconds(50));
        try
        {
            await Mvcc_snapshot_lifetime_is_drained_before_sealed_transfer(profile);
        }
        finally
        {
            DatabaseTimestampCostOptions.QualificationOverride.Value = null;
        }
    }

    [TestMethod]
    public async Task Timestamp_cost_preparation_and_clock_recovery_survive_transfer()
    {
        DatabaseTimestampCostOptions.QualificationOverride.Value = new(Guid.NewGuid(),
            () => Stopwatch.GetElapsedTime(0).Ticks, () => true, TimeSpan.FromMilliseconds(50));
        try
        {
            await Prepared_transaction_survives_transfer_then_finishes_at_destination_local_position();
        }
        finally
        {
            DatabaseTimestampCostOptions.QualificationOverride.Value = null;
        }
    }

    [TestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph, false)]
    [DataRow(DatabaseProfileId.PropertyGraph, true)]
    [DataRow(DatabaseProfileId.Rdf, false)]
    [DataRow(DatabaseProfileId.Rdf, true)]
    [DataRow(DatabaseProfileId.Semantic, false)]
    [DataRow(DatabaseProfileId.Semantic, true)]
    public async Task Timestamp_cost_actual_profiles_recover_WAL_and_checkpoint(DatabaseProfileId profile, bool checkpoint)
    {
        DatabaseTimestampCostOptions.QualificationOverride.Value = new(Guid.NewGuid(),
            () => Stopwatch.GetElapsedTime(0).Ticks, () => true, TimeSpan.FromMilliseconds(50));
        try
        {
            var authority = new Authority();
            string path = Path.Combine(_root, "time-reopen");
            DatabaseVersion version;
            long floor;
            await using (var node = await PartitionOwnedNode.CreateAsync(path, profile, authority.Lease, Routing, authority, Options))
            {
                if (profile == DatabaseProfileId.Semantic)
                {
                    var execution = await node.CreateSemanticExecutionAsync();
                    var captured = execution.CaptureSave(new Item("time", "value"), EntityForge.Db.Semantic.SemanticEntityWriteOperation.Create());
                    await execution.ExecuteAsync(node.Domain, captured.OperationId, captured.Export());
                }
                else
                {
                    Guid operation = Guid.NewGuid();
                    await node.Authority.ExecuteAsync(async () =>
                    {
                        await node.Kernel.CommitAsync(operation, NativePayload(profile, Routing, new("a"), operation));
                        return true;
                    });
                }
                using (var pin = await node.Kernel.PartitionTransactions.CaptureSnapshotAsync())
                {
                    Assert.IsTrue(await node.Kernel.PartitionTransactions.ValidateReadEpochAsync(pin));
                }

                version = node.Kernel.CurrentVersion;
                floor = node.Kernel.TimestampCostState!.ReservedReadThrough;
                if (checkpoint)
                {
                    await node.Kernel.CreateCheckpointAsync();
                }
            }
            await using var reopened = await PartitionOwnedNode.OpenAsync(path, profile, authority.Lease, Routing, authority, Options);
            Assert.AreEqual(version, reopened.Kernel.CurrentVersion);
            Assert.IsTrue(reopened.Kernel.TimestampCostState!.ReservedReadThrough >= floor);
            using var fresh = await reopened.Kernel.PartitionTransactions.CaptureSnapshotAsync();
            Assert.IsTrue(await reopened.Kernel.PartitionTransactions.ValidateReadEpochAsync(fresh));
        }
        finally
        {
            DatabaseTimestampCostOptions.QualificationOverride.Value = null;
        }
    }

    [TestMethod]
    public async Task Timestamp_cost_concurrent_readers_share_one_durable_reservation_and_failed_clock_blocks_admission()
    {
        bool healthy = true;
        DatabaseTimestampCostOptions.QualificationOverride.Value = new(Guid.NewGuid(),
            () => TimeSpan.TicksPerSecond, () => healthy, TimeSpan.FromMilliseconds(50));
        try
        {
            var authority = new Authority();
            await using var node = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "time-concurrent"),
                DatabaseProfileId.PropertyGraph, authority.Lease, Routing, authority, Options);
            var pins = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ =>
                node.Kernel.PartitionTransactions.CaptureSnapshotAsync().AsTask()));
            try
            {
                Assert.AreEqual(32, node.Kernel.TimestampProtectionCount);
                Assert.AreEqual(1L, node.Kernel.TimestampReservationForces);
                healthy = false;
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
                    await node.Kernel.PartitionTransactions.CaptureSnapshotAsync());
                Assert.AreEqual(32, node.Kernel.TimestampProtectionCount);
            }
            finally
            {
                foreach (var pin in pins)
                {
                    pin.Dispose();
                }
            }
            Assert.AreEqual(0, node.Kernel.TimestampProtectionCount);
        }
        finally
        {
            DatabaseTimestampCostOptions.QualificationOverride.Value = null;
        }
    }

    [TestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph, false)]
    [DataRow(DatabaseProfileId.PropertyGraph, true)]
    [DataRow(DatabaseProfileId.Rdf, false)]
    [DataRow(DatabaseProfileId.Rdf, true)]
    public async Task Timestamp_cost_real_HTTP_epoch_reopen_and_retention(DatabaseProfileId profile, bool live)
    {
        DatabaseTimestampCostOptions.QualificationOverride.Value = new(Guid.NewGuid(),
            () => Stopwatch.GetElapsedTime(0).Ticks, () => true, TimeSpan.FromMilliseconds(50));
        try
        {
            await Read_epoch_https_reuses_pin_and_preserves_minimum_after_write(profile, live);
        }
        finally
        {
            DatabaseTimestampCostOptions.QualificationOverride.Value = null;
        }
    }

    [TestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph)]
    [DataRow(DatabaseProfileId.Rdf)]
    [DataRow(DatabaseProfileId.Semantic)]
    public async Task Timestamp_cost_profile_pins_block_reclamation_and_release(DatabaseProfileId profile)
    {
        DatabaseTimestampCostOptions.QualificationOverride.Value = new(Guid.NewGuid(),
            () => Stopwatch.GetElapsedTime(0).Ticks, () => true, TimeSpan.FromMilliseconds(50));
        try
        {
            var authority = new Authority();
            await using var node = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "time-pin"), profile,
                authority.Lease, Routing, authority, Options);
            using var pin = await node.Kernel.PartitionTransactions.CaptureSnapshotAsync();
            Assert.AreEqual(1, node.Kernel.TimestampProtectionCount);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await node.Kernel.CreateCheckpointAsync());
            Assert.IsTrue(await node.Kernel.PartitionTransactions.ValidateReadEpochAsync(pin));
            pin.Dispose();
            Assert.AreEqual(0, node.Kernel.TimestampProtectionCount);
            await node.Kernel.CreateCheckpointAsync();
            Assert.IsFalse(await node.Kernel.PartitionTransactions.ValidateReadEpochAsync(pin));
        }
        finally
        {
            DatabaseTimestampCostOptions.QualificationOverride.Value = null;
        }
    }

    [TestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph)]
    [DataRow(DatabaseProfileId.Rdf)]
    public async Task Timestamp_cost_two_owner_partial_apply_and_late_projection(DatabaseProfileId profile)
    {
        DatabaseTimestampCostOptions.QualificationOverride.Value = new(Guid.NewGuid(),
            () => Stopwatch.GetElapsedTime(0).Ticks, () => true, TimeSpan.FromMilliseconds(50));
        try
        {
            await Read_epoch_partial_application_cannot_refresh_to_a_mixed_cut(profile);
        }
        finally
        {
            DatabaseTimestampCostOptions.QualificationOverride.Value = null;
        }
    }
}
