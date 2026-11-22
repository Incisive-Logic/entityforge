using EntityForge.Db.Graph.Contracts;

namespace EntityForge.Db.Graph;

internal sealed partial record GraphReadView
{
    private GraphVertex[]? _denseVertices;

    internal IReadOnlyList<GraphVertex>? TryDenseCandidates(IReadOnlyList<GraphVertexId> candidates, CancellationToken cancellation)
    {
        if (candidates.Count != Vertices.Count || candidates.Count > 1_000_000)
        {
            return null;
        }
        // One bounded reference array per immutable view, never carried into a
        // changed view. Avoid repeated immutable-map lookups on dense scans.
        var vertices = LazyInitializer.EnsureInitialized(ref _denseVertices, () =>
        {
            var values = new GraphVertex[Vertices.Count];
            int index = 0;
            foreach (var vertex in Vertices.Values)
            {
                cancellation.ThrowIfCancellationRequested();
                values[index++] = vertex;
            }
            return values;
        });
        for (int i = 0; i < candidates.Count; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            // Preserve the requested order, including floating accumulation
            // order; a reordered or selective candidate stream uses lookup.
            if (vertices[i].Id != candidates[i])
            {
                return null;
            }
        }
        return vertices;
    }
}
