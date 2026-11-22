using System.Text.Json;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Distributed.Hosting;

// Role-aware composition owns the join; native storage retains genuine bags,
// not synthetic vertices or duplicated topology on property owners.
internal static class PartitionTopologyProperties
{
    internal sealed record Bag(bool Edge, Guid Identity, IReadOnlyDictionary<string, DatabaseValue> Properties);

    internal static GraphPropertyShard.Role Role(DistributedRoutingBinding routing, LogicalShardId owner) =>
        new(routing.Shards.ToList().IndexOf(owner), routing.Shards.Count - 1);

    internal static GraphVertex WithProperties(GraphVertex vertex, JsonElement bag) =>
        new(vertex.Id, vertex.Labels, GraphWireJson.Properties(RequireBag(bag)));

    internal static GraphEdge WithProperties(GraphEdge edge, JsonElement bag) =>
        new(edge.Id, edge.From, edge.To, edge.Label, GraphWireJson.Properties(RequireBag(bag)), edge.StorageRole);

    private static JsonElement RequireBag(JsonElement value) => value.ValueKind == JsonValueKind.Object ? value :
        throw new InvalidDataException("Live Graph topology has no property bag at the protected cut.");

    internal static PartitionTransactionMember Member(Guid operation, PartitionExecutionDomain domain,
        DistributedRoutingBinding routing, DatabasePositionVector position, IReadOnlyList<GraphMutation> mutations, IReadOnlyList<PartitionGraphPlanning.Fact>? facts = null)
    {
        var role = Role(routing, domain.Partition);
        var local = mutations.Where(m => role.IsTopology || m is not (AddVertexLabel or RemoveVertexLabel) &&
            routing.RouteGraphProperties(GraphPropertyShard.Runtime.Identity(m).Id, GraphPropertyShard.Runtime.Identity(m).Edge) == domain.Partition).ToArray();
        var version = position.Positions.Single(p => p.Identity == domain.Identity).Version;
        // Use the native role codec; durable framing has one definition.
        var reads = facts?.Where(f => f.Owner == domain.Partition.Value).Select(f => f.Query.Kind switch
        {
            "vertex" or "vertex-properties" => (GraphReadPredicate)new WholeVertexRead(new(f.Query.Identity)),
            "edge" or "edge-properties" => new WholeEdgeRead(new(f.Query.Identity)),
            "outgoing" => new OutgoingAdjacencyRead(new(f.Query.Identity), f.Query.Label),
            "incoming" => new IncomingAdjacencyRead(new(f.Query.Identity), f.Query.Label),
            _ => throw new InvalidDataException("Unsupported split planning dependency.")
        }).Distinct().ToArray();
        byte[] payload = GraphPropertyShard.EncodeBody(role, GraphTransactionCodec.Encode(new(operation, local, version, reads)));
        return new(domain, version, payload);
    }
}

internal sealed partial class PartitionOwnedNode
{
    private PartitionProjectionValue ReadSplitNative(GraphPropertyShard split, DatabaseVersion version, PartitionReadQuery query)
    {
        using var lease = split.Acquire(version);
        return ReadSplitState(split, lease.State, query);
    }

    private PartitionProjectionValue ReadSplitState(GraphPropertyShard split, GraphPropertyShard.State state, PartitionReadQuery query)
    {
        if (query.Identity == Guid.Empty || query.Subject is not null || query.Label?.Length > PartitionQueryLimits.MaximumStatementCharacters)
        {
            throw new InvalidDataException("Invalid split Graph leaf.");
        }

        if (query.Kind is "vertex-properties" or "edge-properties")
        {
            bool edge = query.Kind == "edge-properties";
            if (split.Configuration.IsTopology || Routing.RouteGraphProperties(query.Identity, edge) != Domain.Partition)
            {
                throw new InvalidDataException("Property read belongs to another role.");
            }

            return new(state.Bags.TryGetValue(new(edge, query.Identity), out var bag) ? bag : null);
        }
        if (!split.Configuration.IsTopology)
        {
            throw new InvalidDataException("Topology read belongs to another role.");
        }

        var view = state.Topology;
        object? result = query.Kind switch
        {
            "vertex" => view.Vertices.GetValueOrDefault(new(query.Identity)),
            "edge" => view.Edges.GetValueOrDefault(new(query.Identity)),
            "outgoing" => view.Outgoing(new(query.Identity), query.Label),
            "incoming" => view.Incoming(new(query.Identity), query.Label),
            _ => throw new NotSupportedException("Unknown split Graph leaf.")
        };
        return new(result);
    }

    private PartitionQueryRelation CaptureSplitRelation(GraphPropertyShard split, DatabaseVersion version)
    {
        using var lease = split.Acquire(version);
        if (lease.State.Topology.Vertices.Count + lease.State.Topology.Edges.Count + lease.State.Bags.Count > PartitionQueryLimits.MaximumFacts)
        {
            throw new InvalidOperationException("Split Graph relation exceeds its fact bound.");
        }

        return split.Configuration.IsTopology
            ? new(Vertices: lease.State.Topology.Vertices.Values.ToArray(), Edges: lease.State.Topology.Edges.Values.ToArray(), PropertyBags: [])
            : new(Vertices: [], Edges: [], PropertyBags: lease.State.Bags.Select(p => new PartitionTopologyProperties.Bag(p.Key.Edge, p.Key.Id, p.Value)).ToArray());
    }
}

internal static class PartitionTopologyPropertyQueries
{
    internal static void Join(DistributedRoutingBinding routing, IPartitionTransactionParticipant[] owners,
        PartitionQueryData[] relations, Dictionary<GraphVertexId, GraphVertex> vertices, Dictionary<GraphEdgeId, GraphEdge> edges)
    {
        var bags = new Dictionary<(bool Edge, Guid Id), JsonElement>();
        for (int index = 0; index < owners.Length; index++)
        {
            foreach (var item in relations[index].Rows("propertyBags"))
            {
                GraphWireJson.UniqueMembers(item);
                bool edge = item.GetProperty("edge").GetBoolean();
                Guid id = item.GetProperty("identity").GetGuid();
                if (id == Guid.Empty || routing.RouteGraphProperties(id, edge) != owners[index].Domain.Partition ||
                    !bags.TryAdd((edge, id), item.GetProperty("properties")))
                {
                    throw new InvalidDataException("Property-bag exchange ownership or identity differs.");
                }

                if (bags.Count + vertices.Count + edges.Count > PartitionQueryLimits.MaximumFacts)
                {
                    throw new InvalidOperationException("Joined Graph exceeds its fact bound.");
                }
            }
        }

        JoinBags(bags, vertices, edges);
    }

    internal static void JoinBags(Dictionary<(bool Edge, Guid Id), JsonElement> bags,
        Dictionary<GraphVertexId, GraphVertex> vertices, Dictionary<GraphEdgeId, GraphEdge> edges)
    {
        foreach (var id in vertices.Keys.ToArray())
        {
            vertices[id] = PartitionTopologyProperties.WithProperties(vertices[id], bags.Remove((false, id.Value), out var bag) ? bag : default);
        }

        foreach (var id in edges.Keys.ToArray())
        {
            edges[id] = PartitionTopologyProperties.WithProperties(edges[id], bags.Remove((true, id.Value), out var bag) ? bag : default);
        }

        if (bags.Count != 0)
        {
            throw new InvalidDataException("Property bags have no corresponding live topology at the cut.");
        }
    }
}
