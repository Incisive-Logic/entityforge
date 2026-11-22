using EntityForge.Db.Semantic;
using EntityForge.Db.Kernel;
using EntityForge.Db;
using EntityForge.Db.Storage.Local;
using EntityForge.Db.Storage;
using EntityForge.Db.Durability;
using EntityForge.Db.Mapping;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed partial class DatabaseKernelTests
{
    private const string GraphCodec = "entityforge.graph.transactions";
    private const int GraphCodecVersion = 1;
    private string? _directory;

    [DataTestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph)]
    [DataRow(DatabaseProfileId.Rdf)]
    [DataRow(DatabaseProfileId.Semantic)]
    public async Task Partition_preparation_survives_restart_blocks_local_writes_and_assigns_a_local_position(DatabaseProfileId profile)
    {
        string path = CreatePath(), checkpoint = Path.Combine(_directory!, "mixed.checkpoint");
        Guid incarnation = Guid.NewGuid(), operation = Guid.NewGuid();
        string descriptor = new('A', 64);
        async Task<DatabaseKernel> Open(CheckpointRuntime runtime)
        {
            var kernel = new DatabaseKernel(await GroupedRedoDatabaseCommitLog.OpenAsync(path), Changes(path), runtime,
                checkpointStore: new FileDatabaseCheckpointStore(checkpoint), durableDatabaseIncarnation: incarnation,
                versioningMode: DatabaseVersioningMode.PartitionLocal);
            await kernel.RecoverAsync();
            return kernel;
        }
        await using (var kernel = await Open(new(profile)))
        {
            await kernel.CommitAsync(Guid.NewGuid(), new byte[] { 1 });
            await kernel.PreparePartitionTransactionAsync(operation, descriptor, new(1), new byte[] { 2 });
            await kernel.PreparePartitionTransactionAsync(operation, descriptor, new(1), new byte[] { 2 });
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await kernel.AdmitPartitionReadAsync());
            await Assert.ThrowsExactlyAsync<EntityForgeDbIdempotencyException>(async () =>
                await kernel.PreparePartitionTransactionAsync(operation, descriptor, new(1), new byte[] { 3 }));
            await Assert.ThrowsExactlyAsync<DatabasePartitionConflictException>(async () => await kernel.CommitAsync(Guid.NewGuid(), new byte[] { 3 }));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await kernel.CommitGroupWithReceiptsAsync([new(Guid.NewGuid(), new byte[] { 3 })]));
            Assert.AreEqual(1L, kernel.CurrentVersion.Value, "Preparation must not allocate an external native version.");
        }
        await using (var kernel = await Open(new(profile)))
        {
            Assert.AreEqual(DurablePartitionTransactionState.Prepared, (await kernel.ReadPartitionTransactionsAsync()).Single().State);
            await Assert.ThrowsExactlyAsync<DatabasePartitionConflictException>(async () => await kernel.CommitAsync(Guid.NewGuid(), new byte[] { 3 }));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => await kernel.CompletePartitionTransactionAsync(operation, new('B', 64), true));
            Assert.AreEqual(2L, (await kernel.CompletePartitionTransactionAsync(operation, descriptor, true))!.Version.Value);
            await kernel.CommitAsync(Guid.NewGuid(), new byte[] { 3 });
        }
        var state = new CheckpointRuntime(profile);
        await using var reopened = await Open(state);
        Assert.AreEqual(2L, (await reopened.CompletePartitionTransactionAsync(operation, descriptor, true))!.Version.Value);
        Assert.AreEqual(3L, reopened.CurrentVersion.Value);
        Assert.AreEqual(reopened.CurrentVersion, await reopened.AdmitPartitionReadAsync());
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, state.State.ToArray());
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => await reopened.CompletePartitionTransactionAsync(operation, descriptor, false));
    }

    [TestMethod]
    public async Task Partition_recovery_reconstructs_local_receipt_after_native_publication_failure()
    {
        string path = CreatePath(), checkpoint = Path.Combine(_directory!, "mixed.checkpoint");
        Guid incarnation = Guid.NewGuid(), operation = Guid.NewGuid();
        string descriptor = new('A', 64);
        var state = new CheckpointRuntime();
        await using (var kernel = new DatabaseKernel(await GroupedRedoDatabaseCommitLog.OpenAsync(path), Changes(path), state,
            checkpointStore: new FileDatabaseCheckpointStore(checkpoint), durableDatabaseIncarnation: incarnation,
            versioningMode: DatabaseVersioningMode.PartitionLocal))
        {
            await kernel.RecoverAsync();
            await kernel.PreparePartitionTransactionAsync(operation, descriptor, new(0), new byte[] { 7 });
            state.FailNextApply = true;
            var failure = await Assert.ThrowsExactlyAsync<EntityOperationCommittedException<DatabaseCommitReceipt>>(async () =>
                await kernel.CompletePartitionTransactionAsync(operation, descriptor, true));
            Assert.AreEqual(1L, failure.Receipt.Version.Value);
            Assert.AreEqual(DatabaseKernelState.RecoveryRequired, kernel.State);
        }
        state = new();
        await using var recovered = new DatabaseKernel(await GroupedRedoDatabaseCommitLog.OpenAsync(path), Changes(path), state,
            checkpointStore: new FileDatabaseCheckpointStore(checkpoint), durableDatabaseIncarnation: incarnation,
            versioningMode: DatabaseVersioningMode.PartitionLocal);
        await recovered.RecoverAsync();
        var evidence = (await recovered.ReadPartitionTransactionsAsync()).Single();
        Assert.AreEqual(DurablePartitionTransactionState.Committed, evidence.State);
        Assert.AreEqual(1L, evidence.LocalVersion!.Value.Value);
        Assert.AreEqual(1L, (await recovered.CompletePartitionTransactionAsync(operation, descriptor, true))!.Version.Value);
        await recovered.CommitAsync(Guid.NewGuid(), new byte[] { 8 });
        CollectionAssert.AreEqual(new byte[] { 7, 8 }, state.State.ToArray());
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public async Task Partition_group_publication_failure_preserves_every_members_known_commit(int failMember)
    {
        string path = CreatePath(), checkpoint = Path.Combine(_directory!, "cohort.checkpoint");
        Guid incarnation = Guid.NewGuid();
        Guid[] operations = [Guid.NewGuid(), Guid.NewGuid()];
        string descriptor = new('A', 64);
        var profile = new CohortCheckpointRuntime { FailMember = failMember };
        await using (var kernel = new DatabaseKernel(await GroupedRedoDatabaseCommitLog.OpenAsync(path), Changes(path), profile,
            checkpointStore: new FileDatabaseCheckpointStore(checkpoint), durableDatabaseIncarnation: incarnation,
            versioningMode: DatabaseVersioningMode.PartitionLocal))
        {
            await kernel.RecoverAsync();
            for (int index = 0; index < operations.Length; index++)
            {
                await kernel.PreparePartitionTransactionAsync(operations[index], descriptor, new(0), new byte[] { (byte)(index + 1) });
            }

            var gate = (SemaphoreSlim)typeof(DatabaseKernel).GetField("_localWriteAuthority",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(kernel)!;
            await gate.WaitAsync();
            Task<DatabaseCommitReceipt?>[] completing;
            try
            {
                completing = operations.Select(id => kernel.CompletePartitionTransactionAsync(id, descriptor, true).AsTask()).ToArray();
            }
            finally
            {
                gate.Release();
            }
            for (int index = 0; index < operations.Length; index++)
            {
                var failure = await Assert.ThrowsExactlyAsync<EntityOperationCommittedException<DatabaseCommitReceipt>>(() => completing[index]);
                Assert.AreEqual(operations[index], failure.Receipt.TransactionId);
                Assert.AreEqual(index + 1L, failure.Receipt.Version.Value);
            }
            Assert.AreEqual(DatabaseKernelState.RecoveryRequired, kernel.State);
        }
        profile = new();
        await using var recovered = new DatabaseKernel(await GroupedRedoDatabaseCommitLog.OpenAsync(path), Changes(path), profile,
            checkpointStore: new FileDatabaseCheckpointStore(checkpoint), durableDatabaseIncarnation: incarnation,
            versioningMode: DatabaseVersioningMode.PartitionLocal);
        await recovered.RecoverAsync();
        Assert.IsTrue((await recovered.ReadPartitionTransactionsAsync()).All(entry => entry.State == DurablePartitionTransactionState.Committed));
        for (int index = 0; index < operations.Length; index++)
        {
            Assert.AreEqual(index + 1L, (await recovered.ResolvePartitionTransactionAsync(operations[index], descriptor, CancellationToken.None))!.Version.Value);
        }

        CollectionAssert.AreEqual(new byte[] { 1, 2 }, profile.Inner.State.ToArray());
    }

    private sealed class CohortCheckpointRuntime : ICheckpointableDatabaseProfileRuntime,
        IDatabaseProfileOrderedPreparationRuntime, IDatabaseProfilePartitionReservations
    {
        internal CheckpointRuntime Inner { get; } = new();

        internal int FailMember;
        private int _applied;

        public DatabaseProfileId Profile => Inner.Profile;

        public string CodecId => Inner.CodecId;

        public int CodecVersion => Inner.CodecVersion;

        public string CheckpointCodecId => Inner.CheckpointCodecId;

        public int CheckpointCodecVersion => Inner.CheckpointCodecVersion;

        public ValueTask ApplyAsync(DatabaseVersion version, Guid id, ReadOnlyMemory<byte> payload, CancellationToken token = default)
        {
            Inner.FailNextApply = ++_applied == FailMember;
            return Inner.ApplyAsync(version, id, payload, token);
        }

        public ValueTask<ReadOnlyMemory<byte>> CaptureCheckpointAsync(CancellationToken token = default) => Inner.CaptureCheckpointAsync(token);

        public ValueTask RestoreCheckpointAsync(DatabaseVersion version, ReadOnlyMemory<byte> payload, CancellationToken token = default) => Inner.RestoreCheckpointAsync(version, payload, token);

        public ValueTask StagePreparedStateAsync(DatabaseVersion version, Guid id, ReadOnlyMemory<byte> payload, CancellationToken token = default) => ValueTask.CompletedTask;

        public ValueTask BeginPreparedPublicationAsync(CancellationToken token = default) => ValueTask.CompletedTask;

        public ValueTask AbandonPreparedStatesAsync(IReadOnlyList<Guid> ids, CancellationToken token = default) => ValueTask.CompletedTask;

        public DatabaseTransactionFootprint DescribeTransaction(Guid id, ReadOnlyMemory<byte> payload) => new(new(0), [id.ToString()], [id.ToString()]);

        public IDisposable EnterPreparedCompletion(Guid id) => new Completion();

        private sealed class Completion : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }

    [TestMethod]
    public async Task Partition_abort_is_durable_and_cannot_be_reused_as_a_local_identity()
    {
        string path = CreatePath(), checkpoint = Path.Combine(_directory!, "mixed.checkpoint");
        Guid incarnation = Guid.NewGuid(), operation = Guid.NewGuid();
        string descriptor = new('A', 64);
        await using (var kernel = new DatabaseKernel(await GroupedRedoDatabaseCommitLog.OpenAsync(path), Changes(path), new CheckpointRuntime(),
            checkpointStore: new FileDatabaseCheckpointStore(checkpoint), durableDatabaseIncarnation: incarnation,
            versioningMode: DatabaseVersioningMode.PartitionLocal))
        {
            await kernel.RecoverAsync();
            await kernel.PreparePartitionTransactionAsync(operation, descriptor, new(0), new byte[] { 7 });
            Assert.IsNull(await kernel.CompletePartitionTransactionAsync(operation, descriptor, false));
            await kernel.CommitAsync(Guid.NewGuid(), new byte[] { 8 });
            await Assert.ThrowsExactlyAsync<EntityForgeDbCommitException>(async () => await kernel.CommitAsync(operation, new byte[] { 7 }));
        }
        await using var reopened = new DatabaseKernel(await GroupedRedoDatabaseCommitLog.OpenAsync(path), Changes(path), new CheckpointRuntime(),
            checkpointStore: new FileDatabaseCheckpointStore(checkpoint), durableDatabaseIncarnation: incarnation,
            versioningMode: DatabaseVersioningMode.PartitionLocal);
        await reopened.RecoverAsync();
        Assert.IsNull(await reopened.CompletePartitionTransactionAsync(operation, descriptor, false));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => await reopened.CompletePartitionTransactionAsync(operation, descriptor, true));
    }

    [DataTestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph)]
    [DataRow(DatabaseProfileId.Rdf)]
    [DataRow(DatabaseProfileId.Semantic)]
    public async Task Partition_local_kernel_allocates_its_clock_and_replays_without_external_versions(DatabaseProfileId profile)
    {
        string path = CreatePath();
        Guid incarnation = Guid.NewGuid(), original = Guid.NewGuid();
        await using (var kernel = new DatabaseKernel(await GroupedRedoDatabaseCommitLog.OpenAsync(path), Changes(path),
            new RecordingProfileRuntime(profile, GraphCodec, GraphCodecVersion), durableDatabaseIncarnation: incarnation,
            versioningMode: DatabaseVersioningMode.PartitionLocal))
        {
            await kernel.RecoverAsync();
            Assert.AreEqual(1L, (await kernel.CommitWithReceiptAsync(original, new byte[] { 1 })).Version.Value);
            await Assert.ThrowsExactlyAsync<NotSupportedException>(async () => await kernel.EnterDistributedWriteModeAsync());
            await Assert.ThrowsExactlyAsync<NotSupportedException>(async () => await kernel.RetainTransferHistoryAsync());
            await Assert.ThrowsExactlyAsync<NotSupportedException>(async () =>
            {
                await foreach (var item in kernel.ExportTransferCommitsAsync(new(0), new(1)))
                {
                    Assert.Fail("Partition data escaped through the global-version transfer codec.");
                }
            });
            await Assert.ThrowsExactlyAsync<NotSupportedException>(async () =>
                await kernel.ApplyAuthoritativeCommitAsync(Guid.NewGuid(), new byte[] { 2 }, new(900)));
            await Assert.ThrowsExactlyAsync<NotSupportedException>(async () =>
                await kernel.ApplyAuthoritativeCommitGroupAsync([new(Guid.NewGuid(), new byte[] { 2 }, new(900))]));
            Assert.AreEqual(2L, (await kernel.CommitWithReceiptAsync(Guid.NewGuid(), new byte[] { 2 })).Version.Value);
            Assert.AreEqual(1L, (await kernel.CommitWithReceiptAsync(original, new byte[] { 1 })).Version.Value);
        }
        await using (var wrongMode = new DatabaseKernel(await GroupedRedoDatabaseCommitLog.OpenAsync(path), Changes(path),
            new RecordingProfileRuntime(profile, GraphCodec, GraphCodecVersion), durableDatabaseIncarnation: incarnation))
        {
            await Assert.ThrowsExactlyAsync<EntityForgeDbFormatException>(async () => await wrongMode.RecoverAsync());
        }

        await using var reopened = new DatabaseKernel(await GroupedRedoDatabaseCommitLog.OpenAsync(path), Changes(path),
            new RecordingProfileRuntime(profile, GraphCodec, GraphCodecVersion), durableDatabaseIncarnation: incarnation,
            versioningMode: DatabaseVersioningMode.PartitionLocal);
        await reopened.RecoverAsync();
        Assert.AreEqual(2L, reopened.CurrentVersion.Value);
        Assert.AreEqual(1L, (await reopened.CommitWithReceiptAsync(original, new byte[] { 1 })).Version.Value);
        Assert.AreEqual(3L, (await reopened.CommitWithReceiptAsync(Guid.NewGuid(), new byte[] { 3 })).Version.Value);
    }

    [TestMethod]
    public async Task Enrollment_release_retries_after_bounded_admission_rejection()
    {
        string path = CreatePath();
        var admission = new DatabaseCommitCoordinator(maximumQueuedPublications: 1);
        await using var kernel = new DatabaseKernel(await GroupedRedoDatabaseCommitLog.OpenAsync(path),
            Changes(path), new RecordingProfileRuntime(DatabaseProfileId.PropertyGraph, GraphCodec, GraphCodecVersion),
            commitCoordinator: admission, durableDatabaseIncarnation: Guid.NewGuid());
        await kernel.RecoverAsync();
        var enrollment = await kernel.EnterDistributedWriteModeAsync();
        await admission.WaitAsync();
        Task waiting = admission.WaitAsync().AsTask();
        try
        {
            Assert.AreEqual(1, admission.Snapshot().WaitingPublications);
            await Assert.ThrowsExceptionAsync<DatabaseCommitAdmissionException>(async () => await enrollment.DisposeAsync());
        }
        finally
        {
            admission.Release();
            await waiting;
            admission.Release();
        }
        await Task.WhenAll(enrollment.DisposeAsync().AsTask(), enrollment.DisposeAsync().AsTask());
        await kernel.CommitAsync(Guid.NewGuid(), new byte[] { 1 });
        await using var nextEnrollment = await kernel.EnterDistributedWriteModeAsync();
    }

    [TestMethod]
    public async Task Partition_checkpoint_rejects_ordinary_recovery_after_wal_reclamation()
    {
        string path = CreatePath(), checkpoint = Path.Combine(_directory!, "partition.checkpoint");
        Guid incarnation = Guid.NewGuid();
        await using (var kernel = new DatabaseKernel(await GroupedRedoDatabaseCommitLog.OpenAsync(path), Changes(path),
            new CheckpointRuntime(), checkpointStore: new FileDatabaseCheckpointStore(checkpoint), durableDatabaseIncarnation: incarnation,
            versioningMode: DatabaseVersioningMode.PartitionLocal))
        {
            await kernel.RecoverAsync();
            await kernel.CommitAsync(Guid.NewGuid(), new byte[] { 7 });
            await kernel.CreateCheckpointAsync();
        }
        await using (var wrong = new DatabaseKernel(await GroupedRedoDatabaseCommitLog.OpenAsync(path), Changes(path),
            new CheckpointRuntime(), checkpointStore: new FileDatabaseCheckpointStore(checkpoint), durableDatabaseIncarnation: incarnation))
        {
            await Assert.ThrowsExactlyAsync<EntityForgeDbFormatException>(async () => await wrong.RecoverAsync());
        }

        var state = new CheckpointRuntime();
        await using var correct = new DatabaseKernel(await GroupedRedoDatabaseCommitLog.OpenAsync(path), Changes(path), state,
            checkpointStore: new FileDatabaseCheckpointStore(checkpoint), durableDatabaseIncarnation: incarnation,
            versioningMode: DatabaseVersioningMode.PartitionLocal);
        await correct.RecoverAsync();
        CollectionAssert.AreEqual(new byte[] { 7 }, state.State.ToArray());
        Assert.AreEqual(1L, correct.CurrentVersion.Value);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Physical_maintenance_requires_a_covering_checkpoint_and_does_not_replay_logical_state(bool covered)
    {
        string path = CreatePath();
        string checkpoint = Path.Combine(_directory!, "maintenance.checkpoint");
        Guid incarnation = Guid.NewGuid();
        await using (var kernel = new DatabaseKernel(await GroupedRedoDatabaseCommitLog.OpenAsync(path), Changes(path),
            new CheckpointRuntime(), checkpointStore: new FileDatabaseCheckpointStore(checkpoint), durableDatabaseIncarnation: incarnation))
        {
            await kernel.RecoverAsync();
            await kernel.CommitAsync(Guid.NewGuid(), new byte[] { 7 });
            if (covered)
            {
                await kernel.CreateCheckpointAsync();
            }
        }
        await using (var log = await GroupedRedoDatabaseCommitLog.OpenAsync(path))
        {
            await log.CommitAsync(new(incarnation, new(1), Guid.NewGuid(),
                new(DatabaseProfileId.PropertyGraph, GraphCodec, GraphCodecVersion, ReadOnlyMemory<byte>.Empty),
                DurableCommittedChangeKind.CheckpointMaintenance, null));
        }

        var state = new CheckpointRuntime();
        await using var reopened = new DatabaseKernel(await GroupedRedoDatabaseCommitLog.OpenAsync(path), Changes(path), state,
            checkpointStore: new FileDatabaseCheckpointStore(checkpoint), durableDatabaseIncarnation: incarnation);
        if (!covered)
        {
            await Assert.ThrowsExactlyAsync<EntityForgeDbFormatException>(async () => await reopened.RecoverAsync());
        }
        else
        {
            await reopened.RecoverAsync();
            CollectionAssert.AreEqual(new byte[] { 7 }, state.State.ToArray());
            Assert.AreEqual(0, state.ReplayedAfterRestore);
            Assert.AreEqual(1L, reopened.CurrentVersion.Value);
        }
    }

    [TestMethod]
    public async Task Enrollment_ahead_of_queued_local_write_rejects_without_waiting_for_unenrollment()
    {
        string path = CreatePath();
        await using var kernel = new DatabaseKernel(await GroupedRedoDatabaseCommitLog.OpenAsync(path), Changes(path),
            new RecordingProfileRuntime(DatabaseProfileId.PropertyGraph, GraphCodec, GraphCodecVersion), durableDatabaseIncarnation: Guid.NewGuid());
        await kernel.RecoverAsync();
        // Hold the admission gate solely to establish enrollment-before-local-entry ordering.
        var gate = (SemaphoreSlim)typeof(DatabaseKernel).GetField("_localWriteAuthority",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(kernel)!;
        await gate.WaitAsync();
        var enrollment = kernel.EnterDistributedWriteModeAsync().AsTask();
        var local = kernel.CommitAsync(Guid.NewGuid(), new byte[] { 1 }).AsTask();
        gate.Release();
        await using (var lease = await enrollment)
        {
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await local.WaitAsync(TimeSpan.FromSeconds(5)));
            await kernel.ApplyAuthoritativeCommitAsync(Guid.NewGuid(), new byte[] { 2 }, new(1));
        }
        await kernel.CommitAsync(Guid.NewGuid(), new byte[] { 3 });
    }

    [TestMethod]
    public async Task Distributed_retention_checkpoint_keeps_native_state_and_rejects_expired_commits_after_reopen()
    {
        string path = CreatePath();
        string checkpointPath = Path.Combine(_directory!, "retention.checkpoint");
        Guid incarnation = Guid.NewGuid();
        Guid old = EntityOperationIds.New(DateTimeOffset.UtcNow.AddDays(-2));
        Guid recent = EntityOperationIds.New();
        long floor = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds();
        await using (var kernel = new DatabaseKernel(await GroupedRedoDatabaseCommitLog.OpenAsync(path), Changes(path), new CheckpointRuntime(),
            checkpointStore: new FileDatabaseCheckpointStore(checkpointPath), durableDatabaseIncarnation: incarnation))
        {
            await kernel.RecoverAsync();
            await using var enrollment = await kernel.EnterDistributedWriteModeAsync();
            await kernel.ApplyAuthoritativeCommitAsync(old, new byte[] { 1 }, new(1));
            await kernel.ApplyAuthoritativeCommitAsync(recent, new byte[] { 2 }, new(2));
            await enrollment.RetainOperationsAsync(floor, new HashSet<Guid> { old });
            var checkpoint = (await new FileDatabaseCheckpointStore(checkpointPath).LoadAsync())!;
            Assert.AreEqual(floor, checkpoint.AuthoritativeOperationFloor);
            Assert.AreEqual(1, checkpoint.CommittedTransactions.Count);
            await Assert.ThrowsExactlyAsync<EntityOperationExpiredException>(async () => await kernel.ApplyAuthoritativeCommitAsync(old, new byte[] { 1 }, new(1)));
        }
        var state = new CheckpointRuntime();
        await using var reopened = new DatabaseKernel(await GroupedRedoDatabaseCommitLog.OpenAsync(path), Changes(path), state,
            checkpointStore: new FileDatabaseCheckpointStore(checkpointPath), durableDatabaseIncarnation: incarnation);
        await reopened.RecoverAsync();
        await using var retained = await reopened.EnterDistributedWriteModeAsync();
        CollectionAssert.AreEqual(new byte[] { 1, 2 }, state.State.ToArray());
        await Assert.ThrowsExactlyAsync<EntityOperationExpiredException>(async () => await reopened.ApplyAuthoritativeCommitAsync(old, new byte[] { 1 }, new(1)));
        await reopened.ApplyAuthoritativeCommitAsync(recent, new byte[] { 2 }, new(2));
        Assert.AreEqual(2, state.State.Count);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (_directory is not null && Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Profile_replay_pruning_is_published_only_after_retention_checkpoint_save(bool failSave)
    {
        string path = CreatePath();
        var runtime = new CheckpointRuntime();
        Guid expired = EntityOperationIds.New(DateTimeOffset.UtcNow.AddDays(-2));
        IDatabaseCheckpointStore store = failSave ? new FailingCheckpointStore()
            : new FileDatabaseCheckpointStore(Path.Combine(_directory!, "profile-retention.checkpoint"));
        await using var kernel = new DatabaseKernel(await GroupedRedoDatabaseCommitLog.OpenAsync(path),
            Changes(path), runtime, checkpointStore: store, durableDatabaseIncarnation: Guid.NewGuid());
        await kernel.RecoverAsync();
        await using var enrollment = await kernel.EnterDistributedWriteModeAsync();
        await kernel.ApplyAuthoritativeCommitAsync(expired, new byte[] { 1 }, new(1));
        long floor = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds();
        if (failSave)
        {
            await Assert.ThrowsExactlyAsync<IOException>(async () =>
                await enrollment.RetainOperationsAsync(floor, new HashSet<Guid> { expired }));
        }
        else
        {
            await enrollment.RetainOperationsAsync(floor, new HashSet<Guid> { expired });
        }

        Assert.AreEqual(1, runtime.RetentionCaptures);
        Assert.AreEqual(!failSave, runtime.Pruned.Contains(expired));
        if (!failSave)
        {
            var checkpoint = (await store.LoadAsync())!;
            Assert.AreEqual(floor, checkpoint.AuthoritativeOperationFloor);
            Assert.AreEqual(0, checkpoint.CommittedTransactions.Count);
        }
    }

    [TestMethod]
    public async Task Commit_is_durable_before_profile_state_is_published_and_recovers_after_restart()
    {
        string path = CreatePath();
        var firstRuntime = new RecordingProfileRuntime(DatabaseProfileId.PropertyGraph, GraphCodec, GraphCodecVersion);
        await using (var kernel = new DatabaseKernel(await GroupedRedoDatabaseCommitLog.OpenAsync(path), Changes(path), firstRuntime))
        {
            await kernel.RecoverAsync();
            DatabaseVersion version = await kernel.CommitAsync(Guid.NewGuid(), new byte[] { 42 });
            Assert.AreEqual(1L, version.Value);
            Assert.AreEqual(1, firstRuntime.Applied.Count);
        }

        var recoveredRuntime = new RecordingProfileRuntime(DatabaseProfileId.PropertyGraph, GraphCodec, GraphCodecVersion);
        await using var reopened = new DatabaseKernel(await GroupedRedoDatabaseCommitLog.OpenAsync(path), Changes(path), recoveredRuntime);
        await reopened.RecoverAsync();

        Assert.AreEqual(1L, reopened.CurrentVersion.Value);
        CollectionAssert.AreEqual(new byte[] { 42 }, recoveredRuntime.Applied.Single());
    }

    [TestMethod]
    public async Task Authoritative_commit_accepts_global_gaps_and_replays_only_at_the_same_version()
    {
        string path = CreatePath();
        var transaction = Guid.NewGuid();
        var runtime = new RecordingProfileRuntime(DatabaseProfileId.PropertyGraph, GraphCodec, GraphCodecVersion);
        await using var kernel = new DatabaseKernel(await GroupedRedoDatabaseCommitLog.OpenAsync(path),
            Changes(path), runtime, durableDatabaseIncarnation: Guid.NewGuid());
        await kernel.RecoverAsync();
        await using DatabaseDistributedWriteLease authority = await kernel.EnterDistributedWriteModeAsync();

        DatabaseCommitReceipt applied = await kernel.ApplyAuthoritativeCommitAsync(
            transaction, new byte[] { 42 }, new(17));
        DatabaseCommitReceipt replayed = await kernel.ApplyAuthoritativeCommitAsync(
            transaction, new byte[] { 42 }, new(17));

        Assert.AreEqual(17, applied.Version.Value);
        Assert.AreEqual(DatabaseCommitOutcome.IdempotentReplay, replayed.Outcome);
        Assert.AreEqual(17, kernel.DurableVersion.Value);
        await Assert.ThrowsExactlyAsync<EntityForgeDbIdempotencyException>(async () =>
            await kernel.ApplyAuthoritativeCommitAsync(transaction, new byte[] { 42 }, new(18)));
        EntityForgeDbCommitException stale = await Assert.ThrowsExactlyAsync<EntityForgeDbCommitException>(async () =>
            await kernel.ApplyAuthoritativeCommitAsync(Guid.NewGuid(), new byte[] { 43 }, new(16)));
        Assert.IsInstanceOfType<InvalidOperationException>(stale.InnerException);
    }

    [TestMethod]
    public async Task Authoritative_commit_version_survives_recovery()
    {
        string path = CreatePath();
        var transaction = Guid.NewGuid();
        await using (var kernel = new DatabaseKernel(await GroupedRedoDatabaseCommitLog.OpenAsync(path),
                         Changes(path), new RecordingProfileRuntime(DatabaseProfileId.PropertyGraph,
                             GraphCodec, GraphCodecVersion), durableDatabaseIncarnation: Guid.NewGuid()))
        {
            await kernel.RecoverAsync();
            await using DatabaseDistributedWriteLease authority = await kernel.EnterDistributedWriteModeAsync();
            await kernel.ApplyAuthoritativeCommitAsync(transaction, new byte[] { 7 }, new(23));
        }

        var recovered = new RecordingProfileRuntime(DatabaseProfileId.PropertyGraph, GraphCodec, GraphCodecVersion);
        await using var reopened = new DatabaseKernel(await GroupedRedoDatabaseCommitLog.OpenAsync(path),
            Changes(path), recovered);
        await reopened.RecoverAsync();
        Assert.AreEqual(23, reopened.DurableVersion.Value);
        CollectionAssert.AreEqual(new byte[] { 7 }, recovered.Applied.Single());
    }

    [TestMethod]
    public async Task Transfer_stream_preserves_authoritative_versions_payloads_and_idempotency()
    {
        string sourcePath = CreatePath();
        var sourceRuntime = new RecordingProfileRuntime(DatabaseProfileId.PropertyGraph, GraphCodec,
            GraphCodecVersion);
        await using var source = new DatabaseKernel(await GroupedRedoDatabaseCommitLog.OpenAsync(sourcePath),
            Changes(sourcePath), sourceRuntime, durableDatabaseIncarnation: Guid.NewGuid());
        await source.RecoverAsync();
        await using DatabaseDistributedWriteLease sourceAuthority = await source.EnterDistributedWriteModeAsync();
        Guid firstId = Guid.NewGuid(), secondId = Guid.NewGuid();
        await source.ApplyAuthoritativeCommitAsync(firstId, new byte[] { 1 }, new(7));
        await source.ApplyAuthoritativeCommitAsync(secondId, new byte[] { 2 }, new(11));

        IReadOnlyList<DatabaseTransferCommit> commits = await ReadAllAsync(
            source.ExportTransferCommitsAsync(new(0), new(11)));
        Assert.AreEqual(2, commits.Count);
        CollectionAssert.AreEqual(new long[] { 7, 11 }, commits.Select(item => item.Version.Value).ToArray());
        CollectionAssert.AreEqual(new long[] { 1, 2 }, commits.Select(item => item.SourceSequence).ToArray());
        CollectionAssert.AreEqual(commits[0].ChainHash.ToArray(), commits[1].PreviousChainHash.ToArray());

        string destinationPath = Path.Combine(_directory!, "transfer-destination");
        var destinationRuntime = new RecordingProfileRuntime(DatabaseProfileId.PropertyGraph, GraphCodec,
            GraphCodecVersion);
        await using var destination = new DatabaseKernel(
            await GroupedRedoDatabaseCommitLog.OpenAsync(destinationPath), Changes(destinationPath),
            destinationRuntime, durableDatabaseIncarnation: source.DatabaseIncarnation);
        await destination.RecoverAsync();
        await using DatabaseDistributedWriteLease destinationAuthority =
            await destination.EnterDistributedWriteModeAsync();
        foreach (DatabaseTransferCommit commit in commits)
        {
            await destination.ApplyTransferCommitAsync(commit);
        }

        DatabaseCommitReceipt replay = await destination.ApplyTransferCommitAsync(commits[1]);

        Assert.AreEqual(DatabaseCommitOutcome.IdempotentReplay, replay.Outcome);
        Assert.AreEqual(11, destination.CurrentVersion.Value);
        CollectionAssert.AreEqual(new byte[] { 1, 2 }, destinationRuntime.Applied.SelectMany(item => item).ToArray());
    }

    [TestMethod]
    public async Task Transfer_retention_prevents_checkpoint_from_reclaiming_required_delta()
    {
        string logPath = CreatePath();
        string checkpointPath = Path.Combine(_directory!, "transfer.checkpoint");
        await using var kernel = new DatabaseKernel(await GroupedRedoDatabaseCommitLog.OpenAsync(logPath),
            Changes(logPath), new CheckpointRuntime(), checkpointStore: new FileDatabaseCheckpointStore(checkpointPath),
            historyRetention: new FixedHistoryRetentionPolicy(DatabaseHistoryDisposition.Reclaim));
        await kernel.RecoverAsync();
        await kernel.CommitAsync(Guid.NewGuid(), new byte[] { 1 });
        await using DatabaseTransferRetentionLease retention = await kernel.RetainTransferHistoryAsync();
        await kernel.CommitAsync(Guid.NewGuid(), new byte[] { 2 });
        await kernel.CreateCheckpointAsync();

        IReadOnlyList<DatabaseTransferCommit> delta = await ReadAllAsync(
            kernel.ExportTransferCommitsAsync(new(1), new(2)));
        Assert.AreEqual(1, delta.Count);
        CollectionAssert.AreEqual(new byte[] { 2 }, delta[0].Payload.ToArray());
    }

    private static async Task<IReadOnlyList<T>> ReadAllAsync<T>(IAsyncEnumerable<T> source)
    {
        var result = new List<T>();
        await foreach (T item in source)
        {
            result.Add(item);
        }

        return result;
    }

    [TestMethod]
    public async Task Transfer_retention_admission_is_serialized_after_an_inflight_checkpoint()
    {
        string logPath = CreatePath();
        var runtime = new BlockingCheckpointRuntime();
        await using var kernel = new DatabaseKernel(await GroupedRedoDatabaseCommitLog.OpenAsync(logPath),
            Changes(logPath), runtime,
            checkpointStore: new FileDatabaseCheckpointStore(Path.Combine(_directory!, "atomic.checkpoint")));
        await kernel.RecoverAsync();
        await kernel.CommitAsync(Guid.NewGuid(), new byte[] { 1 });
        Task checkpoint = kernel.CreateCheckpointAsync().AsTask();
        await runtime.Entered.Task;

        Task<DatabaseTransferRetentionLease> retention = kernel.RetainTransferHistoryAsync().AsTask();
        await Task.Delay(50);
        Assert.IsFalse(retention.IsCompleted);
        runtime.Release.SetResult();
        await checkpoint;
        await using DatabaseTransferRetentionLease lease = await retention;
        Assert.AreEqual(1, lease.Boundary.Value);
    }

    [TestMethod]
    public async Task Distributed_write_enrollment_excludes_local_commits_and_requires_authority()
    {
        string path = CreatePath();
        await using var kernel = new DatabaseKernel(await GroupedRedoDatabaseCommitLog.OpenAsync(path),
            Changes(path), new RecordingProfileRuntime(DatabaseProfileId.PropertyGraph,
                GraphCodec, GraphCodecVersion), durableDatabaseIncarnation: Guid.NewGuid());
        await kernel.RecoverAsync();

        EntityForgeDbCommitException missingAuthority =
            await Assert.ThrowsExactlyAsync<EntityForgeDbCommitException>(async () =>
                await kernel.ApplyAuthoritativeCommitAsync(Guid.NewGuid(), new byte[] { 1 }, new(2)));
        Assert.IsInstanceOfType<InvalidOperationException>(missingAuthority.InnerException);

        await using (DatabaseDistributedWriteLease authority = await kernel.EnterDistributedWriteModeAsync())
        {
            Assert.AreEqual(kernel.DatabaseIncarnation, authority.DatabaseIncarnation);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
                await kernel.CommitAsync(Guid.NewGuid(), new byte[] { 2 }));
            await kernel.ApplyAuthoritativeCommitAsync(Guid.NewGuid(), new byte[] { 3 }, new(2));
        }

        Assert.AreEqual(3, (await kernel.CommitAsync(Guid.NewGuid(), new byte[] { 4 })).Value);
    }

    [TestMethod]
    public async Task Recovery_rejects_opening_graph_transactions_through_a_semantic_runtime()
    {
        string path = CreatePath();
        await using (var kernel = new DatabaseKernel(await GroupedRedoDatabaseCommitLog.OpenAsync(path), Changes(path),
                         new RecordingProfileRuntime(DatabaseProfileId.PropertyGraph, GraphCodec, GraphCodecVersion)))
        {
            await kernel.RecoverAsync();
            await kernel.CommitAsync(Guid.NewGuid(), new byte[] { 1 });
        }

        await using var wrongProfile = new DatabaseKernel(await GroupedRedoDatabaseCommitLog.OpenAsync(path), Changes(path),
            new RecordingProfileRuntime(DatabaseProfileId.Semantic, "entityforge.semantic.transactions", 1));
        await Assert.ThrowsExceptionAsync<EntityForgeDbFormatException>(async () =>
            await wrongProfile.RecoverAsync());
    }

    [TestMethod]
    public async Task Kernel_restores_profile_checkpoint_then_replays_only_later_transactions()
    {
        string logPath = CreatePath();
        string checkpointPath = Path.Combine(_directory!, "kernel.checkpoint");
        var first = new CheckpointRuntime();
        await using (var kernel = new DatabaseKernel(await GroupedRedoDatabaseCommitLog.OpenAsync(logPath), Changes(logPath), first,
                         checkpointStore: new FileDatabaseCheckpointStore(checkpointPath)))
        {
            await kernel.RecoverAsync();
            await kernel.CommitAsync(Guid.NewGuid(), new byte[] { 1 });
            await kernel.CreateCheckpointAsync();
            Assert.AreEqual(1, first.PublishedCheckpoints);
            await kernel.CommitAsync(Guid.NewGuid(), new byte[] { 2 });
        }

        var recovered = new CheckpointRuntime();
        await using var reopened = new DatabaseKernel(await GroupedRedoDatabaseCommitLog.OpenAsync(logPath), Changes(logPath), recovered,
            checkpointStore: new FileDatabaseCheckpointStore(checkpointPath));
        await reopened.RecoverAsync();

        CollectionAssert.AreEqual(new byte[] { 1, 2 }, recovered.State.ToArray());
        Assert.AreEqual(1, recovered.ReplayedAfterRestore);
        Assert.AreEqual(2L, reopened.CurrentVersion.Value);
    }

    [TestMethod]
    public async Task Profile_checkpoint_publication_is_not_announced_when_durable_save_fails()
    {
        string logPath = CreatePath();
        var runtime = new CheckpointRuntime();
        await using var kernel = new DatabaseKernel(await GroupedRedoDatabaseCommitLog.OpenAsync(logPath),
            Changes(logPath), runtime, checkpointStore: new FailingCheckpointStore());
        await kernel.RecoverAsync();
        await kernel.CommitAsync(Guid.NewGuid(), new byte[] { 1 });

        await Assert.ThrowsExactlyAsync<IOException>(async () => await kernel.CreateCheckpointAsync());
        Assert.AreEqual(0, runtime.PublishedCheckpoints);
    }

    [TestMethod]
    public async Task Checkpoint_capture_rejection_preserves_previous_checkpoint_and_later_wal()
    {
        string logPath = CreatePath();
        string checkpointPath = Path.Combine(_directory!, "bounded.checkpoint");
        var runtime = new CheckpointRuntime();
        await using (var kernel = new DatabaseKernel(await GroupedRedoDatabaseCommitLog.OpenAsync(logPath),
                         Changes(logPath), runtime,
                         checkpointStore: new FileDatabaseCheckpointStore(checkpointPath),
                         historyRetention: new FixedHistoryRetentionPolicy(DatabaseHistoryDisposition.Reclaim)))
        {
            await kernel.RecoverAsync();
            await kernel.CommitAsync(Guid.NewGuid(), new byte[] { 1 });
            await kernel.CreateCheckpointAsync();
            await kernel.CommitAsync(Guid.NewGuid(), new byte[] { 2 });
            runtime.RejectCheckpointCapture = true;
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
                await kernel.CreateCheckpointAsync());
        }

        var recovered = new CheckpointRuntime();
        await using var reopened = new DatabaseKernel(await GroupedRedoDatabaseCommitLog.OpenAsync(logPath),
            Changes(logPath), recovered, checkpointStore: new FileDatabaseCheckpointStore(checkpointPath));
        await reopened.RecoverAsync();
        CollectionAssert.AreEqual(new byte[] { 1, 2 }, recovered.State.ToArray());
        Assert.AreEqual(1, recovered.ReplayedAfterRestore);
    }

    [TestMethod]
    public async Task Recovery_accepts_wal_prefix_already_covered_by_atomically_replaced_checkpoint()
    {
        string logPath = CreatePath();
        string checkpointPath = Path.Combine(_directory!, "overlap.checkpoint");
        var first = new CheckpointRuntime();
        await using (var kernel = new DatabaseKernel(await GroupedRedoDatabaseCommitLog.OpenAsync(logPath),
                         Changes(logPath), first,
                         checkpointStore: new FileDatabaseCheckpointStore(checkpointPath,
                             observer: new FailAfterCheckpointReplacement())))
        {
            await kernel.RecoverAsync();
            await kernel.CommitAsync(Guid.NewGuid(), new byte[] { 1 });
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
                await kernel.CreateCheckpointAsync());
        }

        var recovered = new CheckpointRuntime();
        await using var reopened = new DatabaseKernel(await GroupedRedoDatabaseCommitLog.OpenAsync(logPath),
            Changes(logPath), recovered, checkpointStore: new FileDatabaseCheckpointStore(checkpointPath));
        await reopened.RecoverAsync();

        CollectionAssert.AreEqual(new byte[] { 1 }, recovered.State.ToArray());
        Assert.AreEqual(0, recovered.ReplayedAfterRestore);
        Assert.AreEqual(1L, reopened.CurrentVersion.Value);
    }

    [TestMethod]
    public async Task Checkpoint_retains_canonical_history_when_retention_policy_holds()
    {
        string logPath = CreatePath();
        string checkpointPath = Path.Combine(_directory!, "kernel.checkpoint");
        var policy = new FixedHistoryRetentionPolicy(DatabaseHistoryDisposition.Hold);
        await using (var kernel = new DatabaseKernel(await GroupedRedoDatabaseCommitLog.OpenAsync(logPath),
                         Changes(logPath), new CheckpointRuntime(),
                         checkpointStore: new FileDatabaseCheckpointStore(checkpointPath),
                         historyRetention: policy))
        {
            await kernel.RecoverAsync();
            await kernel.CommitAsync(Guid.NewGuid(), new byte[] { 1 });
            await kernel.CreateCheckpointAsync();
        }

        await using GroupedRedoDatabaseCommitLog log = await GroupedRedoDatabaseCommitLog.OpenAsync(logPath);
        Assert.AreEqual(1, (await log.RecoverAsync()).Count);
        Assert.AreEqual(DatabaseHistoryDisposition.Hold, policy.LastDecision);
    }

    [TestMethod]
    public async Task Checkpoint_reclaims_only_after_archive_policy_returns_durable_verification()
    {
        string logPath = CreatePath();
        string checkpointPath = Path.Combine(_directory!, "kernel.checkpoint");
        var policy = new FixedHistoryRetentionPolicy(DatabaseHistoryDisposition.ArchiveVerifiedAndReclaim);
        await using (var kernel = new DatabaseKernel(await GroupedRedoDatabaseCommitLog.OpenAsync(logPath),
                         Changes(logPath), new CheckpointRuntime(),
                         checkpointStore: new FileDatabaseCheckpointStore(checkpointPath),
                         historyRetention: policy))
        {
            await kernel.RecoverAsync();
            await kernel.CommitAsync(Guid.NewGuid(), new byte[] { 1 });
            await kernel.CreateCheckpointAsync();
        }

        await using GroupedRedoDatabaseCommitLog log = await GroupedRedoDatabaseCommitLog.OpenAsync(logPath);
        Assert.AreEqual(0, (await log.RecoverAsync()).Count);
        Assert.AreEqual(DatabaseHistoryDisposition.ArchiveVerifiedAndReclaim, policy.LastDecision);
    }

    [TestMethod]
    public async Task Publication_failure_after_durability_faults_writes_without_reusing_version()
    {
        string path = CreatePath();
        var runtime = new FailingPublicationRuntime();
        await using (var kernel = new DatabaseKernel(await GroupedRedoDatabaseCommitLog.OpenAsync(path), Changes(path), runtime))
        {
            await kernel.RecoverAsync();
            Guid operation = Guid.NewGuid();
            var failure = await Assert.ThrowsExceptionAsync<EntityOperationCommittedException<DatabaseCommitReceipt>>(async () =>
                await kernel.CommitAsync(operation, new byte[] { 1 }));
            Assert.AreEqual(operation, failure.Receipt.TransactionId);
            Assert.AreEqual(1L, failure.Receipt.Version.Value);
            Assert.AreEqual(DatabaseKernelState.RecoveryRequired, kernel.State);
            Assert.AreEqual(1L, kernel.DurableVersion.Value);
            Assert.AreEqual(0L, kernel.CurrentVersion.Value);
            await Assert.ThrowsExceptionAsync<EntityForgeDbRecoveryRequiredException>(async () =>
                await kernel.CommitAsync(Guid.NewGuid(), new byte[] { 2 }));
        }

        var recovered = new RecordingProfileRuntime(DatabaseProfileId.PropertyGraph, GraphCodec, GraphCodecVersion);
        await using var reopened = new DatabaseKernel(await GroupedRedoDatabaseCommitLog.OpenAsync(path), Changes(path), recovered);
        await reopened.RecoverAsync();
        Assert.AreEqual(1L, reopened.CurrentVersion.Value);
    }

    [TestMethod]
    public async Task Published_read_waits_until_profile_state_and_version_are_coherent()
    {
        string path = CreatePath();
        var runtime = new BlockingPublicationRuntime();
        await using var kernel = new DatabaseKernel(await GroupedRedoDatabaseCommitLog.OpenAsync(path),
            Changes(path), runtime);
        await kernel.RecoverAsync();

        Task<DatabaseVersion> commit = kernel.CommitAsync(Guid.NewGuid(), new byte[] { 1 }).AsTask();
        await runtime.Entered.Task;
        Task<DatabaseVersion> read = Task.Run(() => kernel.ReadPublished(version => version));
        await Task.Delay(50);
        Assert.IsFalse(read.IsCompleted);

        runtime.Release.SetResult();
        Assert.AreEqual(new DatabaseVersion(1), await commit);
        Assert.AreEqual(new DatabaseVersion(1), await read);
    }

    [TestMethod]
    public async Task Published_read_releases_a_captured_result_when_shutdown_wins_the_capture()
    {
        string path = CreatePath();
        var kernel = new DatabaseKernel(await GroupedRedoDatabaseCommitLog.OpenAsync(path), Changes(path),
            new RecordingProfileRuntime(DatabaseProfileId.PropertyGraph, GraphCodec, GraphCodecVersion));
        await kernel.RecoverAsync();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var captured = new TrackedDisposable();

        Task<TrackedDisposable> read = Task.Run(() => kernel.ReadPublished(_ =>
        {
            entered.Set();
            release.Wait();
            return captured;
        }));
        Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(10)));
        await kernel.DisposeAsync();
        release.Set();

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () => await read);
        Assert.AreEqual(1, captured.DisposeCount);
    }

    [TestMethod]
    public async Task Wal_failure_faults_live_kernel_and_reopen_recovers_without_version_reuse()
    {
        string path = CreatePath();
        Guid first = Guid.NewGuid();
        var runtime = new RecordingProfileRuntime(DatabaseProfileId.PropertyGraph, GraphCodec, GraphCodecVersion);
        await using (var kernel = new DatabaseKernel(await GroupedRedoDatabaseCommitLog.OpenAsync(path,
                         new(maximumGroupDelay: TimeSpan.Zero), new ThrowOnceRedoObserver(RedoDurabilityStage.AfterWalFlush)),
                     Changes(path), runtime))
        {
            await kernel.RecoverAsync();
            await Assert.ThrowsExceptionAsync<EntityForgeDbCommitException>(async () =>
                await kernel.CommitAsync(first, new byte[] { 1 }));
            Assert.AreEqual(DatabaseKernelState.RecoveryRequired, kernel.State);
            await Assert.ThrowsExceptionAsync<EntityForgeDbRecoveryRequiredException>(async () =>
                await kernel.CommitAsync(Guid.NewGuid(), new byte[] { 2 }));
        }

        var recovered = new RecordingProfileRuntime(DatabaseProfileId.PropertyGraph, GraphCodec, GraphCodecVersion);
        await using var reopened = new DatabaseKernel(await GroupedRedoDatabaseCommitLog.OpenAsync(path),
            Changes(path), recovered);
        await reopened.RecoverAsync();
        Assert.AreEqual(new DatabaseVersion(1), reopened.CurrentVersion);
        Assert.AreEqual(1, recovered.Applied.Count);
    }

    [TestMethod]
    public async Task Wal_and_abandonment_failures_preserve_both_errors_and_release_admission()
    {
        string path = CreatePath();
        var runtime = new FailingAbandonmentRuntime();
        await using var kernel = new DatabaseKernel(await GroupedRedoDatabaseCommitLog.OpenAsync(path,
            new(maximumGroupDelay: TimeSpan.Zero), new ThrowOnceRedoObserver(RedoDurabilityStage.BeforeWalWrite)),
            Changes(path), runtime);
        await kernel.RecoverAsync();

        AggregateException failure = await Assert.ThrowsExceptionAsync<AggregateException>(async () =>
            await kernel.CommitAsync(Guid.NewGuid(), new byte[] { 1 }));

        Assert.IsInstanceOfType<EntityForgeDbCommitException>(failure.InnerExceptions[0]);
        StringAssert.Contains(failure.InnerExceptions[1].Message, "abandonment");
        Assert.AreEqual(DatabaseKernelState.RecoveryRequired, kernel.State);
        await Assert.ThrowsExceptionAsync<EntityForgeDbRecoveryRequiredException>(async () =>
            await kernel.CommitAsync(Guid.NewGuid(), new byte[] { 2 }).AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [TestMethod]
    public async Task Transaction_identity_reuse_with_different_content_fails_closed()
    {
        string path = CreatePath();
        Guid transactionId = Guid.NewGuid();
        await using var kernel = new DatabaseKernel(await GroupedRedoDatabaseCommitLog.OpenAsync(path), Changes(path),
            new RecordingProfileRuntime(DatabaseProfileId.PropertyGraph, GraphCodec, GraphCodecVersion));
        await kernel.RecoverAsync();
        DatabaseVersion first = await kernel.CommitAsync(transactionId, new byte[] { 1 });
        Assert.AreEqual(first, await kernel.CommitAsync(transactionId, new byte[] { 1 }));
        await Assert.ThrowsExceptionAsync<EntityForgeDbIdempotencyException>(async () =>
            await kernel.CommitAsync(transactionId, new byte[] { 2 }));
    }

    [TestMethod]
    public async Task Logical_transaction_and_staged_physical_page_are_protected_and_published_atomically()
    {
        string path = CreatePath();
        var pageId = new PhysicalPageId(0);
        byte[] before = new SlottedDataPage(PhysicalPageFileOptions.DefaultPageSize, pageId).Encode();
        var changed = new SlottedDataPage(PhysicalPageFileOptions.DefaultPageSize, pageId);
        Assert.IsTrue(changed.TryInsert(new byte[] { 7, 8, 9 }, 1, out _));
        var runtime = new PhysicalPlanningRuntime(new DatabasePhysicalPageMutation(
            pageId.Value, 0, changed.Encode(), before));
        await using (var kernel = new DatabaseKernel(await GroupedRedoDatabaseCommitLog.OpenAsync(path),
                         Changes(path), runtime))
        {
            await kernel.RecoverAsync();
            await kernel.CommitAsync(Guid.NewGuid(), new byte[] { 1 });
            Assert.AreEqual(1, runtime.Published);
        }

        string pagesPath = Path.Combine(_directory!, "physical.pages");
        await using GroupedRedoDatabaseCommitLog reopened = await GroupedRedoDatabaseCommitLog.OpenAsync(path);
        await using PhysicalPageFile pages = await PhysicalPageFile.OpenAsync(pagesPath);
        await reopened.RecoverPagesAsync(pages);
        SlottedDataPage restored = SlottedDataPage.Decode((await pages.ReadAsync(pageId)).Span, pageId);
        CollectionAssert.AreEqual(new byte[] { 7, 8, 9 }, restored.OccupiedRecords.Single().ToArray());
    }

    [TestMethod]
    public async Task Failed_physical_preparation_is_abandoned_without_advancing_durability()
    {
        string path = CreatePath();
        var runtime = new FailingPhysicalPlanningRuntime();
        await using var kernel = new DatabaseKernel(await GroupedRedoDatabaseCommitLog.OpenAsync(path),
            Changes(path), runtime);
        await kernel.RecoverAsync();

        await Assert.ThrowsExceptionAsync<EntityForgeDbCommitException>(async () =>
            await kernel.CommitAsync(Guid.NewGuid(), new byte[] { 1 }));

        Assert.AreEqual(1, runtime.Abandoned);
        Assert.AreEqual(0L, kernel.DurableVersion.Value);
        Assert.AreEqual(DatabaseKernelState.Ready, kernel.State);
    }

    private string CreatePath()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"entityforge-kernel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        return Path.Combine(_directory, "kernel.wal");
    }

    private static SegmentedDatabaseCommittedChangeStore Changes(string logPath) => new(logPath + ".changes");

    private sealed class RecordingProfileRuntime(
        DatabaseProfileId profile, string codecId, int codecVersion) : IDatabaseProfileRuntime
    {
        public DatabaseProfileId Profile { get; } = profile;

        public string CodecId { get; } = codecId;

        public int CodecVersion { get; } = codecVersion;

        public List<byte[]> Applied { get; } = [];

        public ValueTask ApplyAsync(DatabaseVersion version, Guid transactionId,
            ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        {
            Applied.Add(payload.ToArray());
            return ValueTask.CompletedTask;
        }
    }

    private class CheckpointRuntime(DatabaseProfileId profile = DatabaseProfileId.PropertyGraph) : ICheckpointableDatabaseProfileRuntime,
        IDatabaseProfileCheckpointPublicationObserver, IDatabaseProfileOperationRetention
    {
        public DatabaseProfileId Profile => profile;

        public string CodecId => GraphCodec;

        public int CodecVersion => GraphCodecVersion;

        public string CheckpointCodecId => "entityforge.test.graph.checkpoint";

        public int CheckpointCodecVersion => 1;

        public List<byte> State { get; } = [];

        public int ReplayedAfterRestore
        {
            get; private set;
        }

        public int PublishedCheckpoints
        {
            get; private set;
        }

        public bool RejectCheckpointCapture
        {
            get; set;
        }

        public bool FailNextApply
        {
            get; set;
        }

        public int RetentionCaptures
        {
            get; private set;
        }

        public HashSet<Guid> Pruned { get; } = [];

        public ValueTask<ReadOnlyMemory<byte>> CaptureRetentionCheckpointAsync(IReadOnlySet<Guid> expired,
            CancellationToken cancellationToken)
        {
            Assert.AreEqual(0, Pruned.Count, "Capture must not prune live replay evidence.");
            RetentionCaptures++;
            return CaptureCheckpointAsync(cancellationToken);
        }

        public void RetentionCheckpointPublished(IReadOnlySet<Guid> expired) => Pruned.UnionWith(expired);

        private bool _restored;

        public ValueTask ApplyAsync(DatabaseVersion version, Guid transactionId, ReadOnlyMemory<byte> payload,
            CancellationToken cancellationToken = default)
        {
            State.AddRange(payload.ToArray());
            if (FailNextApply)
            {
                FailNextApply = false;
                throw new IOException("Injected native publication failure.");
            }
            if (_restored)
            {
                ReplayedAfterRestore++;
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask<ReadOnlyMemory<byte>> CaptureCheckpointAsync(CancellationToken cancellationToken = default) =>
            RejectCheckpointCapture
                ? ValueTask.FromException<ReadOnlyMemory<byte>>(
                    new InvalidOperationException("Injected checkpoint encoding rejection."))
                : ValueTask.FromResult<ReadOnlyMemory<byte>>(State.ToArray());

        public ValueTask RestoreCheckpointAsync(DatabaseVersion version, ReadOnlyMemory<byte> payload,
            CancellationToken cancellationToken = default)
        {
            State.AddRange(payload.ToArray());
            _restored = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask CheckpointPublishedAsync(DatabaseVersion version,
            CancellationToken cancellationToken = default)
        {
            PublishedCheckpoints++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailingCheckpointStore : IDatabaseCheckpointStore
    {
        public ValueTask SaveAsync(DurableCheckpointEnvelope checkpoint,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("Injected checkpoint save failure."));

        public ValueTask<DurableCheckpointEnvelope?> LoadAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<DurableCheckpointEnvelope?>(null);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BlockingCheckpointRuntime : ICheckpointableDatabaseProfileRuntime
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public DatabaseProfileId Profile => DatabaseProfileId.PropertyGraph;

        public string CodecId => GraphCodec;

        public int CodecVersion => GraphCodecVersion;

        public string CheckpointCodecId => "entityforge.test.graph.blocking-checkpoint";

        public int CheckpointCodecVersion => 1;

        public ValueTask ApplyAsync(DatabaseVersion version, Guid transactionId, ReadOnlyMemory<byte> payload,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public async ValueTask<ReadOnlyMemory<byte>> CaptureCheckpointAsync(
            CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return ReadOnlyMemory<byte>.Empty;
        }

        public ValueTask RestoreCheckpointAsync(DatabaseVersion version, ReadOnlyMemory<byte> payload,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class FailingPublicationRuntime : IDatabaseProfileRuntime
    {
        public DatabaseProfileId Profile => DatabaseProfileId.PropertyGraph;

        public string CodecId => GraphCodec;

        public int CodecVersion => GraphCodecVersion;

        public ValueTask ApplyAsync(DatabaseVersion version, Guid transactionId,
            ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new InvalidOperationException("Injected publication failure."));
    }

    private sealed class BlockingPublicationRuntime : IDatabaseProfileRuntime
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public DatabaseProfileId Profile => DatabaseProfileId.PropertyGraph;

        public string CodecId => GraphCodec;

        public int CodecVersion => GraphCodecVersion;

        public async ValueTask ApplyAsync(DatabaseVersion version, Guid transactionId,
            ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class TrackedDisposable : IDisposable
    {
        public int DisposeCount
        {
            get; private set;
        }

        public void Dispose() => DisposeCount++;
    }

    private sealed class FailAfterCheckpointReplacement : IDatabaseCheckpointPublicationObserver
    {
        public void Reached(DatabaseCheckpointPublicationStage stage)
        {
            if (stage == DatabaseCheckpointPublicationStage.AfterReplacement)
            {
                throw new InvalidOperationException("Injected post-replacement failure.");
            }
        }
    }

    private sealed class ThrowOnceRedoObserver(RedoDurabilityStage target) : IRedoDurabilityObserver
    {
        private int _thrown;

        public void Reached(RedoDurabilityStage stage)
        {
            if (stage == target && Interlocked.Exchange(ref _thrown, 1) == 0)
            {
                throw new IOException("Injected WAL fault.");
            }
        }
    }

    private sealed class FixedHistoryRetentionPolicy(DatabaseHistoryDisposition decision) :
        IDatabaseHistoryRetentionPolicy
    {
        public DatabaseHistoryDisposition? LastDecision
        {
            get; private set;
        }

        public ValueTask<DatabaseHistoryDisposition> DecideAsync(DatabaseHistoryReclamationRequest request,
            CancellationToken cancellationToken = default)
        {
            LastDecision = decision;
            return ValueTask.FromResult(decision);
        }
    }

    private sealed class PhysicalPlanningRuntime(DatabasePhysicalPageMutation mutation) :
        IDatabaseProfileRuntime, IDatabaseProfilePhysicalMutationPlanner
    {
        public DatabaseProfileId Profile => DatabaseProfileId.PropertyGraph;

        public string CodecId => GraphCodec;

        public int CodecVersion => GraphCodecVersion;

        public int Published
        {
            get; private set;
        }

        public ValueTask ApplyAsync(DatabaseVersion version, Guid transactionId,
            ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask<IReadOnlyList<DatabasePhysicalPageMutation>> PreparePhysicalMutationsAsync(
            DatabaseVersion version, Guid transactionId, ReadOnlyMemory<byte> payload,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<DatabasePhysicalPageMutation>>([mutation]);

        public ValueTask PublishPhysicalMutationsAsync(Guid transactionId,
            CancellationToken cancellationToken = default)
        {
            Published++;
            return ValueTask.CompletedTask;
        }

        public ValueTask AbandonPhysicalMutationsAsync(Guid transactionId,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class FailingPhysicalPlanningRuntime : IDatabaseProfileRuntime,
        IDatabaseProfilePhysicalMutationPlanner
    {
        public DatabaseProfileId Profile => DatabaseProfileId.PropertyGraph;

        public string CodecId => GraphCodec;

        public int CodecVersion => GraphCodecVersion;

        public int Abandoned
        {
            get; private set;
        }

        public ValueTask ApplyAsync(DatabaseVersion version, Guid transactionId,
            ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask<IReadOnlyList<DatabasePhysicalPageMutation>> PreparePhysicalMutationsAsync(
            DatabaseVersion version, Guid transactionId, ReadOnlyMemory<byte> payload,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<IReadOnlyList<DatabasePhysicalPageMutation>>(
                new InvalidOperationException("Injected physical preparation failure."));

        public ValueTask PublishPhysicalMutationsAsync(Guid transactionId,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask AbandonPhysicalMutationsAsync(Guid transactionId,
            CancellationToken cancellationToken = default)
        {
            Abandoned++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailingAbandonmentRuntime : IDatabaseProfileRuntime,
        IDatabaseProfilePhysicalMutationPlanner
    {
        public DatabaseProfileId Profile => DatabaseProfileId.PropertyGraph;

        public string CodecId => GraphCodec;

        public int CodecVersion => GraphCodecVersion;

        public ValueTask ApplyAsync(DatabaseVersion version, Guid transactionId, ReadOnlyMemory<byte> payload,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask<IReadOnlyList<DatabasePhysicalPageMutation>> PreparePhysicalMutationsAsync(
            DatabaseVersion version, Guid transactionId, ReadOnlyMemory<byte> payload,
            CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<DatabasePhysicalPageMutation>>(
            [DatabasePhysicalPageMutation.CreatePage(1, new byte[PhysicalPageFileOptions.DefaultPageSize])]);

        public ValueTask PublishPhysicalMutationsAsync(Guid transactionId,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask AbandonPhysicalMutationsAsync(Guid transactionId,
            CancellationToken cancellationToken = default) => ValueTask.FromException(
            new IOException("Injected physical abandonment failure."));
    }
}
