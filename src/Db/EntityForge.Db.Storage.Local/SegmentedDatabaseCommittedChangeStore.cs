using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Diagnostics;
using System.Buffers.Binary;
using System.Security.Cryptography;
using EntityForge.Db;
using EntityForge.Db.Storage;

namespace EntityForge.Db.Storage.Local;

/// <summary>Persists committed database changes in a segmented local journal with retention and snapshot support.</summary>
public sealed partial class SegmentedDatabaseCommittedChangeStore : IDatabaseCommittedChangeStore,
    IDatabaseChangeJournalDiagnostics, IDatabaseChangeJournalSnapshotParticipant
{
    private const string ManifestFileName = "manifest.efcm";
    private const string BatchSuffix = ".efcb";
    private const int BatchCodecVersion = 1;
    private readonly DatabaseChangeJournalRetentionOptions _retention;
    private readonly TimeProvider _timeProvider;
    private readonly ChecksummedJournalArtifactStore _artifacts;
    private readonly ChangeJournalRetentionPlanner _retentionPlanner;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ChangeJournalManifest _manifest = ChangeJournalManifest.Empty;
    private bool _loaded;
    private bool _disposed;
    private readonly object _disposeGate = new();
    private Task? _disposeTask;
    private TaskCompletionSource _published = NewSignal();
    private string? _visibleCursor;
    private long _compactedBytes;
    private long _compactedRecords;
    private long _indexedCandidates;
    private long _decodedRecords;
    private long _subscriptionWakeUps;
    private long _expiredCursors;
    private readonly HashSet<string> _deferredArtifactDeletes = new(StringComparer.Ordinal);
    private readonly DatabaseChangeJournalTelemetry _telemetry;
    private readonly bool _ownsTelemetry;
    private readonly Action<string> _deleteArtifact;

    /// <summary>Creates a segmented local change journal with the supplied persistence and retention configuration.</summary>
    public SegmentedDatabaseCommittedChangeStore(string directory,
        DatabaseChangeJournalRetentionOptions? retention = null, TimeProvider? timeProvider = null,
        DatabaseChangeJournalTelemetry? telemetry = null) :
        this(directory, retention, timeProvider, telemetry, null)
    {
    }

    internal SegmentedDatabaseCommittedChangeStore(string directory,
        DatabaseChangeJournalRetentionOptions? retention, TimeProvider? timeProvider,
        DatabaseChangeJournalTelemetry? telemetry, Action<string>? deleteArtifact)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        try
        {
            _retention = retention ?? new();
            _timeProvider = timeProvider ?? TimeProvider.System;
            _artifacts = new(directory);
            _retentionPlanner = new(_retention, _timeProvider);
            _telemetry = telemetry ?? new();
            _ownsTelemetry = telemetry is null;
            _deleteArtifact = deleteArtifact ?? _artifacts.Delete;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new EntityForgeDbStorageException("The segmented committed-change journal could not be opened.", exception);
        }
    }

    /// <inheritdoc />
    public async ValueTask AppendAsync(CommittedChangeSet changeSet,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changeSet);
        await AppendBatchAsync([changeSet], cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask AppendBatchAsync(IReadOnlyList<CommittedChangeSet> changeSets,
        CancellationToken cancellationToken = default, DatabaseChangeCursor? publishedThrough = null)
    {
        ArgumentNullException.ThrowIfNull(changeSets);
        if (changeSets.Count == 0)
        {
            return;
        }

        using Activity? activity = _telemetry.Start("db.change_journal.materialize");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            ChangeJournalManifest next = await AppendNativeBatchAsync(
                _manifest, changeSets, cancellationToken).ConfigureAwait(false);
            bool advancedPublication;
            (next, advancedPublication) = AdvanceRetainedPublication(next, publishedThrough);
            if (ReferenceEquals(next, _manifest))
            {
                return;
            }

            next = next with
            {
                Generation = checked(_manifest.Generation + 1)
            };
            long manifestStarted = Stopwatch.GetTimestamp();
            long manifestAllocated = GC.GetTotalAllocatedBytes();
            await PublishCatalogAppendAsync(next, cancellationToken).ConfigureAwait(false);
            _telemetry.MaterializationPhase("manifest", manifestStarted, manifestAllocated);
            Volatile.Write(ref _manifest, next);
            if (advancedPublication)
            {
                _telemetry.Published();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (EntityForgeDbException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidDataException or JsonException or ArgumentException)
        {
            throw new EntityForgeDbStorageException("The committed change could not be materialized.", exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static (ChangeJournalManifest Manifest, bool Advanced) AdvanceRetainedPublication(
        ChangeJournalManifest next, DatabaseChangeCursor? publishedThrough)
    {
        bool advancedPublication = false;
        // The WAL-backed owner supplies only its already-visible predecessor, never
        // the current unpublished change. Persist that frontier with the new batch.
        if (publishedThrough is { } visible)
        {
            int requestedIndex = FindCursorIndex(next.Segments, visible.Value);
            long? requestedVersion = requestedIndex < 0 ? null : next.Segments[requestedIndex].Version;
            long? publishedVersion = next.PublishedCursor is null ? null :
                next.Segments[FindCursorIndex(next.Segments, next.PublishedCursor)].Version;
            // Recovery can carry a cursor outside the retained catalog. Ignore it rather
            // than inferring a publication identity from history that is no longer available.
            if (requestedVersion is { } requested && (publishedVersion is null || requested > publishedVersion))
            {
                next = next with
                {
                    PublishedCursor = visible.Value
                };
                advancedPublication = true;
            }
        }
        return (next, advancedPublication);
    }

    private async ValueTask<ChangeJournalManifest> AppendNativeBatchAsync(ChangeJournalManifest manifest,
        IReadOnlyList<CommittedChangeSet> changeSets, CancellationToken cancellationToken)
    {
        var eligible = new List<CommittedChangeSet>(changeSets.Count);
        long lastVersion = manifest.Segments.Count == 0 ? long.MinValue : manifest.Segments[^1].Version;
        Guid? databaseIncarnation = manifest.DatabaseIncarnation;
        foreach (CommittedChangeSet change in changeSets)
        {
            ChangeJournalSegmentDescriptor? existing = FindByVersion(
                manifest.Segments, change.Version.Value);
            if (existing is { } retained)
            {
                if (retained.TransactionId == change.TransactionId && retained.Cursor == change.Cursor.Value)
                {
                    continue;
                }

                throw new EntityForgeDbFormatException("Committed-change journal versions must be unique and increasing.");
            }
            if (manifest.RetentionFloorVersion is long floor && change.Version.Value <= floor)
            {
                continue;
            }

            if (change.Version.Value <= lastVersion)
            {
                throw new EntityForgeDbFormatException("Committed-change journal versions must be unique and increasing.");
            }

            if (databaseIncarnation is Guid incarnation && incarnation != change.DatabaseIncarnation)
            {
                throw new EntityForgeDbFormatException("A committed-change journal cannot contain multiple database incarnations.");
            }

            eligible.Add(change);
            lastVersion = change.Version.Value;
            databaseIncarnation = change.DatabaseIncarnation;
        }
        if (eligible.Count == 0)
        {
            return manifest;
        }

        string batchName = $"{eligible[0].Version.Value:D20}-{eligible[^1].Version.Value:D20}" + BatchSuffix;
        long encodeStarted = Stopwatch.GetTimestamp();
        long encodeAllocated = GC.GetTotalAllocatedBytes();
        const int MinimumParallelSections = 128;
        const int MaximumEncodingWorkers = 4;
        var sections = new (CommittedChangeSet Change, byte[] Record, byte[] Index)[eligible.Count];
        void EncodeRange(int start, int end)
        {
            using var encoder = new ChangeJournalSectionEncoder();
            for (int index = start; index < end; index++)
            {
                CommittedChangeSet change = eligible[index];
                sections[index] = (change, encoder.Record(change, cancellationToken),
                    encoder.Index(change.Profile, change.Payload.Routing, cancellationToken));
            }
        }
        int workers = eligible.Count < MinimumParallelSections ? 1 : Math.Min(MaximumEncodingWorkers, Environment.ProcessorCount);
        if (workers == 1)
        {
            EncodeRange(0, eligible.Count);
        }
        else
        {
            // Each worker owns its encoder and disjoint ordered slots. Drain every worker before
            // publication or failure; no artifact can refer to a partial encoding result.
            await Task.WhenAll(Enumerable.Range(0, workers).Select(worker => Task.Run(() =>
                EncodeRange(worker * eligible.Count / workers, (worker + 1) * eligible.Count / workers))))
                .ConfigureAwait(false);
        }
        _telemetry.MaterializationPhase("encode_sections", encodeStarted, encodeAllocated);
        var offsets = new List<(long RecordOffset, int RecordLength, long IndexOffset, int IndexLength)>();
        long artifactStarted = Stopwatch.GetTimestamp();
        long artifactAllocated = GC.GetTotalAllocatedBytes();
        JournalArtifactWriteResult artifact = await _artifacts.WriteStreamingAsync(batchName, false,
            async (stream, hash, token) =>
            {
                using var output = new IncrementalHashWriteStream(stream, hash);
                byte[] header = new byte[sizeof(int) * 2];
                BinaryPrimitives.WriteInt32LittleEndian(header, BatchCodecVersion);
                BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(sizeof(int)), sections.Length);
                await output.WriteAsync(header, token).ConfigureAwait(false);
                foreach ((_, byte[] record, byte[] index) in sections)
                {
                    byte[] lengths = new byte[sizeof(int) * 2];
                    BinaryPrimitives.WriteInt32LittleEndian(lengths, record.Length);
                    BinaryPrimitives.WriteInt32LittleEndian(lengths.AsSpan(sizeof(int)), index.Length);
                    await output.WriteAsync(lengths, token).ConfigureAwait(false);
                    long recordOffset = stream.Position;
                    await output.WriteAsync(record, token).ConfigureAwait(false);
                    long indexOffset = stream.Position;
                    await output.WriteAsync(index, token).ConfigureAwait(false);
                    offsets.Add((recordOffset, record.Length, indexOffset, index.Length));
                }
            }, cancellationToken).ConfigureAwait(false);
        _telemetry.MaterializationPhase("combined", artifactStarted, artifactAllocated);
        var addedSegments = new ChangeJournalSegmentDescriptor[sections.Length];
        for (int index = 0; index < sections.Length; index++)
        {
            CommittedChangeSet change = sections[index].Change;
            var position = offsets[index];
            string id = $"{change.Version.Value:D20}-{change.TransactionId:N}";
            var descriptor = new ChangeJournalSegmentDescriptor(id, batchName, batchName,
                change.Version.Value, change.TransactionId, change.Cursor.Value, _timeProvider.GetUtcNow(),
                artifact.Bytes / sections.Length, artifact.Checksum, artifact.Checksum,
                position.RecordOffset, position.RecordLength, position.IndexOffset, position.IndexLength);
            addedSegments[index] = descriptor;
            _telemetry.Materialized();
        }
        // Persistent nodes preserve published snapshots without copying the full retained history.
        var segments = JournalSegmentList.From(manifest.Segments);
        return manifest with
        {
            DatabaseIncarnation = databaseIncarnation,
            Segments = segments.AddRange(addedSegments)
        };
    }

    private static ChangeJournalSegmentDescriptor? FindByVersion(
        IReadOnlyList<ChangeJournalSegmentDescriptor> segments, long version)
    {
        if (segments.Count == 0 || version > segments[^1].Version || version < segments[0].Version)
        {
            return null;
        }

        int lower = 0;
        int upper = segments.Count - 1;
        while (lower <= upper)
        {
            int middle = lower + ((upper - lower) / 2);
            long candidate = segments[middle].Version;
            if (candidate == version)
            {
                return segments[middle];
            }

            if (candidate < version)
            {
                lower = middle + 1;
            }
            else
            {
                upper = middle - 1;
            }
        }
        return null;
    }

    /// <inheritdoc />
    public async ValueTask PublishThroughAsync(DatabaseChangeCursor cursor,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        using Activity? activity = _telemetry.Start("db.change_journal.publish");
        try
        {
            ThrowIfDisposed();
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            int index = FindCursor(cursor);
            int currentIndex = _manifest.PublishedCursor is null ? -1 : FindValue(_manifest.PublishedCursor);
            if (index <= currentIndex)
            {
                return;
            }

            ChangeJournalManifest next = _manifest with
            {
                Generation = checked(_manifest.Generation + 1),
                PublishedCursor = cursor.Value
            };
            long started = Stopwatch.GetTimestamp();
            long allocated = GC.GetTotalAllocatedBytes();
            await PublishFrontierAsync(next, cancellationToken).ConfigureAwait(false);
            _telemetry.MaterializationPhase("publication_manifest", started, allocated);
            Volatile.Write(ref _manifest, next);
            _telemetry.Published();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (EntityForgeDbException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidDataException or JsonException or ArgumentException)
        {
            throw new EntityForgeDbStorageException("The committed-change durable publication frontier could not advance.", exception);
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
        _ = await NotifyPublishedAndGetFrontierAsync(cursor, cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask<(DatabaseChangeCursor Cursor, long Version)> NotifyPublishedAndGetFrontierAsync(
        DatabaseChangeCursor cursor,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            int requested = FindCursor(cursor);
            int durable = _manifest.PublishedCursor is null ? -1 : FindValue(_manifest.PublishedCursor);
            if (requested > durable)
            {
                throw new EntityForgeDbFormatException(
                    "A change cannot become visible before its durable journal publication.");
            }

            int visible = _visibleCursor is null ? -1 : FindValue(_visibleCursor);
            if (requested <= visible)
            {
                ChangeJournalSegmentDescriptor current = _manifest.Segments[visible];
                return (new(current.Cursor), current.Version);
            }
            RetryDeferredDeletes();
            long retentionStarted = Stopwatch.GetTimestamp();
            long retentionAllocated = GC.GetTotalAllocatedBytes();
            ChangeJournalRetentionResult retention = _retentionPlanner.Plan(
                _manifest, cursor.Value);
            _telemetry.MaterializationPhase("retention_plan", retentionStarted, retentionAllocated);
            ChangeJournalManifest next = retention.Manifest;
            IReadOnlyList<ChangeJournalSegmentDescriptor> removed = retention.Removed;
            if (!ReferenceEquals(next, _manifest))
            {
                await PublishManifestAsync(next, cancellationToken).ConfigureAwait(false);
                Volatile.Write(ref _manifest, next);
            }
            _visibleCursor = cursor.Value;
            TaskCompletionSource signal = _published;
            _published = NewSignal();
            signal.TrySetResult();
            foreach (ChangeJournalSegmentDescriptor segment in removed)
            {
                if (!next.Segments.Any(item => item.SegmentFile == segment.SegmentFile ||
                                               item.IndexFile == segment.SegmentFile))
                {
                    DeleteOrDefer(segment.SegmentFile);
                }

                if (segment.IndexFile != segment.SegmentFile &&
                    !next.Segments.Any(item => item.SegmentFile == segment.IndexFile ||
                                               item.IndexFile == segment.IndexFile))
                {
                    DeleteOrDefer(segment.IndexFile);
                }

                _compactedBytes += ChangeJournalStorageFootprint.Measure(segment);
                _compactedRecords++;
            }
            if (removed.Count != 0)
            {
                _telemetry.Compacted(removed.Sum(ChangeJournalStorageFootprint.Measure), removed.Count);
            }

            ChangeJournalSegmentDescriptor effective = _manifest.Segments[FindValue(_visibleCursor!)];
            return (new(effective.Cursor), effective.Version);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (EntityForgeDbException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidDataException or JsonException or ArgumentException)
        {
            throw new EntityForgeDbStorageException("The committed-change visible publication frontier could not advance.", exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async ValueTask ValidateRecoverySetAsync(EntityForge.Db.Durability.DurableCheckpointEnvelope checkpoint,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            if (_manifest.PublishedCursor != checkpoint.MaterializedChangeCursor?.Value ||
                (_manifest.Segments.Count == 0 ? null : _manifest.Segments[^1].Cursor) != _manifest.PublishedCursor ||
                (_manifest.PublishedCursor is not null && _manifest.DatabaseIncarnation != checkpoint.DatabaseIncarnation))
            {
                throw new InvalidDataException("The journal does not cover the checkpoint boundary.");
            }

            long previousVersion = _manifest.RetentionFloorVersion ?? 0;
            var transactions = checkpoint.CommittedTransactions.ToDictionary(item => item.Version.Value);
            var required = transactions.Values
                .Where(item => item.ChangeKind == EntityForge.Db.Durability.DurableCommittedChangeKind.UserDataWithChanges &&
                    item.Version.Value > previousVersion).Select(item => item.Version.Value).ToHashSet();
            foreach (var segment in _manifest.Segments)
            {
                // Decode both referenced sections, including bounds/checksums, before restore publication.
                ChangeJournalRoutingIndex index = await ReadIndexAsync(segment, cancellationToken).ConfigureAwait(false);
                var change = await ReadSegmentAsync(segment, cancellationToken).ConfigureAwait(false);
                if (index.Profile != change.Profile ||
                    !index.RoutingKeys.Select(key => (key.Kind, key.Value)).ToHashSet()
                        .SetEquals(change.Payload.Routing.Keys.Select(key => (key.Kind, key.Value))) ||
                    !index.OverflowedKinds.ToHashSet(StringComparer.Ordinal)
                        .SetEquals(change.Payload.Routing.OverflowedKinds))
                {
                    throw new InvalidDataException("Journal routing index differs from its change record.");
                }

                if (transactions.TryGetValue(segment.Version, out var transaction) &&
                    (transaction.ChangeKind != EntityForge.Db.Durability.DurableCommittedChangeKind.UserDataWithChanges ||
                     transaction.TransactionId != change.TransactionId || transaction.EffectCount != change.Payload.EffectCount))
                {
                    throw new InvalidDataException("Journal change differs from checkpoint transaction evidence.");
                }

                required.Remove(segment.Version);
                if (change.DatabaseIncarnation != checkpoint.DatabaseIncarnation ||
                    change.Profile != checkpoint.ProfileState.Profile || change.Version > checkpoint.Version ||
                    change.Cursor.Value != segment.Cursor || change.Version.Value != segment.Version ||
                    change.TransactionId != segment.TransactionId || segment.Version <= previousVersion)
                {
                    throw new InvalidDataException("Journal evidence differs from the checkpoint.");
                }

                previousVersion = segment.Version;
            }
            if (required.Count != 0)
            {
                throw new InvalidDataException("The journal omits retained checkpoint changes.");
            }
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
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            // Bounds need two cursors, not a copy of every retained descriptor.
            if (_visibleCursor is not null)
            {
                _ = FindValue(_visibleCursor);
            }

            return new(_visibleCursor is null ? null : new(_manifest.Segments[0].Cursor),
                _visibleCursor is null ? null : new(_visibleCursor));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (EntityForgeDbException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new EntityForgeDbStorageException("The committed-change bounds could not be read.", exception);
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

        long started = Stopwatch.GetTimestamp();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        int scannedRecords = 0;
        int returnedRecords = 0;
        try
        {
            ThrowIfDisposed();
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            IReadOnlyList<ChangeJournalSegmentDescriptor> published = PublishedSegments();
            int start = StartIndex(published, cursor);
            var changes = new List<CommittedChangeSet>(maximumCount);
            DatabaseChangeCursor? next = cursor;
            bool hasMore = false;
            for (int index = start; index < published.Count; index++)
            {
                scannedRecords++;
                ChangeJournalSegmentDescriptor segment = published[index];
                bool matches = await MatchesAsync(segment, selector, cancellationToken).ConfigureAwait(false);
                if (matches && changes.Count == maximumCount)
                {
                    hasMore = true;
                    break;
                }
                next = new(segment.Cursor);
                if (!matches)
                {
                    continue;
                }

                changes.Add(await ReadSegmentAsync(segment, cancellationToken).ConfigureAwait(false));
                returnedRecords++;
                _decodedRecords++;
                _telemetry.Decoded();
            }
            return new(changes, next, hasMore);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (EntityForgeDbException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidDataException or JsonException or ArgumentException)
        {
            throw new EntityForgeDbStorageException("The committed-change page could not be read.", exception);
        }
        finally
        {
            _telemetry.PageRead(Stopwatch.GetElapsedTime(started), scannedRecords, returnedRecords);
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
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
                if (position?.Value != _visibleCursor)
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
            _subscriptionWakeUps++;
            _telemetry.SubscriptionWakeUp();
        }
    }

    /// <inheritdoc />
    public async ValueTask<DatabaseChangeJournalStatus> GetStatusAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            int published = _visibleCursor is null ? 0 : FindValue(_visibleCursor) + 1;
            long retainedBytes = ChangeJournalStorageFootprint.Measure(_manifest);
            return new(retainedBytes, Math.Max(0, retainedBytes - _retention.MaximumRetainedBytes),
                _manifest.Segments.Count,
                _manifest.RetentionFloor is null ? null : new(_manifest.RetentionFloor),
                _manifest.Segments.Count == 0 ? null : new(_manifest.Segments[^1].Cursor),
                _visibleCursor is null ? null : new(_visibleCursor),
                _manifest.Segments.Count - published, _compactedBytes, _compactedRecords,
                _indexedCandidates, _decodedRecords, _subscriptionWakeUps, _expiredCursors, _manifest.RetentionFloorVersion);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (EntityForgeDbException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new EntityForgeDbStorageException("The committed-change diagnostics could not be read.", exception);
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
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            if (cursor is null)
            {
                return new(Array.Empty<DatabaseChangeJournalSnapshotArtifact>());
            }

            int through = FindCursor(cursor.Value);
            IReadOnlyList<ChangeJournalSegmentDescriptor> segments = SegmentPrefix(through + 1);
            ChangeJournalManifest snapshot = _manifest with
            {
                Generation = 1,
                PublishedCursor = cursor.Value.Value,
                Segments = segments
            };
            var artifacts = new List<DatabaseChangeJournalSnapshotArtifact>
            {
                new(ManifestFileName,
                    ChecksummedJournalArtifactStore.Encode(SerializeManifest(snapshot)))
            };
            foreach (string name in segments.SelectMany(segment =>
                         new[] { segment.SegmentFile, segment.IndexFile }).Distinct(StringComparer.Ordinal))
            {
                artifacts.Add(new(name,
                    await _artifacts.ReadRawAsync(name, cancellationToken).ConfigureAwait(false)));
            }
            return new(artifacts);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (EntityForgeDbException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new EntityForgeDbStorageException("The committed-change snapshot could not be captured.", exception);
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
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            Directory.CreateDirectory(destinationDirectory);
            int through = cursor is null ? -1 : FindCursor(cursor.Value);
            IReadOnlyList<ChangeJournalSegmentDescriptor> segments = SegmentPrefix(through + 1);
            // Even a checkpoint without a change cursor restores an initialized journal.
            // Do not copy later publication/retention metadata into that empty snapshot.
            ChangeJournalManifest snapshot = cursor is null ? ChangeJournalManifest.Empty : _manifest with
            {
                Generation = 1,
                PublishedCursor = cursor.Value.Value,
                Segments = segments
            };
            var destination = new ChecksummedJournalArtifactStore(destinationDirectory);
            await destination.WriteStreamingAsync(ManifestFileName, false, async (stream, hash, token) =>
            {
                using var output = new IncrementalHashWriteStream(stream, hash);
                await ChangeJournalManifestCodec.WriteAsync(output, snapshot, token).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
            // Many descriptors share a packed artifact. Do not reserve two entries per record.
            var artifacts = new List<DatabaseChangeJournalFileArtifact>()
            {
                FileArtifact(destinationDirectory, ManifestFileName)
            };
            foreach (string name in segments.SelectMany(segment =>
                         new[] { segment.SegmentFile, segment.IndexFile }).Distinct(StringComparer.Ordinal))
            {
                artifacts.Add(await CopyFileArtifactAsync(name, destinationDirectory,
                    cancellationToken).ConfigureAwait(false));
            }
            return artifacts;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (EntityForgeDbException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new EntityForgeDbStorageException("The committed-change file snapshot could not be captured.", exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<DatabaseChangeJournalFileArtifact> CopyFileArtifactAsync(string name,
        string destinationDirectory, CancellationToken cancellationToken)
    {
        string path = Path.Combine(destinationDirectory, name);
        long length = await _artifacts.CopyRawToAsync(name, path, cancellationToken).ConfigureAwait(false);
        return new(name, path, length);
    }

    private static DatabaseChangeJournalFileArtifact FileArtifact(string directory, string name)
    {
        string path = Path.Combine(directory, name);
        return new(name, path, new FileInfo(path).Length);
    }

    /// <summary>Stops journal activity and releases owned persistence resources.</summary>
    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            return new(_disposeTask ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        _disposed = true;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _published.TrySetException(new ObjectDisposedException(nameof(SegmentedDatabaseCommittedChangeStore)));
            RetryDeferredDeletes();
            if (_ownsTelemetry)
            {
                _telemetry.Dispose();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_loaded)
        {
            return;
        }

        _catalogDeltas.Clear();
        var admittedArtifacts = new HashSet<string>(StringComparer.Ordinal);
        if (!_artifacts.Exists(ManifestFileName))
        {
            if (_artifacts.Exists(PublicationFileName) || _artifacts.Exists(CatalogHeadFileName))
            {
                throw new EntityForgeDbFormatException("A journal publication requires its catalog manifest.");
            }

            await PublishManifestAsync(ChangeJournalManifest.Empty, cancellationToken).ConfigureAwait(false);
            _manifest = ChangeJournalManifest.Empty;
        }
        else
        {
            byte[] payload = await _artifacts.ReadAsync(ManifestFileName, cancellationToken)
                .ConfigureAwait(false);
            _manifest = ChangeJournalManifestCodec.Read(payload);
            if (_manifest.CodecVersion != ChangeJournalManifest.CurrentCodecVersion)
            {
                throw new EntityForgeDbFormatException("The change-journal manifest codec is unsupported.");
            }

            foreach (ChangeJournalSegmentDescriptor segment in _manifest.Segments)
            {
                EnsureRecoveryArtifact(segment.SegmentFile, admittedArtifacts);
                EnsureRecoveryArtifact(segment.IndexFile, admittedArtifacts);
            }
        }
        await RestoreCatalogDeltasAsync(admittedArtifacts, cancellationToken).ConfigureAwait(false);
        _catalogGeneration = _manifest.Generation;
        await RestoreFrontierAsync(cancellationToken).ConfigureAwait(false);
        // Finish an uncertain root replacement before deleting superseded catalogs or segments.
        _artifacts.SyncDirectory();
        foreach (string name in _artifacts.EnumerateNames("catalog-*.efcd"))
        {
            if (!_catalogDeltas.Contains(name, StringComparer.Ordinal))
            {
                DeleteOrDefer(name);
            }
        }

        _artifacts.DeleteTemporaryArtifacts();
        Volatile.Write(ref _loaded, true);
    }

    private IReadOnlyList<ChangeJournalSegmentDescriptor> PublishedSegments()
    {
        if (_visibleCursor is null)
        {
            return Array.Empty<ChangeJournalSegmentDescriptor>();
        }

        int last = FindValue(_visibleCursor);
        return SegmentPrefix(last + 1);
    }

    private IReadOnlyList<ChangeJournalSegmentDescriptor> SegmentPrefix(int count) =>
        count == _manifest.Segments.Count ? _manifest.Segments : new JournalSegmentPrefix(_manifest.Segments, count);

    // Catalogs are immutable after publication. A bounded view retains the selected
    // catalog without copying it or exposing later unpublished records.
    private sealed class JournalSegmentPrefix(IReadOnlyList<ChangeJournalSegmentDescriptor> source, int count)
        : IReadOnlyList<ChangeJournalSegmentDescriptor>
    {
        public int Count => count;

        public ChangeJournalSegmentDescriptor this[int index] => (uint)index < (uint)count
            ? source[index] : throw new ArgumentOutOfRangeException(nameof(index));

        public IEnumerator<ChangeJournalSegmentDescriptor> GetEnumerator() => source.Take(count).GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private int StartIndex(IReadOnlyList<ChangeJournalSegmentDescriptor> segments,
        DatabaseChangeCursor? cursor)
    {
        if (cursor is null)
        {
            return 0;
        }

        for (int index = 0; index < segments.Count; index++)
        {
            if (segments[index].Cursor == cursor.Value.Value)
            {
                return index + 1;
            }
        }

        if (_manifest.RetentionFloor == cursor.Value.Value)
        {
            return 0;
        }

        if (_manifest.RetentionFloor is not null)
        {
            _expiredCursors++;
            _telemetry.Expired();
            throw new EntityForgeChangeCursorExpiredException(cursor.Value,
                segments.Count == 0 ? null : new(segments[0].Cursor));
        }
        throw new EntityForgeChangeCursorInvalidException("The change cursor is not present in this journal.");
    }

    private int FindCursor(DatabaseChangeCursor cursor) => FindValue(cursor.Value);

    internal async ValueTask<ChangeReplayClassification> ClassifyReplayAsync(
        CommittedChangeSet change, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _loaded))
        {
            ThrowIfDisposed();
            return ClassifyReplay(Volatile.Read(ref _manifest), change);
        }
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            return ClassifyReplay(_manifest, change);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static ChangeReplayClassification ClassifyReplay(ChangeJournalManifest manifest, CommittedChangeSet change)
    {
        ChangeJournalSegmentDescriptor? existing = FindByVersion(
            manifest.Segments, change.Version.Value);
        if (existing is { } retained)
        {
            if (retained.TransactionId != change.TransactionId || retained.Cursor != change.Cursor.Value)
            {
                throw new EntityForgeDbFormatException(
                    "A recovered change conflicts with durable committed-change metadata.");
            }

            return ChangeReplayClassification.AlreadyMaterialized;
        }
        return manifest.RetentionFloorVersion is long floor && change.Version.Value <= floor
            ? ChangeReplayClassification.RetainedAway
            : ChangeReplayClassification.RequiresMaterialization;
    }

    internal async ValueTask<long> GetVersionAsync(DatabaseChangeCursor cursor,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            return _manifest.Segments[FindCursor(cursor)].Version;
        }
        finally
        {
            _gate.Release();
        }
    }

    private int FindValue(string cursor)
    {
        int index = FindCursorIndex(_manifest.Segments, cursor);
        if (index >= 0)
        {
            return index;
        }

        throw new EntityForgeChangeCursorInvalidException("The change cursor is not present in this journal.");
    }

    private static int FindCursorIndex(IReadOnlyList<ChangeJournalSegmentDescriptor> segments, string cursor)
    {
        // Publication normally addresses the newest batch. Preserve opaque cursor semantics.
        for (int index = segments.Count - 1; index >= 0; index--)
        {
            if (segments[index].Cursor == cursor)
            {
                return index;
            }
        }

        return -1;
    }

    private async ValueTask<bool> MatchesAsync(ChangeJournalSegmentDescriptor segment,
        DatabaseChangeSelector selector,
        CancellationToken cancellationToken)
    {
        if (selector.Profile is null && selector.AnyRoutingKeys.Count == 0)
        {
            return true;
        }

        ChangeJournalRoutingIndex index = await ReadIndexAsync(segment, cancellationToken).ConfigureAwait(false);
        if (selector.Profile is not null && selector.Profile != index.Profile)
        {
            return false;
        }

        if (selector.AnyRoutingKeys.Count == 0)
        {
            return true;
        }

        _indexedCandidates++;
        _telemetry.IndexedCandidate();
        return selector.AnyRoutingKeys.Any(requested =>
            index.RoutingKeys.Any(actual => actual.Kind == requested.Kind && actual.Value == requested.Value) ||
            index.OverflowedKinds.Contains(requested.Kind, StringComparer.Ordinal));
    }

    private async ValueTask<ChangeJournalRoutingIndex> ReadIndexAsync(ChangeJournalSegmentDescriptor segment,
        CancellationToken cancellationToken)
    {
        long verifiedLength = await _artifacts.VerifyRawAsync(segment.IndexFile, cancellationToken).ConfigureAwait(false);
        await using Stream stream = await OpenSectionAsync(segment.IndexFile, segment.IndexOffset,
            segment.IndexLength, verifiedLength, cancellationToken).ConfigureAwait(false);
        return await ChangeJournalBinaryCodec.ReadIndexAsync(stream, segment.IndexLength ?? verifiedLength,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<CommittedChangeSet> ReadSegmentAsync(ChangeJournalSegmentDescriptor segment,
        CancellationToken cancellationToken)
    {
        long verifiedLength = await _artifacts.VerifyRawAsync(segment.SegmentFile, cancellationToken)
            .ConfigureAwait(false);
        long payloadLength = segment.RecordLength ?? verifiedLength;
        await using Stream stream = await OpenSectionAsync(segment.SegmentFile, segment.RecordOffset,
            segment.RecordLength, verifiedLength, cancellationToken).ConfigureAwait(false);
        return await ChangeJournalBinaryCodec.ReadChangeSetAsync(stream, payloadLength,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<Stream> OpenSectionAsync(string name, long? offset, int? length,
        long verifiedLength, CancellationToken cancellationToken)
    {
        ValidateSection(offset, length, verifiedLength);
        if (offset is null)
        {
            return _artifacts.OpenRawRead(name);
        }

        await using FileStream source = _artifacts.OpenRawRead(name);
        source.Position = offset.Value;
        byte[] content = new byte[length!.Value];
        await source.ReadExactlyAsync(content, cancellationToken).ConfigureAwait(false);
        return new MemoryStream(content, writable: false);
    }

    internal static void ValidateSection(long? offset, int? length, long verifiedLength)
    {
        if (offset.HasValue != length.HasValue || verifiedLength < 0 ||
            (offset is { } start && (start < 0 || start > verifiedLength ||
                length!.Value < 0 || length.Value > verifiedLength - start)))
        {
            throw new EntityForgeDbFormatException("A journal section is outside its verified artifact.");
        }
    }

    private async ValueTask PublishManifestAsync(ChangeJournalManifest manifest,
        CancellationToken cancellationToken)
    {
        try
        {
            await _artifacts.WriteStreamingAsync(ManifestFileName, true, async (stream, hash, token) =>
            {
                await using var hashingStream = new IncrementalHashWriteStream(stream, hash);
                await ChangeJournalManifestCodec.WriteAsync(hashingStream, manifest, token).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
            _catalogGeneration = manifest.Generation;
            foreach (string delta in _catalogDeltas)
            {
                DeleteOrDefer(delta);
            }

            _catalogDeltas.Clear();
        }
        catch (Exception exception)
        {
            // Publication may already be durable. Never reuse an old in-memory generation.
            _publicationPersistenceFailure = exception;
            throw;
        }
    }

    private void DeleteOrDefer(string name)
    {
        try
        {
            _deleteArtifact(name);
            _deferredArtifactDeletes.Remove(name);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _deferredArtifactDeletes.Add(name);
        }
    }

    private void RetryDeferredDeletes()
    {
        foreach (string name in _deferredArtifactDeletes.ToArray())
        {
            DeleteOrDefer(name);
        }
    }

    private static byte[] SerializeManifest(ChangeJournalManifest manifest)
    {
        return ChangeJournalManifestCodec.Write(manifest);
    }

    private static T Deserialize<T>(ReadOnlySpan<byte> payload, string artifact)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(payload) ??
                throw new EntityForgeDbFormatException($"The {artifact} is empty.");
        }
        catch (JsonException exception)
        {
            throw new EntityForgeDbFormatException($"The {artifact} payload is invalid.", exception);
        }
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(SegmentedDatabaseCommittedChangeStore));
        }

        if (Volatile.Read(ref _publicationPersistenceFailure) is { } failure)
        {
            throw new EntityForgeDbStorageException(
                "The change journal requires reopening after a publication persistence failure.", failure);
        }
    }

}

internal sealed record DatabaseChangeJournalFileArtifact(string Name, string Path, long Length);
