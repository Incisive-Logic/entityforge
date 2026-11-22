using EntityForge.Db.Graph.Contracts;
using EntityForge.Db;

namespace EntityForge.Db.Graph;

internal static class GraphChangeRoutingSummaryBuilder
{
    public static IReadOnlyList<CommittedChangeRoutingKey> Build(GraphCommittedEffects changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var keys = new HashSet<CommittedChangeRoutingKey>();
        foreach (GraphCommittedEffect effect in changes.Effects)
        {
            keys.Add(new(GraphChangeRoutingKinds.EffectKind, effect.Kind.ToString()));
            switch (effect)
            {
                case GraphVertexCreated value:
                    AddVertex(keys, value.Vertex);
                    break;
                case GraphVertexDeleted value:
                    AddVertex(keys, value.Vertex);
                    break;
                case GraphEdgeCreated value:
                    AddEdge(keys, value.Edge);
                    break;
                case GraphEdgeDeleted value:
                    AddEdge(keys, value.Edge);
                    break;
                case GraphVertexPropertyChanged value:
                    keys.Add(new(GraphChangeRoutingKinds.Vertex, value.Vertex.Value.ToString("N")));
                    keys.Add(new(GraphChangeRoutingKinds.Property, value.Property));
                    break;
                case GraphEdgePropertyChanged value:
                    keys.Add(new(GraphChangeRoutingKinds.Edge, value.Edge.Value.ToString("N")));
                    keys.Add(new(GraphChangeRoutingKinds.Property, value.Property));
                    break;
                case GraphVertexLabelsChanged value:
                    keys.Add(new(GraphChangeRoutingKinds.Vertex, value.Vertex.Value.ToString("N")));
                    foreach (string label in value.Before.Concat(value.After))
                    {
                        keys.Add(new(GraphChangeRoutingKinds.Label, label));
                    }

                    break;
            }
        }
        return keys.ToArray();
    }

    private static void AddVertex(HashSet<CommittedChangeRoutingKey> keys, GraphVertex vertex)
    {
        keys.Add(new(GraphChangeRoutingKinds.Vertex, vertex.Id.Value.ToString("N")));
        foreach (string label in vertex.Labels)
        {
            keys.Add(new(GraphChangeRoutingKinds.Label, label));
        }
    }

    private static void AddEdge(HashSet<CommittedChangeRoutingKey> keys, GraphEdge edge)
    {
        keys.Add(new(GraphChangeRoutingKinds.Edge, edge.Id.Value.ToString("N")));
        keys.Add(new(GraphChangeRoutingKinds.Label, edge.Label));
    }
}
