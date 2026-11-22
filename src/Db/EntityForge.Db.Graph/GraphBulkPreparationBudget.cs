using EntityForge.Db.Graph.Contracts;

namespace EntityForge.Db.Graph;

// Bounds derived preparation work. Caller-owned input and the CLR's total RSS are
// outside this budget. Charge structure as well as bytes: tiny values are not free.
internal static class GraphBulkPreparationBudget
{
    internal const long MaximumBytes = 64L * 1024 * 1024;
    internal const long MaximumItems = 100_000;
    private const int StructureCharge = 128;

    internal static void Validate(IReadOnlyList<GraphVertex> vertices, IReadOnlyList<GraphEdge> edges,
        CancellationToken cancellationToken, long maximumBytes = MaximumBytes, long maximumItems = MaximumItems)
        => CaptureCore(vertices, edges, cancellationToken, maximumBytes, maximumItems, null);

    internal static GraphMutation[] Capture(IReadOnlyList<GraphVertex> vertices, IReadOnlyList<GraphEdge> edges,
        CancellationToken cancellationToken)
    {
        var mutations = new List<GraphMutation>();
        CaptureCore(vertices, edges, cancellationToken, MaximumBytes, MaximumItems, mutations);
        return mutations.ToArray();
    }

    private static void CaptureCore(IReadOnlyList<GraphVertex> vertices, IReadOnlyList<GraphEdge> edges,
        CancellationToken cancellationToken, long maximumBytes, long maximumItems, List<GraphMutation>? mutations)
    {
        long bytes = 0, items = 0;
        void Charge(long serializedBytes, long count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            items = checked(items + count);
            bytes = checked(bytes + serializedBytes + count * StructureCharge);
            if (items > maximumItems || bytes > maximumBytes)
            {
                throw new InvalidOperationException("Graph bulk preparation exceeds its byte or work-item budget.");
            }
        }
        foreach (GraphVertex vertex in vertices)
        {
            ArgumentNullException.ThrowIfNull(vertex);
            Charge(24, 1);
            foreach (string label in vertex.Labels)
            {
                Charge(GraphCheckpointSize.String(label), 1);
            }

            mutations?.Add(new CreateVertex(vertex.Id, vertex.Labels));
            foreach (var property in vertex.Properties)
            {
                Charge(GraphCheckpointSize.String(property.Key) + GraphCheckpointSize.Value(property.Value), 1);
                mutations?.Add(new SetVertexProperty(vertex.Id, property.Key, property.Value));
            }
        }
        foreach (GraphEdge edge in edges)
        {
            ArgumentNullException.ThrowIfNull(edge);
            // An edge produces four physical adjacency entries as well as its logical record.
            Charge(checked(53 + 3 * GraphCheckpointSize.String(edge.Label)), 5);
            mutations?.Add(new CreateEdge(edge.Id, edge.From, edge.To, edge.Label));
            foreach (var property in edge.Properties)
            {
                Charge(GraphCheckpointSize.String(property.Key) + GraphCheckpointSize.Value(property.Value), 1);
                mutations?.Add(new SetEdgeProperty(edge.Id, property.Key, property.Value));
            }
        }
    }
}
