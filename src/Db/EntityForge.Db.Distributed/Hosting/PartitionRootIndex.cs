using EntityForge.Db.Query.Adapters;
using EntityForge.Db.Gql;
using System.Text.Json;
using EntityForge.Db.Kernel;
using EntityForge.Db.Durability;
using EntityForge.Db.Distributed.Sharding;
using EntityForge.Db.Graph;
using EntityForge.Db.Rdf;
using EntityForge.Db.Semantic;

namespace EntityForge.Db.Distributed.Hosting;

internal sealed record PartitionRootIndexDemand(Guid Id, DatabaseRootIndexColumn Column, string Value, PartitionQueryExchangeRequest Selection)
{
    // Local scheduling metadata; each cohort must honor every reader's admission.
    internal DurableTimestampState? AdmissionTime
    {
        get; init;
    }
}

internal sealed record PartitionRootIndexRequest(Guid Id, DatabaseRootIndexColumn Column, PartitionRootIndexDemand[]? Lookups = null)
{
    private sealed record FingerprintCache(Guid Id, DatabaseRootIndexColumn Column, PartitionRootIndexDemand[]? Lookups, string Value);

    private FingerprintCache? _fingerprint;

    [System.Text.Json.Serialization.JsonIgnore]
    internal string Fingerprint
    {
        get
        {
            if (_fingerprint is { } cached && cached.Id == Id && cached.Column == Column && ReferenceEquals(cached.Lookups, Lookups))
            {
                return cached.Value;
            }

            string value = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(this, PartitionQueryJson.Options)));
            _fingerprint = new(Id, Column, Lookups, value);
            return value;
        }
    }

    [System.Text.Json.Serialization.JsonIgnore]
    internal bool OwnedLocalPages
    {
        get; init;
    }
}

internal sealed record PartitionRootIndexResponse(DatabaseRootIndexMembership Membership,
    PartitionQueryPendingCompletion Pending, JsonElement? Catalog = null, Dictionary<Guid, byte[]>? Pages = null, Guid[]? Deferred = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public Dictionary<Guid, PartitionProjectionValue>? NativePages
    {
        get; init;
    }

    internal IEnumerable<Guid> PageIdentities => (Pages?.Keys.AsEnumerable() ?? []).Concat(NativePages?.Keys.AsEnumerable() ?? []);
}

internal sealed record PartitionRootIndexRound(DurableTimestampState Time, PartitionRootIndexResponse[] Owners);

internal static class PartitionRootIndexRouting
{
    // Own and validate the entire cohort before any admission await. Later page
    // execution must use this same detached request.
    internal static PartitionRootIndexRequest Capture(PartitionRootIndexRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (request.Lookups is not { } source)
        {
            return request;
        }

        if (source.Length is < 1 or > 32)
        {
            throw new InvalidDataException("Root lookup batch is invalid.");
        }

        var lookups = new PartitionRootIndexDemand[source.Length];
        var identities = new HashSet<Guid>();
        for (int i = 0; i < lookups.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            var lookup = source[i];
            if (lookup.Id == Guid.Empty || !identities.Add(lookup.Id) || lookup.Column != request.Column)
            {
                throw new InvalidDataException("Root lookup batch is invalid.");
            }

            var owned = PartitionQueryOwnedSelection.Capture(lookup.Selection);
            if (owned.Value.Offset != 0 || owned.Value.CompleteQuery ||
                Probe(owned.Value) is not { } probe || probe.Column != request.Column || probe.Value != lookup.Value)
            {
                throw new InvalidDataException("Root lookup interpretation differs.");
            }

            lookups[i] = lookup with
            {
                Selection = owned.Value
            };
        }
        return request with
        {
            Lookups = lookups
        };
    }

    internal static string Fingerprint(PartitionRootIndexRequest request) => request.Fingerprint;

    internal static (DatabaseRootIndexColumn Column, string Value)? Probe(PartitionQueryExchangeRequest selection)
    {
        if (selection.Language == "gql" && selection.GraphFrontier is { Stage: 0 } && selection.Statement is { } statement)
        {
            var query = PartitionQueryCompilation.CompileGraph(statement).Query;
            return BoundedGraphPatternExecutor.SupportsFragments(query) || BoundedGraphPatternExecutor.SupportsFrontierExchange(query)
                ? BoundedGraphPatternExecutor.RootIndexProbe(query) : null;
        }
        if (selection.ProfileFragments && selection.Language == "sparql" && selection.Statement is { } sparql)
        {
            return RdfSparql.RootIndexProbe(sparql);
        }

        if (selection.ProfileFragments && selection.Language == "entity" && PartitionQuerySelection.IsDirectScalar(selection.EntityQuery))
        {
            var query = new PortableEntityQueryRequestCompiler().Compile(selection.EntityQuery!).Query;
            if (query.ConceptMatchMode == EntityForge.Query.Querying.EntityConceptMatchMode.Direct)
            {
                return (new(SemanticDurabilityRuntime.ConceptRootIndex, ""), query.RootConcept.Identifier.AbsoluteUri);
            }
        }
        return null;
    }
}

internal sealed partial class PartitionReadCuts
{
    private sealed partial class Entry
    {
        private async ValueTask<PartitionProjectionValue> ReadRootIndexAsync(PartitionReadCut cut, PartitionReadQuery query, CancellationToken token)
        {
            Require(cut);
            if (!IsHistoryProtection || TimestampProtection is null || query.ReadTime is not { Mvcc: true } time ||
                time.Domain != cut.ClockDomain || query.RootIndex is not { Id: var id, Column: var column } || id == Guid.Empty ||
                query.Identity != Guid.Empty || query.Subject is not null || query.Label is not null || query.Batch is not null ||
                query.Projection is not null || query.Source is not null || query.SnapshotPrepared is not null ||
                query.QueryExchange is not null || query.QueryJob is not null || query.Serving is not null || _node.QueryHomeBinding is not { } home)
            {
                throw new InvalidDataException("Invalid root-index snapshot association.");
            }

            var request = PartitionRootIndexRouting.Capture(query.RootIndex, token);
            return await UseEpochAsync(cut, async cancellation => await _node.Authority.ExecutePendingQueryAsync(async () =>
            {
                using var capacity = _node.EnterQueryJob();
                var snapshot = await _node.Kernel.PartitionTransactions.CaptureRootIndexesAsync(TimestampProtection, time, [column], cancellation);
                if (snapshot.Memberships.Length != 1 || snapshot.Memberships[0].Column != column)
                {
                    throw new InvalidDataException("Native root-index interpretation differs.");
                }

                var pending = new PartitionQueryPendingCompletion(id, cut.Reference, PartitionRootIndexRouting.Fingerprint(request), 1,
                    _node.CurrentLease.Placement.Claim, home, time);
                JsonElement? catalog = snapshot.Interpretation is null ? null : JsonSerializer.SerializeToElement(snapshot.Interpretation, PartitionQueryJson.Options);
                Dictionary<Guid, byte[]>? pages = null;
                var deferred = new List<Guid>();
                Dictionary<Guid, PartitionProjectionValue>? nativePages = request.OwnedLocalPages ? new() : null;
                if (request.Lookups is { } lookups)
                {
                    if (lookups.Length is < 1 or > 32 || lookups.Select(lookup => lookup.Id).Distinct().Count() != lookups.Length)
                    {
                        throw new InvalidDataException("Root lookup batch is invalid.");
                    }

                    pages = new();
                    long responseBytes = PartitionProjectionValue.Measure(new PartitionRootIndexResponse(snapshot.Memberships[0], pending, catalog, pages, []),
                        PartitionParticipantWire.MaximumResponseBytes, PartitionQueryJson.Options);
                    using var context = _node.EnterQueryTimestamp(snapshot.View!, time);
                    foreach (var lookup in lookups)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if (lookup.Id == Guid.Empty || lookup.Column != column || lookup.Selection.Offset != 0 || lookup.Selection.CompleteQuery ||
                            PartitionRootIndexRouting.Probe(lookup.Selection) is not { } probe || probe.Column != column || probe.Value != lookup.Value)
                        {
                            throw new InvalidDataException("Root lookup interpretation differs.");
                        }

                        if (!snapshot.Memberships[0].MayContain(lookup.Value))
                        {
                            continue;
                        }
                        // Each lookup has its own interpretation and workspace: a
                        // cohort shares the cut, never another query's root relation.
                        var owned = PartitionQueryOwnedSelection.Capture(lookup.Selection);
                        using var workspace = new PartitionQueryWorkspace(_node, new PartitionQueryInterpretation(owned));
                        using var workspaceContext = _node.EnterQueryWorkspace(workspace);
                        var value = await _node.ReadProjectionAsync(cut.Position.Version,
                            new("query-relation-page", QueryExchange: owned.Value), cancellation, nativeExclusion: false);
                        var control = new PartitionQueryOperatorWire.Control(_node.Domain.Identity, _node.TransactionAuthorityIdentity ?? throw new InvalidDataException("No root-index authority."),
                            lookup.Id, cut.Reference, true, true, null);
                        var encoded = nativePages is null ? PartitionQueryOperatorWire.Encode(control, new(true, value, true)) : null;
                        // Base64 expansion plus dictionary framing is accounted
                        // before attaching a page. Large roots retain the exact
                        // timestamp and use ordinary bounded page execution.
                        const int PageFramingAllowance = 128;
                        long encodedBytes = encoded is not null ? checked(((encoded.LongLength + 2) / 3) * 4 + PageFramingAllowance)
                            : checked(PartitionProjectionValue.Measure(value.Native!, PartitionQueryLimits.ExchangePageBytes, PartitionQueryJson.Options) + PageFramingAllowance);
                        if (encodedBytes > PartitionParticipantWire.MaximumResponseBytes - responseBytes)
                        {
                            deferred.Add(lookup.Id);
                            responseBytes += PageFramingAllowance;
                        }
                        else
                        {
                            if (nativePages is not null)
                            {
                                nativePages.Add(lookup.Id, value);
                            }
                            else
                            {
                                pages.Add(lookup.Id, encoded!);
                            }

                            responseBytes += encodedBytes;
                        }
                    }
                }
                // Cohort execution keeps the index on its native owner. The home
                // needs matched lookup identities, not a copy of index bits.
                var membership = request.Lookups is null ? snapshot.Memberships[0] : new DatabaseRootIndexMembership(column, []);
                var response = new PartitionRootIndexResponse(membership, pending, catalog, pages, deferred.ToArray()) { NativePages = nativePages };
                PartitionProjectionValue.Measure(response, PartitionParticipantWire.MaximumResponseBytes, PartitionQueryJson.Options);
                return new PartitionProjectionValue(response, PartitionQueryJson.Options);
            }), token);
        }
    }
}

internal sealed partial class PartitionRemoteRead
{
    internal async ValueTask<PartitionRootIndexRound> ReadRootIndexesAsync(DatabaseRootIndexColumn column,
        PartitionQueryHomeAuthority home, CancellationToken token, PartitionRootIndexDemand[]? lookups = null)
    {
        var pairs = _cuts.OrderBy(pair => pair.Key.Domain.Partition.Value, StringComparer.Ordinal).ToArray();
        var replies = new PartitionRootIndexResponse[pairs.Length];
        await RunSnapshotRoundAsync(Enumerable.Range(0, pairs.Length), async (i, ct) =>
        {
            var pair = pairs[i];
            var request = new PartitionRootIndexRequest(Guid.NewGuid(), column, lookups);
            var fingerprint = PartitionRootIndexRouting.Fingerprint(request);
            PartitionRootIndexResponse response;
            if (pair.Key is PartitionOwnedNode local)
            {
                response = (await local.ReadCutProjectionAsync(pair.Value, new("root-index", ReadTime: _logicalTime,
                    RootIndex: request with
                    {
                        OwnedLocalPages = true
                    }), ct)).Native as PartitionRootIndexResponse
                    ?? throw new InvalidDataException("Missing owned root index.");
            }
            else if (pair.Key is HttpPartitionTransactionParticipant remote)
            {
                response = await remote.ReadRootIndexResponseAsync(pair.Value, new("root-index", ReadTime: _logicalTime, RootIndex: request), ct);
            }
            else
            {
                response = (await pair.Key.ReadCutValueAsync(pair.Value, new("root-index", ReadTime: _logicalTime, RootIndex: request), ct))
                    .Deserialize<PartitionRootIndexResponse>(PartitionQueryJson.Options) ?? throw new InvalidDataException("Missing root index.");
            }

            var pending = response.Pending;
            if (response.Membership.Column != column || response.Membership.Bits.Length != (lookups is null ? DatabaseRootIndexMembership.Bytes : 0) ||
                pending.Job != request.Id || pending.Selection != fingerprint || pending.Sequence != 1 || pending.ReadTime != _logicalTime ||
                pending.Cut != pair.Value.Reference || pending.Authority != home.Binding || pending.Claim.Shard != pair.Key.Domain.Partition ||
                pending.Claim.Generation.Value != pair.Key.Domain.Generation)
            {
                throw new InvalidDataException("Root-index response changed its snapshot, owner or interpretation.");
            }

            replies[i] = response;
            if (lookups is not null)
            {
                var allowed = lookups.Select(lookup => lookup.Id).ToHashSet();
                if (response.Pages is null || response.Deferred is null || response.Deferred.Distinct().Count() != response.Deferred.Length ||
                    response.PageIdentities.Distinct().Count() != response.PageIdentities.Count() ||
                    response.PageIdentities.Intersect(response.Deferred).Any() || response.PageIdentities.Concat(response.Deferred).Any(id => !allowed.Contains(id)))
                {
                    throw new InvalidDataException("Indexed root page set differs.");
                }

                if (response.NativePages?.Values.Any(page => page.Native is not PartitionQueryExchangePage { Result: null }) == true)
                {
                    throw new InvalidDataException("Invalid owned root page.");
                }

                foreach (var page in response.Pages)
                {
                    var decoded = PartitionQueryOperatorWire.Decode(page.Value);
                    if (decoded.Control.Target != pair.Key.Domain.Identity || decoded.Control.Authority != ((IPartitionTransactionParticipant)pair.Key).TransactionAuthorityIdentity ||
                        decoded.Control.Operation != page.Key || decoded.Control.Cut != pair.Value.Reference ||
                        decoded.Result is not { Valid: true, Completed: true, Value.Native: PartitionQueryExchangePage { Result: null } })
                    {
                        throw new InvalidDataException("Indexed root page association differs.");
                    }
                }
            }
        }, token);
        return new(_logicalTime!, replies);
    }
}

internal sealed partial class PartitionApplicationClient
{
    internal bool IndexDirectedRoots { get; init; } = true;

    private PartitionRootQueryQueue? _rootDirectory;
    private readonly object _rootDirectorySync = new();

    private (DatabaseRootIndexColumn Column, string Value)? RootIndexProbe(PartitionQueryRequest request,
        CompiledGraphGqlQuery? graph, CompiledPortableEntityQuery? semantic, bool profileFragments)
    {
        if (!IndexDirectedRoots || _routing.StorageLayout != DatabaseStorageLayout.Current || _owners.Count <= 1)
        {
            return null;
        }

        if (graph is { RequiresCompleteGraph: false } && (BoundedGraphPatternExecutor.SupportsFragments(graph.Query) || BoundedGraphPatternExecutor.SupportsFrontierExchange(graph.Query)))
        {
            return BoundedGraphPatternExecutor.RootIndexProbe(graph.Query);
        }

        if (profileFragments && Profile == DatabaseProfileId.Rdf)
        {
            return RdfSparql.RootIndexProbe(request.Statement!);
        }

        if (profileFragments && semantic?.Query.ConceptMatchMode == EntityForge.Query.Querying.EntityConceptMatchMode.Direct)
        {
            return (new(SemanticDurabilityRuntime.ConceptRootIndex, ""), semantic.Query.RootConcept.Identifier.AbsoluteUri);
        }

        return null;
    }

    private ValueTask<PartitionRootIndexRound> ReadRootDirectoryAsync(PartitionRootIndexDemand demand,
        PartitionQueryHomeAuthority home, CancellationToken token)
    {
        PartitionRootQueryQueue directory;
        lock (_rootDirectorySync)
        {
            directory = _rootDirectory ??= new(async (lookups, ct) =>
            (await ExecuteReadEpochAsync((read, cancellation) =>
                read.ForRootCohort(lookups).ReadRootIndexesAsync(lookups[0].Column, home, cancellation, lookups), null, ct, indexProgress: true)).Value);
        }
        // Only requests selected before dispatch share a timestamp. Later readers
        // get a new closed prefix, never a cached negative from an earlier query.
        return directory.ReadAsync(demand, token);
    }

    private async ValueTask CloseRootDirectoryAsync()
    {
        PartitionRootQueryQueue? directory;
        lock (_rootDirectorySync)
        {
            directory = _rootDirectory;
            _rootDirectory = null;
        }
        if (directory is not null)
        {
            await directory.DisposeAsync();
        }
    }
}

internal sealed partial class HttpPartitionTransactionParticipant
{
    internal async ValueTask<PartitionRootIndexResponse> ReadRootIndexResponseAsync(PartitionReadCut cut, PartitionReadQuery query, CancellationToken token)
    {
        using var trace = DistributedReadTelemetry.Start("partition.rpc");
        trace?.SetTag("entityforge.phase", "root-index");
        var request = new PartitionParticipantWire.Request(Domain.Identity, TransactionAuthorityIdentity, cut.Id, Cut: cut.Reference, Query: query);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(request, PartitionParticipantWire.Json);
        if (bytes.Length > PartitionParticipantWire.MaximumRequestBytes)
        {
            throw new InvalidDataException("Root request exceeds its bound.");
        }

        trace?.SetTag("entityforge.request_bytes", bytes.Length);
        var content = await ExchangeOwnedAsync("read-root-cut", bytes, token);
        trace?.SetTag("entityforge.response_bytes", content.Length);
        var control = PartitionRootIndexWire.Decode(content);
        if (control.Authority != TransactionAuthorityIdentity || control.Response.Pending.Job != query.RootIndex!.Id ||
            control.Response.Pending.Cut != cut.Reference || control.Response.Pending.Cut.Position.Identity != Domain.Identity)
        {
            throw new InvalidDataException("Root wire association differs.");
        }

        return control.Response;
    }
}
