using System.Text.Json;
using EntityForge.Db.Kernel;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Distributed.Hosting;

internal sealed partial class HttpPartitionTransactionParticipant
{
    // The shared query-job default. Qualification can select the previous wire
    // path explicitly; no hidden fallback occurs after submission.
    internal bool TypedQueryProtocol { get; init; } = true;

    public async ValueTask<PartitionQueryJobResult> ReadQueryJobValueAsync(PartitionReadCut cut, PartitionReadQuery query, CancellationToken token = default)
    {
        // General relation/complete-owner evaluation still consumes JSON. Select
        // its established wire before dispatch; compact operators and Semantic frontiers consume typed
        // pages directly. Value-less finish/abandon commands share the framing.
        if (!TypedQueryProtocol || query.QueryJob?.Selection is { GraphFragments: false, ProfileFragments: false, SemanticFrontier: null })
        {
            return (await ReadCutValueAsync(cut, query, token)).Deserialize<PartitionQueryJobResult>(PartitionParticipantWire.Json)
                ?? throw new InvalidDataException("Missing query job response.");
        }

        using var trace = DistributedReadTelemetry.Start("partition.rpc");
        trace?.SetTag("entityforge.phase", "query-job");
        var request = new PartitionParticipantWire.Request(Domain.Identity, TransactionAuthorityIdentity, cut.Id, Cut: cut.Reference, Query: query);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(request, PartitionParticipantWire.Json);
        if (bytes.Length > PartitionParticipantWire.MaximumRequestBytes)
        {
            throw new ArgumentException("Operator request exceeds its bound.");
        }

        trace?.SetTag("entityforge.request_bytes", bytes.Length);
        byte[] content = await ExchangeOwnedAsync("read-query-cut", bytes, token);
        trace?.SetTag("entityforge.response_bytes", content.Length);
        var (control, result) = PartitionQueryOperatorWire.Decode(content);
        if (control.Target != Domain.Identity || control.Authority != TransactionAuthorityIdentity || control.Operation != cut.Id || control.Cut != cut.Reference)
        {
            throw new InvalidDataException("Operator response association differs.");
        }

        return result;
    }

    public DatabaseSharedTimestampService? SharedTimestamps
    {
        get; set;
    }

    public async ValueTask<PartitionReadCut> AcquireScopedSnapshotCutAsync(Guid id, DateTimeOffset expires, PartitionReadQuery[] queries, CancellationToken token = default)
    {
        var response = await SendAsync("acquire-scoped-snapshot-cut", new(Domain.Identity, TransactionAuthorityIdentity, id,
            Expires: expires, ScopeQueries: queries), token);
        RequireCutResponse(response, "acquire-scoped-snapshot-cut");
        if (response.Cut is not { SnapshotEvidence: not null, Scope: not null } cut || cut.Id != id || cut.Expires != expires || cut.Position.Identity != Domain.Identity)
        {
            throw new InvalidDataException("Invalid scoped snapshot acquisition response.");
        }

        return cut;
    }

    public async ValueTask<PartitionReadCut> AcquireTimestampCutAsync(Guid id, DatabaseTimestampRead read, CancellationToken token = default)
    {
        var response = await SendAsync("acquire-timestamp-cut", new(Domain.Identity, TransactionAuthorityIdentity, id, Expires: read.Expires, Timestamp: read), token);
        RequireCutResponse(response, "acquire-timestamp-cut");
        if (response.Cut is not { } cut || cut.Id != id || cut.Expires != read.Expires || cut.Position.Identity != Domain.Identity ||
            cut.Position.Version.Value != read.Positions[PartitionOwnedNode.TimestampParticipant(Domain.Identity)])
        {
            throw new InvalidDataException("Timestamp cut association differs.");
        }

        return cut;
    }

    public async ValueTask<PartitionReadCut> AcquireHistoryProtectionAsync(Guid id, DateTimeOffset expires, CancellationToken token = default)
    {
        var response = await SendAsync("acquire-history-cut", new(Domain.Identity, TransactionAuthorityIdentity, id, Expires: expires), token);
        RequireCutResponse(response, "acquire-history-cut");
        if (response.Cut is not { HistoryProtection: true, SnapshotEvidence: null, Scope: null, ClockDomain: not null, Maintenance: false } cut ||
            cut.Id != id || cut.Expires != expires || cut.Position.Identity != Domain.Identity || response.Value is not null)
        {
            throw new InvalidDataException("Invalid timestamp-history protection association.");
        }

        return cut;
    }

    public async ValueTask<PartitionReadCut> AcquireSnapshotCutAsync(Guid id, DateTimeOffset expires, CancellationToken token = default)
    {
        var response = await SendAsync("acquire-snapshot-cut", new(Domain.Identity, TransactionAuthorityIdentity, id, Expires: expires), token);
        RequireCutResponse(response, "acquire-snapshot-cut");
        if (response.Cut is not { SnapshotEvidence: not null, Scope: null } cut || cut.Id != id || cut.Expires != expires || cut.Position.Identity != Domain.Identity)
        {
            throw new InvalidDataException("Invalid snapshot acquisition response.");
        }

        return cut;
    }

    public async ValueTask<PartitionReadCut> AcquireCutAsync(Guid id, DateTimeOffset expires, CancellationToken token = default)
    {
        var response = await SendAsync("acquire-cut", new(Domain.Identity, TransactionAuthorityIdentity, id, Expires: expires), token);
        RequireCutResponse(response, "acquire-cut");
        if (response.Cut is not { } cut || cut.Id != id || cut.Position.Identity != Domain.Identity || cut.Expires != expires || cut.Maintenance || response.Value is not null)
        {
            throw new InvalidDataException("Read acquisition changed its association.");
        }

        return cut;
    }

    public async ValueTask PrepareMaintenanceAsync(CancellationToken token = default)
    {
        var response = await SendAsync("prepare-maintenance-cut", new(Domain.Identity, TransactionAuthorityIdentity, Guid.NewGuid()), token);
        if (response.Code != "prepare-maintenance-cut" || response.Cut is not null || response.Value is not null ||
            response.Receipt is not null || response.Resolution is not null || response.Descriptor is not null || response.Member is not null)
        {
            throw new InvalidDataException("Invalid maintenance preparation response.");
        }
    }

    public async ValueTask<PartitionReadCut> AcquireMaintenanceAsync(Guid id, DateTimeOffset expires, CancellationToken token = default)
    {
        var response = await SendAsync("acquire-maintenance-cut", new(Domain.Identity, TransactionAuthorityIdentity, id, Expires: expires), token);
        RequireCutResponse(response, "acquire-maintenance-cut");
        if (response.Cut is not { } cut || cut.Id != id || cut.Position.Identity != Domain.Identity || cut.Expires != expires || !cut.Maintenance || response.Value is not null)
        {
            throw new InvalidDataException("Maintenance acquisition changed its association.");
        }

        return cut;
    }

    public async ValueTask<byte[]> MaintainAsync(PartitionReadCut cut, PartitionMaintenanceCommand command, CancellationToken token = default)
    {
        var response = await SendAsync("maintain-cut", new(Domain.Identity, TransactionAuthorityIdentity, cut.Id, Cut: cut, Maintenance: command), token);
        RequireCutResponse(response, "maintain-cut");
        if (response.Cut != cut || response.Value is null || !cut.Maintenance)
        {
            throw new InvalidDataException("Maintenance response changed its selected lease.");
        }

        return response.Value;
    }

    public ValueTask<JsonElement> ReadCutValueAsync(PartitionReadCut cut, PartitionReadQuery query, CancellationToken token = default) =>
        ReadOwnedValueAsync("read-cut", cut, query, token);

    public ValueTask<JsonElement> ReadAndCompleteCutValueAsync(PartitionReadCut cut, PartitionReadQuery query, CancellationToken token = default) =>
        ReadOwnedValueAsync("read-complete-cut", cut, query, token);

    private async ValueTask<JsonElement> ReadOwnedValueAsync(string action, PartitionReadCut cut, PartitionReadQuery query, CancellationToken token)
    {
        var response = await SendAsync(action, new(Domain.Identity, TransactionAuthorityIdentity, cut.Id, Cut: cut.Reference, Query: query), token, preserveReadValue: true);
        RequireCutResponse(response, action);
        if (response.Cut != cut.Reference || (response.Value is null) == (response.ReadValue is null))
        {
            throw new InvalidDataException("Read response changed its selected position or value framing.");
        }

        return response.ReadValue is { } structured ? structured.Value.Element : JsonSerializer.Deserialize<JsonElement>(response.Value!);
    }

    public async ValueTask<byte[]> ReadCutAsync(PartitionReadCut cut, PartitionReadQuery query, CancellationToken token = default)
    {
        var response = await SendAsync("read-cut", new(Domain.Identity, TransactionAuthorityIdentity, cut.Id, Cut: cut.Reference, Query: query), token);
        RequireCutResponse(response, "read-cut");
        if (response.Cut != cut.Reference || response.Value is null)
        {
            throw new InvalidDataException("Read response changed its selected position.");
        }

        return response.Value;
    }

    public async ValueTask<byte[]> ReadAndCompleteCutAsync(PartitionReadCut cut, PartitionReadQuery query, CancellationToken token = default)
    {
        var response = await SendAsync("read-complete-cut", new(Domain.Identity, TransactionAuthorityIdentity, cut.Id, Cut: cut.Reference, Query: query), token);
        RequireCutResponse(response, "read-complete-cut");
        if (response.Cut != cut.Reference || response.Value is null)
        {
            throw new InvalidDataException("Final projection changed its selected position.");
        }

        return response.Value;
    }

    public async ValueTask CompleteCutAsync(PartitionReadCut cut, CancellationToken token = default)
    {
        var response = await SendAsync("complete-cut", new(Domain.Identity, TransactionAuthorityIdentity, cut.Id, Cut: cut.Reference), token);
        RequireCutResponse(response, "complete-cut");
        if (response.Cut != cut.Reference || response.Value is not null)
        {
            throw new InvalidDataException("Completion changed its selected position.");
        }
    }

    public async ValueTask AbandonCutAsync(Guid id, CancellationToken token = default)
    {
        var response = await SendAsync("abandon-cut", new(Domain.Identity, TransactionAuthorityIdentity, id), token);
        RequireCutResponse(response, "abandon-cut");
        if (response.Cut is not null || response.Value is not null)
        {
            throw new InvalidDataException("Rollback carried unexpected results.");
        }
    }

    private static void RequireCutResponse(PartitionParticipantWire.Response response, string code)
    {
        if (response.Code != code || response.Receipt is not null || response.Member is not null || response.Descriptor is not null || response.Resolution is not null)
        {
            throw new InvalidDataException("Invalid read-cut response framing.");
        }
    }
}

internal sealed partial class PartitionRemoteRead
{
    private bool _epoch;
    private EntityForge.Db.Durability.DurableTimestampState? _logicalTime;

    internal PartitionRemoteRead ForTimestampQuery(EntityForge.Db.Kernel.IDatabaseBoundedClock? clock = null)
    {
        if (_logicalTime is not null)
        {
            return this;
        }

        var domains = _cuts.Values.Select(c => c.ClockDomain).Distinct().ToArray();
        if (domains.Length == 1 && domains[0] is null)
        {
            throw new InvalidOperationException("Timestamp reads require timestamp-enabled owners; no alternate read protocol is selected.");
        }
        if (domains.Length != 1 || domains[0] is not Guid domain)
        {
            throw new InvalidDataException("Read owners have incompatible clock domains.");
        }

        return new(_cuts.ToDictionary(p => p.Key, p => p.Value))
        {
            _epoch = true,
            _logicalTime = PartitionTimestampClock.Read(domain, clock)
        };
    }

    internal void UseAsEpoch() => _epoch = true;

    internal EntityForge.Db.Durability.DurableTimestampState? Timestamp => _logicalTime;

    internal PartitionRemoteRead ForRootCohort(IEnumerable<PartitionRootIndexDemand> demands)
    {
        if (!_epoch || _logicalTime is null)
        {
            throw new InvalidOperationException("Root cohorts require a timestamp read.");
        }

        var time = _logicalTime;
        foreach (var demand in demands)
        {
            if (demand.AdmissionTime is not { } admitted)
            {
                continue;
            }

            if (admitted.Domain != time.Domain || !admitted.Mvcc)
            {
                throw new InvalidDataException("Root cohort admission has a different timestamp domain.");
            }

            if (admitted.CompareTo(time) > 0)
            {
                time = admitted;
            }
        }
        // A bounded clock's upper bound can decrease. Select the maximum before
        // owners execute, so every result and completion proof uses this time.
        return AtTimestamp(time);
    }

    internal PartitionRemoteRead AtTimestamp(EntityForge.Db.Durability.DurableTimestampState time)
    {
        if (!_epoch || _logicalTime is null || time.Domain != _logicalTime.Domain || !time.Mvcc || time.CompareTo(_logicalTime) < 0)
        {
            throw new InvalidDataException("Root-index timestamp predates query admission or has a different domain.");
        }

        return new(_cuts.ToDictionary(p => p.Key, p => p.Value))
        {
            _epoch = true,
            _logicalTime = time
        };
    }

    private readonly IReadOnlyDictionary<IPartitionReadParticipant, PartitionReadCut> _cuts;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string, PartitionReadQuery), byte[]> _initial = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<IPartitionReadParticipant, byte> _completed = new();

    internal DatabasePositionVector Position
    {
        get;
    }

    internal async ValueTask<byte[]> ReadAndCompleteAsync(string partition, PartitionReadQuery query, CancellationToken token)
    {
        if (_epoch)
        {
            return await ReadAsync(partition, query, token);
        }

        var pair = _cuts.Single(pair => pair.Key.Domain.Partition.Value == partition);
        if (_completed.ContainsKey(pair.Key))
        {
            throw new InvalidOperationException("The owner's projection is already complete.");
        }

        byte[] value = await pair.Key.ReadAndCompleteCutAsync(pair.Value, query, token);
        _completed.TryAdd(pair.Key, 0);
        return value;
    }

    internal async ValueTask<JsonElement> ReadAndCompleteValueAsync(string partition, PartitionReadQuery query, CancellationToken token)
    {
        if (_epoch)
        {
            return await ReadValueAsync(partition, query, token);
        }

        var pair = _cuts.Single(pair => pair.Key.Domain.Partition.Value == partition);
        if (_completed.ContainsKey(pair.Key))
        {
            throw new InvalidOperationException("The owner's projection is already complete.");
        }

        var value = await pair.Key.ReadAndCompleteCutValueAsync(pair.Value, query, token);
        _completed.TryAdd(pair.Key, 0);
        return value;
    }

    private PartitionRemoteRead(Dictionary<IPartitionReadParticipant, PartitionReadCut> cuts)
    {
        _cuts = cuts;
        Position = new(cuts.Values.Select(cut => cut.Position).ToArray());
    }

    internal async ValueTask<byte[]> ReadAsync(string partition, PartitionReadQuery query, CancellationToken token)
    {
        var pair = _cuts.Single(pair => pair.Key.Domain.Partition.Value == partition);
        if (_completed.ContainsKey(pair.Key))
        {
            throw new InvalidOperationException("The owner's projection is already complete.");
        }

        if (_epoch)
        {
            if (_logicalTime is { } time)
            {
                return await pair.Key.ReadCutAsync(pair.Value, new("timestamp-project", Projection: query, ReadTime: time), token);
            }

            throw new InvalidOperationException("A retained read requires a selected timestamp.");
        }
        if (_initial.TryGetValue((partition, query), out var initial))
        {
            return initial;
        }

        return await pair.Key.ReadCutAsync(pair.Value, query, token);
    }

    internal async ValueTask<JsonElement> ReadValueAsync(string partition, PartitionReadQuery query, CancellationToken token)
    {
        var pair = _cuts.Single(pair => pair.Key.Domain.Partition.Value == partition);
        if (_completed.ContainsKey(pair.Key))
        {
            throw new InvalidOperationException("The owner's projection is already complete.");
        }

        if (_epoch)
        {
            if (_logicalTime is { } time)
            {
                return await pair.Key.ReadCutValueAsync(pair.Value, new("timestamp-project", Projection: query, ReadTime: time), token);
            }

            throw new InvalidOperationException("A retained read requires a selected timestamp.");
        }
        if (_initial.TryGetValue((partition, query), out var initial))
        {
            return JsonSerializer.Deserialize<JsonElement>(initial);
        }

        return await pair.Key.ReadCutValueAsync(pair.Value, query, token);
    }

    internal static async ValueTask<(T Value, DatabasePositionVector Position)> ExecuteProtectedAsync<T>(
        IReadOnlyList<IPartitionReadParticipant> owners, DistributedRoutingBinding routing,
        Func<PartitionRemoteRead, CancellationToken, ValueTask<T>> projection, DatabasePositionVector? minimum,
        CancellationToken token = default)
    {
        if (owners.Count != routing.Shards.Count || owners.Count is < 1 or > DatabasePositionVector.MaximumPartitions ||
            owners.Select(owner => owner.Domain.Partition).Distinct().Count() != owners.Count ||
            owners.Any(owner => !routing.Shards.Contains(owner.Domain.Partition)) ||
            owners.Any(owner => owner.Domain.Generation != owners[0].Domain.Generation || owner.Domain.Profile != owners[0].Domain.Profile ||
                owner.Domain.RoutingFingerprint != routing.Fingerprint))
        {
            throw new InvalidDataException("Timestamp protection requires the exact enrolled owner set.");
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(PartitionReadCuts.MaximumDuration);
        var cuts = new System.Collections.Concurrent.ConcurrentDictionary<IPartitionReadParticipant, PartitionReadCut>();
        var attempted = owners.Select(owner => (Owner: owner, Id: Guid.NewGuid())).ToArray();
        var expires = DateTimeOffset.UtcNow + PartitionReadCuts.HistoryAcquisitionDuration;
        try
        {
            using (DistributedReadTelemetry.Start("partition.read.history_protect"))
            {
                await RunSnapshotRoundAsync(attempted, async (attempt, cancellation) =>
                {
                    var cut = await attempt.Owner.AcquireHistoryProtectionAsync(attempt.Id,
                        expires, cancellation);
                    if (!cut.HistoryProtection || cut.SnapshotEvidence is not null || cut.Scope is not null || cut.Maintenance ||
                        cut.ClockDomain is null || cut.Id != attempt.Id || cut.Position.Identity != attempt.Owner.Domain.Identity || cut.Expires != expires)
                    {
                        throw new InvalidDataException("Timestamp protection response differs from its acquisition.");
                    }

                    cuts[attempt.Owner] = cut;
                }, deadline.Token);
            }

            var read = new PartitionRemoteRead(cuts.ToDictionary(pair => pair.Key, pair => pair.Value));
            if (minimum is not null && !read.Position.Covers(minimum))
            {
                throw new InvalidOperationException("Timestamp protection does not cover the requested position.");
            }

            var value = await projection(read, deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            await RunSnapshotRoundAsync(cuts, (pair, cancellation) => pair.Key.CompleteCutAsync(pair.Value, cancellation), deadline.Token);
            return (value, read.Position);
        }
        catch (Exception failure)
        {
            try
            {
                await RunSnapshotRoundAsync(attempted, (attempt, cancellation) => attempt.Owner.AbandonCutAsync(attempt.Id, cancellation), CancellationToken.None);
            }
            catch (Exception cleanup)
            {
                throw new AggregateException("Timestamp protection cleanup remains owned.", failure, cleanup);
            }
            throw;
        }
    }

    // A Semantic proposal may inspect one source owner before its dependency closure
    // is known. The caller must prove the complete closure before retaining selection.
    // This shares snapshot validation, deadline, pending-write checks and pin cleanup.
    internal static ValueTask<(T Value, DatabasePositionVector Position)> ExecuteSemanticOwnerAsync<T>(
        IPartitionReadParticipant owner, DistributedRoutingBinding routing,
        Func<PartitionRemoteRead, CancellationToken, ValueTask<T>> projection, CancellationToken token, PartitionReadQuery? initial = null) =>
        owner.Domain.Profile != DatabaseProfileId.Semantic
            ? throw new ArgumentException("A Semantic source owner is required.")
            : ExecuteCoreAsync([owner], routing, projection, token, true, null,
                initial is null ? null : new Dictionary<string, PartitionReadQuery> { [owner.Domain.Partition.Value] = initial }, null, true);

    internal static ValueTask<(T Value, DatabasePositionVector Position)> ExecuteAsync<T>(
        IReadOnlyList<IPartitionReadParticipant> owners, DistributedRoutingBinding routing,
        Func<PartitionRemoteRead, CancellationToken, ValueTask<T>> projection, CancellationToken token = default, bool useSnapshots = false, DatabasePositionVector? minimum = null, IReadOnlyDictionary<string, PartitionReadQuery>? initial = null,
        IReadOnlyDictionary<string, PartitionReadQuery[]>? scopes = null) =>
        ExecuteCoreAsync(owners, routing, projection, token, useSnapshots, minimum, initial, scopes, false);

    private static async ValueTask<(T Value, DatabasePositionVector Position)> ExecuteCoreAsync<T>(
        IReadOnlyList<IPartitionReadParticipant> owners, EntityForge.Db.Distributed.Sharding.DistributedRoutingBinding routing,
        Func<PartitionRemoteRead, CancellationToken, ValueTask<T>> projection, CancellationToken token = default, bool useSnapshots = false, DatabasePositionVector? minimum = null, IReadOnlyDictionary<string, PartitionReadQuery>? initial = null,
        IReadOnlyDictionary<string, PartitionReadQuery[]>? scopes = null, bool semanticOwner = false)
    {
        using var readTrace = DistributedReadTelemetry.Start("partition.read.complete");
        if (owners.Count is < 1 or > DatabasePositionVector.MaximumPartitions || owners.Any(owner => owner is null) ||
            (semanticOwner ? owners.Count != 1 || !routing.Shards.Contains(owners[0].Domain.Partition)
                : owners.Count != routing.Shards.Count || !owners.Select(owner => owner.Domain.Partition).ToHashSet().SetEquals(routing.Shards)))
        {
            throw new ArgumentException("The complete unique read-owner set is required.");
        }

        if (initial is not null && initial.Any(item => item.Value is null || item.Value.Kind is not ("batch" or "graph-source" or "semantic-expansion") || !owners.Any(owner => owner.Domain.Partition.Value == item.Key)))
        {
            throw new ArgumentException("Invalid initial projection owners or queries.");
        }

        var first = owners[0].Domain;
        if (scopes is not null && (initial is not null || scopes.Count != owners.Count || owners.Any(owner =>
            owner.SharedTimestamps is not null || !scopes.ContainsKey(owner.Domain.Partition.Value))))
        {
            throw new ArgumentException("Scoped projection requires a fixed scope on every owner and ordinary MVCC.");
        }

        var ownedScopes = scopes?.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
        var scopeProofs = ownedScopes?.ToDictionary(pair => pair.Key, pair => new PartitionScopedRead(first.Profile, routing, new(pair.Key), pair.Value));
        if (owners.Any(owner => owner.Domain.Generation != first.Generation || owner.Domain.Profile != first.Profile || owner.Domain.RoutingFingerprint != routing.Fingerprint))
        {
            throw new InvalidDataException("Read owners belong to different databases.");
        }

        if (owners[0].SharedTimestamps is { } shared)
        {
            return await ExecuteTimestampAsync(owners, shared, projection, minimum, token);
        }

        if (owners.Any(owner => owner.SharedTimestamps is not null))
        {
            throw new InvalidDataException("Read owners cannot mix timestamp and vector protocols.");
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(PartitionReadCuts.MaximumDuration);
        useSnapshots |= ownedScopes is not null;
        int attempts = 0;
        while (true)
        {
            readTrace?.SetTag("entityforge.read_attempts", ++attempts);
            deadline.Token.ThrowIfCancellationRequested();
            DateTimeOffset expires = DateTimeOffset.UtcNow + PartitionReadCuts.MaximumDuration;
            var attempted = new List<(IPartitionReadParticipant Owner, Guid Id)>();
            var cuts = new Dictionary<IPartitionReadParticipant, PartitionReadCut>();
            try
            {
                if (useSnapshots)
                {
                    var selected = owners.OrderBy(owner => owner.Domain.Partition.Value, StringComparer.Ordinal).ToArray();
                    var acquired = new PartitionReadCut?[selected.Length];
                    // Own every possible remote pin before dispatch, including a lost reply.
                    attempted.AddRange(selected.Select(owner => (owner, Guid.NewGuid())));
                    using (DistributedReadTelemetry.Start("partition.read.capture"))
                    {
                        await RunSnapshotRoundAsync(Enumerable.Range(0, selected.Length), async (index, cancellation) =>
                        {
                            acquired[index] = ownedScopes is null ? await selected[index].AcquireSnapshotCutAsync(attempted[index].Id, expires, cancellation)
                            : await selected[index].AcquireScopedSnapshotCutAsync(attempted[index].Id, expires, ownedScopes[selected[index].Domain.Partition.Value], cancellation);
                        }, deadline.Token);
                    }

                    for (int index = 0; index < selected.Length; index++)
                    {
                        cuts.Add(selected[index], acquired[index] ?? throw new InvalidDataException("Snapshot acquisition returned no cut."));
                    }
                }
                else
                {
                    // Frozen cuts hold write exclusion: retain a single global lock order.
                    foreach (var owner in owners.OrderBy(owner => owner.Domain.Partition.Value, StringComparer.Ordinal))
                    {
                        Guid id = Guid.NewGuid();
                        attempted.Add((owner, id));
                        cuts.Add(owner, await owner.AcquireCutAsync(id, expires, deadline.Token));
                    }
                }

                var read = new PartitionRemoteRead(cuts);
                if (minimum is not null && !read.Position.Covers(minimum))
                {
                    throw new InvalidOperationException("The cut does not cover the requested position.");
                }

                if (useSnapshots)
                {
                    var evidence = cuts.Select(pair =>
                    {
                        var cut = pair.Value;
                        var summary = JsonSerializer.Deserialize<DatabasePartitionSnapshotSummary>(cut.SnapshotEvidence
                            ?? throw new InvalidDataException("A snapshot certificate is required."))
                            ?? throw new InvalidDataException("Missing snapshot certificate.");
                        if (summary.Version != cut.Position.Version || summary.Scope != cut.Scope ||
                            summary.Scope != (scopeProofs is null ? null : scopeProofs[pair.Key.Domain.Partition.Value].Native.Fingerprint))
                        {
                            throw new InvalidDataException("Snapshot summary position differs from its pin.");
                        }

                        return (Owner: pair.Key, Summary: summary);
                    }).ToArray();
                    var prepared = evidence.SelectMany(item => item.Summary.Prepared).ToHashSet();
                    var probe = prepared.ToArray();
                    // Exact sample hits retain the cheap early rejection. Absence is established
                    // only by probing each owner's complete pinned evidence during validation.
                    // Scoped projections require all relevant writes settled, not
                    // merely absence of a cross-owner sample hit. Filtered evidence
                    // cannot establish the state of an unobserved/read-only member.
                    var mixed = scopeProofs is not null ? prepared :
                        evidence.SelectMany(item => item.Summary.CommitmentSample).Where(prepared.Contains).ToHashSet();
                    if (mixed.Count != 0)
                    {
                        using (var rejected = DistributedReadTelemetry.Start("partition.read.rejection"))
                        {
                            if (rejected is not null)
                            {
                                const int maximumDiagnosticOperations = 256;
                                rejected.SetTag("entityforge.phase", scopeProofs is null ? "mixed" : "scope-pending");
                                rejected.SetTag("entityforge.scope_complete", mixed.Count <= maximumDiagnosticOperations);
                                if (mixed.Count <= maximumDiagnosticOperations)
                                {
                                    rejected.SetTag("entityforge.operations", JsonSerializer.Serialize(mixed.Select(id => id.ToString("N")).Order(StringComparer.Ordinal).ToArray()));
                                }
                            }
                        }

                        throw new SnapshotRetry(evidence.Select(item => (item.Owner, Pending: item.Summary.Prepared.Where(mixed.Contains).ToArray()))
                            .Where(item => item.Pending.Length != 0).ToDictionary(item => item.Owner, item => item.Pending));
                    }
                    // Every interval from capture to validation overlaps the end of the capture round.
                    // Equal monotonic native positions and protocol fingerprints certify that common cut.
                    int invalid = 0;
                    long initialBytes = 0;
                    using (DistributedReadTelemetry.Start("partition.read.validate"))
                    {
                        await RunSnapshotRoundAsync(cuts, async (pair, cancellation) =>
                        {
                            if (initial is not null && initial.TryGetValue(pair.Key.Domain.Partition.Value, out var query))
                            {
                                var result = PartitionParticipantBinary.Decode<PartitionValidatedRead>(await pair.Key.ReadCutAsync(pair.Value,
                                    new("validate-project", Projection: query, SnapshotPrepared: probe), cancellation))
                                    ?? throw new InvalidDataException("Missing snapshot projection result.");
                                if (result.Valid != (result.Projection is not null))
                                {
                                    throw new InvalidDataException("Invalid provisional read framing.");
                                }

                                if (!result.Valid)
                                {
                                    Interlocked.Exchange(ref invalid, 1);
                                }
                                else
                                {
                                    byte[] encoded = result.Projection!.Value.EncodeProtocol();
                                    if (Interlocked.Add(ref initialBytes, encoded.Length) > PartitionApplicationWire.MaximumBodyBytes)
                                    {
                                        throw new InvalidOperationException("Initial projections exceed the read response bound.");
                                    }

                                    read._initial[(pair.Key.Domain.Partition.Value, query)] = encoded;
                                }
                            }
                            else if (!JsonSerializer.Deserialize<bool>(await pair.Key.ReadCutAsync(pair.Value, new("validate-snapshot", SnapshotPrepared: probe), cancellation)))
                            {
                                Interlocked.Exchange(ref invalid, 1);
                            }
                        }, deadline.Token);
                    }

                    if (invalid != 0)
                    {
                        using (var rejected = DistributedReadTelemetry.Start("partition.read.rejection"))
                        {
                            rejected?.SetTag("entityforge.phase", "validation");
                        }

                        throw new SnapshotRetry();
                    }
                }
                T value;
                using (DistributedReadTelemetry.Start("partition.read.project"))
                {
                    value = await projection(read, deadline.Token);
                }

                deadline.Token.ThrowIfCancellationRequested();
                // Independent releases may overlap; ordinary success still joins every completion.
                using (DistributedReadTelemetry.Start("partition.read.release"))
                {
                    await RunSnapshotRoundAsync(cuts.Where(pair => !read._completed.ContainsKey(pair.Key)), (pair, cancellation) => pair.Key.CompleteCutAsync(pair.Value, cancellation), deadline.Token);
                }

                return (value, read.Position);
            }
            catch (Exception failure)
            {
                var failures = new List<Exception> { failure };
                // Known client-generated identities let us discharge a pin even when
                // acquisition's response was lost. Rollback is not successful completion.
                try
                {
                    await RunSnapshotRoundAsync(attempted,
                        async (attempt, cancellation) =>
                        {
                            if (failure is SnapshotRetry { Pending: { } pending } && pending.TryGetValue(attempt.Owner, out var operations))
                            {
                                try
                                {
                                    if (!JsonSerializer.Deserialize<bool>(await attempt.Owner.ReadCutAsync(cuts[attempt.Owner],
                                        new("settle-snapshot", SnapshotPrepared: operations), deadline.Token)))
                                    {
                                        throw new InvalidDataException("Snapshot settlement did not release its pin.");
                                    }
                                }
                                catch (Exception settlement)
                                {
                                    // A lost response still owns the original pin identity.
                                    try
                                    {
                                        await attempt.Owner.AbandonCutAsync(attempt.Id, cancellation);
                                    }
                                    catch (Exception cleanup)
                                    {
                                        throw new AggregateException("Snapshot settlement and cleanup failed.", settlement, cleanup);
                                    }
                                    throw;
                                }
                            }
                            else
                            {
                                await attempt.Owner.AbandonCutAsync(attempt.Id, cancellation);
                            }
                        }, CancellationToken.None);
                }
                catch (Exception cleanup)
                {
                    failures.Add(cleanup);
                }
                if (failure is SnapshotRetry && failures.Count == 1)
                {
                    // A targeted settlement established progress; only unclassified
                    // validation failures retain the bounded retry backoff.
                    if (failure is not SnapshotRetry { Pending: not null })
                    {
                        await Task.Delay(SnapshotRetryDelay, deadline.Token);
                    }

                    continue;
                }
                throw new AggregateException("The consistent partition read did not complete.", failures);
            }
        }
    }

    private const int MaximumSnapshotConcurrency = 8;

    internal static async Task RunSnapshotRoundAsync<T>(IEnumerable<T> items,
        Func<T, CancellationToken, ValueTask> action, CancellationToken token)
    {
        var failures = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        try
        {
            await Parallel.ForEachAsync(items, new ParallelOptions
            {
                MaxDegreeOfParallelism = MaximumSnapshotConcurrency,
                CancellationToken = token
            }, async (item, cancellation) =>
            {
                // A rejected sibling must not cancel an accepted but unanswered pin.
                // Collect failures, then join every started operation before rollback.
                try
                {
                    await action(item, cancellation);
                }
                catch (Exception failure)
                {
                    failures.Enqueue(failure);
                }
            });
        }
        catch (Exception failure)
        {
            failures.Enqueue(failure);
        }
        if (!failures.IsEmpty)
        {
            throw new AggregateException("The snapshot round did not complete.", failures);
        }
    }

    private static async ValueTask<(T Value, DatabasePositionVector Position)> ExecuteTimestampAsync<T>(
        IReadOnlyList<IPartitionReadParticipant> owners, DatabaseSharedTimestampService shared,
        Func<PartitionRemoteRead, CancellationToken, ValueTask<T>> projection, DatabasePositionVector? minimum, CancellationToken token)
    {
        if (owners.Any(owner => owner.SharedTimestamps?.Identity != shared.Identity))
        {
            throw new InvalidDataException("All read participants must share one logical clock.");
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(PartitionReadCuts.MaximumDuration);
        var cuts = new Dictionary<IPartitionReadParticipant, PartitionReadCut>();
        var attempted = new List<(IPartitionReadParticipant Owner, Guid Id)>();
        var id = Guid.NewGuid();
        Exception? primary = null;
        try
        {
            var read = await shared.ReadAsync(id, DateTimeOffset.UtcNow + PartitionReadCuts.MaximumDuration,
                minimum?.Positions.ToDictionary(p => PartitionOwnedNode.TimestampParticipant(p.Identity), p => p.Version.Value), deadline.Token);
            foreach (var owner in owners)
            {
                var cutId = Guid.NewGuid();
                attempted.Add((owner, cutId));
                cuts.Add(owner, await owner.AcquireTimestampCutAsync(cutId, read, deadline.Token));
            }
            var selected = new PartitionRemoteRead(cuts);
            var value = await projection(selected, deadline.Token);
            // Independent releases may overlap; ordinary success still joins every completion.
            using (DistributedReadTelemetry.Start("partition.read.release"))
            {
                await RunSnapshotRoundAsync(cuts, (pair, cancellation) => pair.Key.CompleteCutAsync(pair.Value, cancellation), deadline.Token);
            }

            return (value, selected.Position);
        }
        catch (Exception failure)
        {
            primary = failure;
            throw;
        }
        finally
        {
            // An unknown acquisition reply still owns a bounded server pin.
            var failures = new List<Exception>();
            foreach (var pair in attempted)
            {
                try
                {
                    await pair.Owner.AbandonCutAsync(pair.Id, CancellationToken.None);
                }
                catch (Exception e)
                {
                    failures.Add(e);
                }
            }

            try
            {
                await shared.ReleaseAsync(id, CancellationToken.None);
            }
            catch (Exception e)
            {
                failures.Add(e);
            }
            if (failures.Count != 0)
            {
                if (primary is not null)
                {
                    failures.Insert(0, primary);
                }

                throw new AggregateException("Timestamp read cleanup failed.", failures);
            }
        }
    }

    private static readonly TimeSpan SnapshotRetryDelay = TimeSpan.FromMilliseconds(1);

    private sealed class SnapshotRetry(IReadOnlyDictionary<IPartitionReadParticipant, Guid[]>? pending = null) : Exception
    {
        internal IReadOnlyDictionary<IPartitionReadParticipant, Guid[]>? Pending { get; } = pending;
    }
}
