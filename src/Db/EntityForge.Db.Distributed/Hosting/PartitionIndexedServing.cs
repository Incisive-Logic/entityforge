using EntityForge.Db.Gql;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using EntityForge.Db.Durability;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Kernel;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Distributed.Hosting;

// A bounded covering serving replica at the query home, not a static directory.
// Source-native durability remains authoritative. Restart rebuilds an explicit
// baseline; a missing feed prefix cannot produce a negative index answer.
internal sealed class PartitionIndexedServing : IAsyncDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(2);
    private static readonly TimeSpan HistoryWindow = TimeSpan.FromSeconds(10);
    private readonly object _sync = new();
    private readonly Func<IReadOnlyDictionary<string, DatabaseTimestampServingCursor>, DatabasePositionVector?, CancellationToken,
        ValueTask<(PartitionServingResponse[] Value, DatabasePositionVector Position)>> _pull;

    private readonly DistributedRoutingBinding _routing;
    private readonly string[] _indexes;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _demand = new(0, 1);
    private readonly bool _demandDriven;
    private readonly Dictionary<string, DatabaseTimestampServingCursor> _cursors = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, DurableTimestampState> _readers = [];
    private TaskCompletionSource _changed = NewSignal();

    private sealed record ServingEffect(string Source, GraphCommittedEffect[] Effects);

    private sealed record ServingState(ImmutableDictionary<string, GraphReadView> Owners)
    {
        internal GraphReadView? Projection;
    }

    private DatabaseLogicalHistory<ServingState, ServingEffect>? _history;
    private DurableTimestampState? _complete;
    private PartitionServingResponse[] _sources = [];
    private DatabasePositionVector? _position;
    private DatabasePositionVector? _minimum;
    private Exception? _failure;
    private bool _closed;
    private TaskCompletionSource? _drained;
    private readonly Task _worker;

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal PartitionIndexedServing(DistributedRoutingBinding routing, string[] indexes,
        Func<IReadOnlyDictionary<string, DatabaseTimestampServingCursor>, DatabasePositionVector?, CancellationToken,
            ValueTask<(PartitionServingResponse[] Value, DatabasePositionVector Position)>> pull, bool demandDriven = false)
    {
        if (indexes.Length is < 1 or > 32 || indexes.Any(string.IsNullOrWhiteSpace) || indexes.Distinct(StringComparer.Ordinal).Count() != indexes.Length)
        {
            throw new ArgumentException("Serving indexes must be explicitly declared and bounded.");
        }

        _routing = routing;
        _indexes = indexes.ToArray();
        _pull = pull;
        _demandDriven = demandDriven;
        // Background progress belongs to the serving replica, not forever to
        // whichever public query happened to instantiate it.
        var parent = Activity.Current;
        try
        {
            Activity.Current = null;
            _worker = RunAsync();
        }
        finally
        {
            Activity.Current = parent;
        }
    }

    private GraphReadView Index(GraphReadView view)
    {
        if (view.Vertices.Count + view.Edges.Count > PartitionQueryLimits.MaximumFacts)
        {
            throw new InvalidOperationException("Serving projection exceeds its fact bound.");
        }

        foreach (string property in _indexes)
        {
            view.DeclareServingPropertyIndex(property);
        }

        return view;
    }

    private static GraphEdge CompleteEdge(GraphEdge edge) => new(edge.Id, edge.From, edge.To, edge.Label, edge.Properties,
        GraphEdgeStorageRole.Complete | (edge.StorageRole & GraphEdgeStorageRole.ReferenceProjection));

    private ServingState Baseline(PartitionServingResponse[] sources)
    {
        var owners = ImmutableDictionary.CreateBuilder<string, GraphReadView>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            var vertices = ImmutableDictionary.CreateBuilder<GraphVertexId, GraphVertex>();
            var edges = ImmutableDictionary.CreateBuilder<GraphEdgeId, GraphEdge>();
            var value = source.Baseline ?? throw new InvalidDataException("Incomplete serving baseline.");
            foreach (var item in value.GetProperty("vertices").EnumerateArray())
            {
                var id = new GraphVertexId(item.GetProperty("id").GetProperty("value").GetGuid());
                if (_routing.RouteGraphVertex(id.Value) != source.Claim.Shard || vertices.ContainsKey(id))
                {
                    throw new InvalidDataException("Serving vertex ownership differs.");
                }

                vertices.Add(id, GraphWireJson.Vertex(item, id));
            }
            foreach (var item in value.GetProperty("edges").EnumerateArray())
            {
                var id = new GraphEdgeId(item.GetProperty("id").GetProperty("value").GetGuid());
                var decoded = GraphWireJson.Edge(item, id);
                if (source.EdgeRoles is null || !source.EdgeRoles.TryGetValue(id.Value, out byte role))
                {
                    throw new InvalidDataException("Serving baseline lacks physical edge role evidence.");
                }

                var edge = new GraphEdge(decoded.Id, decoded.From, decoded.To, decoded.Label, decoded.Properties, (GraphEdgeStorageRole)role);
                if (edges.ContainsKey(id))
                {
                    throw new InvalidDataException("Serving edge ownership differs.");
                }

                edges.Add(id, edge);
            }
            if (source.EdgeRoles is null || source.EdgeRoles.Count != edges.Count)
            {
                throw new InvalidDataException("Serving baseline edge role set differs.");
            }

            owners.Add(source.Claim.Shard.Value, GraphReadView.Build(vertices.ToImmutable(), edges.ToImmutable()));
        }
        return new(owners.ToImmutable());
    }

    private GraphReadView Projection(ServingState state)
    {
        if (state.Projection is { } cached)
        {
            return cached;
        }

        var vertices = ImmutableDictionary.CreateBuilder<GraphVertexId, GraphVertex>();
        var edges = ImmutableDictionary.CreateBuilder<GraphEdgeId, GraphEdge>();
        foreach (var pair in state.Owners)
        {
            foreach (var vertex in pair.Value.Vertices.Values)
            {
                if (_routing.RouteGraphVertex(vertex.Id.Value).Value != pair.Key || !vertices.TryAdd(vertex.Id, vertex))
                {
                    throw new InvalidDataException("Serving vertex owner differs.");
                }
            }

            foreach (var edge in pair.Value.Edges.Values)
            {
                var expected = GraphEdgeStorageRole.None;
                if (_routing.RouteGraphEdge(edge.Id.Value).Value == pair.Key)
                {
                    expected |= GraphEdgeStorageRole.Authoritative;
                }

                if (_routing.RouteGraphVertex(edge.From.Value).Value == pair.Key)
                {
                    expected |= GraphEdgeStorageRole.OutgoingAdjacency;
                }

                if (_routing.RouteGraphVertex(edge.To.Value).Value == pair.Key)
                {
                    expected |= GraphEdgeStorageRole.IncomingAdjacency;
                }

                if ((edge.StorageRole & GraphEdgeStorageRole.Complete) != expected ||
                    (edge.StorageRole & ~GraphEdgeStorageRole.Supported) != 0 || expected == GraphEdgeStorageRole.None)
                {
                    throw new InvalidDataException($"Serving edge {edge.Id} role {edge.StorageRole} differs from expected {expected} on owner {pair.Key}.");
                }

                if (edge.StorageRole.HasFlag(GraphEdgeStorageRole.Authoritative) && !edges.TryAdd(edge.Id, CompleteEdge(edge)))
                {
                    throw new InvalidDataException("Duplicate authoritative serving edge.");
                }
            }
        }
        foreach (var pair in state.Owners)
        {
            foreach (var replica in pair.Value.Edges.Values)
            {
                if (!edges.TryGetValue(replica.Id, out var authoritative) || replica.From != authoritative.From ||
                    replica.To != authoritative.To || replica.Label != authoritative.Label ||
                    (replica.StorageRole & GraphEdgeStorageRole.ReferenceProjection) !=
                    (authoritative.StorageRole & GraphEdgeStorageRole.ReferenceProjection))
                {
                    throw new InvalidDataException("Serving adjacency differs from authoritative edge state.");
                }
            }
        }

        foreach (var edge in edges.Values)
        {
            if (!state.Owners[_routing.RouteGraphVertex(edge.From.Value).Value].Edges.ContainsKey(edge.Id) ||
                !state.Owners[_routing.RouteGraphVertex(edge.To.Value).Value].Edges.ContainsKey(edge.Id))
            {
                throw new InvalidDataException("Serving edge is missing an adjacency obligation.");
            }
        }
        return state.Projection = Index(GraphReadView.Build(vertices.ToImmutable(), edges.ToImmutable()));
    }

    private GraphCommittedEffect[] Effects(PartitionServingResponse source, DatabaseTimestampServingChange change)
    {
        if (change.Effects is not { } payload)
        {
            return [];
        }

        if (payload.CodecId != GraphServingEffectsCodec.CodecId || payload.CodecVersion != GraphServingEffectsCodec.CodecVersion)
        {
            throw new NotSupportedException("Serving projection cannot interpret this effect codec.");
        }

        var result = new List<GraphCommittedEffect>();
        foreach (var effect in GraphServingEffectsCodec.Decode(payload.Content.Span))
        {
            GraphVertexId? vertex = effect switch
            {
                GraphVertexCreated e => e.Vertex.Id,
                GraphVertexDeleted e => e.Vertex.Id,
                GraphVertexPropertyChanged e => e.Vertex,
                GraphVertexLabelsChanged e => e.Vertex,
                _ => null
            };
            if (vertex is { } v && _routing.RouteGraphVertex(v.Value) != source.Claim.Shard)
            {
                throw new InvalidDataException("Serving effect vertex ownership differs.");
            }

            result.Add(effect);
        }
        return result.ToArray();
    }

    private async Task RunAsync()
    {
        try
        {
            while (true)
            {
                using (var progress = DistributedReadTelemetry.Start("partition.serving.progress"))
                {
                    DatabasePositionVector? minimum;
                    lock (_sync)
                    {
                        minimum = _minimum;
                    }

                    var batch = await _pull(_cursors, minimum, _stop.Token);
                    progress?.SetTag("sources", batch.Value.Length);
                    progress?.SetTag("changes", batch.Value.Sum(s => s.Changes.Length));
                    progress?.SetTag("effect-bytes", batch.Value.Sum(s => s.Changes.Sum(c => c.Effects?.Content.Length ?? 0)));
                    lock (_sync)
                    {
                        if (batch.Value.Length == 0)
                        {
                            throw new InvalidDataException("Serving source set is empty.");
                        }

                        var time = batch.Value[0].CompleteThrough;
                        if (batch.Value.Length != _routing.Shards.Count || batch.Value.Any(s => s.CompleteThrough != time || !_routing.Shards.Contains(s.Claim.Shard)) ||
                            batch.Value.Select(s => s.Claim.Shard).Distinct().Count() != batch.Value.Length ||
                            _complete is not null && time.CompareTo(_complete) <= 0)
                        {
                            throw new InvalidDataException("Serving source set or progress differs.");
                        }

                        if (_history is null)
                        {
                            _history = new(Baseline(batch.Value), time, (state, effect) => new(state.Owners.SetItem(effect.Source,
                                GraphProfileRuntime.ApplyServingEffects(state.Owners[effect.Source], effect.Effects))));
                        }

                        foreach (var source in batch.Value)
                        {
                            _cursors.TryGetValue(source.Claim.Shard.Value, out var previous);
                            if (previous is not null && (source.Baseline is not null || source.Cursor.Source != previous.Source || source.Cursor.Sequence < previous.Sequence))
                            {
                                throw new InvalidDataException("Serving source restarted or regressed; reopen the projection.");
                            }

                            long next = previous?.Sequence ?? 0;
                            foreach (var change in source.Changes)
                            {
                                if (previous is not null && change.Sequence != next + 1 || previous is null && (change.Sequence <= next || change.Time.CompareTo(time) <= 0) ||
                                    change.Sequence > source.Cursor.Sequence || change.Time.Domain != time.Domain || !change.Time.Mvcc || change.Operation == Guid.Empty)
                                {
                                    throw new InvalidDataException("Serving change prefix or clock association differs.");
                                }

                                next = change.Sequence;
                                byte[] association = SHA256.HashData(Encoding.UTF8.GetBytes($"{source.Cursor.Source:N}/{change.Sequence}/{change.Operation:N}"));
                                Guid id = new(association.AsSpan(0, 16));
                                _history.Reserve(id, checked(change.Mutation.Length + (change.Effects?.Content.Length ?? 0)));
                                _history.Publish(id, change.Time, new(source.Claim.Shard.Value, Effects(source, change)));
                            }
                            if (previous is not null && next != source.Cursor.Sequence)
                            {
                                throw new InvalidDataException("Serving response omitted a committed change.");
                            }

                            _cursors[source.Claim.Shard.Value] = source.Cursor;
                        }
                        _complete = time;
                        _sources = batch.Value;
                        _position = batch.Position;
                        var floor = new DurableTimestampState(time.Domain, Math.Max(0, time.Physical - HistoryWindow.Ticks), 0, time.ReservedReadThrough, true);
                        foreach (var reader in _readers.Values)
                        {
                            if (reader.CompareTo(floor) < 0)
                            {
                                floor = reader;
                            }
                        }

                        if (floor.CompareTo(_history.Floor) > 0)
                        {
                            _history.Trim(floor);
                        }

                        _changed.TrySetResult();
                        _changed = NewSignal();
                    }
                }
                // A request queued before source dispatch can share its fresh
                // barrier. Later arrivals signal the next round, never join an
                // already-started proof. No idle polling in this experimental arm.
                if (_demandDriven)
                {
                    await _demand.WaitAsync(_stop.Token);
                }
                else
                {
                    await Task.Delay(PollInterval, _stop.Token);
                }
            }
        }
        catch (Exception failure)
        {
            lock (_sync)
            {
                _failure = failure;
                _changed.TrySetResult();
            }
        }
    }

    internal async ValueTask ReadyAsync(CancellationToken token)
    {
        while (true)
        {
            Task wait;
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_closed, this);
                if (_failure is not null)
                {
                    throw new InvalidOperationException("Serving baseline failed.", _failure);
                }

                if (_complete is not null)
                {
                    return;
                }

                wait = _changed.Task;
            }
            await wait.WaitAsync(token);
        }
    }

    internal async ValueTask<Lease> ReadAsync(DurableTimestampState time, DatabasePositionVector? minimum, CancellationToken token)
    {
        using var waitForFreshness = DistributedReadTelemetry.Start("partition.serving.fresh-read");
        while (true)
        {
            Task wait;
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_closed, this);
                if (_failure is not null)
                {
                    throw new InvalidOperationException("Indexed serving requires a fresh baseline.", _failure);
                }

                if (minimum is not null && (_position is null || !_position.Covers(minimum)))
                {
                    // Refresh protected native positions before selecting a new
                    // complete logical read; a counter is not a snapshot proof.
                    _minimum = _minimum is null ? minimum : new(_minimum.Positions.Concat(minimum.Positions)
                        .GroupBy(p => p.Identity.Partition, StringComparer.Ordinal).Select(group =>
                        {
                            if (group.Select(p => p.Identity).Distinct().Count() != 1)
                            {
                                throw new InvalidDataException("Serving minimum belongs to another physical partition.");
                            }

                            return group.MaxBy(p => p.Version.Value)!;
                        }).ToArray());
                }
                else if (_complete is { } complete && complete.CompareTo(time) >= 0)
                {
                    if (_readers.Count >= PartitionOwnedExecution.DefaultMaximumJobs)
                    {
                        throw new InvalidOperationException("Serving query capacity is full.");
                    }

                    var id = Guid.NewGuid();
                    _readers.Add(id, time);
                    try
                    {
                        return new(this, id, Projection(_history!.Read(time)), _position!, _sources);
                    }
                    catch
                    {
                        _readers.Remove(id);
                        throw;
                    }
                }
                wait = _changed.Task;
                if (_demandDriven && _demand.CurrentCount == 0)
                {
                    _demand.Release();
                }
            }
            await wait.WaitAsync(token);
        }
    }

    internal sealed class Lease(PartitionIndexedServing owner, Guid id, GraphReadView view,
        DatabasePositionVector position, PartitionServingResponse[] sources) : IDisposable
    {
        internal GraphReadView View { get; } = view;

        internal DatabasePositionVector Position { get; } = position;

        internal PartitionServingResponse[] Sources { get; } = sources;

        public void Dispose()
        {
            lock (owner._sync)
            {
                owner._readers.Remove(id);
                if (owner._readers.Count == 0)
                {
                    owner._drained?.TrySetResult();
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task drained;
        lock (_sync)
        {
            _closed = true;
            drained = _readers.Count == 0 ? Task.CompletedTask : (_drained ??= NewSignal()).Task;
        }
        _stop.Cancel();
        await _worker;
        await drained;
        _stop.Dispose();
        _demand.Dispose();
    }
}

internal sealed partial class PartitionApplicationClient
{
    internal string[]? IndexedServingProperties
    {
        get; init;
    }

    internal bool IndexedServingDemandDriven
    {
        get; init;
    }

    private PartitionIndexedServing? _indexedServing;
    private readonly object _indexedServingSync = new();

    private async ValueTask<PartitionQueryResult> ExecuteIndexedServingAsync(PartitionQueryRequest request,
        CompiledGraphGqlQuery compiled, CancellationToken token, Action<byte[]>? ownResponse)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(PartitionReadCuts.MaximumDuration);
        token = deadline.Token;
        var home = QueryHome ?? (QueryHomeProvider is { } provider ? await provider(token) : null)
            ?? throw new NotSupportedException("Indexed serving requires the qualified query-home authority.");
        // A caller's unavailable or foreign minimum must not become a shared
        // progress-worker obligation. Use a separate owned admission lifetime:
        // cached epoch refresh could otherwise publish this failure to readers
        // that never requested the minimum. Select the query time afterwards.
        if (request.MinimumPosition is not null)
        {
            await PartitionRemoteRead.ExecuteProtectedAsync(_owners.Values.Cast<IPartitionReadParticipant>().ToArray(),
                _routing, static (_, _) => ValueTask.FromResult(true), request.MinimumPosition, token);
        }

        PartitionIndexedServing serving;
        lock (_indexedServingSync)
        {
            serving = _indexedServing ??= new(_routing, IndexedServingProperties!,
            (cursors, minimum, cancellation) => ExecuteReadEpochAsync((read, ct) => read.ReadServingFeedsAsync(cursors, home, ct), minimum, cancellation),
            IndexedServingDemandDriven);
        }

        await serving.ReadyAsync(token);
        var clock = PartitionTimestampClock.Require(_timestampClock);
        var time = PartitionTimestampClock.Read(clock.Domain, clock);
        using var lease = await serving.ReadAsync(time, request.MinimumPosition, token);
        var view = lease.View;
        var evidenceVersion = lease.Position.Positions.Count == 1 ? lease.Position.Positions[0].Version : default;
        ValueTask<EntityForge.Query.Results.QueryResultEnvelope> Execute(CompiledGraphGqlQuery branch) =>
            new BoundedGraphPatternExecutor(null, capturedView: view).ExecuteCapturedResultAsync(
                branch.Query, new(evidenceVersion, view, view.StatisticsFor(evidenceVersion)), token,
                branch.Projection is null ? null : property => GraphGqlPropertyProjectionShaper.Project(branch.Projection, property), request.IncludeSupportingGraph, branch.StartCandidates);
        var envelope = await compiled.ExecuteAsync(Execute, token);
        var value = GraphOutput(envelope, compiled, request.IncludeSupportingGraph, request.Stream, token, shaped: true);
        await home.ValidateAsync(lease.Sources.Select(source => new PartitionQueryPendingCompletion(source.Request,
            source.Cut, "indexed-serving", 1, source.Claim, source.Authority, time)), token);
        var response = new PartitionQueryResult(Generation, Profile, request.Language, lease.Position, value,
            Statistics: new("graph-indexed-serving", 0, 0, view.Vertices.Count + view.Edges.Count));
        PartitionProjectionValue.Measure(response, OutputBound(request.Stream));
        CompletePublicResponse(response, request.Stream, ownResponse);
        return response;
    }

    internal async ValueTask CloseIndexedServingAsync()
    {
        PartitionIndexedServing? serving;
        lock (_indexedServingSync)
        {
            serving = _indexedServing;
            _indexedServing = null;
        }
        if (serving is not null)
        {
            await serving.DisposeAsync();
        }
    }
}
