using EntityForge.Db.Mapping;
using System.Diagnostics;
using System.Security.Cryptography;

using EntityForge.Db.Durability;
using EntityForge.Db.Storage;

namespace EntityForge.Db.Kernel;

/// <summary>A profile operation submitted for kernel-assigned versioning.</summary>
/// <param name="TransactionId">Stable operation identity used for retained replay and conflict checks.</param>
/// <param name="Payload">Encoded profile operation. Constructing this record does not copy its backing memory.</param>
public sealed record DatabaseCommitRequest(Guid TransactionId, ReadOnlyMemory<byte> Payload);

/// <summary>A profile operation carrying an externally assigned authoritative version.</summary>
/// <param name="TransactionId">Stable operation identity used for retained replay and conflict checks.</param>
/// <param name="Payload">Encoded profile operation. Constructing this record does not copy its backing memory.</param>
/// <param name="Version">Authoritative version, validated by the commit path against current state.</param>
public sealed record DatabaseAuthoritativeCommitRequest(Guid TransactionId, ReadOnlyMemory<byte> Payload, DatabaseVersion Version);

/// <summary>Coordinates version assignment, durable ordering, recovery, and profile dispatch.</summary>
public sealed partial class DatabaseKernel : IDatabaseCommittedChangeJournal, IDatabaseDistributedWriteAuthority,
    IDatabaseTransferRuntime, IEntityOperationCompactionAuthority, IEntityOperationAtomicCommitAuthority
{
    /// <summary>Gets the profile whose state and operations this kernel coordinates.</summary>
    public DatabaseProfileId Profile => _profile.Profile;

    /// <summary>Gets the version-assignment model fixed when this kernel was constructed.</summary>
    public DatabaseVersioningMode VersioningMode
    {
        get;
    }

    private static readonly DiagnosticListener MaintenanceDiagnostics = new("EntityForge.Db.Kernel.CheckpointMaintenance");

    private static void ObserveMaintenance(string stage)
    {
        if (MaintenanceDiagnostics.IsEnabled(stage))
        {
            MaintenanceDiagnostics.Write(stage, (object?)null);
        }
    }

    private const string GroupValidationPhase = "group_validation";
    private const string StateStagingPhase = "state_staging";
    private const string PlanningPhase = "planning";
    private const string PhysicalPreparationPhase = "physical_preparation";
    private const string DurableAppendPhase = "durable_append";
    private const string ChangeMaterializationPhase = "change_materialization";
    private const string PublicationPhase = "publication";

    private readonly IDatabaseCommitLog _commitLog;
    private readonly IDatabaseCommittedChangeStore _changeStore;
    private readonly IDatabaseProfileRuntime _profile;
    private readonly IDatabaseCheckpointStore? _checkpointStore;
    private readonly DatabaseCommitCoordinator _commitCoordinator;
    private readonly Dictionary<Guid, CommittedTransaction> _committedTransactions = [];
    private readonly DatabaseKernelTelemetry _telemetry;
    private readonly bool _ownsTelemetry;
    private readonly IDatabaseChangeCursorCodec _cursorCodec;
    private readonly CommittedChangeCoordinator _changes;
    private readonly DatabaseChangeFeed _changeFeed;
    private readonly IDatabaseHistoryRetentionPolicy _historyRetention;
    private readonly SemaphoreSlim _localWriteAuthority = new(1, 1);
    private readonly SemaphoreSlim _checkpointPublication = new(1, 1);
    private readonly object _transferRetentionSync = new();
    private readonly Dictionary<Guid, DatabaseVersion> _transferRetentions = [];
    private readonly ManualResetEventSlim _readerPublicationReady = new(initialState: true);

    private long _version;
    private long _durableVersion;
    private long _readerPublicationEpoch;
    private TaskCompletionSource? _readerPublicationCompletion;
    private Guid _databaseIncarnation;
    private DatabaseChangeCursor? _checkpointChangeCursor;
    private long _authoritativeOperationFloor;
    private DatabaseKernelState _state = DatabaseKernelState.Created;
    private Guid? _distributedWriteLeaseId;
    private int _distributedWriteMode;

    /// <summary>Whether ordinary local commits are excluded by distributed enrollment.</summary>
    /// <remarks>This observation does not grant admission; callers must still coordinate mode transitions with their work.</remarks>
    public bool IsDistributedWriteMode => Volatile.Read(ref _distributedWriteMode) != 0;

    private readonly bool _hasDurableDatabaseIncarnation;
    private readonly object _disposeSync = new();
    private Task? _disposeTask;

    /// <summary>Constructs a kernel in the created state; recovery is required before serving database operations.</summary>
    /// <param name="commitLog">Commit log disposed by the kernel during shutdown.</param>
    /// <param name="changeStore">Committed-change storage disposed by the kernel during shutdown.</param>
    /// <param name="profile">Profile runtime with a nonblank codec identity and positive codec version.</param>
    /// <param name="commitCoordinator">Commit coordinator, or null to create one. The kernel disposes the coordinator even when supplied.</param>
    /// <param name="checkpointStore">Optional checkpoint storage, disposed by the kernel during shutdown.</param>
    /// <param name="telemetry">Caller-owned telemetry, or null to create telemetry owned by this kernel.</param>
    /// <param name="cursorCodec">Change-cursor codec, or null for the default codec.</param>
    /// <param name="historyRetention">History-retention policy, or null for checkpoint-based retention.</param>
    /// <param name="durableDatabaseIncarnation">Known durable database identity, or null to establish it during recovery. An empty GUID is invalid.</param>
    /// <param name="versioningMode">Version-assignment model for this kernel instance.</param>
    /// <param name="timestampClock">Optional bounded timestamp clock; only valid with partition-local versioning.</param>
    /// <exception cref="ArgumentNullException">A required log, change store or profile is null.</exception>
    /// <exception cref="ArgumentException">Profile codec metadata, database identity, versioning mode or clock configuration is invalid.</exception>
    /// <remarks>The profile is not generally disposed by the kernel. Shutdown does stop its incremental checkpoint compaction when that capability is implemented. Constructor failure does not constitute a completed shutdown of supplied dependencies.</remarks>
    public DatabaseKernel(IDatabaseCommitLog commitLog, IDatabaseCommittedChangeStore changeStore,
        IDatabaseProfileRuntime profile,
        DatabaseCommitCoordinator? commitCoordinator = null, IDatabaseCheckpointStore? checkpointStore = null,
        DatabaseKernelTelemetry? telemetry = null, IDatabaseChangeCursorCodec? cursorCodec = null,
        IDatabaseHistoryRetentionPolicy? historyRetention = null, Guid? durableDatabaseIncarnation = null,
        DatabaseVersioningMode versioningMode = DatabaseVersioningMode.LocalOrAuthoritative,
        IDatabaseBoundedClock? timestampClock = null)
    {
        if (!Enum.IsDefined(versioningMode))
        {
            throw new ArgumentOutOfRangeException(nameof(versioningMode));
        }

        VersioningMode = versioningMode;
        if (timestampClock is not null && versioningMode != DatabaseVersioningMode.PartitionLocal)
        {
            throw new ArgumentException("Timestamp visibility requires partition-local versioning.", nameof(timestampClock));
        }

        TimestampCostOptions = timestampClock is not null ? DatabaseTimestampCostOptions.FromClock(timestampClock) :
            DatabaseTimestampCostOptions.QualificationOverride.Value ?? DatabaseTimestampCostOptions.FromEnvironment(versioningMode);
        _commitLog = commitLog ?? throw new ArgumentNullException(nameof(commitLog));
        _changeStore = changeStore ?? throw new ArgumentNullException(nameof(changeStore));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        ArgumentException.ThrowIfNullOrWhiteSpace(profile.CodecId);
        if (profile.CodecVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(profile));
        }

        _commitCoordinator = commitCoordinator ?? new DatabaseCommitCoordinator();
        _checkpointStore = checkpointStore;
        _telemetry = telemetry ?? new DatabaseKernelTelemetry();
        _ownsTelemetry = telemetry is null;
        _cursorCodec = cursorCodec ?? new DatabaseChangeCursorCodec();
        _historyRetention = historyRetention ?? new CheckpointDatabaseHistoryRetentionPolicy();
        if (durableDatabaseIncarnation == Guid.Empty)
        {
            throw new ArgumentException("A durable database incarnation cannot be empty.",
                nameof(durableDatabaseIncarnation));
        }

        _databaseIncarnation = durableDatabaseIncarnation ?? Guid.Empty;
        _hasDurableDatabaseIncarnation = durableDatabaseIncarnation is not null;
        _changes = new(_changeStore, _cursorCodec, () => _databaseIncarnation, () => DurableVersion);
        _changeFeed = new(_changeStore, _changes, _commitCoordinator, EnsureReady, _telemetry, _profile.Profile);
    }

    /// <summary>Gets the latest version installed in the profile's logical state.</summary>
    /// <remarks>A version observation alone does not capture a consistent profile view. Use a profile snapshot or a published-read capture when reading state associated with a version.</remarks>
    public DatabaseVersion CurrentVersion => new(Interlocked.Read(ref _version));

    internal long AuthoritativeOperationFloor => Interlocked.Read(ref _authoritativeOperationFloor);

    /// <summary>Gets the highest version recorded as durable by the kernel.</summary>
    /// <remarks>This can advance before logical publication completes. It is sampled separately from <see cref="CurrentVersion"/> and does not by itself establish that a corresponding profile view is readable.</remarks>
    public DatabaseVersion DurableVersion => new(Interlocked.Read(ref _durableVersion));

    /// <summary>Gets the storage layout reported by the configured profile runtime.</summary>
    public DatabaseStorageLayout StorageLayout => _profile.StorageLayout;

    /// <summary>Gets the identity of the recovered database generation.</summary>
    /// <remarks>The value can be empty before recovery when no durable incarnation was supplied to the constructor.</remarks>
    public Guid DatabaseIncarnation => _databaseIncarnation;

    /// <summary>Gets the observed kernel lifecycle state.</summary>
    /// <remarks>Reading this property does not reserve admission or prevent a concurrent state transition.</remarks>
    public DatabaseKernelState State => _state;

    /// <summary>Captures profile data across one completed kernel publication epoch, retrying when publication changes during capture.</summary>
    /// <typeparam name="T">The captured result type.</typeparam>
    /// <param name="read">Synchronously captures the view associated with the supplied current version.</param>
    /// <returns>The capture accepted after its publication epoch and kernel readiness are checked.</returns>
    /// <remarks>
    /// The callback can execute more than once and must tolerate retry without externally visible side effects.
    /// If a captured result cannot be returned, the kernel disposes it when it implements <see cref="IDisposable"/>;
    /// asynchronous-only disposal is not performed. The caller owns an accepted result and its lifetime.
    /// The callback must capture a stable view or copy: returning mutable state does not pin it after this call.
    /// This method can block while publication is in progress; the asynchronous overload permits cancellation while waiting.
    /// </remarks>
    /// <exception cref="ArgumentNullException">The callback is null.</exception>
    /// <exception cref="InvalidOperationException">The kernel is not ready.</exception>
    /// <exception cref="EntityForgeDbRecoveryRequiredException">The database must be reopened for recovery before reads can continue.</exception>
    /// <exception cref="AggregateException">A capture cannot be returned and disposal also fails.</exception>
    public T ReadPublished<T>(Func<DatabaseVersion, T> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        while (true)
        {
            _readerPublicationReady.Wait();
            if (TryReadPublished(read, CancellationToken.None, out T result))
            {
                return result;
            }
        }
    }

    /// <summary>Asynchronously waits for publication and captures one completed epoch, retrying a capture invalidated by publication.</summary>
    /// <typeparam name="T">The captured result type.</typeparam>
    /// <param name="read">Synchronously captures the view associated with the supplied current version; it can be invoked repeatedly.</param>
    /// <param name="cancellationToken">Cancels waiting and is checked before returning an accepted capture.</param>
    /// <returns>The capture accepted after its publication epoch and kernel readiness are checked.</returns>
    /// <remarks>
    /// Only the wait is asynchronous. The callback runs synchronously and cancellation does not interrupt it.
    /// Captures must be safe to retry and must not rely on exactly-once side effects. A rejected capture is
    /// disposed when it implements <see cref="IDisposable"/>; asynchronous-only disposal is not performed.
    /// The caller owns the accepted result. A returned mutable reference is not automatically a retained snapshot.
    /// </remarks>
    /// <exception cref="ArgumentNullException">The callback is null.</exception>
    /// <exception cref="InvalidOperationException">The kernel is not ready.</exception>
    /// <exception cref="EntityForgeDbRecoveryRequiredException">The database must be reopened for recovery before reads can continue.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is requested while waiting or before returning the capture.</exception>
    /// <exception cref="AggregateException">A capture cannot be returned and disposal also fails.</exception>
    public async ValueTask<T> ReadPublishedAsync<T>(Func<DatabaseVersion, T> read,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(read);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task ready = Volatile.Read(ref _readerPublicationCompletion)?.Task ?? Task.CompletedTask;
            await ready.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (TryReadPublished(read, cancellationToken, out T result))
            {
                return result;
            }
        }
    }

    private bool TryReadPublished<T>(Func<DatabaseVersion, T> read, CancellationToken cancellationToken, out T result)
    {
        result = default!;
        EnsureReady();
        long epoch = Interlocked.Read(ref _readerPublicationEpoch);
        if ((epoch & 1) != 0)
        {
            return false;
        }

        try
        {
            result = read(CurrentVersion);
        }
        catch (Exception failure)
        {
            // A changed epoch can invalidate a capture, but cannot erase failed cleanup.
            if (failure is IEntityPreparationCleanupFailure ||
                epoch == Interlocked.Read(ref _readerPublicationEpoch))
            {
                throw;
            }

            return false;
        }
        if (epoch != Interlocked.Read(ref _readerPublicationEpoch))
        {
            DisposeUnreturnedResult(result);
            return false;
        }
        try
        {
            EnsureReady();
            cancellationToken.ThrowIfCancellationRequested();
            return true;
        }
        catch (Exception failure)
        {
            try
            {
                DisposeUnreturnedResult(result);
            }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException(
                    "A published read failed and its captured result could not be released.", failure, cleanupFailure);
            }
            throw;
        }
    }

    private static void DisposeUnreturnedResult<T>(T result)
    {
        if (result is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    /// <inheritdoc />
    public async ValueTask<DatabaseDistributedWriteLease> EnterDistributedWriteModeAsync(
        CancellationToken cancellationToken = default)
    {
        RequireExternalVersioning();
        if (Volatile.Read(ref _distributedWriteMode) != 0)
        {
            throw new InvalidOperationException("The database is already enrolled in distributed-write mode.");
        }

        if (!_hasDurableDatabaseIncarnation)
        {
            throw new InvalidOperationException(
                "Distributed-write enrollment requires a durably persisted database incarnation.");
        }

        await _localWriteAuthority.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureReady();
            if (_distributedWriteLeaseId is not null)
            {
                throw new InvalidOperationException("The database is already enrolled in distributed-write mode.");
            }

            if (_localOperationFloor > 0)
            {
                throw new InvalidOperationException("Local operation expiry cannot be enrolled under an authority without a matching horizon. Use a new database incarnation for distributed provisioning.");
            }

            Guid leaseId = Guid.NewGuid();
            _distributedWriteLeaseId = leaseId;
            Volatile.Write(ref _distributedWriteMode, 1);
            return new(this, leaseId, _databaseIncarnation);
        }
        finally
        {
            _localWriteAuthority.Release();
        }
    }

    /// <summary>
    /// Captures an immutable profile view and the exact committed-change boundary represented by it
    /// while publication is briefly paused.
    /// </summary>
    public async ValueTask<DatabaseMaintenanceSnapshot<TSnapshot>> CaptureMaintenanceSnapshotAsync<TSnapshot>(
        Func<TSnapshot> capture, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capture);
        await _commitCoordinator.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureReady();
            DatabaseChangeFeedBounds bounds = await _changeStore.GetBoundsAsync(cancellationToken)
                .ConfigureAwait(false);
            return new(CurrentVersion, bounds.Current, capture());
        }
        finally
        {
            _commitCoordinator.Release();
        }
    }

    /// <summary>Marks the kernel as requiring recovery and invalidates scoped snapshots after a fault.</summary>
    /// <param name="operation">Nonempty operation identifier reported through fault telemetry.</param>
    /// <remarks>This method does not perform recovery or reopen storage. A disposed kernel is left unchanged.</remarks>
    public void RequireRecovery(string operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        if (_state is DatabaseKernelState.Disposed)
        {
            return;
        }

        _state = DatabaseKernelState.RecoveryRequired;
        InvalidateScopedSnapshots();
        NotifySnapshotProgress();
        _telemetry.Fault(_profile.Profile, operation);
    }

    /// <summary>Restores checkpoint and durable-log state into a newly created kernel and makes it ready for operations.</summary>
    /// <param name="cancellationToken">Cancels cancellable recovery work.</param>
    /// <returns>A task that completes after recovered state and version boundaries have been published.</returns>
    /// <remarks>
    /// Recovery requires the created lifecycle state. It is not an in-place repair operation for a running or
    /// recovery-required kernel, and successful recovery cannot be repeated on the same instance.
    /// Dispose and reopen the database through its storage composition when recovery is required after a fault.
    /// Cancellation or failure must not be interpreted as successful recovery or an empty database.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The kernel is no longer in its created lifecycle state.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is requested during cancellable recovery work.</exception>
    public async ValueTask RecoverAsync(CancellationToken cancellationToken = default)
    {
        using Activity? activity = _telemetry.Start("db.kernel.recover", _profile.Profile);
        await _checkpointPublication.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RecoverExcludedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _checkpointPublication.Release();
        }
    }

    private async ValueTask RecoverExcludedAsync(CancellationToken cancellationToken)
    {
        await _commitCoordinator.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureCanRecover();
            await DiscardPhysicalPreparationAsync().ConfigureAwait(false);
            _state = DatabaseKernelState.Recovering;

            long recoveryStarted = Stopwatch.GetTimestamp();
            long recoveredVersion = await RestoreCheckpointAsync(cancellationToken).ConfigureAwait(false);
            long phaseStarted = Stopwatch.GetTimestamp();
            RestoreCommittedOperationProjections();
            _telemetry.RecoveryPhase(_profile.Profile, "operation_projections", phaseStarted);
            phaseStarted = Stopwatch.GetTimestamp();
            IReadOnlyList<DurableTransactionEnvelope> transactions =
                await _commitLog.RecoverAsync(cancellationToken).ConfigureAwait(false);
            _telemetry.RecoveryPhase(_profile.Profile, "wal_transactions", phaseStarted);
            phaseStarted = Stopwatch.GetTimestamp();
            recoveredVersion = await ReplayRecoveredTransactionsAsync(
                transactions, recoveredVersion, cancellationToken).ConfigureAwait(false);
            _telemetry.RecoveryPhase(_profile.Profile, "wal_replay", phaseStarted);
            phaseStarted = Stopwatch.GetTimestamp();
            long publicationStarted = phaseStarted;
            ReconcilePartitionTransactions();
            await InitializeTimestampClockAsync(cancellationToken).ConfigureAwait(false);
            _telemetry.RecoveryPhase(_profile.Profile, "timestamp_clock", phaseStarted);
            phaseStarted = Stopwatch.GetTimestamp();

            EnsureDatabaseIncarnation();
            DatabaseChangeCursor? recoveredCurrent = GetRecoveredChangeCursor(transactions);
            await RestoreRecoveredPublicationAsync(recoveredCurrent, cancellationToken).ConfigureAwait(false);
            _telemetry.RecoveryPhase(_profile.Profile, "restored_publication", phaseStarted);
            phaseStarted = Stopwatch.GetTimestamp();

            Interlocked.Exchange(ref _version, recoveredVersion);
            Interlocked.Exchange(ref _durableVersion, Math.Max(DurableVersion.Value, recoveredVersion));
            await InitializeTimestampVisibilityAsync(cancellationToken).ConfigureAwait(false);
            _telemetry.RecoveryPhase(_profile.Profile, "timestamp_visibility", phaseStarted);
            _state = DatabaseKernelState.Ready;
            _telemetry.RecoveryPhase(_profile.Profile, "publication_and_authority", publicationStarted);
            _telemetry.RecoveryPhase(_profile.Profile, "total", recoveryStarted);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (EntityForgeDbException)
        {
            if (_state is not DatabaseKernelState.RecoveryRequired)
            {
                _state = DatabaseKernelState.Faulted;
            }

            throw;
        }
        catch (Exception exception)
        {
            _state = DatabaseKernelState.Faulted;
            throw new EntityForgeDbRecoveryException(
                "The database kernel could not recover its profile state.", exception);
        }
        finally
        {
            _commitCoordinator.Release();
        }
    }

    /// <summary>Commits an encoded profile transaction and returns its durable database version.</summary>
    public async ValueTask<DatabaseVersion> CommitAsync(
        Guid transactionId, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default) =>
        (await CommitWithReceiptAsync(transactionId, payload, cancellationToken).ConfigureAwait(false)).Version;

    /// <inheritdoc />
    public async ValueTask<DatabaseTransferRetentionLease> RetainTransferHistoryAsync(
        CancellationToken cancellationToken = default)
    {
        RequireExternalVersioning();
        await _commitCoordinator.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureReady();
            DatabaseVersion boundary = DurableVersion;
            Guid id = Guid.NewGuid();
            lock (_transferRetentionSync)
            {
                _transferRetentions.Add(id, boundary);
            }

            return new DatabaseTransferRetentionLease(id, boundary, ReleaseTransferRetentionAsync);
        }
        finally
        {
            _commitCoordinator.Release();
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<DatabaseTransferCommit> ExportTransferCommitsAsync(
        DatabaseVersion afterExclusive, DatabaseVersion throughInclusive,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        RequireExternalVersioning();
        if (afterExclusive.Value < 0 || throughInclusive.Value < afterExclusive.Value)
        {
            throw new ArgumentOutOfRangeException(nameof(afterExclusive));
        }

        DurableTransactionEnvelope[] selected;
        await _commitCoordinator.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureReady();
            if (throughInclusive.Value > DurableVersion.Value)
            {
                throw new ArgumentOutOfRangeException(nameof(throughInclusive));
            }

            IReadOnlyList<DurableTransactionEnvelope> durable = await _commitLog.RecoverAsync(cancellationToken)
                .ConfigureAwait(false);
            selected = durable.Where(item => item.Version.Value > afterExclusive.Value &&
                    item.Version.Value <= throughInclusive.Value)
                .OrderBy(item => item.Version.Value)
                .ToArray();
        }
        finally
        {
            _commitCoordinator.Release();
        }

        byte[] chain = DatabaseTransferIntegrity.CreateSeed(_databaseIncarnation, afterExclusive);
        long sequence = 0;
        foreach (DurableTransactionEnvelope item in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] contentHash = SHA256.HashData(item.Payload.Content.Span);
            byte[] previous = chain;
            chain = DatabaseTransferIntegrity.Advance(previous, ++sequence, item.Version,
                item.TransactionId, item.Payload.Profile, item.Payload.CodecId,
                item.Payload.CodecVersion, contentHash, item.StorageLayout);
            yield return new DatabaseTransferCommit(item.DatabaseIncarnation, sequence, item.Version,
                item.TransactionId, item.Payload.Profile, item.Payload.CodecId,
                item.Payload.CodecVersion, item.Payload.Content, contentHash, previous, chain)
            {
                StorageLayout = item.StorageLayout
            };
        }
    }

    /// <inheritdoc />
    public ValueTask<DatabaseCommitReceipt> ApplyTransferCommitAsync(DatabaseTransferCommit commit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commit);
        if (commit.StorageLayout != StorageLayout)
        {
            throw new InvalidDataException("Transfer storage layout differs from the database.");
        }

        if (commit.SourceDatabaseIncarnation != _databaseIncarnation)
        {
            throw new InvalidDataException("The transferred commit belongs to another database incarnation.");
        }

        if (commit.Profile != _profile.Profile || !StringComparer.Ordinal.Equals(commit.CodecId, _profile.CodecId) ||
            commit.CodecVersion != _profile.CodecVersion)
        {
            throw new InvalidDataException("The transferred commit codec does not match the destination profile.");
        }

        if (commit.ContentHash.IsEmpty ||
            !SHA256.HashData(commit.Payload.Span).AsSpan().SequenceEqual(commit.ContentHash.Span))
        {
            throw new InvalidDataException("The transferred commit content hash is invalid.");
        }

        return ApplyAuthoritativeCommitAsync(commit.TransactionId, commit.Payload, commit.Version,
            cancellationToken);
    }

    /// <summary>Commits an encoded profile transaction and returns its identity-bound receipt.</summary>
    public async ValueTask<DatabaseCommitReceipt> CommitWithReceiptAsync(
        Guid transactionId, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default) =>
        await CommitWithReceiptCoreAsync(transactionId, payload, null, cancellationToken).ConfigureAwait(false);

    /// <summary>Resolves an exact previously committed transaction without resubmitting it.</summary>
    public async ValueTask<DatabaseCommitReceipt?> ResolveReplayReceiptAsync(
        Guid transactionId, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        if (transactionId == Guid.Empty)
        {
            throw new ArgumentException("A transaction identity is required.", nameof(transactionId));
        }

        await _commitCoordinator.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureReady();
            byte[] contentHash = SHA256.HashData(payload.Span);
            RequireLocalOperation(transactionId, payload);
            return TryGetReplayReceipt(transactionId, contentHash, out DatabaseCommitReceipt receipt)
                ? receipt : null;
        }
        finally
        {
            _commitCoordinator.Release();
        }
    }

    /// <summary>Applies a transaction at an authoritative externally allocated version.</summary>
    public async ValueTask<DatabaseCommitReceipt> ApplyAuthoritativeCommitAsync(
        Guid transactionId, ReadOnlyMemory<byte> payload, DatabaseVersion authoritativeVersion,
        CancellationToken cancellationToken = default)
    {
        RequireExternalVersioning();
        return await CommitWithReceiptCoreAsync(transactionId, payload, authoritativeVersion, cancellationToken)
            .ConfigureAwait(false);
    }

    private void RequireExternalVersioning()
    {
        if (VersioningMode == DatabaseVersioningMode.PartitionLocal)
        {
            throw new NotSupportedException("This partition allocates native versions locally. External decision versions cannot be used as native log positions.");
        }
    }

    private async ValueTask<DatabaseCommitReceipt> CommitWithReceiptCoreAsync(
        Guid transactionId, ReadOnlyMemory<byte> payload, DatabaseVersion? authoritativeVersion,
        CancellationToken cancellationToken, bool partitionAdmissionOwned = false)
    {
        if (transactionId == Guid.Empty)
        {
            throw new ArgumentException("A transaction identity is required.", nameof(transactionId));
        }

        if (authoritativeVersion is { Value: <= 0 })
        {
            throw new ArgumentOutOfRangeException(nameof(authoritativeVersion));
        }

        long started = Stopwatch.GetTimestamp();
        long admissionStarted = started;
        long admissionAllocated = GC.GetTotalAllocatedBytes();
        bool localAuthorityEntered = false;
        if (authoritativeVersion is null && !partitionAdmissionOwned)
        {
            await EnterLocalWriteAuthorityAsync(cancellationToken, allowReservations: true).ConfigureAwait(false);
            localAuthorityEntered = true;
        }
        try
        {
            await _commitCoordinator.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (localAuthorityEntered)
            {
                _localWriteAuthority.Release();
            }

            throw;
        }
        RecordPhase("admission", admissionStarted, admissionAllocated);
        bool appendedDurably = false;
        DatabaseCommitReceipt? durableReceipt = null;
        IDatabaseProfilePhysicalMutationPlanner? stagedPhysicalPlanner = null;
        Exception? primaryFailure = null;
        using Activity? activity = _telemetry.Start("db.kernel.commit", _profile.Profile);
        try
        {
            EnsureReady();
            if (authoritativeVersion is not null)
            {
                EntityOperationIds.RequireRetained(transactionId, _authoritativeOperationFloor);
            }

            RequireLocalOperation(transactionId, payload, partitionAdmissionOwned);
            if (authoritativeVersion is not null && Volatile.Read(ref _distributedWriteMode) == 0)
            {
                throw new InvalidOperationException(
                    "Authoritative commits require an active distributed-write authority lease.");
            }

            byte[] contentHash = SHA256.HashData(payload.Span);
            if (TryGetReplayReceipt(transactionId, contentHash, out DatabaseCommitReceipt replayReceipt))
            {
                if (authoritativeVersion is not null && replayReceipt.Version != authoritativeVersion)
                {
                    throw new EntityForgeDbIdempotencyException(transactionId);
                }

                return replayReceipt;
            }

            DatabaseVersion version = authoritativeVersion ?? new(checked(_durableVersion + 1));
            if (version.Value <= _durableVersion)
            {
                throw new InvalidOperationException("An authoritative commit version must advance local durable state.");
            }

            PreparedKernelCommit? retained = TakePhysicalPreparation(transactionId, payload, version, partitionAdmissionOwned);
            PreparedKernelCommit prepared;
            if (retained is not null)
            {
                stagedPhysicalPlanner = retained.PhysicalPlanner;
                cancellationToken.ThrowIfCancellationRequested();
                RequireCompatibleReservations(transactionId, payload, partitionAdmissionOwned);
                using var completion = EnterPreparedCompletion(transactionId, payload, partitionAdmissionOwned);
                if (TimestampMvccEnabled)
                {
                    TrimLogicalHistory();
                    TimestampProfile.ValidateTimestampMutation(transactionId, payload);
                }
                await _profile.ValidateAsync(transactionId, payload, cancellationToken).ConfigureAwait(false);
                prepared = retained with
                {
                    Transaction = retained.Transaction with
                    {
                        Timestamp = CommitTimestamp(transactionId, partitionAdmissionOwned)
                    }
                };
                RetainedPhysicalPreparationHits++;
                _telemetry.PartitionCohort(Profile, "physical_preparation_reused", 1);
            }
            else
            {
                prepared = await PlanCommitAsync(transactionId, payload, version, cancellationToken, partitionAdmissionOwned)
                    .ConfigureAwait(false);
                stagedPhysicalPlanner = prepared.PhysicalPlanner;
                prepared = await PreparePhysicalMutationsAsync(prepared, cancellationToken).ConfigureAwait(false);
            }

            durableReceipt = PreparedReceipt(prepared);
            var append = await AppendDurablyAsync(prepared, cancellationToken).ConfigureAwait(false);
            appendedDurably = true;
            RecordPhase(DurableAppendPhase, append.Started, append.Allocated);

            CommittedTransaction committed = RegisterDurableCommit(prepared, contentHash);
            await MaterializeCommittedChangesAsync(prepared).ConfigureAwait(false);
            BeginReaderPublication();
            await PublishCommitAsync(prepared).ConfigureAwait(false);
            // Transfer/native authoritative receivers do not pass through the application registry.
            if (authoritativeVersion is not null)
            {
                try
                {
                    RestoreCommittedOperationProjection(transactionId);
                }
                catch
                {
                    RequireRecovery("operation_projection");
                    throw;
                }
            }
            stagedPhysicalPlanner = null;

            Interlocked.Exchange(ref _version, prepared.Version.Value);
            CompleteReaderPublication();
            _telemetry.Commit(_profile.Profile, Stopwatch.GetElapsedTime(started));
            return Receipt(transactionId, committed, OutcomeFor(prepared.ChangeKind));
        }
        catch (Exception exception) when (appendedDurably)
        {
            RequireRecovery("commit_completion");
            primaryFailure = new EntityOperationCommittedException<DatabaseCommitReceipt>(
                durableReceipt!, durableReceipt!, exception);
            throw primaryFailure;
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            primaryFailure = exception;
            throw;
        }
        catch (DatabaseCommitLogRecoveryRequiredException exception)
        {
            RequireRecovery("commit_log_append");
            primaryFailure = new EntityForgeDbCommitException(
                "The commit log requires recovery before another transaction can be accepted.", exception);
            throw primaryFailure;
        }
        catch (EntityForgeDbException exception)
        {
            primaryFailure = exception;
            throw;
        }
        catch (EntityOperationExpiredException exception)
        {
            primaryFailure = exception;
            throw;
        }
        catch (Exception exception)
        {
            primaryFailure = new EntityForgeDbCommitException(
                    "The database kernel could not commit the profile transaction.", exception);
            throw primaryFailure;
        }
        finally
        {
            AbortReaderPublicationIfRequired();
            Exception? cleanupFailure = null;
            if (!appendedDurably && stagedPhysicalPlanner is not null)
            {
                try
                {
                    await stagedPhysicalPlanner.AbandonPhysicalMutationsAsync(
                    transactionId, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    cleanupFailure = exception;
                }
            }
            try
            {
                _commitCoordinator.Release();
            }
            finally
            {
                if (localAuthorityEntered)
                {
                    _localWriteAuthority.Release();
                }
            }
            if (cleanupFailure is not null)
            {
                throw primaryFailure is null ? cleanupFailure : new AggregateException(
                    "Database commit and physical-stage cleanup failed.", primaryFailure, cleanupFailure);
            }
        }
    }

    /// <summary>Submits an ordered group of profile transactions and returns their individual durable receipts.</summary>
    public ValueTask<IReadOnlyList<DatabaseCommitReceipt>> CommitGroupWithReceiptsAsync(
        IReadOnlyList<DatabaseCommitRequest> requests, CancellationToken cancellationToken = default) =>
        CommitGroupCoreAsync(requests, null, cancellationToken);

    /// <summary>Applies ordered, independently authoritative decisions without allocating replacement versions.</summary>
    public ValueTask<IReadOnlyList<DatabaseCommitReceipt>> ApplyAuthoritativeCommitGroupAsync(
        IReadOnlyList<DatabaseAuthoritativeCommitRequest> requests, CancellationToken cancellationToken = default)
    {
        RequireExternalVersioning();
        ArgumentNullException.ThrowIfNull(requests);
        var owned = requests.Select(request => new DatabaseAuthoritativeCommitRequest(
            request.TransactionId, request.Payload.ToArray(), request.Version)).ToArray();
        long previous = 0;
        foreach (var request in owned)
        {
            if (request.Version.Value <= previous)
            {
                throw new ArgumentException("Authoritative group versions must increase.", nameof(requests));
            }

            previous = request.Version.Value;
        }
        return CommitGroupCoreAsync(owned.Select(request => new DatabaseCommitRequest(request.TransactionId, request.Payload)).ToArray(),
            null, cancellationToken, owned.ToDictionary(request => request.TransactionId, request => request.Version));
    }

    private async ValueTask<IReadOnlyList<DatabaseCommitReceipt>> CommitGroupCoreAsync(
        IReadOnlyList<DatabaseCommitRequest> requests, IReadOnlyList<EntityOperationBoundCommit>? bound,
        CancellationToken cancellationToken, IReadOnlyDictionary<Guid, DatabaseVersion>? authoritativeVersions = null,
        bool partitionAdmissionOwned = false)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count == 0 || requests.Any(request => request.TransactionId == Guid.Empty) ||
            requests.Select(request => request.TransactionId).Distinct().Count() != requests.Count)
        {
            throw new ArgumentException("A commit group requires unique, non-empty transaction identities.",
                nameof(requests));
        }

        if (_commitLog is not IGroupedPhysicalDatabaseCommitLog groupedLog ||
            _profile is not IDatabaseProfileOrderedPreparationRuntime orderedRuntime)
        {
            throw new InvalidOperationException("This database composition does not support ordered commit groups.");
        }

        if (_profile is IDatabaseProfileGroupedPhysicalMutationPlanner { SupportsAtomicGroupPublication: false })
        {
            throw new InvalidOperationException(
                "This profile requires individual commits until grouped reader publication is atomic.");
        }

        bool localAuthorityEntered = authoritativeVersions is null && !partitionAdmissionOwned;
        if (localAuthorityEntered)
        {
            await EnterLocalWriteAuthorityAsync(cancellationToken, allowReservations: true).ConfigureAwait(false);
        }

        try
        {
            await _commitCoordinator.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (localAuthorityEntered)
            {
                _localWriteAuthority.Release();
            }

            throw;
        }
        var prepared = new List<PreparedKernelCommit>(requests.Count);
        long groupStarted = Stopwatch.GetTimestamp();
        bool durable = false;
        bool submissionStarted = false;
        DatabaseCommitReceipt[] durableReceipts = [];
        var originalRequests = requests;
        var replayReceipts = new Dictionary<Guid, DatabaseCommitReceipt>();
        try
        {
            EnsureReady();
            if (authoritativeVersions is not null)
            {
                if (Volatile.Read(ref _distributedWriteMode) == 0)
                {
                    throw new InvalidOperationException("Authoritative groups require active distributed enrollment.");
                }

                foreach (var request in requests)
                {
                    EntityOperationIds.RequireRetained(request.TransactionId, _authoritativeOperationFloor);
                    RequireLocalOperation(request.TransactionId, request.Payload);
                    var version = authoritativeVersions[request.TransactionId];
                    if (TryGetReplayReceipt(request.TransactionId, SHA256.HashData(request.Payload.Span), out var replay))
                    {
                        if (replay.Version != version)
                        {
                            throw new EntityForgeDbIdempotencyException(request.TransactionId);
                        }

                        replayReceipts.Add(request.TransactionId, replay);
                    }
                    else if (version.Value <= _durableVersion)
                    {
                        throw new InvalidOperationException("An unapplied authoritative version must advance durable state.");
                    }
                }
                requests = requests.Where(request => !replayReceipts.ContainsKey(request.TransactionId)).ToArray();
                if (requests.Count == 0)
                {
                    return originalRequests.Select(request => replayReceipts[request.TransactionId]).ToArray();
                }
            }
            ValidateCommitGroupBindings(requests, bound);
            long firstVersion = checked(_durableVersion + 1);
            for (int index = 0; index < requests.Count; index++)
            {
                DatabaseCommitRequest request = requests[index];
                if (_committedTransactions.ContainsKey(request.TransactionId))
                {
                    throw new EntityForgeDbIdempotencyException(request.TransactionId);
                }

                long memberStarted = bound is not null && _telemetry.MeasureCommitPhases ? Stopwatch.GetTimestamp() : 0;
                long memberAllocated = memberStarted == 0 ? 0 : GC.GetTotalAllocatedBytes();
                PreparedKernelCommit item = await PlanCommitAsync(request.TransactionId, request.Payload,
                    authoritativeVersions is null ? new(checked(firstVersion + index)) : authoritativeVersions[request.TransactionId], cancellationToken, partitionAdmissionOwned).ConfigureAwait(false);
                prepared.Add(item);
                long stagingStarted = Stopwatch.GetTimestamp();
                long stagingAllocated = GC.GetTotalAllocatedBytes();
                await orderedRuntime.StagePreparedStateAsync(item.Version, request.TransactionId,
                    request.Payload, cancellationToken).ConfigureAwait(false);
                RecordPhase(StateStagingPhase, stagingStarted, stagingAllocated);
                if (memberStarted != 0)
                {
                    RecordPhase(index % 2 == 0 ? "bound_binding_prepare" : "bound_native_prepare", memberStarted, memberAllocated);
                }
            }

            await PrepareCommitGroupPagesAsync(prepared, cancellationToken).ConfigureAwait(false);

            var commits = prepared.Select(item => new DatabasePhysicalCommit(
                item.Transaction, item.PhysicalMutations)).ToArray();
            groupedLog.ValidateGroup(commits);
            durableReceipts = prepared.Select(PreparedReceipt).ToArray();
            if (authoritativeVersions is not null && replayReceipts.Count != 0)
            {
                var allReceipts = durableReceipts.Concat(replayReceipts.Values).ToDictionary(receipt => receipt.TransactionId);
                durableReceipts = originalRequests.Select(request => allReceipts[request.TransactionId]).ToArray();
            }
            submissionStarted = true;
            long appendStarted = Stopwatch.GetTimestamp();
            long appendAllocated = GC.GetTotalAllocatedBytes();
            bool readWindow = OpenAppendReadWindow(prepared);
            try
            {
                await groupedLog.CommitGroupAsync(commits, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (readWindow)
                {
                    CloseAppendReadWindow();
                }
            }
            durable = true;
            RecordPhase(DurableAppendPhase, appendStarted, appendAllocated);
            long publicationStarted = Stopwatch.GetTimestamp();
            long publicationAllocated = GC.GetTotalAllocatedBytes();
            await orderedRuntime.BeginPreparedPublicationAsync(CancellationToken.None).ConfigureAwait(false);
            var receipts = new List<DatabaseCommitReceipt>(prepared.Count);
            bool atomicPublication = _profile is IDatabaseProfileGroupedPhysicalMutationPlanner;
            if (atomicPublication)
            {
                BeginReaderPublication();
            }

            foreach (PreparedKernelCommit item in prepared)
            {
                long memberStarted = bound is not null && _telemetry.MeasureCommitPhases ? Stopwatch.GetTimestamp() : 0;
                long memberAllocated = memberStarted == 0 ? 0 : GC.GetTotalAllocatedBytes();
                byte[] contentHash = SHA256.HashData(item.Transaction.Payload.Content.Span);
                CommittedTransaction committed = RegisterDurableCommit(item, contentHash);
                await MaterializeCommittedChangesAsync(item).ConfigureAwait(false);
                if (!atomicPublication)
                {
                    BeginReaderPublication();
                }

                await PublishCommitAsync(item).ConfigureAwait(false);
                if (authoritativeVersions is not null)
                {
                    RestoreCommittedOperationProjection(item.Transaction.TransactionId);
                }

                if (!atomicPublication)
                {
                    Interlocked.Exchange(ref _version, item.Version.Value);
                }

                if (!atomicPublication)
                {
                    CompleteReaderPublication();
                }

                receipts.Add(Receipt(item.Transaction.TransactionId, committed, OutcomeFor(item.ChangeKind)));
                _telemetry.Commit(_profile.Profile, Stopwatch.GetElapsedTime(groupStarted));
                if (memberStarted != 0)
                {
                    RecordPhase(receipts.Count % 2 == 1 ? "bound_binding_publish" : "bound_native_publish", memberStarted, memberAllocated);
                }
            }
            if (atomicPublication)
            {
                Interlocked.Exchange(ref _version, prepared[^1].Version.Value);
                CompleteReaderPublication();
            }
            RecordPhase("ordered_publication", publicationStarted, publicationAllocated);
            if (bound is not null)
            {
                long projectionStarted = Stopwatch.GetTimestamp();
                long projectionAllocated = GC.GetTotalAllocatedBytes();
                Dictionary<Guid, Exception>? failures = null;
                foreach (var request in bound)
                {
                    try
                    {
                        RestoreCommittedOperationProjection(request.Binding.OperationId, request);
                    }
                    catch (Exception failure)
                    {
                        (failures ??= []).Add(request.Binding.OperationId, failure);
                    }
                }
                RecordPhase("bound_terminal_projection", projectionStarted, projectionAllocated);
                if (failures is not null)
                {
                    throw new EntityOperationGroupCompletionException(receipts, failures);
                }
            }
            if (authoritativeVersions is null)
            {
                return receipts;
            }

            foreach (var receipt in receipts)
            {
                replayReceipts.Add(receipt.TransactionId, receipt);
            }

            return originalRequests.Select(request => replayReceipts[request.TransactionId]).ToArray();
        }
        catch (Exception failure)
        {
            if (durable && failure is not EntityOperationGroupCompletionException)
            {
                RequireRecovery("ordered_group_publication");
                throw new EntityOperationGroupDurableException(durableReceipts, failure);
            }
            if (failure is DatabaseCommitLogRecoveryRequiredException)
            {
                RequireRecovery("group_commit_log_append");
            }

            if (!durable)
            {
                IReadOnlyList<Exception> cleanup = await AbandonPreparedGroupAsync(prepared, orderedRuntime,
                    requests.Select(request => request.TransactionId).ToArray())
                    .ConfigureAwait(false);
                if (cleanup.Count != 0)
                {
                    throw new AggregateException("Database commit group and staged-state cleanup failed.",
                        new[] { failure }.Concat(cleanup));
                }

                if (!submissionStarted && failure is not OperationCanceledException)
                {
                    throw new DatabaseCommitGroupPreparationException(failure);
                }
            }
            throw;
        }
        finally
        {
            AbortReaderPublicationIfRequired();
            _commitCoordinator.Release();
            if (localAuthorityEntered)
            {
                _localWriteAuthority.Release();
            }
        }
    }

    private static async ValueTask<IReadOnlyList<Exception>> AbandonPreparedGroupAsync(
        IReadOnlyList<PreparedKernelCommit> prepared,
        IDatabaseProfileOrderedPreparationRuntime orderedRuntime, IReadOnlyList<Guid> transactionIds)
    {
        var failures = new List<Exception>();
        foreach (PreparedKernelCommit item in prepared)
        {
            if (item.PhysicalPlanner is not null)
            {
                await CaptureCleanupAsync(() => item.PhysicalPlanner.AbandonPhysicalMutationsAsync(
                    item.Transaction.TransactionId, CancellationToken.None), failures).ConfigureAwait(false);
            }
        }

        await CaptureCleanupAsync(() => orderedRuntime.AbandonPreparedStatesAsync(
            transactionIds, CancellationToken.None), failures)
            .ConfigureAwait(false);
        return failures;
    }

    internal async ValueTask ReleaseDistributedWriteAuthorityAsync(Guid leaseId)
    {
        await _localWriteAuthority.WaitAsync().ConfigureAwait(false);
        try
        {
            await _commitCoordinator.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_distributedWriteLeaseId != leaseId)
                {
                    throw new InvalidOperationException("The distributed-write authority lease is not current.");
                }
                // Local maintenance may replace source lineage after unenrollment.
                // Drain the private distributed job before local admission resumes.
                if (_profile is IDatabaseProfileIncrementalCheckpointCompaction incremental)
                {
                    try
                    {
                        await incremental.StopCheckpointCompactionAsync().ConfigureAwait(false);
                    }
                    catch
                    {
                        RequireRecovery("checkpoint_compaction_unenrollment");
                        throw;
                    }
                }
                _distributedWriteLeaseId = null;
                Volatile.Write(ref _distributedWriteMode, 0);
            }
            finally
            {
                _commitCoordinator.Release();
            }
        }
        finally
        {
            _localWriteAuthority.Release();
        }
    }

    private DatabaseVersion TransferReclamationBoundary(DatabaseVersion checkpoint)
    {
        lock (_transferRetentionSync)
        {
            return _transferRetentions.Count == 0 ? checkpoint :
                new(Math.Min(checkpoint.Value, _transferRetentions.Values.Min(item => item.Value)));
        }
    }

    private ValueTask ReleaseTransferRetentionAsync(Guid id)
    {
        lock (_transferRetentionSync)
        {
            if (!_transferRetentions.Remove(id))
            {
                throw new InvalidOperationException("The transfer-retention lease is not current.");
            }
        }

        return ValueTask.CompletedTask;
    }

    private async ValueTask EnterLocalWriteAuthorityAsync(CancellationToken cancellationToken, bool allowReservations = false)
    {
        await _localWriteAuthority.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (Volatile.Read(ref _distributedWriteMode) != 0)
        {
            _localWriteAuthority.Release();
            throw new InvalidOperationException(
                "Ordinary local commits are disabled while the database is enrolled in distributed-write mode.");
        }
        if (!allowReservations && _partitionReservations.Count != 0)
        {
            _localWriteAuthority.Release();
            throw new InvalidOperationException("A distributed preparation reserves this partition until its authoritative outcome is applied.");
        }
    }

    private async ValueTask MaintainCheckpointPagesAsync(IDatabaseProfileCheckpointMaintenance maintenance,
        DatabaseVersion version, CancellationToken token, bool compact = true, int compactionPasses = 2)
    {
        if (_commitLog is not IPhysicalDatabaseCommitLog physicalLog)
        {
            throw new InvalidOperationException("Checkpoint maintenance requires physical redo durability.");
        }
        // The second pass can release pages retired by the first after its snapshots have drained.
        const int MaintenancePasses = 3;
        BeginReaderPublication();
        try
        {
            for (int pass = 0; pass < MaintenancePasses; pass++)
            {
                Guid id = Guid.NewGuid();
                bool submitted = false;
                try
                {
                    var mutations = await maintenance.PrepareMaintenanceAsync(id, version, compact && pass < compactionPasses, token).ConfigureAwait(false);
                    if (mutations.Count == 0)
                    {
                        continue;
                    }

                    var envelope = new DurableTransactionEnvelope(_databaseIncarnation, version, id,
                        new(_profile.Profile, _profile.CodecId, _profile.CodecVersion, ReadOnlyMemory<byte>.Empty),
                        DurableCommittedChangeKind.CheckpointMaintenance, null, StorageLayout, VersioningMode);
                    submitted = true;
                    await physicalLog.CommitWithPhysicalMutationsAsync(envelope, mutations, token).ConfigureAwait(false);
                    ObserveMaintenance("redo");
                    await maintenance.PublishMaintenanceAsync(id, CancellationToken.None).ConfigureAwait(false);
                    ObserveMaintenance("publish");
                    if (_profile is IDatabaseProfileDurabilityBarrier barrier)
                    {
                        await barrier.FlushPhysicalStateAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    else
                    {
                        throw new InvalidOperationException("Checkpoint maintenance requires a physical durability barrier.");
                    }

                    ObserveMaintenance("flush");
                }
                catch (Exception failure)
                {
                    if (submitted)
                    {
                        RequireRecovery("checkpoint_physical_maintenance");
                    }
                    else
                    {
                        try
                        {
                            await maintenance.AbandonMaintenanceAsync(id).ConfigureAwait(false);
                        }
                        catch (Exception cleanup)
                        {
                            RequireRecovery("checkpoint_maintenance_cleanup");
                            EntityFailureDiagnostics.Attach(failure, cleanup);
                        }
                    }
                    throw;
                }
            }
            await maintenance.CompleteMaintenanceAsync(CancellationToken.None).ConfigureAwait(false);
            ObserveMaintenance("trim");
            CompleteReaderPublication();
        }
        catch
        {
            RequireRecovery("checkpoint_physical_maintenance");
            throw;
        }
        finally
        {
            AbortReaderPublicationIfRequired();
        }
    }

    /// <inheritdoc />
    public ValueTask<DatabaseChangeFeedBounds> GetBoundsAsync(CancellationToken cancellationToken = default) =>
        _changeFeed.GetBoundsAsync(cancellationToken);

    /// <inheritdoc />
    public ValueTask<DatabaseChangePage> ReadPageAfterAsync(DatabaseChangeCursor? cursor,
        DatabaseChangeSelector selector, int maximumCount, CancellationToken cancellationToken = default) =>
        _changeFeed.ReadPageAfterAsync(cursor, selector, maximumCount, cancellationToken);

    /// <inheritdoc />
    public IAsyncEnumerable<CommittedChangeSet> SubscribeAsync(DatabaseChangeCursor? cursor,
        DatabaseChangeSelector selector, DatabaseChangeSubscriptionOptions options,
        CancellationToken cancellationToken = default) =>
        _changeFeed.SubscribeAsync(cursor, selector, options, cancellationToken);

    /// <summary>Stops background work, drains partition work and disposes the kernel's owned storage and coordinator.</summary>
    /// <returns>The shared shutdown operation; repeated calls observe the same completion or failure.</returns>
    /// <exception cref="AggregateException">One or more failures were collected during the resource-cleanup phase.</exception>
    /// <remarks>Waits for checkpoint publication exclusion and does not accept a cancellation token. Caller-supplied telemetry remains caller-owned. Shutdown is not a replacement for creating and verifying a backup.</remarks>
    public ValueTask DisposeAsync()
    {
        lock (_disposeSync)
        {
            return new(_disposeTask ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        await StopPrivateCheckpointCompactionAsync().ConfigureAwait(false);
        await CloseTimestampReadAdmissionAsync().ConfigureAwait(false);
        await DrainPartitionWorkAsync().ConfigureAwait(false);
        await _checkpointPublication.WaitAsync().ConfigureAwait(false);
        try
        {
            await DisposeCheckpointExcludedAsync().ConfigureAwait(false);
        }
        finally
        {
            _checkpointPublication.Release();
        }
    }

    private async Task DisposeCheckpointExcludedAsync()
    {
        _state = DatabaseKernelState.Disposed;
        NotifySnapshotProgress();
        var failures = new List<Exception>();
        if (_privateCompactionFailure is { } privateFailure)
        {
            failures.Add(privateFailure);
        }

        await CaptureCleanupAsync(() => DiscardPhysicalPreparationAsync(), failures).ConfigureAwait(false);
        await CaptureCleanupAsync(_commitCoordinator.DisposeAsync, failures).ConfigureAwait(false);
        if (_profile is IDatabaseProfileIncrementalCheckpointCompaction incremental)
        {
            await CaptureCleanupAsync(incremental.StopCheckpointCompactionAsync, failures).ConfigureAwait(false);
        }

        await CaptureCleanupAsync(_commitLog.DisposeAsync, failures).ConfigureAwait(false);
        await CaptureCleanupAsync(_changeStore.DisposeAsync, failures).ConfigureAwait(false);
        if (_checkpointStore is not null)
        {
            await CaptureCleanupAsync(_checkpointStore.DisposeAsync, failures).ConfigureAwait(false);
        }

        if (_ownsTelemetry)
        {
            try
            {
                _telemetry.Dispose();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        _readerPublicationReady.Dispose();
        if (failures.Count != 0)
        {
            throw new AggregateException("Database kernel cleanup failed.", failures);
        }
    }

    private static async ValueTask CaptureCleanupAsync(Func<ValueTask> cleanup, ICollection<Exception> failures)
    {
        try
        {
            await cleanup().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }

    private async ValueTask<PreparedKernelCommit> PlanCommitAsync(
        Guid transactionId, ReadOnlyMemory<byte> payload, DatabaseVersion version,
        CancellationToken cancellationToken, bool partitionAdmissionOwned = false)
    {
        await DiscardPhysicalPreparationAsync().ConfigureAwait(false);
        RequireLocalOperation(transactionId, payload, partitionAdmissionOwned);
        if (TimestampMvccEnabled)
        {
            TrimLogicalHistory();
            TimestampProfile.ValidateTimestampMutation(transactionId, payload);
        }
        RequireCompatibleReservations(transactionId, payload, partitionAdmissionOwned);
        using var preparedCompletion = EnterPreparedCompletion(transactionId, payload, partitionAdmissionOwned);
        long phaseStarted = Stopwatch.GetTimestamp();
        long phaseAllocated = GC.GetTotalAllocatedBytes();
        var transition = (_profile as IEntityOperationCompactionProfile)?.ReadOperationTransition(payload);
        if (transition?.Committed is not null)
        {
            ValidateCommittedTransition(transition);
        }
        else if (_profile is IEntityOperationBindingAdmission admission &&
            admission.BindingIdentity(payload) is Guid operationId && _committedTransactions.ContainsKey(operationId))
        {
            throw new EntityForgeDbIdempotencyException(operationId);
        }

        var immediate = _profile as IDatabaseProfileValidatedChangePlanner<DatabaseProfileChangePlan>;
        if (immediate is null)
        {
            await _profile.ValidateAsync(transactionId, payload, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            DatabaseProfileChangePlan changePlan;
            if (immediate is not null)
            {
                changePlan = await immediate.ValidateAndPlanCommittedChangesAsync(
                    version, transactionId, payload, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                changePlan = _profile is IDatabaseProfileChangePlanner planner
                    ? await planner.PlanCommittedChangesAsync(version, transactionId, payload, cancellationToken)
                        .ConfigureAwait(false)
                    : DatabaseProfileChangePlan.System();
            }
            RecordPhase(PlanningPhase, phaseStarted, phaseAllocated);

            ValidateChangePlan(changePlan);
            if (TimestampMvccEnabled)
            {
                TimestampProfile.ReserveTimestampMutation(transactionId, payload, changePlan);
            }

            DurableCommittedChangeKind changeKind = ChangeKindFor(changePlan);
            var transaction = new DurableTransactionEnvelope(
                _databaseIncarnation,
                version,
                transactionId,
                new DurableProfilePayload(_profile.Profile, _profile.CodecId, _profile.CodecVersion, payload),
                changeKind,
                changePlan.Changes, StorageLayout, VersioningMode)
            {
                Timestamp = CommitTimestamp(transactionId, partitionAdmissionOwned)
            };

            return new PreparedKernelCommit(
                version,
                transaction,
                changePlan,
                changeKind,
                _profile as IDatabaseProfilePhysicalMutationPlanner,
                Array.Empty<DatabasePhysicalPageMutation>(),
                transition?.OperationId ?? OperationIdentity(transactionId, payload));
        }
        catch (Exception primary)
        {
            // Ownership starts before profile planning: a rejected candidate may already
            // contain speculative state even though no physical plan was returned.
            if (_profile is IDatabaseProfilePhysicalMutationPlanner cleanup)
            {
                try
                {
                    await cleanup.AbandonPhysicalMutationsAsync(transactionId, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception cleanupFailure)
                {
                    EntityFailureDiagnostics.Attach(primary, cleanupFailure);
                }
            }
            throw;
        }
    }

    private async ValueTask<PreparedKernelCommit> PreparePhysicalMutationsAsync(
        PreparedKernelCommit prepared, CancellationToken cancellationToken)
    {
        long phaseStarted = Stopwatch.GetTimestamp();
        long phaseAllocated = GC.GetTotalAllocatedBytes();
        IReadOnlyList<DatabasePhysicalPageMutation> physicalMutations = prepared.PhysicalPlanner is { } planner
            ? await planner.PreparePhysicalMutationsAsync(
                prepared.Version,
                prepared.Transaction.TransactionId,
                prepared.Transaction.Payload.Content,
                cancellationToken).ConfigureAwait(false)
            : Array.Empty<DatabasePhysicalPageMutation>();
        RecordPhase(PhysicalPreparationPhase, phaseStarted, phaseAllocated);

        _telemetry.PhysicalCommit(
            _profile.Profile,
            physicalMutations.Count,
            physicalMutations.Sum(mutation => (long)mutation.Bytes.Length));

        if (physicalMutations.Count != 0 && _commitLog is not IPhysicalDatabaseCommitLog)
        {
            throw new EntityForgeDbCommitException(
                "The selected profile produced physical mutations but the commit log cannot protect them.",
                new InvalidOperationException("A physical-capable commit log is required."));
        }

        return prepared with
        {
            PhysicalMutations = physicalMutations
        };
    }

    private async ValueTask<(long Started, long Allocated)> AppendDurablyAsync(
        PreparedKernelCommit prepared, CancellationToken cancellationToken)
    {
        long phaseStarted = Stopwatch.GetTimestamp();
        long phaseAllocated = GC.GetTotalAllocatedBytes();
        bool readWindow = OpenAppendReadWindow(prepared);
        try
        {
            if (_commitLog is IPhysicalDatabaseCommitLog physicalCommitLog)
            {
                await physicalCommitLog.CommitWithPhysicalMutationsAsync(
                    prepared.Transaction, prepared.PhysicalMutations, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _commitLog.CommitAsync(prepared.Transaction, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (readWindow)
            {
                CloseAppendReadWindow();
            }
        }

        // The caller records commitment before any telemetry or publication can fail.
        return (phaseStarted, phaseAllocated);
    }

    private CommittedTransaction RegisterDurableCommit(
        PreparedKernelCommit prepared, byte[] contentHash)
    {
        Interlocked.Exchange(ref _durableVersion, prepared.Version.Value);
        var committed = new CommittedTransaction(
            prepared.Version,
            contentHash,
            prepared.ChangeKind,
            prepared.ChangePlan.Changes?.EffectCount ?? 0,
            prepared.ApplicationOperationId, prepared.Transaction.Timestamp);
        _committedTransactions.Add(prepared.Transaction.TransactionId, committed);
        _unresolvedTimestampPreparations.Remove(prepared.Transaction.TransactionId);
        return committed;
    }

    private async ValueTask MaterializeCommittedChangesAsync(PreparedKernelCommit prepared)
    {
        if (prepared.ChangePlan.Changes is null)
        {
            return;
        }

        try
        {
            long phaseStarted = Stopwatch.GetTimestamp();
            long phaseAllocated = GC.GetTotalAllocatedBytes();
            await _changes.MaterializeAsync(prepared.Transaction, CancellationToken.None).ConfigureAwait(false);
            RecordPhase(ChangeMaterializationPhase, phaseStarted, phaseAllocated);
        }
        catch
        {
            RequireRecovery(ChangeMaterializationPhase);
            throw;
        }
    }

    private async ValueTask PublishCommitAsync(PreparedKernelCommit prepared)
    {
        try
        {
            long phaseStarted = Stopwatch.GetTimestamp();
            long phaseAllocated = GC.GetTotalAllocatedBytes();
            Guid transactionId = prepared.Transaction.TransactionId;

            InvalidateScopedSnapshots(transactionId, prepared.Transaction.Payload.Content);

            await _profile.ApplyAsync(
                prepared.Version,
                transactionId,
                prepared.Transaction.Payload.Content,
                CancellationToken.None).ConfigureAwait(false);

            if (prepared.PhysicalPlanner is not null)
            {
                await prepared.PhysicalPlanner.PublishPhysicalMutationsAsync(
                    transactionId, CancellationToken.None).ConfigureAwait(false);
            }

            if (prepared.ChangePlan.Changes is not null)
            {
                await _changes.PublishAsync(prepared.Version, CancellationToken.None).ConfigureAwait(false);
            }

            if (_profile is IDatabaseProfilePublicationRuntime publication)
            {
                await publication.PublishStagedStateAsync(transactionId, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            if (prepared.ChangePlan.Changes is not null)
            {
                await _changes.NotifyPublishedAsync(prepared.Version, CancellationToken.None).ConfigureAwait(false);
            }

            RecordPhase(PublicationPhase, phaseStarted, phaseAllocated);
            PinTimestampVersion(prepared.Version);
            if (TimestampMvccEnabled)
            {
                TimestampProfile.PublishTimestampVersion(transactionId, prepared.Transaction.Timestamp!, prepared.Transaction.Payload.Content);
                TrimLogicalHistory();
                await WaitForTimestampClockAsync(prepared.Transaction.Timestamp!, CancellationToken.None).ConfigureAwait(false);
                PublishServingChange(prepared);
            }
        }
        catch
        {
            RequireRecovery(PublicationPhase);
            throw;
        }
    }

    private bool TryGetReplayReceipt(
        Guid transactionId, byte[] contentHash, out DatabaseCommitReceipt receipt)
    {
        if (!_committedTransactions.TryGetValue(transactionId, out CommittedTransaction committed))
        {
            receipt = default!;
            return false;
        }

        if (!contentHash.AsSpan().SequenceEqual(committed.ContentHash))
        {
            throw new EntityForgeDbIdempotencyException(transactionId);
        }

        receipt = Receipt(transactionId, committed, DatabaseCommitOutcome.IdempotentReplay);
        return true;
    }

    private static void ValidateChangePlan(DatabaseProfileChangePlan changePlan)
    {
        if (changePlan.Changes is { HasEffects: false })
        {
            throw new InvalidDataException(
                "A profile change planner must represent a logical no-op with no payload.");
        }

        if (changePlan.Changes is { Content.Length: > DatabaseChangeFeedLimits.MaximumCommittedChangePayloadBytes })
        {
            throw new InvalidDataException(
                "A profile committed-change payload exceeds the supported materialization size.");
        }
    }

    private void BeginReaderPublication()
    {
        // Publish the new wait before marking the epoch in progress. Readers racing this
        // transition still validate the epoch and release any invalidated captured result.
        Volatile.Write(ref _readerPublicationCompletion, new(TaskCreationOptions.RunContinuationsAsynchronously));
        _readerPublicationReady.Reset();
        long epoch = Interlocked.Increment(ref _readerPublicationEpoch);
        if ((epoch & 1) == 0)
        {
            throw new InvalidOperationException("A reader publication is already in progress.");
        }
    }

    private void CompleteReaderPublication()
    {
        long epoch = Interlocked.Increment(ref _readerPublicationEpoch);
        if ((epoch & 1) != 0)
        {
            throw new InvalidOperationException("The reader publication epoch is invalid.");
        }

        _readerPublicationReady.Set();
        Volatile.Read(ref _readerPublicationCompletion)?.TrySetResult();
    }

    private void AbortReaderPublicationIfRequired()
    {
        if ((Interlocked.Read(ref _readerPublicationEpoch) & 1) != 0)
        {
            _readerPublicationReady.Set();
            Volatile.Read(ref _readerPublicationCompletion)?.TrySetResult();
        }
    }

    private static DurableCommittedChangeKind ChangeKindFor(DatabaseProfileChangePlan changePlan) =>
        !changePlan.IsUserData
            ? DurableCommittedChangeKind.System
            : changePlan.Changes is null
                ? DurableCommittedChangeKind.UserDataNoChange
                : DurableCommittedChangeKind.UserDataWithChanges;

    private static DatabaseCommitOutcome OutcomeFor(DurableCommittedChangeKind changeKind) =>
        changeKind == DurableCommittedChangeKind.UserDataWithChanges
            ? DatabaseCommitOutcome.AppliedWithChanges
            : DatabaseCommitOutcome.AppliedNoChange;

    private void EnsureCanRecover()
    {
        if (_state is not DatabaseKernelState.Created)
        {
            throw new InvalidOperationException("A database kernel instance can be recovered only once.");
        }
    }

    private async ValueTask<long> ReplayRecoveredTransactionsAsync(
        IReadOnlyList<DurableTransactionEnvelope> transactions,
        long priorVersion,
        CancellationToken cancellationToken)
    {
        long priorWalVersion = DatabaseVersion.Initial.Value;
        long checkpointVersion = priorVersion;
        foreach (DurableTransactionEnvelope transaction in transactions)
        {
            RestoreTimestamp(transaction.Timestamp);
            if (transaction.ChangeKind == DurableCommittedChangeKind.TimestampEvidence)
            {
                if (transaction.Timestamp is null || transaction.DatabaseIncarnation != _databaseIncarnation && _databaseIncarnation != Guid.Empty ||
                    transaction.VersioningMode != VersioningMode || transaction.StorageLayout != StorageLayout ||
                    transaction.Payload.Profile != Profile || transaction.Payload.CodecId != TimestampEvidenceCodec ||
                    transaction.Payload.CodecVersion != 1 || !transaction.Payload.Content.IsEmpty)
                {
                    throw new EntityForgeDbFormatException("Invalid timestamp reservation evidence.");
                }

                _databaseIncarnation = transaction.DatabaseIncarnation;
                continue;
            }
            bool partitionEvidence = transaction.ChangeKind == DurableCommittedChangeKind.PartitionEvidence;
            bool maintenance = transaction.ChangeKind == DurableCommittedChangeKind.CheckpointMaintenance;
            ValidateEnvelope(transaction, priorWalVersion, maintenance);
            if (!partitionEvidence)
            {
                priorWalVersion = Math.Max(priorWalVersion, transaction.Version.Value);
            }

            if (maintenance)
            {
                if (transaction.Payload.Content.IsEmpty)
                {
                    if (transaction.Version.Value > checkpointVersion)
                    {
                        throw new EntityForgeDbFormatException("Physical maintenance requires its original covering checkpoint.");
                    }
                }
                else
                {
                    PrivateCompactionRecord.Validate(transaction.Payload.Content.Span,
                    transaction.Version.Value, checkpointVersion, priorVersion);
                }
            }
            if (_databaseIncarnation != Guid.Empty &&
                _databaseIncarnation != transaction.DatabaseIncarnation)
            {
                throw new EntityForgeDbFormatException(
                    "The checkpoint and durable log belong to different database incarnations.");
            }

            // Atomic checkpoint replacement necessarily precedes WAL reclamation. A crash in
            // that interval leaves a valid checkpoint plus an overlapping committed WAL prefix.
            // The checkpoint already contains both profile state and committed-change coverage,
            // so validate but do not rematerialize that prefix.
            if (transaction.Version.Value <= priorVersion)
            {
                continue;
            }

            if (partitionEvidence)
            {
                if (transaction.Version.Value != checked(priorVersion + 1))
                {
                    throw new EntityForgeDbFormatException("Partition evidence has an invalid native reclamation boundary.");
                }

                if (_databaseIncarnation == Guid.Empty)
                {
                    _databaseIncarnation = transaction.DatabaseIncarnation;
                }

                ReplayPartitionEvidence(transaction);
                continue;
            }

            RegisterCommitted(transaction);
            await _changes.MaterializeAsync(transaction, cancellationToken).ConfigureAwait(false);
            _telemetry.Recovered(_profile.Profile);
            Interlocked.Exchange(ref _durableVersion, transaction.Version.Value);

            await _profile.ApplyAsync(
                transaction.Version,
                transaction.TransactionId,
                transaction.Payload.Content,
                cancellationToken).ConfigureAwait(false);
            // Release plan capacity in replay order, just as live completion does. Waiting until
            // the end would accumulate historical full plans and could overflow the catalog.
            // Binding and maintenance transaction IDs are not application operation IDs.
            // Resolve the identity already classified during registration before consulting
            // the expiry-aware operation catalog; metadata without an operation has no projection.
            if (_committedTransactions[transaction.TransactionId].ApplicationOperationId is { } operationId)
            {
                RestoreCommittedOperationProjection(operationId);
            }

            priorVersion = transaction.Version.Value;
        }

        return priorVersion;
    }

    private void EnsureDatabaseIncarnation()
    {
        if (_databaseIncarnation == Guid.Empty)
        {
            _databaseIncarnation = Guid.NewGuid();
        }
    }

    private DatabaseChangeCursor? GetRecoveredChangeCursor(
        IReadOnlyList<DurableTransactionEnvelope> transactions) =>
        transactions.LastOrDefault(item => item.CommittedChanges is not null) is
        { CommittedChanges: not null } lastChange
            ? EncodeCursor(lastChange.Version)
            : _checkpointChangeCursor;

    private async ValueTask RestoreRecoveredPublicationAsync(
        DatabaseChangeCursor? recoveredCurrent, CancellationToken cancellationToken)
    {
        if (recoveredCurrent is not null)
        {
            await _changes.RestorePublishedFrontierAsync(recoveredCurrent, cancellationToken)
                .ConfigureAwait(false);
        }

        if (_profile is IDatabaseProfilePublicationRuntime publication)
        {
            await publication.PublishStagedStateAsync(Guid.Empty, cancellationToken).ConfigureAwait(false);
        }

        if (recoveredCurrent is not null)
        {
            await _changes.NotifyRecoveredAsync(recoveredCurrent.Value, cancellationToken).ConfigureAwait(false);
        }

        await _changes.ValidateCheckpointCoverageAsync(_checkpointChangeCursor, cancellationToken)
            .ConfigureAwait(false);
        RestoreCommittedOperationProjections();
    }

    private void EnsureReady()
    {
        if (_state is DatabaseKernelState.RecoveryRequired)
        {
            throw new EntityForgeDbRecoveryRequiredException(
                "A durable transaction was not published; dispose and reopen the database before continuing.");
        }

        if (_state is not DatabaseKernelState.Ready)
        {
            throw new InvalidOperationException($"The database kernel is not ready (state: {_state}).");
        }
    }

    private void RegisterCommitted(DurableTransactionEnvelope transaction)
    {
        if (_databaseIncarnation == Guid.Empty)
        {
            _databaseIncarnation = transaction.DatabaseIncarnation;
        }
        else if (_databaseIncarnation != transaction.DatabaseIncarnation)
        {
            throw new EntityForgeDbFormatException(
                "The durable log contains more than one database incarnation.");
        }

        byte[] hash = SHA256.HashData(transaction.Payload.Content.Span);
        if (_committedTransactions.TryGetValue(transaction.TransactionId, out CommittedTransaction existing))
        {
            if (!hash.AsSpan().SequenceEqual(existing.ContentHash))
            {
                throw new EntityForgeDbIdempotencyException(transaction.TransactionId);
            }

            throw new EntityForgeDbFormatException(
                $"Transaction '{transaction.TransactionId}' appears more than once in the durable log.");
        }

        _committedTransactions.Add(
            transaction.TransactionId,
            new CommittedTransaction(
                transaction.Version,
                hash,
                transaction.ChangeKind,
                transaction.CommittedChanges?.EffectCount ?? 0,
                OperationIdentity(transaction.TransactionId, transaction.Payload.Content), transaction.Timestamp));
    }

    private void ValidateEnvelope(DurableTransactionEnvelope transaction, long priorVersion, bool maintenance = false)
    {
        if (TimestampCostOptions is not null && transaction.Timestamp is null && !maintenance)
        {
            throw new EntityForgeDbFormatException("Untimestamped history cannot enter the timestamp experiment.");
        }

        if (transaction.StorageLayout != StorageLayout || transaction.VersioningMode != VersioningMode)
        {
            throw new EntityForgeDbFormatException("The WAL storage layout or versioning mode differs from the database.");
        }

        if (transaction.Payload.Profile != _profile.Profile)
        {
            throw new EntityForgeDbFormatException(
                $"The durable transaction profile '{transaction.Payload.Profile}' cannot be opened as '{_profile.Profile}'.");
        }

        bool partitionEvidence = transaction.ChangeKind == DurableCommittedChangeKind.PartitionEvidence;
        if (partitionEvidence && VersioningMode != DatabaseVersioningMode.PartitionLocal)
        {
            throw new EntityForgeDbFormatException("Partition evidence requires partition-local versioning.");
        }

        if (!StringComparer.Ordinal.Equals(transaction.Payload.CodecId, partitionEvidence ? PartitionEvidenceCodec.Id : _profile.CodecId) ||
            (partitionEvidence ? transaction.Payload.CodecVersion is not (1 or PartitionEvidenceCodec.Version)
                : transaction.Payload.CodecVersion != _profile.CodecVersion))
        {
            throw new EntityForgeDbFormatException(
                $"Unsupported {_profile.Profile} payload codec '{transaction.Payload.CodecId}' " +
                $"version {transaction.Payload.CodecVersion}.");
        }

        if (transaction.Version.Value < priorVersion || (!maintenance && transaction.Version.Value == priorVersion))
        {
            throw new EntityForgeDbFormatException(
                "Durable transaction versions must be strictly increasing.");
        }
    }

    private async ValueTask<long> RestoreCheckpointAsync(CancellationToken cancellationToken)
    {
        if (_checkpointStore is null)
        {
            if (_commitLog is ICheckpointRecoveryDatabaseCommitLog validatedLog)
            {
                await validatedLog.ValidateRecoveryCheckpointAsync(null, cancellationToken).ConfigureAwait(false);
            }
            return DatabaseVersion.Initial.Value;
        }

        if (_profile is not ICheckpointableDatabaseProfileRuntime profile)
        {
            throw new EntityForgeDbFormatException(
                "The selected profile cannot restore the configured checkpoint.");
        }

        long started = Stopwatch.GetTimestamp();
        DurableCheckpointEnvelope? checkpoint = await _checkpointStore.LoadAsync(cancellationToken)
            .ConfigureAwait(false);
        _telemetry.RecoveryPhase(_profile.Profile, "checkpoint_load", started);
        if (_commitLog is ICheckpointRecoveryDatabaseCommitLog recoveryLog)
        {
            await recoveryLog.ValidateRecoveryCheckpointAsync(checkpoint, cancellationToken).ConfigureAwait(false);
        }
        if (checkpoint is null)
        {
            return DatabaseVersion.Initial.Value;
        }

        if (TimestampCostOptions is not null && checkpoint.Timestamp is null)
        {
            throw new EntityForgeDbFormatException("Untimestamped checkpoint cannot enter the timestamp experiment.");
        }

        RestoreTimestamp(checkpoint.Timestamp);

        if (checkpoint.StorageLayout != StorageLayout || checkpoint.VersioningMode != VersioningMode)
        {
            throw new EntityForgeDbFormatException("The checkpoint storage layout or versioning mode differs from the database.");
        }

        _databaseIncarnation = checkpoint.DatabaseIncarnation;
        _checkpointChangeCursor = checkpoint.MaterializedChangeCursor;
        _authoritativeOperationFloor = checkpoint.AuthoritativeOperationFloor;
        _localOperationFloor = checkpoint.LocalOperationFloor;
        RestorePartitionTransactions(checkpoint);
        if (checkpoint.ProfileState.Profile != profile.Profile ||
            !StringComparer.Ordinal.Equals(checkpoint.ProfileState.CodecId, profile.CheckpointCodecId) ||
            !profile.CanReadCheckpointVersion(checkpoint.ProfileState.CodecVersion))
        {
            throw new EntityForgeDbFormatException(
                $"Unsupported {profile.Profile} checkpoint codec '{checkpoint.ProfileState.CodecId}' " +
                $"version {checkpoint.ProfileState.CodecVersion}.");
        }

        started = Stopwatch.GetTimestamp();
        await profile.RestoreCheckpointAsync(
            checkpoint.Version,
            checkpoint.ProfileState.CodecVersion,
            checkpoint.ProfileState.Content,
            cancellationToken).ConfigureAwait(false);
        _telemetry.RecoveryPhase(_profile.Profile, "profile_restore", started);
        if (_profile is IEntityOperationCompactionProfile operationProfile)
        {
            operationProfile.OperationCatalog.InstallFloor(_localOperationFloor);
        }

        foreach (DurableCommittedTransaction transaction in checkpoint.CommittedTransactions)
        {
            _committedTransactions[transaction.TransactionId] = new CommittedTransaction(
                transaction.Version,
                transaction.ContentHash.ToArray(),
                transaction.ChangeKind,
                transaction.EffectCount, transaction.ApplicationOperationId, transaction.Timestamp);
        }

        Interlocked.Exchange(ref _durableVersion, checkpoint.Version.Value);
        return checkpoint.Version.Value;
    }

    private DatabaseCommitReceipt PreparedReceipt(PreparedKernelCommit prepared) => new(
        _databaseIncarnation, prepared.Transaction.TransactionId, _profile.Profile, prepared.Version,
        OutcomeFor(prepared.ChangeKind),
        prepared.ChangeKind == DurableCommittedChangeKind.UserDataWithChanges ? EncodeCursor(prepared.Version) : null,
        prepared.ChangePlan.Changes?.EffectCount ?? 0);

    private DatabaseCommitReceipt Receipt(
        Guid transactionId, CommittedTransaction committed, DatabaseCommitOutcome outcome) =>
        new(
            _databaseIncarnation,
            transactionId,
            _profile.Profile,
            committed.Version,
            outcome,
            committed.ChangeKind == DurableCommittedChangeKind.UserDataWithChanges
                ? EncodeCursor(committed.Version)
                : null,
            committed.EffectCount);

    private DatabaseChangeCursor EncodeCursor(DatabaseVersion version) =>
        _changes.Cursor(version);

    private long DecodeCursor(DatabaseChangeCursor cursor) =>
        _changes.Decode(cursor);

    private void RecordPhase(string phase, long started, long allocatedBefore) =>
        _telemetry.CommitPhase(_profile.Profile, phase, Stopwatch.GetElapsedTime(started),
            GC.GetTotalAllocatedBytes() - allocatedBefore);

    private sealed record PreparedKernelCommit(
        DatabaseVersion Version,
        DurableTransactionEnvelope Transaction,
        DatabaseProfileChangePlan ChangePlan,
        DurableCommittedChangeKind ChangeKind,
        IDatabaseProfilePhysicalMutationPlanner? PhysicalPlanner,
        IReadOnlyList<DatabasePhysicalPageMutation> PhysicalMutations,
        Guid? ApplicationOperationId);

    private readonly record struct CommittedTransaction(
        DatabaseVersion Version,
        byte[] ContentHash,
        DurableCommittedChangeKind ChangeKind,
        int EffectCount,
        Guid? ApplicationOperationId, DurableTimestampState? Timestamp = null);
}
