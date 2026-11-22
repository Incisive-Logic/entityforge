using EntityForge.Db.Query.Adapters;
using EntityForge.Db.Gql;
using System.Text.Json;
using EntityForge.Db.Kernel;
using EntityForge.Query.Querying;
using System.Security.Cryptography;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using EntityForge.Db.Mapping;
using EntityForge.Values;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Distributed.Hosting;

internal sealed record PartitionQueryWrite(Guid Generation, DatabaseProfileId Profile, Guid Operation, string Language, string Statement);

internal static class PartitionQueryUpdate
{
    private static ReadOnlySpan<byte> Prefix => "EFQU3\n"u8;

    internal sealed record Fact(DatabasePartitionIdentity Owner, byte[] Relation);

    internal sealed record Evidence(DatabasePositionVector Position, Fact[] Facts, RdfMutation[]? RdfMutations = null, byte[]? GraphPlan = null);

    internal static bool HasValidStatement(PartitionQueryWrite request) =>
        request.Statement is { Length: <= PartitionQueryLimits.MaximumStatementCharacters } &&
        (request.Statement.Length != 0 || request.Profile == DatabaseProfileId.Rdf && request.Language == "sparql");

    internal static async ValueTask<Evidence> MaterializeAsync(PartitionQueryWrite request, Evidence evidence,
        DistributedRoutingBinding routing, CancellationToken token, RdfSparqlHttpResources? resources = null)
    {
        if (request.Profile == DatabaseProfileId.PropertyGraph && request.Language == "gql")
        {
            GraphGqlStandardSyntax.Validate(request.Statement);
            token.ThrowIfCancellationRequested();
            var (vertices, edges) = ReadGraphFacts(evidence, routing);
            var compiled = GraphGqlMutationCompiler.Compile(GraphGqlStandardSyntax.Prepare(request.Statement), transactionId: request.Operation);
            IReadOnlyList<GraphMutation> graphMutations = GraphGqlMutationCompiler.BindDirect(compiled);
            byte[] queryResult = GqlUpdateResultCodec.CaptureOmittedResult();
            bool omittedResult = GraphGqlStandardSyntax.OmitsResult(request.Statement);
            if (compiled.Program is { } program)
            {
                graphMutations = await GraphGqlMutationCompiler.BindProgramAsync(program, GraphReadView.Build(vertices, edges),
                    default, request.Operation, token, rows => queryResult = GqlUpdateResultCodec.CaptureResult(CompiledGraphGqlQuery.RenameOutput(rows, compiled.OutputNames), omittedResult)).ConfigureAwait(false);
            }
            else if (compiled.Match is not null)
            {
                var view = GraphReadView.Build(vertices, edges);
                var executor = new BoundedGraphPatternExecutor(null, capturedView: view);
                var matches = await compiled.Match.ExecuteAsync(plan => executor.ExecuteCapturedResultAsync(plan.Query,
                    new(default, view, view.StatisticsFor(default)), token, startCandidates: plan.StartCandidates), token).ConfigureAwait(false);
                queryResult = GqlUpdateResultCodec.CaptureResult(matches, omittedResult: true);
                graphMutations = compiled.RowMerge is null ? GraphGqlMutationCompiler.Bind(compiled, matches)
                    : GraphGqlMutationCompiler.BindRowMerge(compiled, matches, vertices.Values, token, edges.Values);
            }
            if (compiled.DetachDelete)
            {
                graphMutations = GraphGqlMutationCompiler.ExpandDetachDeletes(graphMutations, edges.Values);
            }
            GraphGqlMutationCompiler.ValidateNodeDeletes(graphMutations, edges.Values);
            return evidence with
            {
                GraphPlan = GraphTransactionCodec.Encode(new(request.Operation, graphMutations)
                {
                    QueryResult = queryResult
                })
            };
        }
        if (request.Profile != DatabaseProfileId.Rdf || request.Language != "sparql")
        {
            return evidence;
        }
        var quads = new HashSet<RdfQuad>();
        foreach (var fact in evidence.Facts)
        {
            token.ThrowIfCancellationRequested();
            using var document = JsonDocument.Parse(fact.Relation);
            foreach (var item in document.RootElement.GetProperty("quads").EnumerateArray())
            {
                var quad = RdfWireJson.Quad(item);
                if (routing.Route(DistributedDatabaseProfile.Rdf, ProfilePartitionRouting.RdfOwner(quad.Subject)).Value != fact.Owner.Partition || !quads.Add(quad))
                {
                    throw new InvalidDataException("Query RDF evidence ownership differs.");
                }
                if (quads.Count > PartitionQueryLimits.MaximumFacts)
                {
                    throw new InvalidDataException("Query update exceeds its fact bound.");
                }
            }
        }
        IReadOnlyList<RdfMutation> mutations;
        string scope = RdfSparqlUpdate.NewBlankNodeScope();
        try
        {
            mutations = RdfSparqlUpdate.Parse(request.Statement, scope);
        }
        catch (NotSupportedException)
        {
            mutations = await RdfSparqlUpdate.ParseWhereAsync(quads.ToArray(), request.Statement, scope, resources, token).ConfigureAwait(false);
        }
        // Persist the one global evaluation before owner selection. Replaying
        // compilation must not regenerate UUIDs, blank nodes, RAND or NOW values.
        return evidence with
        {
            RdfMutations = mutations.ToArray()
        };
    }

    internal static byte[] Encode(PartitionQueryWrite request)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(request, PartitionParticipantWire.Json);
        var bytes = new byte[Prefix.Length + json.Length];
        Prefix.CopyTo(bytes);
        json.CopyTo(bytes, Prefix.Length);
        return bytes;
    }

    internal static bool Is(PartitionApplicationBinding application) => application.Requests.Count == 1 && application.Requests[0].Span.StartsWith(Prefix);

    internal static PartitionQueryWrite Request(PartitionApplicationBinding application) =>
        JsonSerializer.Deserialize<PartitionQueryWrite>(application.Requests[0].Span[Prefix.Length..], PartitionParticipantWire.Json)
            ?? throw new InvalidDataException("Missing selected query update.");

    internal static Evidence Read(PartitionApplicationBinding application, DistributedRoutingBinding routing)
    {
        if (!Is(application) || application.NativeDescriptor is not null)
        {
            throw new InvalidDataException("Not a selected query update.");
        }

        var evidence = JsonSerializer.Deserialize<Evidence>(application.PlanningEvidence.Span, PartitionParticipantWire.Json)
            ?? throw new InvalidDataException("Missing query update evidence.");
        if (evidence.Position is null || evidence.Facts is null || evidence.Facts.Length != routing.Shards.Count ||
            !evidence.Facts.Select(f => f.Owner).ToHashSet().SetEquals(application.Owners) ||
            !evidence.Position.Positions.Select(p => p.Identity).ToHashSet().SetEquals(application.Owners) ||
            !application.Owners.Select(o => o.Partition).ToHashSet().SetEquals(routing.Shards.Select(s => s.Value)))
        {
            throw new InvalidDataException("Query updates require the complete unique routing cut.");
        }

        return evidence;
    }

    private static (Dictionary<GraphVertexId, GraphVertex> Vertices, Dictionary<GraphEdgeId, GraphEdge> Edges)
        ReadGraphFacts(Evidence evidence, DistributedRoutingBinding routing)
    {
        var vertices = new Dictionary<GraphVertexId, GraphVertex>();
        var edges = new Dictionary<GraphEdgeId, GraphEdge>();
        var bags = new Dictionary<(bool Edge, Guid Id), JsonElement>();
        foreach (var fact in evidence.Facts)
        {
            using var document = JsonDocument.Parse(fact.Relation);
            foreach (var item in document.RootElement.GetProperty("vertices").EnumerateArray())
            {
                var id = new GraphVertexId(item.GetProperty("id").GetProperty("value").GetGuid());
                if (routing.RouteGraphVertex(id.Value).Value != fact.Owner.Partition || !vertices.TryAdd(id, GraphWireJson.Vertex(item, id)))
                {
                    throw new InvalidDataException("Query vertex evidence ownership differs.");
                }
            }
            foreach (var item in document.RootElement.GetProperty("edges").EnumerateArray())
            {
                var id = new GraphEdgeId(item.GetProperty("id").GetProperty("value").GetGuid());
                if (routing.RouteGraphEdge(id.Value).Value != fact.Owner.Partition || !edges.TryAdd(id, GraphWireJson.Edge(item, id)))
                {
                    throw new InvalidDataException("Query edge evidence ownership differs.");
                }
            }
            if (routing.StorageLayout == DatabaseStorageLayout.GraphTopologyProperties)
            {
                foreach (var item in document.RootElement.GetProperty("propertyBags").EnumerateArray())
                {
                    bool edge = item.GetProperty("edge").GetBoolean();
                    Guid id = item.GetProperty("identity").GetGuid();
                    if (id == Guid.Empty || routing.RouteGraphProperties(id, edge).Value != fact.Owner.Partition || !bags.TryAdd((edge, id), item.GetProperty("properties").Clone()))
                    {
                        throw new InvalidDataException("Query property evidence ownership differs.");
                    }
                }
            }

            if (vertices.Count + edges.Count + bags.Count > PartitionQueryLimits.MaximumFacts)
            {
                throw new InvalidDataException("Query update exceeds its fact bound.");
            }
        }
        if (routing.StorageLayout == DatabaseStorageLayout.GraphTopologyProperties)
        {
            PartitionTopologyPropertyQueries.JoinBags(bags, vertices, edges);
        }

        return (vertices, edges);
    }

    internal static ValueTask<PartitionTransactionMember> CompileAsync(PartitionApplicationBinding application,
        PartitionExecutionDomain domain, DistributedRoutingBinding routing, CancellationToken token) =>
        ValueTask.FromResult(Compile(application, domain, routing, token));

    private static PartitionTransactionMember Compile(PartitionApplicationBinding application,
        PartitionExecutionDomain domain, DistributedRoutingBinding routing, CancellationToken token)
    {
        var request = Request(application);
        if (request.Generation != domain.Generation || request.Profile != domain.Profile || request.Operation != application.Operation ||
            !HasValidStatement(request))
        {
            throw new InvalidDataException("Query update association differs.");
        }

        var evidence = Read(application, routing);
        var position = evidence.Position.Positions.Single(p => p.Identity == domain.Identity).Version;
        if (domain.Profile == DatabaseProfileId.PropertyGraph && request.Language == "gql")
        {
            var (vertices, edges) = ReadGraphFacts(evidence, routing);
            var compiled = GraphGqlMutationCompiler.Compile(GraphGqlStandardSyntax.Prepare(request.Statement), transactionId: request.Operation);
            if (evidence.GraphPlan is not { } plan)
            {
                throw new InvalidDataException("A selected GQL update requires its captured native mutation plan.");
            }
            var mutations = GraphTransactionCodec.Decode(request.Operation, plan).Mutations;
            if (mutations.Count > 10_000)
            {
                throw new InvalidDataException("A selected GQL update exceeds its native mutation bound.");
            }
            var deleteEdges = mutations.OfType<DeleteEdge>().Select(m => m.Edge).Distinct().ToArray();
            // Validate the complete logical mutation before splitting replicas. Native
            // replica admission cannot establish a remote endpoint's existence.
            var logical = compiled.Program is not null ? mutations.ToArray() : deleteEdges.Select(id => (GraphMutation)new DeleteEdge(id))
                .Concat(mutations.Where(m => m is not DeleteEdge)).ToArray();
            _ = new GraphTransactionPlanner(JsonValueAdmissionPolicy.Default).Plan(
                new(application.Operation, logical), vertices, edges);
            if (routing.StorageLayout == DatabaseStorageLayout.GraphTopologyProperties)
            {
                return PartitionTopologyProperties.Member(application.Operation, domain, routing, evidence.Position, logical);
            }

            var local = new List<GraphMutation>();
            var edgeIdentities = edges.ToDictionary(pair => pair.Key, pair => pair.Value);
            foreach (var mutation in logical)
            {
                switch (mutation)
                {
                    case DeleteEdge deleted:
                        if (edgeIdentities.TryGetValue(deleted.Edge, out var removed))
                        {
                            var deleteRole = Role(routing, domain.Partition, removed.Id, removed.From, removed.To);
                            if (deleteRole != GraphEdgeStorageRole.None)
                            {
                                local.Add(new DeleteEdgeReplica(removed.Id, removed.From, removed.To, removed.Label, deleteRole));
                            }
                            edgeIdentities.Remove(deleted.Edge);
                        }
                        break;
                    case CreateEdge edge:
                        edgeIdentities[edge.Edge] = new(edge.Edge, edge.From, edge.To, edge.Label, new Dictionary<string, DatabaseValue>());
                        var role = Role(routing, domain.Partition, edge.Edge, edge.From, edge.To);
                        if (role != GraphEdgeStorageRole.None)
                        {
                            local.Add(new CreateEdgeReplica(edge.Edge, edge.From, edge.To, edge.Label, role | GraphEdgeStorageRole.ReferenceProjection));
                        }

                        break;
                    case SetEdgeProperty edge when routing.RouteGraphEdge(edge.Edge.Value) == domain.Partition:
                        local.Add(edge);
                        break;
                    case RemoveEdgeProperty edge when routing.RouteGraphEdge(edge.Edge.Value) == domain.Partition:
                        local.Add(edge);
                        break;
                    case SetEdgeProperty or RemoveEdgeProperty:
                        break;
                    default:
                        var vertex = mutation switch
                        {
                            CreateVertex m => m.Vertex,
                            DeleteVertex m => m.Vertex,
                            SetVertexProperty m => m.Vertex,
                            RemoveVertexProperty m => m.Vertex,
                            AddVertexLabel m => m.Vertex,
                            RemoveVertexLabel m => m.Vertex,
                            _ => throw new NotSupportedException("Unsupported query Graph mutation.")
                        };
                        if (routing.RouteGraphVertex(vertex.Value) == domain.Partition)
                        {
                            local.Add(mutation);
                        }

                        break;
                }
            }
            GraphReadPredicate[] dependencies = [new DatabaseRead()];
            if (compiled.RowMerge is null && compiled.Match is { Pipeline: null, Union: null, Query: var query } && query.Relationships.Count == 0 && query.Aggregation is null &&
                local.All(m => m is SetVertexProperty) && compiled.MatchedMutations.All(m => m.Kind == "SET" && m.Binding == query.Start.Binding))
            {
                var properties = query.Start.Predicates.Select(p => p.Property).ToHashSet(StringComparer.Ordinal);
                CollectProperties(query.Filter, properties);
                foreach (var relationship in query.Relationships)
                {
                    CollectProperties(relationship.Filter, properties);
                }
                properties.UnionWith(compiled.MatchedMutations.SelectMany(mutation => mutation.Expression?.Leaves ?? [])
                    .Select(leaf => leaf.Property).OfType<string>());
                dependencies = [GraphQueryStateRead.Capture(properties, local.Cast<SetVertexProperty>().Select(m => m.Vertex),
                    vertices.Values.Where(v => routing.RouteGraphVertex(v.Id.Value) == domain.Partition))];
            }
            return new(domain, position, GraphTransactionCodec.Encode(new(application.Operation, local, position, dependencies)));
        }
        if (domain.Profile == DatabaseProfileId.Rdf && request.Language == "sparql")
        {
            var quads = new HashSet<RdfQuad>();
            foreach (var fact in evidence.Facts)
            {
                using var document = JsonDocument.Parse(fact.Relation);
                foreach (var item in document.RootElement.GetProperty("quads").EnumerateArray())
                {
                    var quad = RdfWireJson.Quad(item);
                    if (routing.Route(DistributedDatabaseProfile.Rdf, ProfilePartitionRouting.RdfOwner(quad.Subject)).Value != fact.Owner.Partition || !quads.Add(quad))
                    {
                        throw new InvalidDataException("Query RDF evidence ownership differs.");
                    }

                    if (quads.Count > PartitionQueryLimits.MaximumFacts)
                    {
                        throw new InvalidDataException("Query update exceeds its fact bound.");
                    }
                }
            }
            if (evidence.RdfMutations is not { } mutations || mutations.Length > RdfSparqlUpdate.MaximumMutations)
            {
                throw new InvalidDataException("A selected RDF update requires its bounded materialized mutation set.");
            }
            foreach (var mutation in mutations)
            {
                mutation.Quad.Validate();
            }
            var local = mutations.Where(m => routing.Route(DistributedDatabaseProfile.Rdf, ProfilePartitionRouting.RdfOwner(m.Quad.Subject)) == domain.Partition).ToArray();
            RdfReadPredicate[]? dependencies = null;
            if (RdfSparqlUpdate.QueryUpdateAnchor(request.Statement) is { Predicate: { } predicate, Object: { } value })
            {
                var dependency = RdfQueryStateRead.Capture(predicate, value,
                    quads.Where(q => routing.Route(DistributedDatabaseProfile.Rdf, ProfilePartitionRouting.RdfOwner(q.Subject)) == domain.Partition));
                if (local.All(m => dependency.Subjects.Contains(m.Quad.Subject)))
                {
                    dependencies = [dependency];
                }
            }
            // Unsupported joins retain whole-partition exclusion, including phantoms.
            return new(domain, position, RdfTransactionCodec.Encode(new(application.Operation, local,
                SourceCommandHash: Convert.ToHexString(SHA256.HashData(application.Requests[0].Span)), ReadVersion: position, ReadPredicates: dependencies)));
        }
        throw new NotSupportedException("This profile has no supported query-update language.");
    }

    private static void CollectProperties(GraphPatternFilter? filter, HashSet<string> properties)
    {
        switch (filter)
        {
            case null:
                return;
            case GraphPatternExpressionFilter expression:
                properties.UnionWith(expression.Expression.Leaves.Select(leaf => leaf.Property).OfType<string>());
                return;
            case GraphPatternNullTest test:
                if (test.Property is not null)
                {
                    properties.Add(test.Property);
                }
                return;
            case GraphPatternPropertyComparison comparison:
                properties.Add(comparison.Property);
                return;
            case GraphPatternBooleanFilter boolean:
                CollectProperties(boolean.Left, properties);
                CollectProperties(boolean.Right, properties);
                return;
            case GraphPatternNotFilter not:
                CollectProperties(not.Operand, properties);
                return;
            default:
                throw new NotSupportedException("Unsupported scalar query dependency.");
        }
    }

    internal static bool SupportsSnapshotSelection(PartitionQueryWrite request, DistributedRoutingBinding routing)
    {
        if (request.Profile == DatabaseProfileId.Rdf)
        {
            return RdfSparqlUpdate.QueryUpdateAnchor(request.Statement) is not null;
        }

        if (request.Profile != DatabaseProfileId.PropertyGraph || routing.StorageLayout == DatabaseStorageLayout.GraphTopologyProperties)
        {
            return false;
        }

        string clean = GraphGqlQueryCompiler.StripComments(request.Statement);
        if (GraphGqlQueryCompiler.BindStatementClock(clean, DateTimeOffset.UnixEpoch) != clean)
        {
            return false;
        }
        var compiled = GraphGqlMutationCompiler.Compile(GraphGqlStandardSyntax.Prepare(request.Statement), transactionId: request.Operation);
        return compiled.RowMerge is null && compiled.Match is { Pipeline: null, Union: null, Query: var query } && query.Relationships.Count == 0 && query.Aggregation is null &&
            compiled.MatchedMutations.All(m => m.Kind == "SET" && m.Binding == query.Start.Binding);
    }

    internal static bool HasCurrentStateDependencies(DatabaseProfileId profile, Guid operation, ReadOnlySpan<byte> payload) =>
        profile == DatabaseProfileId.PropertyGraph
            ? GraphTransactionCodec.Decode(operation, payload).ReadPredicates is { Count: > 0 } graph && graph.All(p => p is GraphQueryStateRead)
            : profile == DatabaseProfileId.Rdf && RdfTransactionCodec.DecodeTransaction(operation, payload).ReadPredicates is { Count: > 0 } rdf && rdf.All(p => p is RdfQueryStateRead);

    private static GraphEdgeStorageRole Role(DistributedRoutingBinding routing, LogicalShardId owner, GraphEdgeId edge, GraphVertexId from, GraphVertexId to) =>
        (routing.RouteGraphEdge(edge.Value) == owner ? GraphEdgeStorageRole.Authoritative : GraphEdgeStorageRole.None) |
        (routing.RouteGraphVertex(from.Value) == owner ? GraphEdgeStorageRole.OutgoingAdjacency : GraphEdgeStorageRole.None) |
        (routing.RouteGraphVertex(to.Value) == owner ? GraphEdgeStorageRole.IncomingAdjacency : GraphEdgeStorageRole.None);
}

internal sealed partial class PartitionOwnedNode
{
    internal async ValueTask ValidateQueryUpdateEvidenceAsync(PartitionApplicationBinding application, CancellationToken token)
    {
        var member = await PartitionQueryUpdate.CompileAsync(application, Domain, Routing, token);
        // The receiver reconstructs these dependencies from the exact selected
        // query/evidence. Native admission validates their current state atomically;
        // no historical view is required after the capture lease is released.
        if (Routing.StorageLayout != DatabaseStorageLayout.GraphTopologyProperties &&
            PartitionQueryUpdate.HasCurrentStateDependencies(Domain.Profile, application.Operation, member.Payload.Span))
        {
            return;
        }

        var evidence = PartitionQueryUpdate.Read(application, Routing);
        var position = evidence.Position.Positions.Single(p => p.Identity == Domain.Identity).Version;
        var expected = evidence.Facts.Single(f => f.Owner == Domain.Identity).Relation;
        // A stale fallback is rejected by whole-partition native admission, which
        // also durably terminalises a selected coordinator home. Do not throw
        // before that admission and strand the application's selected evidence.
        if (position.Value < Kernel.CurrentVersion.Value)
        {
            return;
        }

        if (position.Value > Kernel.CurrentVersion.Value)
        {
            throw new InvalidDataException("Query selection is in the future.");
        }

        byte[] current;
        try
        {
            current = await ReadFrozenAsync(position, new("query-relation"), token);
        }
        catch (Exception failure) when (failure is GraphSnapshotTooOldException or RdfSnapshotTooOldException)
        {
            if (position.Value < Kernel.CurrentVersion.Value)
            {
                return;
            }

            throw;
        }
        if (!current.AsSpan().SequenceEqual(expected))
        {
            throw new InvalidDataException("Query selection differs from its native evidence.");
        }
    }
}

internal sealed partial class PartitionApplicationClient
{
    internal async ValueTask<DatabaseDistributedCommitReceipt> UpdateQueryAsync(PartitionQueryWrite request, CancellationToken token,
        Action<JsonElement?>? captureResult = null)
    {
        if (request.Language == "gql" && request.Statement is { } statement && GqlProcedures is { } procedures)
        {
            request = request with
            {
                Statement = procedures.Expand(statement)
            };
        }
        PartitionOwnerOperation.RequireCoordinatedNamespace(request.Operation);
        if (request.Generation != Generation || request.Profile != Profile || request.Operation == Guid.Empty ||
            !PartitionQueryUpdate.HasValidStatement(request) ||
            !(Profile == DatabaseProfileId.PropertyGraph && request.Language == "gql" || Profile == DatabaseProfileId.Rdf && request.Language == "sparql"))
        {
            throw new InvalidDataException("Invalid query update destination or language.");
        }

        var bytes = PartitionQueryUpdate.Encode(request);
        var selected = await _authority.ReadApplicationAsync(request.Operation, token);
        var owners = _owners.Values.OrderBy(o => o.Domain.Partition.Value, StringComparer.Ordinal).ToArray();
        if (selected is null)
        {
            var capture = await PartitionRemoteRead.ExecuteAsync(owners.Cast<IPartitionReadParticipant>().ToArray(), _routing,
                async (read, cancellation) =>
                {
                    var facts = new List<PartitionQueryUpdate.Fact>();
                    long size = 0;
                    foreach (var owner in owners)
                    {
                        var value = await read.ReadAsync(owner.Domain.Partition.Value, new("query-relation"), cancellation);
                        size += value.Length;
                        if (size > PartitionApplicationBinding.MaximumRequestBytes / 2)
                        {
                            throw new InvalidOperationException("Query update selection exceeds its durable evidence budget.");
                        }

                        facts.Add(new(owner.Domain.Identity, value));
                    }
                    return new PartitionQueryUpdate.Evidence(read.Position, facts.ToArray());
                }, token, useSnapshots: PartitionQueryUpdate.SupportsSnapshotSelection(request, _routing));
            var materialized = await PartitionQueryUpdate.MaterializeAsync(request, capture.Value, _routing, token, RdfHttpResources);
            var proposal = new PartitionApplicationBinding(request.Operation, owners.Select(o => o.Domain.Identity).ToArray(),
                [bytes], planningEvidence: JsonSerializer.SerializeToUtf8Bytes(materialized, PartitionParticipantWire.Json));
            // Compile before durable selection: unsupported syntax must not strand a binding.
            var proposedMembers = new List<PartitionTransactionMember>();
            foreach (var owner in owners)
            {
                proposedMembers.Add(await PartitionQueryUpdate.CompileAsync(proposal, owner.Domain, _routing, token));
            }

            proposal = PartitionQueryUpdateEvidence.Narrow(proposal, _routing, proposedMembers);
            try
            {
                if (_authority is PartitionNativeHomeAuthority combined)
                {
                    await combined.SelectCombinedAsync(proposal, new(request.Operation, _routing.MapVersion, proposedMembers), token);
                }
                else
                {
                    await _authority.BindApplicationAsync(proposal, token);
                }
            }
            catch (EntityForgeDbIdempotencyException)
            {
                /* Adopt only the exact original request below. */
            }
            selected = await _authority.ReadApplicationAsync(request.Operation, token) ?? throw new InvalidDataException("Query selection disappeared.");
        }
        if (!PartitionQueryUpdate.Is(selected) || !selected.Requests[0].Span.SequenceEqual(bytes))
        {
            throw new EntityForgeDbIdempotencyException(request.Operation);
        }

        if (await _authority.ReadDecisionAsync(request.Operation, token) is { ApplicationOnly: true })
        {
            throw new EntityOperationAbortedException(request.Operation);
        }

        var plan = await _authority.ReadPlanAsync(request.Operation, token);
        if (plan is null)
        {
            var members = new List<PartitionTransactionMember>();
            foreach (var owner in owners)
            {
                members.Add(await PartitionQueryUpdate.CompileAsync(selected, owner.Domain, _routing, token));
            }

            plan = new(request.Operation, _routing.MapVersion, members);
            await _authority.BindAsync(plan, token);
        }
        var result = await new PartitionTransactionCoordinator(_authority).ExecuteSelectedAsync(plan, owners, token);
        if (!result.Decision.Commit)
        {
            throw new EntityOperationAbortedException(request.Operation);
        }

        if (captureResult is not null && request.Profile == DatabaseProfileId.PropertyGraph)
        {
            var evidence = JsonSerializer.Deserialize<PartitionQueryUpdate.Evidence>(selected.PlanningEvidence.Span, PartitionParticipantWire.Json)
                ?? throw new InvalidDataException("Missing query result evidence.");
            captureResult(evidence.GraphPlan is { } graphPlan
                ? GqlUpdateResultCodec.ReadResult(GraphTransactionCodec.Decode(request.Operation, graphPlan).QueryResult) : null);
        }
        return result.Completion;
    }
}
