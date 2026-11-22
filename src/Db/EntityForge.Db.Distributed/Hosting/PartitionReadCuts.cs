using System.Text.Json;
using System.Text.Json.Serialization;
using EntityForge.Db.Kernel;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Distributed.Hosting;

internal sealed record PartitionReadCut(Guid Id, DatabasePartitionPosition Position, DateTimeOffset Expires, bool Maintenance = false, string? SnapshotEvidence = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Scope = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? ClockDomain = null, bool HistoryProtection = false)
{
    // Evidence is acquired once; subsequent calls identify the same retained owner entry.
    internal PartitionReadCut Reference => SnapshotEvidence is null ? this : this with { SnapshotEvidence = null };
}

// Leaf queries also occur in persisted planning facts. Read-only extensions must
// not change their ordinary encoding when those extensions are absent.
internal sealed record PartitionReadQuery(string Kind, Guid Identity = default, string? Subject = null, string? Label = null, PartitionReadBatch? Batch = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PartitionReadQuery? Projection = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PartitionGraphSource? Source = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid[]? SnapshotPrepared = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] EntityForge.Db.Durability.DurableTimestampState? ReadTime = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PartitionQueryExchangeRequest? QueryExchange = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PartitionQueryJobCommand? QueryJob = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PartitionServingRequest? Serving = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PartitionRootIndexRequest? RootIndex = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] byte[]? Captured = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] byte[]? PlanningDependencies = null);

internal sealed record PartitionMaintenanceCommand(string Action, Guid Operation = default, long Floor = 0);

internal interface IPartitionReadParticipant
{
    PartitionExecutionDomain Domain
    {
        get;
    }

    DatabaseSharedTimestampService? SharedTimestamps => null;

    ValueTask<PartitionReadCut> AcquireTimestampCutAsync(Guid id, DatabaseTimestampRead read, CancellationToken token = default) =>
        ValueTask.FromException<PartitionReadCut>(new NotSupportedException("Shared timestamps unavailable."));

    ValueTask<PartitionReadCut> AcquireHistoryProtectionAsync(Guid id, DateTimeOffset expires, CancellationToken token = default) =>
        ValueTask.FromException<PartitionReadCut>(new NotSupportedException("Timestamp history protection is unavailable."));

    ValueTask<PartitionReadCut> AcquireCutAsync(Guid id, DateTimeOffset expires, CancellationToken token = default);

    ValueTask<PartitionReadCut> AcquireSnapshotCutAsync(Guid id, DateTimeOffset expires, CancellationToken token = default) =>
        ValueTask.FromException<PartitionReadCut>(new NotSupportedException("MVCC cuts are not supported by this participant."));

    ValueTask<PartitionReadCut> AcquireScopedSnapshotCutAsync(Guid id, DateTimeOffset expires, PartitionReadQuery[] queries, CancellationToken token = default) =>
        ValueTask.FromException<PartitionReadCut>(new NotSupportedException("Scoped MVCC cuts are not supported by this participant."));

    ValueTask<byte[]> ReadCutAsync(PartitionReadCut cut, PartitionReadQuery query, CancellationToken token = default);

    async ValueTask<PartitionQueryJobResult> ReadQueryJobValueAsync(PartitionReadCut cut, PartitionReadQuery query, CancellationToken token = default) =>
        (await ReadCutValueAsync(cut, query, token)).Deserialize<PartitionQueryJobResult>(PartitionParticipantWire.Json)
        ?? throw new InvalidDataException("Missing query job response.");

    // Owned JsonElement values survive transport buffers and disposable documents.
    async ValueTask<JsonElement> ReadCutValueAsync(PartitionReadCut cut, PartitionReadQuery query, CancellationToken token = default) =>
        JsonSerializer.Deserialize<JsonElement>(await ReadCutAsync(cut, query, token));

    async ValueTask<JsonElement> ReadAndCompleteCutValueAsync(PartitionReadCut cut, PartitionReadQuery query, CancellationToken token = default) =>
        JsonSerializer.Deserialize<JsonElement>(await ReadAndCompleteCutAsync(cut, query, token));

    async ValueTask<byte[]> ReadAndCompleteCutAsync(PartitionReadCut cut, PartitionReadQuery query, CancellationToken token = default)
    {
        byte[] value = await ReadCutAsync(cut, query, token);
        await CompleteCutAsync(cut, token);
        return value;
    }

    ValueTask CompleteCutAsync(PartitionReadCut cut, CancellationToken token = default);

    ValueTask AbandonCutAsync(Guid id, CancellationToken token = default);
}

// A cut holds native write exclusion and placement admission. Expiry cancels
// work and drains active reads before releasing either resource. Completed entries
// retain their exact outcome briefly so a lost completion reply is retryable.
internal sealed partial class PartitionReadCuts(PartitionOwnedNode node) : IAsyncDisposable
{
    internal static readonly TimeSpan MaximumDuration = TimeSpan.FromSeconds(30);
    // Leave room for clock skew/correction between dispatch and admission;
    // the receiver continues to enforce the full hard lifetime bound.
    internal static readonly TimeSpan HistoryAcquisitionDuration = MaximumDuration - TimeSpan.FromSeconds(1);
    private const int MaximumEntries = 128;
    private readonly object _sync = new();
    private readonly Dictionary<Guid, Entry> _entries = [];

    // Diagnostic only: completed replay bookkeeping does not retain a native cut.
    internal int UnreleasedCount
    {
        get
        {
            lock (_sync)
            {
                return _entries.Values.Count(entry => !entry.Completed.IsCompleted || !entry.ResourcesReleased);
            }
        }
    }

    private bool _closing;

    internal async ValueTask<PartitionReadCut> AcquireAsync(Guid id, DateTimeOffset expires, CancellationToken token, bool maintenance = false, bool snapshot = false, DatabaseTimestampRead? timestamp = null, PartitionScopedRead? scope = null, bool historyProtection = false)
    {
        if (historyProtection && (scope is not null || snapshot || maintenance || timestamp is not null))
        {
            throw new ArgumentException("Logical history protection has an independent purpose.");
        }

        if (scope is not null && (!snapshot || maintenance || timestamp is not null))
        {
            throw new ArgumentException("Scoped pins require ordinary MVCC admission.");
        }

        var now = DateTimeOffset.UtcNow;
        if (id == Guid.Empty || expires <= now || expires - now > MaximumDuration)
        {
            throw new ArgumentException($"An unexpired bounded read-cut identity is required: id={id}, expires={expires:O}, now={now:O}, maximum={MaximumDuration}.");
        }

        Entry entry;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            foreach (var prior in _entries.Values.Where(value => value.Completed.IsCompleted && value.ResourcesReleased && value.Expires + MaximumDuration < now).ToArray())
            {
                _entries.Remove(prior.Id);
            }

            if (_entries.TryGetValue(id, out entry!))
            {
                if (entry.Expires != expires || entry.Scope?.Native.Fingerprint != scope?.Native.Fingerprint || entry.IsMaintenance != maintenance || entry.IsSnapshot != snapshot || entry.IsHistoryProtection != historyProtection || entry.Timestamp?.Id != timestamp?.Id ||
                    (entry.Timestamp is { } previous && (timestamp is null || previous.Timestamp != timestamp.Timestamp ||
                        previous.Expires != timestamp.Expires || previous.Positions.Count != timestamp.Positions.Count ||
                        previous.Positions.Any(p => !timestamp.Positions.TryGetValue(p.Key, out var v) || p.Value != v))))
                {
                    throw new InvalidDataException("Lease identity was reused with another deadline or purpose.");
                }
            }
            else
            {
                // Completed retry evidence must not consume the entire active
                // admission budget. Eviction can make an old completion retry
                // fail closed; it cannot turn an unfinished lease into success.
                if (_entries.Count >= MaximumEntries)
                {
                    foreach (var prior in _entries.Values.Where(value => value.Completed.IsCompleted && value.ResourcesReleased)
                        .OrderBy(value => value.Expires).Take(_entries.Count - MaximumEntries + 1).ToArray())
                    {
                        _entries.Remove(prior.Id);
                    }
                }

                if (_entries.Count >= MaximumEntries)
                {
                    throw new InvalidOperationException("Read-cut capacity is exhausted.");
                }

                entry = new(node, id, expires, maintenance, snapshot, timestamp, scope, historyProtection);
                _entries.Add(id, entry);
            }
        }
        return await entry.Ready.WaitAsync(token).ConfigureAwait(false);
    }

    private Entry Require(Guid id)
    {
        lock (_sync)
        {
            return _entries.TryGetValue(id, out var entry) ? entry : throw new InvalidOperationException("The read cut is absent or expired.");
        }
    }

    internal async ValueTask<PartitionProjectionValue> ReadAsync(PartitionReadCut cut, PartitionReadQuery query, CancellationToken token)
    {
        var entry = Require(cut.Id);
        if (query.Kind != "settle-snapshot")
        {
            return await entry.ReadAsync(cut, query, token);
        }

        entry.Require(cut);
        // Retry cleanup may wait for a finite set captured by this pin, but owns
        // its release on every outcome, including cancellation and recovery failure.
        try
        {
            return await entry.ReadAsync(cut, query, token);
        }
        finally
        {
            await AbandonAsync(cut.Id, CancellationToken.None);
        }
    }

    internal async ValueTask<PartitionProjectionValue> ReadAndCompleteAsync(PartitionReadCut cut, PartitionReadQuery query, CancellationToken token)
    {
        var entry = Require(cut.Id);
        entry.Require(cut); // A malformed association must not release another cut.
        try
        {
            if (cut.Maintenance || !IsFinalProjection(query))
            {
                throw new InvalidDataException("Final projection requires a bounded Graph/RDF leaf batch.");
            }

            var value = await entry.ReadAsync(cut, query, token);
            entry.Release();
            // Completion includes native pin disposal and the authority's final
            // placement check. Never expose the projection before both finish.
            await entry.Completed.WaitAsync(token).ConfigureAwait(false);
            return value;
        }
        catch (Exception failure)
        {
            try
            {
                await AbandonAsync(cut.Id, CancellationToken.None);
            }
            catch (Exception cleanup)
            {
                throw new AggregateException("Final projection and cleanup failed.", failure, cleanup);
            }
            throw;
        }
    }

    private bool IsFinalProjection(PartitionReadQuery query)
    {
        string? kind = node.Domain.Profile == DatabaseProfileId.PropertyGraph ? "vertex"
            : node.Domain.Profile == DatabaseProfileId.Rdf ? "subject" : null;
        return kind is not null && query is
        {
            Kind: "batch", Identity: var id, Subject: null, Label: null,
            Source: null, Projection: null, SnapshotPrepared: null, Batch:
            {
                Manifests.Length: 0,
                Queries.Length: > 0 and <= PartitionReadBatch.MaximumQueries
            } batch
        } && id == Guid.Empty &&
            batch.Queries.All(item => item is not null && item.Kind == kind);
    }

    internal ValueTask<byte[]> MaintainAsync(PartitionReadCut cut, PartitionMaintenanceCommand command, CancellationToken token) => Require(cut.Id).MaintainAsync(cut, command, token);

    internal async ValueTask CompleteAsync(PartitionReadCut cut, CancellationToken token)
    {
        var entry = Require(cut.Id);
        entry.Require(cut);
        entry.Release();
        await entry.Completed.WaitAsync(token).ConfigureAwait(false);
    }

    internal async ValueTask AbandonAsync(Guid id, CancellationToken token)
    {
        Entry? entry;
        lock (_sync)
        {
            _entries.TryGetValue(id, out entry);
        }

        if (entry is null)
        {
            return; // rollback only, never proof of successful read completion
        }

        entry.Cancel();
        try
        {
            await entry.Completed.WaitAsync(token).ConfigureAwait(false);
        }
        catch when (entry.Completed.IsCompleted)
        {

        } // the read failed; its exclusion handles have unwound
        await entry.RetryCleanupAsync();
    }

    public async ValueTask DisposeAsync()
    {
        Entry[] entries;
        lock (_sync)
        {
            _closing = true;
            entries = _entries.Values.ToArray();
        }
        foreach (var entry in entries)
        {
            entry.Cancel();
        }

        var failures = new List<Exception>();
        foreach (var entry in entries)
        {
            try
            {
                await entry.Completed.ConfigureAwait(false);
            }
            // Completed includes unwinding the concrete kernel semaphore and
            // supervisor/fence admission handles. Its fault is the read outcome
            // (including authority uncertainty), not a remaining release obligation.
            // Keep that outcome on the entry for CompleteAsync, but do not turn a
            // rejected or expired read into permanently failed node disposal.
            catch
            {

            }
            try
            {
                await entry.RetryCleanupAsync();
            }
            catch (Exception failure)
            {
                failures.Add(failure);
            }
        }
        if (failures.Count != 0)
        {
            throw new AggregateException("Partition lease cleanup remains owned.", failures);
        }
    }

    private sealed partial class Entry
    {
        private readonly PartitionOwnedNode _node;
        private readonly CancellationTokenSource _deadline = new();
        private readonly CancellationToken _token;
        private bool _deadlineDisposed;
        private readonly SemaphoreSlim _use = new(1, 1);
        private readonly object _epochSync = new();
        private int _epochUsers;
        private TaskCompletionSource? _epochDrained;
        private readonly TaskCompletionSource<PartitionReadCut> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _closing;
        private PartitionOwnedNode.Maintenance? _maintenance;
        private readonly SemaphoreSlim _cleanup = new(1, 1);
        private bool _maintenanceReleased;

        internal bool ResourcesReleased => _maintenance is null || _maintenanceReleased;

        internal Guid Id
        {
            get;
        }

        internal DateTimeOffset Expires
        {
            get;
        }

        internal bool IsMaintenance
        {
            get;
        }

        internal bool IsSnapshot
        {
            get;
        }

        internal DatabaseTimestampRead? Timestamp
        {
            get;
        }

        internal PartitionScopedRead? Scope
        {
            get;
        }

        private DatabasePartitionSnapshot? _snapshot;
        private DatabaseTimestampProtection? _historyProtection;

        private IDatabaseTimestampProtection? TimestampProtection => (IDatabaseTimestampProtection?)_historyProtection ?? _snapshot;

        internal bool IsHistoryProtection
        {
            get;
        }

        internal Task<PartitionReadCut> Ready => _ready.Task;

        internal Task Completed
        {
            get;
        }

        internal Entry(PartitionOwnedNode node, Guid id, DateTimeOffset expires, bool maintenance, bool snapshot, DatabaseTimestampRead? timestamp, PartitionScopedRead? scope, bool historyProtection)
        {
            _node = node;
            Timestamp = timestamp;
            Scope = scope;
            Id = id;
            Expires = expires;
            IsMaintenance = maintenance;
            IsSnapshot = snapshot;
            IsHistoryProtection = historyProtection;
            _token = _deadline.Token;
            _deadline.CancelAfter(TimeSpan.FromTicks(Math.Max(0, (expires - DateTimeOffset.UtcNow).Ticks)));
            Completed = RunAsync();
            // Retain the fault as an explicit completion outcome without an unobserved task exception.
            _ = Completed.ContinueWith(task =>
{
    _ = task.Exception;
}, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            _ = Ready.ContinueWith(task =>
{
    _ = task.Exception;
}, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        internal void Require(PartitionReadCut cut)
        {
            if (!Ready.IsCompletedSuccessfully || Ready.Result.Reference != cut.Reference ||
                (cut.SnapshotEvidence is not null && Ready.Result.SnapshotEvidence != cut.SnapshotEvidence))
            {
                throw new InvalidDataException("Read-cut position or identity differs.");
            }
        }

        internal void Release()
        {
            Interlocked.Exchange(ref _closing, 1);
            _release.TrySetResult();
        }

        internal void Cancel()
        {
            Interlocked.Exchange(ref _closing, 1);
            lock (_deadline)
            {
                if (!_deadlineDisposed)
                {
                    _deadline.Cancel();
                }
            }
        }

        internal async ValueTask<PartitionProjectionValue> ReadAsync(PartitionReadCut cut, PartitionReadQuery query, CancellationToken token)
        {
            if (query.PlanningDependencies is not null && query.Kind != "semantic-expansion")
            {
                throw new InvalidDataException("Unexpected planning dependencies.");
            }

            if (query.Captured is not null && query.Kind is not ("semantic-expansion" or "semantic-mapping-validation"))
            {
                throw new InvalidDataException("Unexpected captured planning input.");
            }

            if (IsMaintenance)
            {
                throw new InvalidDataException("Maintenance admission cannot be used as a snapshot.");
            }

            if (query.Kind == "root-index")
            {
                return await ReadRootIndexAsync(cut, query, token);
            }

            if (query.RootIndex is not null)
            {
                throw new InvalidDataException("Unexpected root-index request.");
            }

            if (query.Kind == "serving-feed")
            {
                return await ReadServingFeedAsync(cut, query, token);
            }

            if (query.Serving is not null)
            {
                throw new InvalidDataException("Unexpected serving request.");
            }

            if (query.Kind == "query-command")
            {
                return await ReadQueryCommandAsync(cut, query, token);
            }

            if (query.Kind == "query-job")
            {
                return await ReadQueryJobAsync(cut, query, token);
            }

            if (query.QueryJob is not null)
            {
                throw new InvalidDataException("Unexpected query job.");
            }

            if (query.Kind == "timestamp-project")
            {
                if (TimestampProtection is null || Scope is not null || Timestamp is not null || !_node.Kernel.PartitionTransactions.TimestampVisibilityEnabled ||
                    query.ReadTime is null || query.ReadTime.Domain != cut.ClockDomain || query.Projection is null || query.Projection.Projection is not null ||
                    query.Projection.ReadTime is not null || query.Identity != Guid.Empty || query.Subject is not null ||
                    query.Label is not null || query.Batch is not null || query.Source is not null || query.SnapshotPrepared is not null)
                {
                    throw new InvalidDataException("Invalid protected timestamp projection.");
                }

                return await UseEpochAsync(cut, cancellation => _node.ReadTimestampProjectionAsync(
                    TimestampProtection!, query.ReadTime, cut.Position.Version, query.Projection, cancellation), token);
            }
            if (IsHistoryProtection)
            {
                throw new InvalidDataException("Logical history protection only admits timestamp reads.");
            }

            if (query.ReadTime is not null)
            {
                throw new InvalidDataException("Only timestamp projection carries a logical time.");
            }

            if (query.Kind == "settle-snapshot")
            {
                if (_snapshot is null || query.SnapshotPrepared is not { Length: > 0 and <= EntityForge.Db.Durability.DurablePartitionTransaction.MaximumTransactions } ||
                    query.Identity != Guid.Empty || query.Subject is not null || query.Label is not null ||
                    query.Batch is not null || query.Source is not null || query.Projection is not null ||
                    query.SnapshotPrepared.Except(_snapshot.Evidence.Prepared).Any())
                {
                    throw new InvalidDataException("Only this snapshot's captured preparations may delay retry cleanup.");
                }

                return await UseAsync(cut, async cancellation =>
                {
                    await _node.Kernel.PartitionTransactions.WaitForSnapshotProgressAsync(query.SnapshotPrepared, cancellation);
                    return new PartitionProjectionValue(true);
                }, token);
            }
            if (query.Kind == "validate-project")
            {
                if (Scope is not null)
                {
                    throw new InvalidDataException("Scoped validation does not permit an undeclared initial projection.");
                }

                if (_snapshot is null || query.SnapshotPrepared is null || query.Identity != Guid.Empty || query.Subject is not null || query.Label is not null ||
                    query.Batch is not null || query.Source is not null || query.Projection is not { Kind: "batch" or "graph-source" or "semantic-expansion", Projection: null, SnapshotPrepared: null })
                {
                    throw new InvalidDataException("Invalid initial snapshot projection.");
                }

                return await UseAsync(cut, async cancellation =>
                {
                    if (!await _node.Kernel.PartitionTransactions.ValidateSnapshotAsync(_snapshot.Evidence, query.SnapshotPrepared, cancellation))
                    {
                        return new PartitionProjectionValue(new PartitionValidatedRead(false, null));
                    }

                    var value = await _node.ReadProjectionAsync(cut.Position.Version, query.Projection, cancellation, nativeExclusion: false);
                    return new PartitionProjectionValue(new PartitionValidatedRead(true, new(value)));
                }, token);
            }
            if (query.Kind == "validate-snapshot")
            {
                if (_snapshot is null || query.SnapshotPrepared is null || query.Identity != Guid.Empty || query.Subject is not null || query.Label is not null || query.Batch is not null || query.Projection is not null || query.Source is not null)
                {
                    throw new InvalidDataException("Invalid snapshot validation.");
                }

                return await UseAsync(cut, async cancellation => new PartitionProjectionValue(
                    await (Scope is null ? _node.Kernel.PartitionTransactions.ValidateSnapshotAsync(_snapshot.Evidence, query.SnapshotPrepared, cancellation)
                        : _node.Kernel.PartitionTransactions.ValidateScopedSnapshotAsync(_snapshot, query.SnapshotPrepared, cancellation))), token);
            }
            Scope?.RequireQuery(query);
            return await UseAsync(cut, cancellation => _node.ReadProjectionAsync(cut.Position.Version, query, cancellation, nativeExclusion: !IsSnapshot), token);
        }

        internal ValueTask<byte[]> MaintainAsync(PartitionReadCut cut, PartitionMaintenanceCommand command, CancellationToken token)
        {
            if (!IsMaintenance)
            {
                throw new InvalidDataException("Snapshot admission cannot be used for maintenance.");
            }

            return UseAsync(cut, cancellation => _node.MaintainFrozenAsync(_maintenance!, command, cancellation, _token), token);
        }

        private async ValueTask<T> UseAsync<T>(PartitionReadCut cut, Func<CancellationToken, ValueTask<T>> execute, CancellationToken token)
        {
            Require(cut);
            if (Volatile.Read(ref _closing) != 0)
            {
                throw new InvalidOperationException("The read cut is closing.");
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _token);
            await _use.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                if (Volatile.Read(ref _closing) != 0)
                {
                    throw new InvalidOperationException("The read cut is closing.");
                }

                linked.Token.ThrowIfCancellationRequested();
                T result = await execute(linked.Token).ConfigureAwait(false);
                linked.Token.ThrowIfCancellationRequested();
                return result;
            }
            finally
            {
                _use.Release();
            }
        }

        private async ValueTask<T> UseEpochAsync<T>(PartitionReadCut cut, Func<CancellationToken, ValueTask<T>> execute, CancellationToken token)
        {
            Require(cut);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _token);
            lock (_epochSync)
            {
                if (Volatile.Read(ref _closing) != 0)
                {
                    throw new InvalidOperationException("The epoch is closing.");
                }

                _epochUsers++;
            }
            try
            {
                linked.Token.ThrowIfCancellationRequested();
                var result = await execute(linked.Token);
                linked.Token.ThrowIfCancellationRequested();
                return result;
            }
            finally
            {
                lock (_epochSync)
                {
                    if (--_epochUsers == 0)
                    {
                        _epochDrained?.TrySetResult();
                    }
                }
            }
        }

        private async Task RunAsync()
        {
            try
            {
                if (IsMaintenance)
                {
                    var maintenance = await _node.DrainForMaintenanceAsync(_token);
                    _maintenance = maintenance;
                    try
                    {
                        await WaitForReleaseAsync();
                        await maintenance.VerifyAsync();
                    }
                    finally
                    {
                        await RetryCleanupAsync();
                    }
                    return;
                }
                await _node.Authority.ExecuteAsync(async () =>
                {
                    if (IsHistoryProtection)
                    {
                        using var protection = await _node.Kernel.PartitionTransactions.ProtectTimestampHistoryAsync(_token);
                        _historyProtection = protection;
                        await WaitForReleaseAsync();
                    }
                    else if (IsSnapshot)
                    {
                        using var snapshot = Scope is not null ? await _node.Kernel.PartitionTransactions.CaptureScopedSnapshotAsync(Scope.Native, _token)
                            : Timestamp is null ? await _node.Kernel.PartitionTransactions.CaptureSnapshotAsync(_token)
                            : await _node.Kernel.PartitionTransactions.CaptureTimestampSnapshotAsync(
                                await _node.RequireTimestampReadAsync(Timestamp, _token), _token);
                        _snapshot = snapshot;
                        await WaitForReleaseAsync();
                    }
                    else
                    {
                        await using var freeze = await _node.Kernel.PartitionTransactions.FreezeAsync(_token).ConfigureAwait(false);
                        await WaitForReleaseAsync();
                    }
                    return true;
                }).ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                _ready.TrySetException(failure);
                throw;
            }
            finally
            {
                lock (_deadline)
                {
                    _deadlineDisposed = true;
                    _deadline.Dispose();
                }
            }
        }

        internal async ValueTask RetryCleanupAsync()
        {
            await _cleanup.WaitAsync();
            try
            {
                if (_maintenance is not null && !_maintenanceReleased)
                {
                    await _maintenance.DisposeAsync();
                    _maintenanceReleased = true;
                }
            }
            finally
            {
                _cleanup.Release();
            }
        }

        private async Task WaitForReleaseAsync()
        {
            try
            {
                _token.ThrowIfCancellationRequested();
                _ready.TrySetResult(new(Id, new(_node.Domain.Identity, _historyProtection?.Position ?? _snapshot?.Evidence.Version ?? _node.Kernel.CurrentVersion), Expires, IsMaintenance,
                    _snapshot is null ? null : JsonSerializer.Serialize(_snapshot.Evidence.Summarize()), Scope?.Native.Fingerprint,
                    _node.Kernel.PartitionTransactions.TimestampVisibilityEnabled ? _node.Kernel.PartitionTransactions.TimestampDomain : null, IsHistoryProtection));
                await _release.Task.WaitAsync(_token).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Exchange(ref _closing, 1);
                await _use.WaitAsync().ConfigureAwait(false);
                _use.Release();
                Task drain;
                lock (_epochSync)
                {
                    drain = _epochUsers == 0 ? Task.CompletedTask :
                    (_epochDrained ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
                }

                await drain;
            }
            _token.ThrowIfCancellationRequested();
        }
    }
}

internal sealed partial class PartitionOwnedNode : IPartitionReadParticipant
{
    PartitionExecutionDomain IPartitionReadParticipant.Domain => Domain;

    public ValueTask<PartitionReadCut> AcquireHistoryProtectionAsync(Guid id, DateTimeOffset expires, CancellationToken token = default) =>
        _readCuts.AcquireAsync(id, expires, token, historyProtection: true);

    public ValueTask<PartitionReadCut> AcquireCutAsync(Guid id, DateTimeOffset expires, CancellationToken token = default) => _readCuts.AcquireAsync(id, expires, token);

    DatabaseSharedTimestampService? IPartitionReadParticipant.SharedTimestamps => SharedTimestamps;

    public ValueTask<PartitionReadCut> AcquireTimestampCutAsync(Guid id, DatabaseTimestampRead read, CancellationToken token = default) =>
        _readCuts.AcquireAsync(id, read.Expires, token, snapshot: true, timestamp: read);

    public ValueTask<PartitionReadCut> AcquireSnapshotCutAsync(Guid id, DateTimeOffset expires, CancellationToken token = default) =>
        _readCuts.AcquireAsync(id, expires, token, snapshot: true);

    public ValueTask<PartitionReadCut> AcquireScopedSnapshotCutAsync(Guid id, DateTimeOffset expires, PartitionReadQuery[] queries, CancellationToken token = default) =>
        _readCuts.AcquireAsync(id, expires, token, snapshot: true, scope: new(Domain.Profile, Routing, Domain.Partition, queries));

    public async ValueTask<byte[]> ReadCutAsync(PartitionReadCut cut, PartitionReadQuery query, CancellationToken token = default) => (await _readCuts.ReadAsync(cut, query, token)).EncodeProtocol();

    public async ValueTask<PartitionQueryJobResult> ReadQueryJobValueAsync(PartitionReadCut cut, PartitionReadQuery query, CancellationToken token = default)
    {
        using var trace = DistributedReadTelemetry.Start("partition.query.local");
        return (await _readCuts.ReadAsync(cut, query, token)).Native as PartitionQueryJobResult
            ?? throw new InvalidDataException("Missing native query job response.");
    }

    internal ValueTask<PartitionProjectionValue> ReadCutProjectionAsync(PartitionReadCut cut, PartitionReadQuery query, CancellationToken token) => _readCuts.ReadAsync(cut, query, token);

    internal ValueTask<PartitionProjectionValue> ReadCompleteProjectionAsync(PartitionReadCut cut, PartitionReadQuery query, CancellationToken token) => _readCuts.ReadAndCompleteAsync(cut, query, token);

    public async ValueTask<byte[]> ReadAndCompleteCutAsync(PartitionReadCut cut, PartitionReadQuery query, CancellationToken token = default) =>
        (await _readCuts.ReadAndCompleteAsync(cut, query, token)).Encode();

    public ValueTask CompleteCutAsync(PartitionReadCut cut, CancellationToken token = default) => _readCuts.CompleteAsync(cut, token);

    public ValueTask AbandonCutAsync(Guid id, CancellationToken token = default) => _readCuts.AbandonAsync(id, token);

    internal int UnreleasedReadCutCount => _readCuts.UnreleasedCount;

    internal async ValueTask<byte[]> ReadFrozenAsync(DatabaseVersion version, PartitionReadQuery query, CancellationToken token, bool nativeExclusion = true) =>
        (await ReadProjectionAsync(version, query, token, nativeExclusion)).Encode();

    internal async ValueTask<PartitionProjectionValue> ReadProjectionAsync(DatabaseVersion version, PartitionReadQuery query, CancellationToken token, bool nativeExclusion = true)
    {
        const int maximumItems = 4096;
        if (query.PlanningDependencies is not null && query.Kind != "semantic-expansion")
        {
            throw new InvalidDataException("Unexpected planning dependencies.");
        }

        if (query.Captured is not null && query.Kind is not ("semantic-expansion" or "semantic-mapping-validation"))
        {
            throw new InvalidDataException("Only Semantic planning may carry captured input.");
        }

        if (query.ReadTime is not null)
        {
            throw new InvalidDataException("A logical timestamp is only valid on the outer projection.");
        }

        if (query.SnapshotPrepared is not null)
        {
            throw new InvalidDataException("Only snapshot validation may carry a preparation probe.");
        }

        if (query.Projection is not null)
        {
            throw new InvalidDataException("Only validation may carry an initial projection.");
        }

        if (Domain.Profile == DatabaseProfileId.Semantic && query.Kind == "semantic-governance" && query.Identity != Guid.Empty &&
            query.Subject is { Length: > 0 and <= PartitionApplicationBinding.MaximumRequestBytes * 2 })
        {
            return new PartitionProjectionValue(await ReadSemanticGovernanceAsync(version, Convert.FromBase64String(query.Subject), query.Identity, token));
        }

        if (query.Kind == "query-relation")
        {
            return await ReadQueryRelationAsync(version, query, token);
        }

        if (query.Kind == "query-relation-page")
        {
            return await ReadQueryRelationPageAsync(version, query, token);
        }

        if (query.Kind == "graph-source")
        {
            return await ReadGraphSourceAsync(version, query, token, nativeExclusion);
        }

        if (query.Source is not null)
        {
            throw new InvalidDataException("Only source queries may carry a source projection.");
        }

        if (query.Kind == "batch")
        {
            return await ReadBatchAsync(version, query, token, nativeExclusion);
        }

        if (query.Batch is not null)
        {
            throw new InvalidDataException("Only batch queries may carry a batch.");
        }

        if (query.Kind is "mapping" or "semantic-project" or "semantic-traverse")
        {
            return await ReadMappedNativeAsync(version, query, token, nativeExclusion);
        }

        if (Domain.Profile == DatabaseProfileId.Semantic && query.Kind == "semantic-mapping-validation" && query.Identity == Guid.Empty && query.Label is null &&
            query.Subject is null && query.Captured is { Length: > 0 and <= PartitionApplicationBinding.MaximumRequestBytes })
        {
            return new PartitionProjectionValue(await ValidateSemanticMappingsAsync(query.Captured, token));
        }

        if (Domain.Profile == DatabaseProfileId.Semantic && query.Kind == "semantic-expansion" && query.Identity == Guid.Empty && query.Label is null &&
            query.Subject is null && query.Captured is { Length: > 0 and <= PartitionApplicationBinding.MaximumRequestBytes })
        {
            return new PartitionProjectionValue(await ExpandSemanticAsync(version, query.Captured, token,
                PartitionSemanticPlanning.Dependencies(query.PlanningDependencies)));
        }

        if (_native.Database is GraphPropertyShard split)
        {
            return ReadSplitNative(split, version, query);
        }

        object? value;
        if (_native.Database is IGraphDatabase graph)
        {
            if (query.Identity == Guid.Empty || query.Subject is not null || query.Label?.Length > 4096)
            {
                throw new InvalidDataException("Invalid Graph read request.");
            }

            var vertex = new GraphVertexId(query.Identity);
            if (query.Kind != "edge" && Routing.RouteGraphVertex(query.Identity) != Domain.Partition)
            {
                throw new InvalidDataException("Graph read belongs to another owner.");
            }

            if (query.Kind == "edge" && Routing.RouteGraphEdge(query.Identity) != Domain.Partition)
            {
                throw new InvalidDataException("Authoritative edge read belongs to another owner.");
            }

            value = query.Kind switch
            {
                "vertex" => await graph.GetVertexAsync(vertex, version, token),
                "edge" => await graph.GetEdgeAsync(new(query.Identity), version, token),
                "outgoing" => graph.Outgoing(vertex, version, query.Label),
                "incoming" => graph.Incoming(vertex, version, query.Label),
                _ => throw new NotSupportedException("Unknown Graph partition read.")
            };
            if (query.Kind == "edge" && value is GraphEdge authorityEdge && !authorityEdge.StorageRole.HasFlag(GraphEdgeStorageRole.Authoritative))
            {
                throw new InvalidDataException("The selected edge owner contains only an adjacency replica.");
            }

            if (value is IReadOnlyList<GraphEdge> edges && edges.Count > maximumItems)
            {
                throw new InvalidOperationException("Adjacency exceeds the bounded read budget.");
            }
        }
        else if (_native.Database is IRdfDatabase rdf && query.Kind == "subject" && query.Subject is { Length: > 0 and <= 4096 } subject &&
            query.Identity == Guid.Empty && query.Label is null)
        {
            var iri = new RdfIri(subject);
            if (Routing.Route(DistributedDatabaseProfile.Rdf, ProfilePartitionRouting.RdfOwner(iri)) != Domain.Partition)
            {
                throw new InvalidDataException("RDF subject belongs to another owner.");
            }

            var pattern = new RdfQuadPattern(Subject: iri, MatchDefaultGraphOnly: true);
            using var snapshot = _timestampProjection.Value && rdf is RdfDatabaseEngine timestampRdf
                ? timestampRdf.AcquireTimestampProjectionLease(version) : await rdf.AcquireSnapshotAsync(version, token);
            value = ((IRdfBoundedSnapshotReader)rdf).FindBounded(pattern, snapshot, maximumItems, token);
        }
        else
        {
            throw new NotSupportedException("The requested native read is not supported by this profile.");
        }

        return new(value);
    }
}
