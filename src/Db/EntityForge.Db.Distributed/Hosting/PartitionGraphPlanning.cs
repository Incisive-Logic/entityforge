using System.Text.Json;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Mapping;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Distributed.Hosting;

// Receiver-reconstructible evidence for the bounded mapped Graph compiler.
// Each owner reserves its observed dependencies at its local position, including
// read-only members. Remote facts are checked by their own receivers.
internal static class PartitionGraphPlanning
{
    internal sealed record Fact(string Owner, PartitionReadQuery Query, byte[] Value);

    internal sealed record Evidence(DatabasePositionVector Position, Fact[] Facts);

    internal static bool Required(DatabaseProfileId profile, DistributedRoutingBinding routing, IReadOnlyList<ReadOnlyMemory<byte>> requests) =>
        profile == DatabaseProfileId.PropertyGraph && (routing.StorageLayout == DatabaseStorageLayout.GraphTopologyProperties || routing.Shards.Count > 1 && requests.Any(bytes =>
        {
            var metadata = EntityCapturedRequest.Restore(bytes.Span, profile).Description;
            return metadata.Delete || metadata.Manifest.Members.Any(member => member.Kind == EntityMemberKind.Reference);
        }));

    internal static async ValueTask<PartitionApplicationBinding> SelectAsync(Guid generation, Guid operation,
        DistributedRoutingBinding routing, IReadOnlyList<IPartitionTransactionParticipant> owners,
        IReadOnlyList<ReadOnlyMemory<byte>> requests, CancellationToken token)
    {
        foreach (var request in requests)
        {
            var captured = EntityCapturedRequest.Restore(request.Span, DatabaseProfileId.PropertyGraph);
            if (captured.Description.OperationId != operation || captured.Description.DatabaseIncarnation != generation || captured.Description.ExpectedVersion is not null)
            {
                throw new InvalidDataException("Invalid Graph planning destination or scalar precondition.");
            }
        }
        _ = EntityCapturedBatch.Validate(requests.Select(bytes => EntityCapturedRequest.Restore(bytes.Span, DatabaseProfileId.PropertyGraph)).ToArray());
        foreach (var owner in owners)
        {
            await owner.InitializeApplicationMappingAsync(requests[0], token);
        }

        var result = await PartitionRemoteRead.ExecuteAsync(owners.Cast<IPartitionReadParticipant>().ToArray(), routing, async (read, cancellation) =>
        {
            var scope = new Scope(routing, (owner, query, ct) => read.ReadAsync(owner, query, ct));
            var changes = await CompileAsync(scope, requests, cancellation);
            await scope.CompleteEdgesAsync(changes.Mutations, cancellation);
            var evidence = new Evidence(read.Position, scope.Facts.ToArray());
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(evidence, PartitionParticipantWire.Json);
            if (bytes.Length > PartitionApplicationBinding.MaximumRequestBytes)
            {
                throw new InvalidOperationException("Graph planning evidence exceeds its bound.");
            }
            // Keep the complete captured cut in the selection; each receiver
            // reconstructs its own dependencies, including genuinely empty members.
            return new PartitionApplicationBinding(operation, owners.Select(owner => owner.Domain.Identity).ToArray(), requests, planningEvidence: bytes);
            // The validated MVCC cut tolerates concurrent distributed preparations and
            // retries mixed cuts. A frozen cut instead rejects a pending reservation.
        }, token, useSnapshots: true);
        return result.Value;
    }

    internal static Evidence Read(PartitionApplicationBinding selected)
    {
        var evidence = JsonSerializer.Deserialize<Evidence>(selected.PlanningEvidence.Span, PartitionParticipantWire.Json)
            ?? throw new InvalidDataException("Missing Graph planning evidence.");
        if (evidence.Position is null || evidence.Facts is null || evidence.Facts.Length > MaximumFacts ||
            evidence.Facts.Any(fact => fact is null || fact.Query is null || fact.Value is null) ||
            !evidence.Position.Positions.Select(position => position.Identity).ToHashSet().SetEquals(selected.Owners) ||
            evidence.Facts.Select(fact => (fact.Owner, fact.Query)).Distinct().Count() != evidence.Facts.Length)
        {
            throw new InvalidDataException("Invalid Graph planning evidence association.");
        }

        return evidence;
    }

    internal static async ValueTask<PartitionTransactionMember> MemberAsync(PartitionApplicationBinding selected,
        PartitionExecutionDomain domain, DistributedRoutingBinding routing, CancellationToken token)
    {
        var evidence = Read(selected);
        if (!selected.Owners.Select(owner => owner.Partition).ToHashSet(StringComparer.Ordinal).SetEquals(routing.Shards.Select(shard => shard.Value)) ||
            selected.Owners.Any(owner => owner.Generation != domain.Generation || owner.Profile != domain.Profile || owner.Routing != routing.Fingerprint) ||
            evidence.Facts.Any(fact => !selected.Owners.Any(owner => owner.Partition == fact.Owner)))
        {
            throw new InvalidDataException("Graph topology planning requires the complete routing cut.");
        }

        foreach (var bytes in selected.Requests)
        {
            var metadata = EntityCapturedRequest.Restore(bytes.Span, DatabaseProfileId.PropertyGraph).Description;
            if (metadata.OperationId != selected.Operation || metadata.DatabaseIncarnation != domain.Generation || metadata.ExpectedVersion is not null)
            {
                throw new InvalidDataException("Graph evidence request association differs.");
            }
        }
        var scope = new Scope(routing, (owner, query, _) => ValueTask.FromResult(evidence.Facts.SingleOrDefault(fact => fact.Owner == owner && fact.Query == query)?.Value
            ?? throw new InvalidDataException("The selected Graph plan omits an observed dependency.")));
        var changes = await CompileAsync(scope, selected.Requests, token);
        await scope.CompleteEdgesAsync(changes.Mutations, token);
        if (scope.Facts.Count != evidence.Facts.Length)
        {
            throw new InvalidDataException("The selected Graph evidence contains unrelated observations.");
        }

        if (routing.StorageLayout == DatabaseStorageLayout.GraphTopologyProperties)
        {
            return PartitionTopologyProperties.Member(selected.Operation, domain, routing, evidence.Position, changes.Mutations, evidence.Facts);
        }

        var mutations = new List<GraphMutation>();
        foreach (var mutation in changes.Mutations)
        {
            switch (mutation)
            {
                case CreateEdge edge:
                    var createRole = Role(routing, domain.Partition, edge.Edge, edge.From, edge.To);
                    if (createRole != GraphEdgeStorageRole.None)
                    {
                        mutations.Add(new CreateEdgeReplica(edge.Edge, edge.From, edge.To, edge.Label, (createRole | GraphEdgeStorageRole.ReferenceProjection)));
                    }

                    break;
                case DeleteEdge edge:
                    var original = scope.Edges[edge.Edge];
                    var deleteRole = Role(routing, domain.Partition, edge.Edge, original.From, original.To);
                    if (deleteRole != GraphEdgeStorageRole.None)
                    {
                        mutations.Add(new DeleteEdgeReplica(edge.Edge, original.From, original.To, original.Label, (deleteRole | (original.StorageRole & GraphEdgeStorageRole.ReferenceProjection))));
                    }

                    break;
                case SetEdgeProperty edge when routing.RouteGraphEdge(edge.Edge.Value) == domain.Partition:
                    mutations.Add(edge);
                    break;
                case RemoveEdgeProperty edge when routing.RouteGraphEdge(edge.Edge.Value) == domain.Partition:
                    mutations.Add(edge);
                    break;
                case SetEdgeProperty or RemoveEdgeProperty:
                    break;
                default:
                    var vertex = mutation switch
                    {
                        CreateVertex value => value.Vertex,
                        SetVertexProperty value => value.Vertex,
                        RemoveVertexProperty value => value.Vertex,
                        AddVertexLabel value => value.Vertex,
                        RemoveVertexLabel value => value.Vertex,
                        _ => throw new NotSupportedException("Unsupported mapped Graph topology mutation.")
                    };
                    if (routing.RouteGraphVertex(vertex.Value) == domain.Partition)
                    {
                        mutations.Add(mutation);
                    }

                    break;
            }
        }
        var version = evidence.Position.Positions.Single(position => position.Identity == domain.Identity).Version;
        // Each receiver protects exactly its own observations at its own version.
        // Include absence/adjacency reads even when that owner has no mutations.
        var reads = evidence.Facts.Where(f => f.Owner == domain.Partition.Value).Select(f => f.Query.Kind switch
        {
            "vertex" => (GraphReadPredicate)new WholeVertexRead(new(f.Query.Identity)),
            "edge" => new WholeEdgeRead(new(f.Query.Identity)),
            "outgoing" => new OutgoingAdjacencyRead(new(f.Query.Identity), f.Query.Label),
            "incoming" => new IncomingAdjacencyRead(new(f.Query.Identity), f.Query.Label),
            _ => throw new InvalidDataException("Unsupported Graph planning dependency.")
        }).Distinct().ToArray();
        return new(domain, version, GraphTransactionCodec.Encode(new(selected.Operation, mutations, version, reads)));
    }

    private const int MaximumFacts = 4096;

    private static ValueTask<GraphEntityBatchPreparation.WriteSet> CompileAsync(Scope scope, IReadOnlyList<ReadOnlyMemory<byte>> requests, CancellationToken token) =>
        GraphEntityBatchPreparation.PrepareSetAsync(scope, requests.Select(bytes => EntityCapturedRequest.Restore(bytes.Span, DatabaseProfileId.PropertyGraph)).ToArray(),
            checked(GraphEntityWrite.MaximumMutations * requests.Count), token);

    private static GraphEdgeStorageRole Role(DistributedRoutingBinding routing, LogicalShardId owner, GraphEdgeId edge, GraphVertexId from, GraphVertexId to) =>
        (routing.RouteGraphEdge(edge.Value) == owner ? GraphEdgeStorageRole.Authoritative : GraphEdgeStorageRole.None) |
        (routing.RouteGraphVertex(from.Value) == owner ? GraphEdgeStorageRole.OutgoingAdjacency : GraphEdgeStorageRole.None) |
        (routing.RouteGraphVertex(to.Value) == owner ? GraphEdgeStorageRole.IncomingAdjacency : GraphEdgeStorageRole.None);

    private sealed class Scope(DistributedRoutingBinding routing, Func<string, PartitionReadQuery, CancellationToken, ValueTask<byte[]>> read) : IGraphEntityPlanningScope
    {
        public DatabaseVersion Version => new(0); // compiler-only placeholder; each emitted member uses its actual local position

        internal List<Fact> Facts { get; } = [];

        internal Dictionary<GraphEdgeId, GraphEdge> Edges { get; } = [];

        private async ValueTask<byte[]> GetAsync(string owner, PartitionReadQuery query, CancellationToken token)
        {
            if (Facts.SingleOrDefault(fact => fact.Owner == owner && fact.Query == query) is { } existing)
            {
                return existing.Value;
            }

            if (Facts.Count == MaximumFacts)
            {
                throw new InvalidOperationException("Graph planning exceeds its observation bound.");
            }

            var bytes = await read(owner, query, token);
            if (Facts.Sum(fact => (long)fact.Value.Length) + bytes.Length > PartitionApplicationBinding.MaximumRequestBytes)
            {
                throw new InvalidOperationException("Graph planning exceeds its byte bound.");
            }

            Facts.Add(new(owner, query, bytes.ToArray()));
            return bytes;
        }

        public async ValueTask<GraphVertex?> GetVertexAsync(GraphVertexId identity, CancellationToken token)
        {
            using var json = JsonDocument.Parse(await GetAsync(routing.RouteGraphVertex(identity.Value).Value, new("vertex", identity.Value), token));
            if (json.RootElement.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            var vertex = GraphWireJson.Vertex(json.RootElement, identity);
            if (routing.StorageLayout != DatabaseStorageLayout.GraphTopologyProperties)
            {
                return vertex;
            }

            using var properties = JsonDocument.Parse(await GetAsync(routing.RouteGraphProperties(identity.Value).Value, new("vertex-properties", identity.Value), token));
            return PartitionTopologyProperties.WithProperties(vertex, properties.RootElement);
        }

        public async ValueTask<IReadOnlyList<GraphEdgeReference>> OutgoingAsync(GraphVertexId identity, string label, CancellationToken token)
        {
            using var json = JsonDocument.Parse(await GetAsync(routing.RouteGraphVertex(identity.Value).Value, new("outgoing", identity.Value, Label: label), token));
            var edges = json.RootElement.EnumerateArray().Select(value => GraphWireJson.EdgeReference(value)).ToArray();
            foreach (var edge in edges)
            {
                var actual = await EdgeAsync(edge.Id, token);
                if (actual.From != identity || actual.To != edge.To || actual.Label != label || edge.From != identity)
                {
                    throw new InvalidDataException("Adjacency disagrees with its authoritative edge.");
                }
            }
            return edges;
        }

        private async ValueTask<GraphEdge> EdgeAsync(GraphEdgeId identity, CancellationToken token)
        {
            if (Edges.TryGetValue(identity, out var existing))
            {
                return existing;
            }

            using var json = JsonDocument.Parse(await GetAsync(routing.RouteGraphEdge(identity.Value).Value, new("edge", identity.Value), token));
            var edge = GraphWireJson.Edge(json.RootElement, identity);
            if (routing.StorageLayout == DatabaseStorageLayout.GraphTopologyProperties)
            {
                using var properties = JsonDocument.Parse(await GetAsync(routing.RouteGraphProperties(identity.Value, true).Value, new("edge-properties", identity.Value), token));
                edge = PartitionTopologyProperties.WithProperties(edge, properties.RootElement);
            }
            Edges.Add(identity, edge);
            return edge;
        }

        internal async ValueTask CompleteEdgesAsync(IReadOnlyList<GraphMutation> mutations, CancellationToken token)
        {
            foreach (var edge in mutations.OfType<DeleteEdge>())
            {
                await EdgeAsync(edge.Edge, token);
            }
        }

        public ValueTask ValidateAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
    }
}

internal sealed partial class PartitionOwnedNode
{
    public ValueTask InitializeApplicationMappingAsync(ReadOnlyMemory<byte> captured, CancellationToken token = default) =>
        SharedTimestamps is null ? InitializeApplicationMappingCoreAsync(captured, token) : InitializeTimestampedMappingAsync(captured, token);

    private async ValueTask<bool> TryValidateApplicationMappingAsync(ReadOnlyMemory<byte> captured, CancellationToken token)
    {
        EntityMappingRegistry mappings;
        IReadOnlyList<EntityMappingManifest> manifests;
        if (_native.Database is EntityForge.Db.Semantic.Engine.SemanticDatabaseEngine semantic)
        {
            var request = EntityForge.Db.Semantic.SemanticEntityRequest.Restore(captured.Span);
            if (request._metadata.DatabaseIncarnation != Domain.Generation)
            {
                throw new InvalidDataException("Semantic mapping generation differs.");
            }

            var original = await PartitionSemanticPlanning.OriginalTypedAsync(request.ForDatabase(Domain.Incarnation));
            manifests = original.Metadata.Manifests;
            mappings = semantic.MappingRegistry;
        }
        else
        {
            var request = EntityCapturedRequest.Restore(captured.Span, DatabaseProfileId.PropertyGraph);
            if (request.Description.DatabaseIncarnation != Domain.Generation || _native.Database is not (IGraphDatabase or GraphPropertyShard))
            {
                throw new InvalidDataException("Mapping destination differs.");
            }

            manifests = [request.Description.Manifest];
            mappings = _native.Database is GraphPropertyShard split ? split.MappingRegistry : ((IGraphDatabase)_native.Database).Mappings();
        }
        await _lifecycle.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            return await Authority.ExecuteAsync(async () =>
            {
                await Kernel.PartitionTransactions.AdmitReadAsync(token);
                return await mappings.TryValidateReadOnlyAsync(manifests, token);
            });
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private async ValueTask InitializeApplicationMappingCoreAsync(ReadOnlyMemory<byte> captured, CancellationToken token)
    {
        if (_native.Database is EntityForge.Db.Semantic.Engine.SemanticDatabaseEngine semantic)
        {
            var semanticRequest = EntityForge.Db.Semantic.SemanticEntityRequest.Restore(captured.Span);
            if (semanticRequest._metadata.DatabaseIncarnation != Domain.Generation)
            {
                throw new InvalidDataException("Semantic mapping generation differs.");
            }

            var metadata = (await PartitionSemanticPlanning.OriginalTypedAsync(semanticRequest.ForDatabase(Domain.Incarnation))).Metadata;
            await _lifecycle.WaitAsync(token);
            try
            {
                ObjectDisposedException.ThrowIf(_closing, this);
                await Authority.ExecuteAsync(() => semantic.MappingRegistry.ExecuteAsync(metadata.Manifests, () => ValueTask.FromResult(true), token));
            }
            finally
            {
                _lifecycle.Release();
            }
            return;
        }
        var request = EntityCapturedRequest.Restore(captured.Span, DatabaseProfileId.PropertyGraph);
        if (request.Description.DatabaseIncarnation != Domain.Generation || _native.Database is not (IGraphDatabase or GraphPropertyShard))
        {
            throw new InvalidDataException("Mapping destination differs.");
        }

        await _lifecycle.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            var mappings = _native.Database is GraphPropertyShard split ? split.MappingRegistry : ((IGraphDatabase)_native.Database).Mappings();
            await Authority.ExecuteAsync(() => mappings.ExecuteAsync(request.Description.Manifest, () => ValueTask.FromResult(true), token));
        }
        finally
        {
            _lifecycle.Release();
        }
    }
}
