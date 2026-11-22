using EntityForge.Db.Graph.Contracts;

namespace EntityForge.Db.Gql;

internal static partial class GraphGqlMutationCompiler
{
    internal static IReadOnlyList<GraphMutation> ExpandDetachDeletes(IReadOnlyList<GraphMutation> mutations, IEnumerable<GraphEdge> edges)
    {
        var vertices = mutations.OfType<DeleteVertex>().Select(item => item.Vertex).ToHashSet();
        var removed = mutations.OfType<DeleteEdge>().Select(item => item.Edge).ToHashSet();
        foreach (var edge in edges)
        {
            if (vertices.Contains(edge.From) || vertices.Contains(edge.To))
            {
                removed.Add(edge.Id);
            }
            if (removed.Count + vertices.Count > MaximumMatchedMutations)
            {
                throw new ArgumentException("DETACH DELETE exceeds 10,000 native mutations.");
            }
        }
        return removed.Select(id => (GraphMutation)new DeleteEdge(id))
            .Concat(vertices.Select(id => new DeleteVertex(id))).ToArray();
    }
}
