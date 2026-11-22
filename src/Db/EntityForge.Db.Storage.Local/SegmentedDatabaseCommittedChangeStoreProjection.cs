using System.Runtime.CompilerServices;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using EntityForge.Db.Storage;

namespace EntityForge.Db.Storage.Local;

/// <summary>Keeps the WAL-authoritative journal tail queryable in memory until a durability boundary.</summary>
public sealed class WalBackedDatabaseCommittedChangeStore : IDatabaseCommittedChangeStore,
    IDatabaseCommittedChangeDurabilityBarrier, IDatabaseCommittedChangeCheckpointPreparation, IDatabaseChangeJournalDiagnostics,
    IDatabaseChangeJournalSnapshotParticipant
{
    /// <summary>The default upper bound on pending changes awaiting journal materialization.</summary>
    public const int DefaultMaximumPendingChanges = 1_024;
    /// <summary>The default upper bound on pending encoded change bytes.</summary>
    public const long DefaultMaximumPendingBytes = 64L * 1024 * 1024;
    private readonly SegmentedDatabaseCommittedChangeStore _durable;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<CommittedChangeSet> _pending = [];
    private readonly Dictionary<DatabaseVersion, CommittedChangeSet> _pendingByVersion = [];
    private readonly Dictionary<DatabaseChangeCursor, CommittedChangeSet> _pendingByCursor = [];
    private static readonly Meter ProjectionMeter = new("EntityForge.Db.ChangeJournal.Projection");
    private static readonly Histogram<double> Backpressure = ProjectionMeter.CreateHistogram<double>(
        "entityforge.db.change_journal.backpressure.duration", "ms");

    private TaskCompletionSource _published = NewSignal();
    private DatabaseChangeCursor? _visibleCursor;
    private long? _visibleVersion;
    private readonly int _maximumPendingChanges;
    private readonly long _maximumPendingBytes;
    private long _pendingBytes;
    private int _closing;
    private int _disposed;
    private readonly object _disposeGate = new();
    private Task? _disposeTask;
    private Task? _materialization;
    private int _materializingCount;
    private long _materializingBytes;
    private readonly Func<CancellationToken, ValueTask>? _beforeDurableSnapshotCapture;
    private readonly Func<ValueTask>? _shutdownFlushFailure;
    private readonly Action? _afterDurableVisibilityCommitted;
    private readonly Func<ValueTask>? _beforeBackgroundMaterialization;

    /// <summary>Creates a WAL-backed change store with explicit pending-work bounds and journal persistence.</summary>
    public WalBackedDatabaseCommittedChangeStore(string directory,
        DatabaseChangeJournalRetentionOptions? retention = null, TimeProvider? timeProvider = null,
        DatabaseChangeJournalTelemetry? telemetry = null,
        int maximumPendingChanges = DefaultMaximumPendingChanges,
        long maximumPendingBytes = DefaultMaximumPendingBytes)
        : this(new(directory, retention, timeProvider, telemetry), maximumPendingChanges,
            maximumPendingBytes, null, null, null)
    {
    }

    internal WalBackedDatabaseCommittedChangeStore(SegmentedDatabaseCommittedChangeStore durable,
        int maximumPendingChanges = DefaultMaximumPendingChanges,
        long maximumPendingBytes = DefaultMaximumPendingBytes,
        Func<CancellationToken, ValueTask>? beforeDurableSnapshotCapture = null,
        Func<ValueTask>? shutdownFlushFailure = null,
        Action? afterDurableVisibilityCommitted = null,
        Func<ValueTask>? beforeBackgroundMaterialization = null)
    {
        if (maximumPendingChanges <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumPendingChanges));
        }

        if (maximumPendingBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumPendingBytes));
        }

        _durable = durable ?? throw new ArgumentNullException(nameof(durable));
        _maximumPendingChanges = maximumPendingChanges;
        _maximumPendingBytes = maximumPendingBytes;
        _beforeDurableSnapshotCapture = beforeDurableSnapshotCapture;
        _shutdownFlushFailure = shutdownFlushFailure;
        _afterDurableVisibilityCommitted = afterDurableVisibilityCommitted;
        _beforeBackgroundMaterialization = beforeBackgroundMaterialization;
    }

    /// <inheritdoc />
    public async ValueTask AppendAsync(CommittedChangeSet changeSet,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changeSet);
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            ChangeReplayClassification replay = await _durable.ClassifyReplayAsync(changeSet,
                cancellationToken).ConfigureAwait(false);
            if (replay is not ChangeReplayClassification.RequiresMaterialization)
            {
                return;
            }

            CommittedChangeSet? sameVersion = _pendingByVersion.GetValueOrDefault(changeSet.Version);
            if (sameVersion is not null)
            {
                if (sameVersion.TransactionId == changeSet.TransactionId && sameVersion.Cursor == changeSet.Cursor)
                {
                    return;
                }

                throw new EntityForgeDbFormatException(
                    "Committed-change projection versions must be unique and increasing.");
            }
            if (_pending.Count != 0 && changeSet.Version.Value <= _pending[^1].Version.Value)
            {
                throw new EntityForgeDbFormatException(
                    "Committed-change projection versions must be unique and increasing.");
            }

            if (_pendingByCursor.ContainsKey(changeSet.Cursor))
            {
                throw new EntityForgeDbFormatException("Committed-change projection cursors must be unique.");
            }

            _pendingByVersion.Add(changeSet.Version, changeSet);
            _pendingByCursor.Add(changeSet.Cursor, changeSet);
            _pending.Add(changeSet);
            _pendingBytes = checked(_pendingBytes + EstimateBytes(changeSet));
            if (_pending.Count >= _maximumPendingChanges || _pendingBytes >= _maximumPendingBytes)
            {
                long started = Stopwatch.GetTimestamp();
                try
                {
                    await FlushPendingLockedAsync(CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    Backpressure.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask PublishThroughAsync(DatabaseChangeCursor cursor,
        CancellationToken cancellationToken = default)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (!_pendingByCursor.ContainsKey(cursor))
            {
                await _durable.PublishThroughAsync(cursor, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask NotifyPublishedAsync(DatabaseChangeCursor cursor,
        CancellationToken cancellationToken = default)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            CommittedChangeSet? pending = _pendingByCursor.GetValueOrDefault(cursor);
            if (pending is not null && _visibleVersion is long visible &&
                pending.Version.Value <= visible)
            {
                return;
            }

            if (pending is null)
            {
                (DatabaseChangeCursor effectiveCursor, long effectiveVersion) =
                    await _durable.NotifyPublishedAndGetFrontierAsync(cursor, cancellationToken)
                        .ConfigureAwait(false);
                _afterDurableVisibilityCommitted?.Invoke();
                if (_visibleVersion is long currentVisible && effectiveVersion <= currentVisible)
                {
                    return;
                }

                _visibleCursor = effectiveCursor;
                _visibleVersion = effectiveVersion;
            }
            else
            {
                _visibleCursor = cursor;
                _visibleVersion = pending.Version.Value;
            }
            TaskCompletionSource signal = _published;
            _published = NewSignal();
            signal.TrySetResult();
            StartMaterializationLocked();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<DatabaseChangeFeedBounds> GetBoundsAsync(
        CancellationToken cancellationToken = default)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            DatabaseChangeFeedBounds durable = await _durable.GetBoundsAsync(cancellationToken)
                .ConfigureAwait(false);
            DatabaseChangeCursor? earliest = durable.Earliest ??
                (_pending.Count == 0 ? null : _pending[0].Cursor);
            return new(earliest, _visibleCursor ?? durable.Current);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<DatabaseChangePage> ReadPageAfterAsync(DatabaseChangeCursor? cursor,
        DatabaseChangeSelector selector, int maximumCount, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selector);
        if (maximumCount <= 0 || maximumCount > DatabaseChangeFeedLimits.MaximumPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            int pendingStart = PendingStart(cursor);
            if (pendingStart < 0)
            {
                DatabaseChangePage durable = await _durable.ReadPageAfterAsync(cursor, selector, maximumCount,
                    cancellationToken).ConfigureAwait(false);
                if (durable.HasMore || durable.Changes.Count == maximumCount || _pending.Count == 0)
                {
                    return durable with
                    {
                        HasMore = durable.HasMore || _pending.Count != 0
                    };
                }

                return AppendPending(durable.Changes, durable.NextCursor, selector, maximumCount,
                    Math.Max(0, PendingStart(durable.NextCursor)));
            }
            return AppendPending([], cursor, selector, maximumCount, pendingStart);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<CommittedChangeSet> SubscribeAsync(DatabaseChangeCursor? cursor,
        DatabaseChangeSelector selector, DatabaseChangeSubscriptionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(options);
        DatabaseChangeCursor? position = cursor;
        while (true)
        {
            DatabaseChangePage page = await ReadPageAfterAsync(position, selector, options.PageSize,
                cancellationToken).ConfigureAwait(false);
            position = page.NextCursor;
            foreach (CommittedChangeSet change in page.Changes)
            {
                yield return change;
            }

            if (page.HasMore)
            {
                continue;
            }

            Task signal;
            await EnterAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                if (position != _visibleCursor)
                {
                    continue;
                }

                signal = _published.Task;
            }
            finally
            {
                _gate.Release();
            }
            await signal.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask PrepareCheckpointAsync(CancellationToken cancellationToken = default)
    {
        Task? prefix;
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            StartMaterializationLocked(force: true);
            prefix = _materialization;
        }
        finally
        {
            _gate.Release();
        }
        // The owned worker is drained by final flush/disposal even if this waiter cancels.
        // New changes remain in the pending tail; preparation does not certify a cut.
        if (prefix is not null)
        {
            await prefix.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await FlushPendingLockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<DatabaseChangeJournalStatus> GetStatusAsync(
        CancellationToken cancellationToken = default)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            DatabaseChangeJournalStatus durable = await _durable.GetStatusAsync(cancellationToken)
                .ConfigureAwait(false);
            return durable with
            {
                SegmentCount = durable.SegmentCount + _pending.Count,
                PublishedCurrent = _visibleCursor ?? durable.PublishedCurrent,
                MaterializationDebt = durable.MaterializationDebt + _pending.Count
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<DatabaseChangeJournalSnapshot> CaptureSnapshotThroughAsync(
        DatabaseChangeCursor? cursor, CancellationToken cancellationToken = default)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await FlushPendingLockedAsync(cancellationToken).ConfigureAwait(false);
            if (_beforeDurableSnapshotCapture is not null)
            {
                await _beforeDurableSnapshotCapture(cancellationToken).ConfigureAwait(false);
            }

            return await _durable.CaptureSnapshotThroughAsync(cursor, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async ValueTask<IReadOnlyList<DatabaseChangeJournalFileArtifact>> CaptureSnapshotFilesThroughAsync(
        DatabaseChangeCursor? cursor, string destinationDirectory,
        CancellationToken cancellationToken = default)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await FlushPendingLockedAsync(cancellationToken).ConfigureAwait(false);
            if (_beforeDurableSnapshotCapture is not null)
            {
                await _beforeDurableSnapshotCapture(cancellationToken).ConfigureAwait(false);
            }

            return await _durable.CaptureSnapshotFilesThroughAsync(cursor, destinationDirectory,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Stops pending materialization and disposes resources owned by the change store.</summary>
    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            return new(_disposeTask ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        Interlocked.Exchange(ref _closing, 1);
        var failures = new List<Exception>();
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _published.TrySetException(new ObjectDisposedException(nameof(WalBackedDatabaseCommittedChangeStore)));
            try
            {
                await CompleteMaterializationLockedAsync().ConfigureAwait(false);
                if (_shutdownFlushFailure is not null)
                {
                    await _shutdownFlushFailure().ConfigureAwait(false);
                }

                await FlushPendingLockedAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
            Interlocked.Exchange(ref _disposed, 1);
        }
        finally
        {
            _gate.Release();
        }
        try
        {
            await _durable.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
        if (failures.Count != 0)
        {
            throw new AggregateException("WAL-backed committed-change store cleanup failed.", failures);
        }
    }

    private DatabaseChangePage AppendPending(IReadOnlyList<CommittedChangeSet> prefix,
        DatabaseChangeCursor? cursor, DatabaseChangeSelector selector, int maximumCount, int start)
    {
        var changes = new List<CommittedChangeSet>(prefix);
        DatabaseChangeCursor? next = cursor;
        bool hasMore = false;
        for (int index = start; index < _pending.Count && Visible(_pending[index]); index++)
        {
            CommittedChangeSet change = _pending[index];
            bool matches = Matches(change, selector);
            if (matches && changes.Count == maximumCount)
            {
                hasMore = true;
                break;
            }
            next = change.Cursor;
            if (matches)
            {
                changes.Add(change);
            }
        }
        return new(changes, next, hasMore);
    }

    private async ValueTask FlushPendingLockedAsync(CancellationToken cancellationToken)
    {
        await CompleteMaterializationLockedAsync().ConfigureAwait(false);
        if (_pending.Count == 0)
        {
            return;
        }

        DatabaseChangeCursor? visible = _visibleCursor;
        await _durable.AppendBatchAsync(_pending, cancellationToken, publishedThrough: visible).ConfigureAwait(false);
        if (visible is not null)
        {
            try
            {
                await _durable.NotifyPublishedAsync(visible.Value, cancellationToken).ConfigureAwait(false);
            }
            catch (EntityForgeChangeCursorInvalidException)
            { /* Recovery may replay a change below the durable retention floor. */
            }
        }
        _pending.Clear();
        _pendingByVersion.Clear();
        _pendingByCursor.Clear();
        _pendingBytes = 0;
    }

    // Start at half the configured bound; the existing full-bound flush applies backpressure.
    // The worker owns an immutable prefix and never enters _gate. Barrier callers can safely
    // await it while holding _gate, before checkpoint/WAL reclamation or shutdown proceeds.
    private void StartMaterializationLocked(bool force = false)
    {
        if (_materialization is not null || _pending.Count == 0 ||
            (!force && _pending.Count < Math.Max(1, _maximumPendingChanges / 2) &&
             _pendingBytes < Math.Max(1, _maximumPendingBytes / 2)))
        {
            return;
        }

        int count = _pending.TakeWhile(Visible).Count();
        if (count == 0)
        {
            return;
        }

        var batch = _pending.Take(count).ToArray();
        _materializingCount = count;
        _materializingBytes = batch.Sum(EstimateBytes);
        var visible = batch[^1].Cursor;
        _materialization = Task.Run(async () =>
        {
            if (_beforeBackgroundMaterialization is not null)
            {
                await _beforeBackgroundMaterialization().ConfigureAwait(false);
            }

            await _durable.AppendBatchAsync(batch, CancellationToken.None, visible).ConfigureAwait(false);
            await _durable.NotifyPublishedAsync(visible, CancellationToken.None).ConfigureAwait(false);
        });
    }

    private async ValueTask CompleteMaterializationLockedAsync()
    {
        if (_materialization is null)
        {
            return;
        }
        // On failure retain the task and prefix: every subsequent operation observes the fault.
        await _materialization.ConfigureAwait(false);
        for (int index = 0; index < _materializingCount; index++)
        {
            _pendingByVersion.Remove(_pending[index].Version);
            _pendingByCursor.Remove(_pending[index].Cursor);
        }
        _pending.RemoveRange(0, _materializingCount);
        _pendingBytes -= _materializingBytes;
        _materialization = null;
        _materializingCount = 0;
        _materializingBytes = 0;
    }

    private static long EstimateBytes(CommittedChangeSet change) => checked(
        change.Payload.Content.Length +
        change.Payload.Routing.Keys.Sum(key => (long)key.Kind.Length + key.Value.Length) +
        change.Payload.Routing.OverflowedKinds.Sum(kind => (long)kind.Length));

    private int PendingStart(DatabaseChangeCursor? cursor)
    {
        if (cursor is null)
        {
            return -1;
        }

        int index = _pending.FindIndex(item => item.Cursor == cursor.Value);
        return index < 0 ? -1 : index + 1;
    }

    private bool Visible(CommittedChangeSet change) => _visibleVersion is long visible &&
        change.Version.Value <= visible;

    private static bool Matches(CommittedChangeSet change, DatabaseChangeSelector selector)
    {
        if (selector.Profile is not null && selector.Profile != change.Profile)
        {
            return false;
        }

        if (selector.AnyRoutingKeys.Count == 0)
        {
            return true;
        }

        return selector.AnyRoutingKeys.Any(change.Payload.Routing.MayContain);
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private async ValueTask EnterAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_materialization?.IsCompleted == true)
            {
                await CompleteMaterializationLockedAsync().ConfigureAwait(false);
            }
        }
        catch
        {
            _gate.Release();
            throw;
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _closing) != 0 || Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(WalBackedDatabaseCommittedChangeStore));
        }
    }
}

internal enum ChangeReplayClassification
{
    RequiresMaterialization,
    AlreadyMaterialized,
    RetainedAway
}
