using EntityForge.Db.Durability;
using EntityForge.Db.Kernel;
using EntityForge.Db.Storage.Local;
using EntityForge.Db.Storage.Pages;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class DatabaseKernelTests
{
    [TestMethod]
    public async Task Timestamp_cost_fragmented_WAL_retains_original_payload_and_timestamp()
    {
        string path = CreatePath();
        Guid incarnation = Guid.NewGuid();
        byte[] payload = Enumerable.Range(0, 100_000).Select(i => (byte)(i % 251)).ToArray();
        var options = new DatabaseTimestampCostOptions(Guid.NewGuid(), () => TimeSpan.TicksPerSecond,
            () => true, TimeSpan.FromMilliseconds(50));
        DurableTimestampState? stamp;
        await using (var kernel = new DatabaseKernel(await GroupedRedoDatabaseCommitLog.OpenAsync(path), Changes(path), new CheckpointRuntime(),
            durableDatabaseIncarnation: incarnation, versioningMode: DatabaseVersioningMode.PartitionLocal)
        {
            TimestampCostOptions = options
        })
        {
            await kernel.RecoverAsync();
            await kernel.CommitAsync(Guid.NewGuid(), payload);
            stamp = kernel.TimestampCostState;
        }
        var runtime = new CheckpointRuntime();
        await using var reopened = new DatabaseKernel(await GroupedRedoDatabaseCommitLog.OpenAsync(path), Changes(path), runtime,
            durableDatabaseIncarnation: incarnation, versioningMode: DatabaseVersioningMode.PartitionLocal)
        {
            TimestampCostOptions = options
        };
        await reopened.RecoverAsync();
        Assert.AreEqual(stamp, reopened.TimestampCostState);
        CollectionAssert.AreEqual(payload, runtime.State.ToArray());
    }

    [TestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph)]
    [DataRow(DatabaseProfileId.Rdf)]
    [DataRow(DatabaseProfileId.Semantic)]
    public async Task Timestamp_cost_native_WAL_checkpoint_and_reclamation_preserve_associations(DatabaseProfileId profile)
    {
        string path = CreatePath(), checkpointPath = path + ".checkpoint";
        Guid incarnation = Guid.NewGuid(), domain = Guid.NewGuid(), operation = Guid.NewGuid();
        long now = TimeSpan.FromSeconds(10).Ticks;
        var options = new DatabaseTimestampCostOptions(domain, () => now, () => true, TimeSpan.FromMilliseconds(50));
        async Task<DatabaseKernel> Open()
        {
            var kernel = new DatabaseKernel(await GroupedRedoDatabaseCommitLog.OpenAsync(path), Changes(path), new CheckpointRuntime(profile),
                checkpointStore: new FileDatabaseCheckpointStore(checkpointPath), durableDatabaseIncarnation: incarnation,
                versioningMode: DatabaseVersioningMode.PartitionLocal)
            {
                TimestampCostOptions = options
            };
            try
            {
                await kernel.RecoverAsync();
                return kernel;
            }
            catch
            {
                await kernel.DisposeAsync();
                throw;
            }
        }
        DurableTimestampState original;
        long promised;
        await using (var kernel = await Open())
        {
            await kernel.CommitAsync(operation, new byte[] { 7 });
            original = kernel.TimestampCostState!;
            await kernel.ReserveTimestampReadAsync(default);
            promised = kernel.TimestampCostState!.ReservedReadThrough;
            await kernel.ReserveTimestampReadAsync(default);
            Assert.AreEqual(1L, kernel.TimestampReservationForces);
            Assert.AreEqual(1L, kernel.CurrentVersion.Value);
            var checkpoint = await kernel.CreateCheckpointAsync();
            Assert.AreEqual(original, checkpoint.CommittedTransactions.Single().Timestamp);
            Assert.AreEqual(promised, checkpoint.Timestamp!.ReservedReadThrough);
        }
        now = promised + TimeSpan.TicksPerMillisecond;
        await using (var reopened = await Open())
        {
            Assert.AreEqual(promised, reopened.TimestampCostState!.ReservedReadThrough);
            var checkpoint = await reopened.CreateCheckpointAsync();
            Assert.AreEqual(original, checkpoint.CommittedTransactions.Single().Timestamp);
            await reopened.CommitAsync(Guid.NewGuid(), new byte[] { 8 });
            Assert.IsTrue(reopened.TimestampCostState!.Physical > promised);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Timestamp_cost_ambiguous_force_recovers_read_promise_or_native_commit(bool native)
    {
        string path = CreatePath();
        Guid incarnation = Guid.NewGuid(), domain = Guid.NewGuid(), operation = Guid.NewGuid();
        long now = TimeSpan.FromSeconds(10).Ticks;
        var options = new DatabaseTimestampCostOptions(domain, () => now, () => true, TimeSpan.FromMilliseconds(50));
        await using (var kernel = new DatabaseKernel(await GroupedRedoDatabaseCommitLog.OpenAsync(path,
            new(maximumGroupDelay: TimeSpan.Zero), new ThrowOnceRedoObserver(RedoDurabilityStage.AfterWalFlush)),
            Changes(path), new CheckpointRuntime(), durableDatabaseIncarnation: incarnation,
            versioningMode: DatabaseVersioningMode.PartitionLocal)
        {
            TimestampCostOptions = options
        })
        {
            await kernel.RecoverAsync();
            try
            {
                if (native)
                {
                    await kernel.CommitAsync(operation, new byte[] { 7 });
                }
                else
                {
                    await kernel.ReserveTimestampReadAsync(default);
                }

                Assert.Fail("Expected ambiguous WAL completion.");
            }
            catch (RedoCommitUncertainException)
            {

            }
            catch (EntityForgeDbCommitException failure) when (failure.InnerException is RedoCommitUncertainException)
            {

            }
            Assert.AreEqual(DatabaseKernelState.RecoveryRequired, kernel.State);
            await Assert.ThrowsExactlyAsync<EntityForgeDbRecoveryRequiredException>(async () => await kernel.ReserveTimestampReadAsync(default));
        }
        now += TimeSpan.TicksPerSecond;
        await using var recovered = new DatabaseKernel(await GroupedRedoDatabaseCommitLog.OpenAsync(path), Changes(path), new CheckpointRuntime(),
            durableDatabaseIncarnation: incarnation, versioningMode: DatabaseVersioningMode.PartitionLocal)
        {
            TimestampCostOptions = options
        };
        await recovered.RecoverAsync();
        Assert.AreEqual(native ? 1L : 0L, recovered.CurrentVersion.Value);
        Assert.IsNotNull(recovered.TimestampCostState);
        if (!native)
        {
            Assert.IsTrue(recovered.TimestampCostState!.ReservedReadThrough > 0);
        }
    }

    [TestMethod]
    public async Task Timestamp_cost_rejects_changed_boot_domain_and_disabled_reopen()
    {
        string path = CreatePath();
        Guid incarnation = Guid.NewGuid(), domain = Guid.NewGuid();
        var options = new DatabaseTimestampCostOptions(domain, () => TimeSpan.TicksPerSecond, () => true, TimeSpan.FromMilliseconds(50));
        await using (var kernel = new DatabaseKernel(await GroupedRedoDatabaseCommitLog.OpenAsync(path), Changes(path), new CheckpointRuntime(),
            durableDatabaseIncarnation: incarnation, versioningMode: DatabaseVersioningMode.PartitionLocal)
        {
            TimestampCostOptions = options
        })
        {
            await kernel.RecoverAsync();
            await kernel.CommitAsync(Guid.NewGuid(), new byte[] { 1 });
        }
        foreach (var changed in new DatabaseTimestampCostOptions?[] { null, options with { Domain = Guid.NewGuid() } })
        {
            await using var kernel = new DatabaseKernel(await GroupedRedoDatabaseCommitLog.OpenAsync(path), Changes(path), new CheckpointRuntime(),
                durableDatabaseIncarnation: incarnation, versioningMode: DatabaseVersioningMode.PartitionLocal)
            {
                TimestampCostOptions = changed
            };
            await Assert.ThrowsExactlyAsync<EntityForgeDbFormatException>(async () => await kernel.RecoverAsync());
        }
    }
}
