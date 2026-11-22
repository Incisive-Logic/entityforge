using EntityForge.Db.Graph.Contracts;

namespace EntityForge.Db.Gql;

internal static partial class GraphGqlMutationCompiler
{
    internal static void ValidateNodeDeletes(IReadOnlyList<GraphMutation> mutations, IEnumerable<GraphEdge> snapshotEdges)
    {
        if (!mutations.Any(mutation => mutation is DeleteVertex))
        {
            return;
        }
        var edges = new Dictionary<GraphEdgeId, (GraphVertexId From, GraphVertexId To)>();
        var degrees = new Dictionary<GraphVertexId, int>();
        void Degree(GraphVertexId vertex, int change) => degrees[vertex] = degrees.GetValueOrDefault(vertex) + change;
        void Add(GraphEdgeId id, GraphVertexId from, GraphVertexId to)
        {
            if (edges.TryAdd(id, (from, to)))
            {
                Degree(from, 1);
                Degree(to, 1);
            }
        }
        foreach (var edge in snapshotEdges)
        {
            Add(edge.Id, edge.From, edge.To);
        }
        foreach (var mutation in mutations)
        {
            switch (mutation)
            {
                case CreateEdge edge:
                    Add(edge.Edge, edge.From, edge.To);
                    break;
                case DeleteEdge edge when edges.Remove(edge.Edge, out var endpoints):
                    Degree(endpoints.From, -1);
                    Degree(endpoints.To, -1);
                    break;
                case DeleteVertex vertex when degrees.GetValueOrDefault(vertex.Vertex) != 0:
                    var failure = new ArgumentException("NODETACH DELETE cannot delete a node with remaining relationships. Delete its relationships or use DETACH DELETE.");
                    failure.Data["EntityForge.GqlStatus"] = "G1001";
                    throw failure;
            }
        }
    }
}
