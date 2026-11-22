using EntityForge.Db.Graph.Contracts;

namespace EntityForge.Db.Graph;

internal static class GraphWriteDependencyBuilder
{
    public static IReadOnlyList<GraphWriteDependency> Capture(GraphTransaction transaction)
    {
        GraphWriteDependency[] dependencies = transaction.Mutations.SelectMany(Capture).Distinct().ToArray();
        return dependencies.Length == 0 ? [new(GraphWriteDependencyKind.Database, Guid.Empty)] : dependencies;
    }

    private static IEnumerable<GraphWriteDependency> Capture(GraphMutation mutation) => mutation switch
    {
        CreateVertex create => [Vertex(create.Vertex)],
        DeleteVertex delete => [Vertex(delete.Vertex)],
        SetVertexProperty set => [Vertex(set.Vertex)],
        RemoveVertexProperty remove => [Vertex(remove.Vertex)],
        AddVertexLabel add => [Vertex(add.Vertex)],
        RemoveVertexLabel remove => [Vertex(remove.Vertex)],
        CreateEdge create => [Edge(create.Edge), Vertex(create.From), Vertex(create.To)],
        CreateEdgeReplica create => [Edge(create.Edge), Vertex(create.From), Vertex(create.To)],
        DeleteEdge delete => [Edge(delete.Edge)],
        DeleteEdgeReplica delete => [Edge(delete.Edge), Vertex(delete.From), Vertex(delete.To)],
        SetEdgeProperty set => [Edge(set.Edge)],
        RemoveEdgeProperty remove => [Edge(remove.Edge)],
        _ => throw new InvalidDataException($"Unsupported Graph mutation '{mutation.GetType().Name}'.")
    };

    private static GraphWriteDependency Vertex(GraphVertexId vertex) =>
        new(GraphWriteDependencyKind.Vertex, vertex.Value);

    private static GraphWriteDependency Edge(GraphEdgeId edge) =>
        new(GraphWriteDependencyKind.Edge, edge.Value);
}
