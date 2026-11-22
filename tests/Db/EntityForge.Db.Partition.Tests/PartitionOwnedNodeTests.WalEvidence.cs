using EntityForge.Db.Durability;
using EntityForge.Db.Graph;
using EntityForge.Db.Kernel;
using EntityForge.Db.Storage.Pages;
using EntityForge.Db.Storage.Local;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [TestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph)]
    [DataRow(DatabaseProfileId.Rdf)]
    public async Task Abort_only_epochs_reclaim_protocol_WAL_without_advancing_native_version(DatabaseProfileId profile)
    {
        string path = Path.Combine(_root, "abort-epochs");
        Guid subject = Guid.NewGuid();
        var created = DateTimeOffset.UtcNow.AddMinutes(-10);
        const int Epochs = 3, OperationsPerEpoch = 2;
        for (int epoch = 0; epoch < Epochs; epoch++)
        {
            var opened = await OpenReservationStore(profile, path);
            await using (opened.Database)
            {
                if (epoch == 0)
                {
                    Guid seed = Guid.NewGuid();
                    await opened.Kernel.CommitAsync(seed, ReservationPayload(profile, seed, [subject], null, true));
                }
                for (int index = 0; index < OperationsPerEpoch; index++)
                {
                    Guid operation = EntityOperationIds.New(created.AddSeconds(epoch * 2));
                    await opened.Kernel.PartitionTransactions.PrepareAsync(operation, ReservationDescriptor, new(1),
                        ReservationPayload(profile, operation, [subject], new(1)));
                    await opened.Kernel.PartitionTransactions.CompleteAsync(operation, ReservationDescriptor, false);
                }
                await opened.Kernel.PartitionTransactions.RetainAsync(created.AddSeconds(epoch * 2 + 1));
                Assert.AreEqual(1L, opened.Kernel.CurrentVersion.Value);
                Assert.AreEqual(0, (await opened.Kernel.PartitionTransactions.ReadAsync()).Count);
            }
            await using var log = await GroupedRedoDatabaseCommitLog.OpenAsync(Path.Combine(path, LocalGraphDatabaseOptions.DefaultCommitLogFileName));
            Assert.AreEqual(0, (await log.RecoverAsync()).Count, "Aborted-only traffic must not accumulate behind an unchanged native position.");
            Assert.AreEqual(1L + (epoch + 1) * OperationsPerEpoch * 2, log.GetRedoStatus().DurableLsn);
        }
    }

    private sealed class NoProtocolCheckpoint : IDatabaseCheckpointPublicationObserver
    {
        public void Reached(DatabaseCheckpointPublicationStage stage) =>
            throw new InvalidOperationException("A protocol transition attempted a full checkpoint.");
    }

    [TestMethod]
    public async Task Prepared_and_committed_evidence_use_two_WAL_forces_and_no_checkpoint()
    {
        string path = Path.Combine(_root, "wal-only");
        var observer = new PartitionWalObserver();
        Guid operation = EntityOperationIds.New(), subject = Guid.NewGuid();
        await using (var db = await GraphDatabase.OpenLocalAsync(new(path)
        {
            VersioningMode = DatabaseVersioningMode.PartitionLocal
        },
            observer, new NoProtocolCheckpoint()))
        {
            var kernel = ((GraphDatabaseEngine)db).PartitionKernel;
            Guid seed = Guid.NewGuid();
            await kernel.CommitAsync(seed, ReservationPayload(DatabaseProfileId.PropertyGraph, seed, [subject], null, true));
            int forces = observer.Forces;
            var version = kernel.CurrentVersion;
            await kernel.PartitionTransactions.PrepareAsync(operation, ReservationDescriptor, version,
                ReservationPayload(DatabaseProfileId.PropertyGraph, operation, [subject], version));
            Assert.AreEqual(version, kernel.CurrentVersion);
            await kernel.PartitionTransactions.CompleteAsync(operation, ReservationDescriptor, true);
            Assert.AreEqual(forces + 2, observer.Forces);
        }
        var reopened = await OpenReservationStore(DatabaseProfileId.PropertyGraph, path);
        await using (reopened.Database)
        {
            Assert.AreEqual(DurablePartitionTransactionState.Committed,
                (await reopened.Kernel.PartitionTransactions.ReadAsync()).Single().State);
            Assert.AreEqual(2L, (await reopened.Kernel.PartitionTransactions.ResolveAsync(operation, ReservationDescriptor))!.Version.Value);
        }
    }

    private sealed class PartitionWalObserver : IRedoDurabilityObserver
    {
        internal bool Fail;
        internal bool FailReclamation;
        internal int Forces;

        public void Reached(RedoDurabilityStage stage)
        {
            if (stage == RedoDurabilityStage.BeforeWalReclamation && FailReclamation)
            {
                FailReclamation = false;
                throw new IOException("Stop after checkpoint, before WAL reclamation.");
            }
            if (stage != RedoDurabilityStage.AfterWalFlush)
            {
                return;
            }

            Forces++;
            if (Fail)
            {
                Fail = false;
                throw new IOException("Lost partition WAL acknowledgement.");
            }
        }
    }

    [TestMethod]
    public async Task Expired_protocol_tail_cannot_resurrect_after_checkpoint_succeeds_and_reclamation_fails()
    {
        string path = Path.Combine(_root, "reclaim-interruption");
        Guid operation = EntityOperationIds.New(DateTimeOffset.UtcNow.AddMinutes(-2)), subject = Guid.NewGuid();
        var observer = new PartitionWalObserver();
        await using (var db = await GraphDatabase.OpenLocalAsync(new(path)
        {
            VersioningMode = DatabaseVersioningMode.PartitionLocal
        }, observer, null))
        {
            var kernel = ((GraphDatabaseEngine)db).PartitionKernel;
            Guid seed = Guid.NewGuid();
            await kernel.CommitAsync(seed, ReservationPayload(DatabaseProfileId.PropertyGraph, seed, [subject], null, true));
            await kernel.PartitionTransactions.PrepareAsync(operation, ReservationDescriptor, new(1),
                ReservationPayload(DatabaseProfileId.PropertyGraph, operation, [subject], new(1)));
            await kernel.PartitionTransactions.CompleteAsync(operation, ReservationDescriptor, false);
            observer.FailReclamation = true;
            await Assert.ThrowsAsync<Exception>(async () => await kernel.PartitionTransactions.RetainAsync(DateTimeOffset.UtcNow.AddMinutes(-1)));
            Assert.AreEqual(DatabaseKernelState.RecoveryRequired, kernel.State);
        }
        await using (var log = await GroupedRedoDatabaseCommitLog.OpenAsync(Path.Combine(path, LocalGraphDatabaseOptions.DefaultCommitLogFileName)))
        {
            Assert.AreEqual(2, (await log.RecoverAsync()).Count(p => p.ChangeKind == DurableCommittedChangeKind.PartitionEvidence));
        }

        var opened = await OpenReservationStore(DatabaseProfileId.PropertyGraph, path);
        await using (opened.Database)
        {
            Assert.AreEqual(0, (await opened.Kernel.PartitionTransactions.ReadAsync()).Count);
            await Assert.ThrowsAsync<EntityOperationExpiredException>(async () => await opened.Kernel.PartitionTransactions.ResolveAsync(operation, ReservationDescriptor));
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Partition_wal_uncertainty_recovers_preparation_or_abort_without_checkpoint(bool abort)
    {
        string path = Path.Combine(_root, "wal-uncertainty");
        var observer = new PartitionWalObserver();
        Guid operation = EntityOperationIds.New(), subject = Guid.NewGuid();
        await using (var db = await GraphDatabase.OpenLocalAsync(new(path)
        {
            VersioningMode = DatabaseVersioningMode.PartitionLocal
        }, observer, null))
        {
            var kernel = ((GraphDatabaseEngine)db).PartitionKernel;
            Guid seed = Guid.NewGuid();
            await kernel.CommitAsync(seed, ReservationPayload(DatabaseProfileId.PropertyGraph, seed, [subject], null, true));
            var version = kernel.CurrentVersion;
            byte[] payload = ReservationPayload(DatabaseProfileId.PropertyGraph, operation, [subject], version);
            if (abort)
            {
                await kernel.PartitionTransactions.PrepareAsync(operation, ReservationDescriptor, version, payload);
            }

            observer.Fail = true;
            await Assert.ThrowsAsync<Exception>(async () =>
            {
                if (abort)
                {
                    await kernel.PartitionTransactions.CompleteAsync(operation, ReservationDescriptor, false);
                }
                else
                {
                    await kernel.PartitionTransactions.PrepareAsync(operation, ReservationDescriptor, version, payload);
                }
            });
            Assert.AreEqual(DatabaseKernelState.RecoveryRequired, kernel.State);
            await Assert.ThrowsAsync<EntityForgeDbRecoveryRequiredException>(async () =>
                await kernel.PartitionTransactions.ResolveAsync(operation, ReservationDescriptor));
        }
        var reopened = await OpenReservationStore(DatabaseProfileId.PropertyGraph, path);
        await using (reopened.Database)
        {
            Assert.AreEqual(1L, reopened.Kernel.CurrentVersion.Value);
            var entry = (await reopened.Kernel.PartitionTransactions.ReadAsync()).Single();
            Assert.AreEqual(abort ? DurablePartitionTransactionState.Aborted : DurablePartitionTransactionState.Prepared, entry.State);
            if (!abort)
            {
                await AssertReservationConflict(reopened.Kernel, DatabaseProfileId.PropertyGraph, subject);
            }

            await reopened.Kernel.PartitionTransactions.CompleteAsync(operation, ReservationDescriptor, false);
        }
    }

    [TestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph)]
    [DataRow(DatabaseProfileId.Rdf)]
    public async Task Protocol_tail_overlapping_unchanged_checkpoint_does_not_resurrect_expired_reservation(DatabaseProfileId profile)
    {
        string path = Path.Combine(_root, "wal-expiry");
        Guid operation = EntityOperationIds.New(DateTimeOffset.UtcNow.AddMinutes(-2)), subject = Guid.NewGuid();
        var opened = await OpenReservationStore(profile, path);
        await using (opened.Database)
        {
            Guid seed = Guid.NewGuid();
            await opened.Kernel.CommitAsync(seed, ReservationPayload(profile, seed, [subject], null, true));
            var version = opened.Kernel.CurrentVersion;
            await opened.Kernel.PartitionTransactions.PrepareAsync(operation, ReservationDescriptor, version,
                ReservationPayload(profile, operation, [subject], version));
            await opened.Kernel.PartitionTransactions.CheckpointForTransferAsync();
            await opened.Kernel.PartitionTransactions.CompleteAsync(operation, ReservationDescriptor, false);
            await opened.Kernel.PartitionTransactions.RetainAsync(DateTimeOffset.UtcNow.AddMinutes(-1));
            Assert.AreEqual(version, opened.Kernel.CurrentVersion);
        }
        opened = await OpenReservationStore(profile, path);
        await using (opened.Database)
        {
            Assert.AreEqual(0, (await opened.Kernel.PartitionTransactions.ReadAsync()).Count);
            await Assert.ThrowsAsync<EntityOperationExpiredException>(async () =>
                await opened.Kernel.PartitionTransactions.ResolveAsync(operation, ReservationDescriptor));
        }
    }
}
