using System.Threading.Channels;
using EntityForge.Db.Durability;
using EntityForge.Db.Storage;
using System.Diagnostics;

namespace EntityForge.Db.Storage.Pages;

/// <summary>Profile-neutral production commit log using bounded grouped redo.</summary>
public sealed class GroupedRedoDatabaseCommitLog : IDatabaseCommitLog, ICheckpointPositionDatabaseCommitLog,
    IDatabaseStorageDiagnostics, IGroupedPhysicalDatabaseCommitLog, IConcurrentCheckpointCommitLog,
    ICheckpointRecoveryDatabaseCommitLog
{
    private const int WalAggregationBufferSize = 1024 * 1024;
    private const int AdmissionCapacityMultiplier = 4;
    private const long MaximumRetainedStartupRecoveryBytes = 64L * 1024 * 1024;
    private const long BeginningOfFile = 0;
    private const string ReplacementSuffix = ".checkpoint-replacement";
    private readonly string _path;
    private readonly FileStream _ownership;
    private readonly RedoDurabilityOptions _options;
    private readonly IRedoDurabilityObserver _observer;
    private readonly Channel<PendingSubmission> _admission;
    private readonly SemaphoreSlim _submissionSlots;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _writer;
    private readonly SemaphoreSlim _fileGate = new(1, 1);
    private readonly SemaphoreSlim _reclamationGate = new(1, 1);
    private readonly RedoDurabilityTelemetry _telemetry = new();
    private readonly HashSet<long> _protectedPages = [];
    private InitializedRedoStream _stream;
    private BufferedStream _walStream;
    private long _nextLsn;
    private long _durableLsn;
    private long _durableGroups;
    private long _durableTransactions;
    private long _maximumObservedGroupCount;
    private long _maximumObservedGroupBytes;
    private RedoWalRecoveryResult? _startupRecovery;
    private Exception? _fault;
    private bool _disposed;

    private GroupedRedoDatabaseCommitLog(string path, InitializedRedoStream stream, FileStream ownership, RedoDurabilityOptions options,
        IRedoDurabilityObserver observer, RedoWalRecoveryResult recovery)
    {
        _path = path;
        _ownership = ownership;
        _stream = stream;
        _walStream = OpenWalStream(stream);
        _options = options;
        _observer = observer;
        _nextLsn = recovery.DurableLsn;
        _durableLsn = recovery.DurableLsn;
        _startupRecovery = stream.Length <= MaximumRetainedStartupRecoveryBytes ? recovery : null;
        int admissionCapacity = checked(options.MaximumGroupCount * AdmissionCapacityMultiplier);
        // Include queued and in-flight submissions, and reserve before copying mutation metadata.
        _submissionSlots = new(admissionCapacity, admissionCapacity);
        _admission = Channel.CreateBounded<PendingSubmission>(new BoundedChannelOptions(admissionCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
        _writer = WriteGroupsAsync();
    }

    /// <summary>Opens the sole selected local redo format and validates its committed prefix.</summary>
    /// <param name="path">The WAL path. Its adjacent ownership file is held exclusively until disposal.</param>
    /// <param name="options">Group-size and durability limits, or null for defaults.</param>
    /// <param name="observer">Optional observer of durability boundaries.</param>
    /// <param name="cancellationToken">Cancels opening and recovery validation.</param>
    /// <returns>The owned log; dispose it before opening another writer for the same path.</returns>
    /// <exception cref="PhysicalPageIoException">Ownership cannot be acquired or local storage cannot be read.</exception>
    public static async ValueTask<GroupedRedoDatabaseCommitLog> OpenAsync(string path,
        RedoDurabilityOptions? options = null, IRedoDurabilityObserver? observer = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        InitializedRedoStream? stream = null;
        FileStream? ownership = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            // Read sharing on WAL files does not enforce a single writer on
            // every OS. Keep a separate exclusive handle across WAL replacement.
            // Never unlink this file: another owner must lock the same inode.
            ownership = new FileStream(fullPath + ".owner", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            // An opener may have passed format discovery before retirement.
            // Recheck only after acquiring the same stable ownership inode.
            LocalDatabaseRetirement.RequireLive(Path.GetDirectoryName(fullPath)!);
            stream = await InitializedRedoStream.OpenAsync(fullPath, cancellationToken: cancellationToken, observer: observer).ConfigureAwait(false);
            RedoWalRecoveryResult recovery = await RedoWalRecovery.ReadAsync(stream, false, cancellationToken)
                .ConfigureAwait(false);
            stream.Seek(BeginningOfFile, SeekOrigin.End);
            var result = new GroupedRedoDatabaseCommitLog(fullPath, stream, ownership, options ?? new(),
                observer ?? new NoRedoDurabilityObserver(), recovery);
            stream = null;
            ownership = null;
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PhysicalPageException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new PhysicalPageIoException("The grouped redo log could not be opened.", exception);
        }
        finally
        {
            try
            {
                if (stream is not null)
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                ownership?.Dispose();
            }
        }
    }

    // Restore only: invoked in an unpublished staging directory before any writer exists.
    internal static async ValueTask InitializeRestoredFloorAsync(string path, long floor,
        CancellationToken cancellationToken)
    {
        if (floor < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(floor));
        }

        if (File.Exists(path))
        {
            throw new InvalidOperationException("A restored WAL must be new.");
        }

        await using var stream = await InitializedRedoStream.OpenAsync(path,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(RedoWalCodec.GroupMarker(floor, 0), cancellationToken).ConfigureAwait(false);
        await stream.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask CommitAsync(DurableTransactionEnvelope transaction,
        CancellationToken cancellationToken = default)
        => await CommitWithRedoAsync(transaction, Array.Empty<PhysicalPageRedoMutation>(), cancellationToken)
            .ConfigureAwait(false);

    /// <summary>Atomically protects a logical transaction, its committed effects and physical page redo.</summary>
    public ValueTask CommitWithRedoAsync(DurableTransactionEnvelope transaction,
        IReadOnlyList<PhysicalPageRedoMutation> mutations, CancellationToken cancellationToken = default) =>
        CommitWithRedoCoreAsync(transaction, mutations, cancellationToken);

    private async ValueTask CommitWithRedoCoreAsync(DurableTransactionEnvelope transaction,
        IEnumerable<PhysicalPageRedoMutation> mutations, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(mutations);
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfFaulted(transaction.TransactionId);
        cancellationToken.ThrowIfCancellationRequested();
        await _submissionSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ThrowIfFaulted(transaction.TransactionId);
            cancellationToken.ThrowIfCancellationRequested();
            var pending = new PendingCommit(transaction, mutations);
            try
            {
                await _admission.Writer.WriteAsync(new([pending], _telemetry.QueueTimestamp()), cancellationToken).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                ThrowIfFaulted(transaction.TransactionId);
                throw;
            }
            await pending.Completion.Task.ConfigureAwait(false);
        }
        finally
        {
            _submissionSlots.Release();
        }
    }

    /// <summary>Submits a logical transaction and its physical mutations to the grouped redo writer.</summary>
    /// <param name="transaction">Logical transaction to durably record.</param>
    /// <param name="mutations">Caller-owned physical mutations and buffers, which must remain immutable until completion.</param>
    /// <param name="cancellationToken">Cancels admission. After admission, completion waits for the durable outcome even if cancelled.</param>
    /// <returns>A task completed after durable recording, or faulted if the writer fails.</returns>
    /// <remarks>This records recovery data; the caller remains responsible for applying and publishing physical changes.</remarks>
    public ValueTask CommitWithPhysicalMutationsAsync(DurableTransactionEnvelope transaction,
        IReadOnlyList<DatabasePhysicalPageMutation> mutations,
        CancellationToken cancellationToken = default) => CommitWithRedoCoreAsync(transaction,
            mutations.Select(PhysicalPageRedoMutation.Borrow), cancellationToken);

    /// <summary>Checks that a nonempty group fits the configured transaction count and estimated logical-byte limits.</summary>
    /// <param name="commits">Ordered commits to inspect without submitting them.</param>
    /// <remarks>The byte estimate includes payload content, committed-change content and fixed WAL framing.
    /// It excludes physical mutation buffers, protection images and compression effects; it is not a bound on
    /// total retained memory or encoded WAL size. Validation does not reserve admission or validate page contents.</remarks>
    public void ValidateGroup(IReadOnlyList<DatabasePhysicalCommit> commits)
    {
        ArgumentNullException.ThrowIfNull(commits);
        if (commits.Count == 0)
        {
            throw new ArgumentException("A redo group cannot be empty.", nameof(commits));
        }

        if (commits.Count > _options.MaximumGroupCount)
        {
            throw new ArgumentOutOfRangeException(nameof(commits),
                $"A redo group cannot exceed {_options.MaximumGroupCount} transactions.");
        }

        long estimatedBytes = commits.Sum(item => Estimate(item.Transaction));
        if (estimatedBytes > _options.MaximumGroupBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(commits),
                $"A redo group cannot exceed {_options.MaximumGroupBytes} estimated bytes.");
        }
    }

    /// <summary>Submits an ordered transaction group under one atomic WAL group marker.</summary>
    /// <param name="commits">Nonempty group within configured limits. Keep envelopes, mutation lists and buffers immutable through completion.</param>
    /// <param name="cancellationToken">Cancels admission, not a group already admitted to the writer.</param>
    /// <returns>A task that waits for every submitted transaction's durable outcome.</returns>
    /// <remarks>The writer may combine this submission with others in a larger bounded group.
    /// Physical application and in-memory publication remain the caller's responsibility.</remarks>
    public async ValueTask CommitGroupAsync(IReadOnlyList<DatabasePhysicalCommit> commits,
        CancellationToken cancellationToken = default)
    {
        ValidateGroup(commits);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfFaulted(commits[0].Transaction.TransactionId);
        await _submissionSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ThrowIfFaulted(commits[0].Transaction.TransactionId);
            cancellationToken.ThrowIfCancellationRequested();
            PendingCommit[] pending = commits.Select(commit => new PendingCommit(commit.Transaction,
                commit.Mutations.Select(PhysicalPageRedoMutation.Borrow))).ToArray();
            try
            {
                await _admission.Writer.WriteAsync(new(pending, _telemetry.QueueTimestamp()), cancellationToken).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                ThrowIfFaulted(pending[0].Transaction.TransactionId);
                throw;
            }
            await Task.WhenAll(pending.Select(item => item.Completion.Task)).ConfigureAwait(false);
        }
        finally
        {
            _submissionSlots.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask ValidateRecoveryCheckpointAsync(DurableCheckpointEnvelope? checkpoint,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _fileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _walStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            RedoWalRecoveryResult recovery = _startupRecovery ??
                await RedoWalRecovery.ReadAsync(_stream, false, cancellationToken).ConfigureAwait(false);
            // Logical-only custom checkpoint stores do not record a physical WAL
            // position. They can still prove checkpoint presence; built-in local
            // compositions additionally provide the position for stale-cut checks.
            if (recovery.RequiredCheckpointLsn > 0 && (checkpoint is null ||
                checkpoint.PhysicalRecovery is { } physical && physical.WalFloor < recovery.RequiredCheckpointLsn))
            {
                throw new EntityForgeDbFormatException(
                    "The checkpoint is missing or predates reclaimed commit history. Restore a complete database backup.");
            }
            if (checkpoint?.PhysicalRecovery is { } checkpointImage && checkpointImage.WalFloor > recovery.DurableLsn)
            {
                throw new EntityForgeDbFormatException(
                    "The commit log predates the checkpoint's durable redo position. Restore a complete database backup.");
            }
        }
        finally
        {
            _stream.Seek(BeginningOfFile, SeekOrigin.End);
            _fileGate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<DurableTransactionEnvelope>> RecoverAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _fileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _walStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            RedoWalRecoveryResult recovery = _startupRecovery ??
                await RedoWalRecovery.ReadAsync(_stream, false, cancellationToken).ConfigureAwait(false);
            DurableTransactionEnvelope[] transactions = recovery.Transactions
                .Select(item => item.Transaction).ToArray();
            _startupRecovery = null;
            return transactions;
        }
        finally
        {
            _stream.Seek(BeginningOfFile, SeekOrigin.End);
            _fileGate.Release();
        }
    }

    /// <inheritdoc />
    public ValueTask<DatabaseCommitLogReclamationResult> ReclaimThroughAsync(
        DatabaseVersion checkpointVersion, CancellationToken cancellationToken = default) =>
        ReclaimCoreAsync(checkpointVersion, null, cancellationToken);

    /// <summary>Starts a new full-page protection epoch while holding the WAL writer's file gate.</summary>
    /// <param name="cancellationToken">Cancels waiting for the file gate.</param>
    /// <returns>The durable LSN captured at the epoch boundary.</returns>
    /// <remarks>The checkpoint coordinator must align this boundary with its captured database state before foreground
    /// writes resume. This resets first-write protection tracking; it does not create or persist a checkpoint.</remarks>
    public async ValueTask<long> BeginCheckpointEpochAsync(CancellationToken cancellationToken = default)
    {
        await WaitForFileGateAsync("checkpoint_epoch", cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ThrowIfFaulted(Guid.Empty);
            _protectedPages.Clear();
            return Interlocked.Read(ref _durableLsn);
        }
        finally
        {
            _fileGate.Release();
        }
    }

    /// <summary>Reads the latest durable LSN without starting a checkpoint epoch or waiting for queued submissions.</summary>
    public long CaptureCheckpointPosition() => Interlocked.Read(ref _durableLsn);

    /// <summary>Rewrites the WAL to reclaim history covered by an already durable checkpoint while retaining its required tail.</summary>
    /// <param name="checkpointVersion">Logical version covered by the durable checkpoint.</param>
    /// <param name="checkpointPosition">Captured physical WAL position, between zero and the current durable LSN.</param>
    /// <param name="cancellationToken">Cancels reclamation where supported.</param>
    /// <returns>Removed-record and reclaimed-byte counts.</returns>
    /// <remarks>The caller must first durably publish a consistent checkpoint and preserve it for recovery.
    /// A range-valid position alone does not prove checkpoint durability or consistency. Replacement failures
    /// after publication begins may fault the writer; cancellation is not a guarantee that the old WAL remains selected.</remarks>
    public ValueTask<DatabaseCommitLogReclamationResult> ReclaimCheckpointAsync(DatabaseVersion checkpointVersion,
        long checkpointPosition, CancellationToken cancellationToken = default)
    {
        if (checkpointPosition < 0 || checkpointPosition > CaptureCheckpointPosition())
        {
            throw new ArgumentOutOfRangeException(nameof(checkpointPosition));
        }

        return ReclaimCoreAsync(checkpointVersion, checkpointPosition, cancellationToken);
    }

    private async ValueTask<DatabaseCommitLogReclamationResult> ReclaimCoreAsync(
        DatabaseVersion checkpointVersion, long? checkpointPosition, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _reclamationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string replacement = _path + ReplacementSuffix;
        bool fileHeld = false;
        long fileHoldStarted = 0;
        string fileHoldPhase = "prefix_capture";
        void ReleaseReclamationFileGate()
        {
            double milliseconds = fileHoldStarted == 0 ? 0 : Stopwatch.GetElapsedTime(fileHoldStarted).TotalMilliseconds;
            _fileGate.Release();
            fileHeld = false;
            // Record after release so synchronous listeners cannot extend writer exclusion.
            if (fileHoldStarted != 0)
            {
                _telemetry.ReclamationFileGateHold(fileHoldPhase, milliseconds);
            }

            fileHoldStarted = 0;
        }
        InitializedRedoStream? target = null;
        bool replacementStarted = false;
        bool replacementCompleted = false;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // Initialize the next generation without holding the active writer gate.
            // It remains unselected until the complete retained tail is durable.
            _observer.Reached(RedoDurabilityStage.BeforeReplacementInitialization);
            File.Delete(replacement);
            long initializeStarted = _telemetry.MeasureReclamation ? Stopwatch.GetTimestamp() : 0;
            target = await _stream.OpenSiblingAsync(replacement, cancellationToken, _telemetry).ConfigureAwait(false);
            _telemetry.ReclamationPhase("replacement_initialize", initializeStarted);
            _observer.Reached(RedoDurabilityStage.AfterReplacementInitialization);
            await WaitForFileGateAsync("reclamation", cancellationToken).ConfigureAwait(false);
            fileHeld = true;
            fileHoldStarted = _telemetry.MeasureReclamationFileGateHold ? Stopwatch.GetTimestamp() : 0;
            _observer.Reached(RedoDurabilityStage.BeforeWalReclamation);
            _startupRecovery = null;
            long phaseStarted = _telemetry.MeasureReclamation ? Stopwatch.GetTimestamp() : 0;
            await _walStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            phaseStarted = _telemetry.ReclamationPhase("source_flush", phaseStarted);
            long sourceLogicalBytes = _stream.Length;
            long originalBytes = _stream.PhysicalLength;
            RedoWalReclamationResult reclamation;
            // Reclamation serialization keeps this generation alive. Use a separate
            // bounded handle so the immutable prefix scan cannot seek the writer.
            await using (var prefix = _stream.OpenReadRange(0, sourceLogicalBytes))
            {
                _stream.Seek(BeginningOfFile, SeekOrigin.End);
                ReleaseReclamationFileGate();
                _observer.Reached(RedoDurabilityStage.BeforeReclamationPrefixScan);
                reclamation = await RedoWalRecovery.RewriteAfterAsync(prefix, target,
                    checkpointVersion, cancellationToken, checkpointPosition).ConfigureAwait(false);
                phaseStarted = _telemetry.ReclamationPhase("source_scan_rewrite", phaseStarted);
            }
            await WaitForFileGateAsync("reclamation_catchup", cancellationToken).ConfigureAwait(false);
            fileHeld = true;
            fileHoldPhase = "catchup_publish";
            fileHoldStarted = _telemetry.MeasureReclamationFileGateHold ? Stopwatch.GetTimestamp() : 0;
            ThrowIfFaulted(Guid.Empty);
            await _walStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            long finalSourceBytes = _stream.Length;
            originalBytes = _stream.PhysicalLength;
            {
                await using (var tail = _stream.OpenReadRange(sourceLogicalBytes, finalSourceBytes))
                {
                    var catchup = await RedoWalRecovery.RewriteAfterAsync(tail, target,
                        checkpointVersion, cancellationToken, checkpointPosition, reclamation.DurableLsn).ConfigureAwait(false);
                    reclamation = new(reclamation.RemovedTransactions + catchup.RemovedTransactions, catchup.DurableLsn);
                }
                phaseStarted = _telemetry.ReclamationPhase("source_catchup", phaseStarted);
                _telemetry.ReclamationWork(finalSourceBytes, reclamation.RemovedTransactions);
                await target.FlushAsync(cancellationToken).ConfigureAwait(false);
                await target.CommitAsync(cancellationToken).ConfigureAwait(false);
                phaseStarted = _telemetry.ReclamationPhase("replacement_commit", phaseStarted);
            }
            await target.DisposeAsync().ConfigureAwait(false);
            target = null;
            replacementStarted = true;
            await _walStream.DisposeAsync().ConfigureAwait(false);
            File.Move(replacement, _path, true);
            InitializedRedoStream.SyncDirectory(Path.GetDirectoryName(_path)!);
            phaseStarted = _telemetry.ReclamationPhase("replacement_publish", phaseStarted);
            _stream = await _stream.OpenSiblingAsync(_path, cancellationToken).ConfigureAwait(false);
            _walStream = OpenWalStream(_stream);
            _stream.Seek(BeginningOfFile, SeekOrigin.End);
            _protectedPages.Clear();
            _telemetry.ReclamationPhase("replacement_reopen", phaseStarted);
            _observer.Reached(RedoDurabilityStage.AfterWalReclamation);
            replacementCompleted = true;
            _telemetry.Reclaimed(originalBytes - _stream.PhysicalLength);
            return new(checkpointVersion, reclamation.RemovedTransactions,
                originalBytes - _stream.PhysicalLength);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PhysicalPageException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new PhysicalPageIoException("The redo log could not be reclaimed after checkpoint publication.", exception);
        }
        finally
        {
            if (replacementStarted && !replacementCompleted)
            {
                Interlocked.CompareExchange(ref _fault, new IOException("Uncertain WAL generation publication."), null);
            }

            try
            {
                if (target is not null)
                {
                    await target.DisposeAsync().ConfigureAwait(false);
                }

                TryDelete(replacement);
                if (fileHeld && _stream.CanSeek)
                {
                    _stream.Seek(BeginningOfFile, SeekOrigin.End);
                }
            }
            finally
            {
                try
                {
                    if (fileHeld)
                    {
                        ReleaseReclamationFileGate();
                    }
                }
                finally
                {
                    _reclamationGate.Release();
                }
            }
        }
    }

    /// <inheritdoc />
    public ValueTask<DatabaseStorageStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        return ValueTask.FromResult(new DatabaseStorageStatus(_stream.PhysicalLength));
    }

    /// <summary>Returns bounded grouped-redo diagnostics.</summary>
    public RedoDurabilityStatus GetRedoStatus() => new(
        Interlocked.Read(ref _durableLsn), _stream.PhysicalLength,
        Interlocked.Read(ref _durableGroups), Interlocked.Read(ref _durableTransactions),
        Interlocked.Read(ref _maximumObservedGroupCount), Interlocked.Read(ref _maximumObservedGroupBytes), _stream.Length);

    /// <summary>Replays committed physical page mutations newer than the stored page LSN.</summary>
    public async ValueTask RecoverPagesAsync(IResizablePhysicalPageStore pages,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pages);
        await _fileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _walStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            RedoWalRecoveryResult recovery = _startupRecovery ??
                await RedoWalRecovery.ReadAsync(_stream, false, cancellationToken).ConfigureAwait(false);
            await RedoPageRecovery.ApplyAsync(recovery.Transactions, pages, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _stream.Seek(BeginningOfFile, SeekOrigin.End);
            _fileGate.Release();
        }
    }

    private async Task WriteGroupsAsync()
    {
        List<PendingCommit>? inFlight = null;
        try
        {
            await foreach (PendingSubmission first in _admission.Reader.ReadAllAsync(_shutdown.Token).ConfigureAwait(false))
            {
                var group = inFlight = new List<PendingCommit>(_options.MaximumGroupCount);
                group.AddRange(first.Commits);
                _telemetry.QueueWait(first.QueuedAt);
                long estimatedBytes = first.Commits.Sum(item => Estimate(item.Transaction));
                if (_options.MaximumGroupDelay > TimeSpan.Zero)
                {
                    long waitStarted = Stopwatch.GetTimestamp();
                    await Task.Delay(_options.MaximumGroupDelay, _shutdown.Token).ConfigureAwait(false);
                    _telemetry.BatchWait(waitStarted);
                }

                while (group.Count < _options.MaximumGroupCount &&
                       _admission.Reader.TryPeek(out PendingSubmission? candidate) &&
                       group.Count + candidate.Commits.Count <= _options.MaximumGroupCount &&
                       estimatedBytes + candidate.Commits.Sum(item => Estimate(item.Transaction)) <=
                           _options.MaximumGroupBytes &&
                       _admission.Reader.TryRead(out PendingSubmission? accepted))
                {
                    group.AddRange(accepted.Commits);
                    _telemetry.QueueWait(accepted.QueuedAt);
                    estimatedBytes += accepted.Commits.Sum(item => Estimate(item.Transaction));
                }
                await PersistGroupAsync(group).ConfigureAwait(false);
                inFlight = null;
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {

        }
        catch (Exception exception)
        {
            // Ownership began when the channel item was removed, not when WAL writing began.
            // Pre-encoding/protection failures must also complete these admitted callers.
            var failure = Volatile.Read(ref _fault) ?? new DatabaseCommitLogRecoveryRequiredException(
                inFlight?.FirstOrDefault()?.Transaction.TransactionId ?? Guid.Empty, false,
                "The redo writer failed before completing its admitted group; reopen before reuse.", exception);
            Interlocked.CompareExchange(ref _fault, failure, null);
            _admission.Writer.TryComplete(failure);
            if (inFlight is not null)
            {
                foreach (PendingCommit pending in inFlight)
                {
                    pending.Completion.TrySetException(failure);
                }
            }

            while (_admission.Reader.TryRead(out PendingSubmission? submission))
            {
                foreach (PendingCommit pending in submission.Commits)
                {
                    pending.Completion.TrySetException(failure);
                }
            }
        }
    }

    private ValueTask WaitForFileGateAsync(string operation, CancellationToken cancellationToken) =>
        _telemetry.MeasureFileGateWait
            ? MeasureFileGateWaitAsync(operation, cancellationToken)
            : new(_fileGate.WaitAsync(cancellationToken));

    private async ValueTask MeasureFileGateWaitAsync(string operation, CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        await _fileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _telemetry.FileGateWait(operation, started);
        }
        catch
        {
            _fileGate.Release();
            throw;
        }
    }

    private async ValueTask PersistGroupAsync(IReadOnlyList<PendingCommit> group)
    {
        long phaseStarted = Stopwatch.GetTimestamp();
        long phaseAllocated = GC.GetTotalAllocatedBytes();
        bool writeStarted = false;
        long encodedTransactionBytes = 0;
        long started = Stopwatch.GetTimestamp();
        bool fileHeld = false;
        try
        {
            await WaitForFileGateAsync("commit", _shutdown.Token).ConfigureAwait(false);
            fileHeld = true;
            ThrowIfFaulted(group[0].Transaction.TransactionId);
            var encoded = new List<(PendingCommit Pending, long Lsn)>(group.Count);
            foreach (PendingCommit pending in group)
            {
                long lsn = Interlocked.Increment(ref _nextLsn);
                pending.PageEntries = Protect(pending.Mutations);
                encoded.Add((pending, lsn));
            }
            _telemetry.Phase("protection", phaseStarted, phaseAllocated);
            long lastLsn = encoded[^1].Lsn;
            byte[] groupMarker = RedoWalCodec.GroupMarker(lastLsn, group.Count);
            {
                _observer.Reached(RedoDurabilityStage.BeforeWalWrite);
                _startupRecovery = null;
                writeStarted = true;
                phaseStarted = Stopwatch.GetTimestamp();
                phaseAllocated = GC.GetTotalAllocatedBytes();
                foreach ((PendingCommit pending, long lsn) in encoded)
                {
                    encodedTransactionBytes += await RedoWalCodec.WriteTransactionAsync(_walStream, lsn,
                        pending.Transaction, pending.PageEntries, _shutdown.Token).ConfigureAwait(false);
                }

                await _walStream.WriteAsync(groupMarker, _shutdown.Token)
                    .ConfigureAwait(false);
                _telemetry.Phase("encode_write", phaseStarted, phaseAllocated);
                _observer.Reached(RedoDurabilityStage.AfterWalWrite);
                _observer.Reached(RedoDurabilityStage.BeforeWalFlush);
                phaseStarted = Stopwatch.GetTimestamp();
                phaseAllocated = GC.GetTotalAllocatedBytes();
                await _walStream.FlushAsync(_shutdown.Token).ConfigureAwait(false);
                _telemetry.Phase("buffer_flush", phaseStarted, phaseAllocated);
                phaseStarted = Stopwatch.GetTimestamp();
                phaseAllocated = GC.GetTotalAllocatedBytes();
                await _stream.CommitAsync(_shutdown.Token, _telemetry).ConfigureAwait(false);
                _telemetry.Phase("durable_flush", phaseStarted, phaseAllocated);
                _observer.Reached(RedoDurabilityStage.AfterWalFlush);
            }
            Interlocked.Exchange(ref _durableLsn, lastLsn);
            Interlocked.Increment(ref _durableGroups);
            Interlocked.Add(ref _durableTransactions, group.Count);
            UpdateMaximum(ref _maximumObservedGroupCount, group.Count);
            UpdateMaximum(ref _maximumObservedGroupBytes,
                encodedTransactionBytes + groupMarker.Length);
            _telemetry.Group(group.Count,
                encodedTransactionBytes + groupMarker.Length,
                started);
            foreach (PendingCommit pending in group)
            {
                pending.Completion.TrySetResult();
            }
        }
        catch (Exception exception)
        {
            if (writeStarted)
            {
                _telemetry.Uncertain(group.Count);
            }

            foreach (PendingCommit pending in group)
            {
                DatabaseCommitLogRecoveryRequiredException failure = writeStarted
                    ? new RedoCommitUncertainException(pending.Transaction.TransactionId, exception)
                    : new DatabaseCommitLogRecoveryRequiredException(pending.Transaction.TransactionId, false,
                        "The redo group was not written, but the live writer requires recovery before reuse.", exception);
                Interlocked.CompareExchange(ref _fault, failure, null);
                pending.Completion.TrySetException(failure);
            }
            _admission.Writer.TryComplete(Volatile.Read(ref _fault));
            throw Volatile.Read(ref _fault)!;
        }
        finally
        {
            if (fileHeld)
            {
                _fileGate.Release();
            }
        }
    }


    private static long Estimate(DurableTransactionEnvelope transaction) =>
        checked((long)transaction.Payload.Content.Length +
                (transaction.CommittedChanges?.Content.Length ?? 0) + RedoWalCodec.HeaderSize + RedoWalCodec.ChecksumSize);

    private IReadOnlyList<RedoPageEntry> Protect(IReadOnlyList<PhysicalPageRedoMutation> mutations)
    {
        var entries = new List<RedoPageEntry>(mutations.Count * 2);
        foreach (PhysicalPageRedoMutation mutation in mutations)
        {
            bool replacesCompletePage = mutation.Offset == 0 &&
                (!mutation.PageExisted || mutation.Bytes.Length == mutation.BeforeImage.Length);
            // A complete after-image is itself sufficient to repair a torn or absent page.
            // Partial patches still require the first protected pre-image in the WAL epoch.
            if (_protectedPages.Add(mutation.PageId.Value) && !replacesCompletePage)
            {
                entries.Add(new(RedoPageEntryKind.FullPageProtection, mutation.PageId.Value, 0,
                    mutation.BeforeImage));
            }

            entries.Add(new(RedoPageEntryKind.Patch, mutation.PageId.Value, mutation.Offset,
                mutation.Bytes));
        }
        return entries;
    }

    private static BufferedStream OpenWalStream(Stream stream) =>
        new(stream, WalAggregationBufferSize);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {

        }
    }

    private static void UpdateMaximum(ref long target, long candidate)
    {
        long observed;
        while (candidate > (observed = Interlocked.Read(ref target)) &&
               Interlocked.CompareExchange(ref target, candidate, observed) != observed)
        {
        }
    }

    private void ThrowIfFaulted(Guid transactionId)
    {
        if (Volatile.Read(ref _fault) is not { } fault)
        {
            return;
        }

        throw new DatabaseCommitLogRecoveryRequiredException(transactionId, true,
            "The redo writer is faulted; reopen and recover the database before committing.", fault);
    }

    /// <summary>Closes commit admission, waits for the writer and releases the redo stream and journal ownership.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _admission.Writer.TryComplete();
            await _writer.ConfigureAwait(false);
            _shutdown.Cancel();
            // An outside-writer-gate prefix scan still owns this generation.
            await _reclamationGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await _walStream.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                _reclamationGate.Release();
            }
            _telemetry.Dispose();
            _shutdown.Dispose();
            _fileGate.Dispose();
        }
        finally
        {
            _ownership.Dispose();
        }
    }

    private sealed class PendingCommit(DurableTransactionEnvelope transaction,
        IEnumerable<PhysicalPageRedoMutation> mutations)
    {
        internal DurableTransactionEnvelope Transaction { get; } = transaction;

        internal IReadOnlyList<PhysicalPageRedoMutation> Mutations { get; } = mutations.ToArray();

        internal IReadOnlyList<RedoPageEntry> PageEntries { get; set; } = Array.Empty<RedoPageEntry>();

        internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record PendingSubmission(IReadOnlyList<PendingCommit> Commits, long QueuedAt);

}
