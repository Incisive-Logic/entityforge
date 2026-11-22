using System.Runtime.CompilerServices;
using EntityForge.Db.Graph.Contracts;

namespace EntityForge.Db.Graph;

internal sealed partial class GraphDatabaseEngine
{
    internal async ValueTask<GraphQuerySnapshot> CaptureScanSnapshotAsync(GraphSnapshotLease lease, CancellationToken cancellation)
    {
        ValidateSnapshot(lease);
        var view = await _kernel.ReadPublishedAsync(_ => _runtime.CaptureReadView(lease.Version), cancellation).ConfigureAwait(false);
        return new(lease.Version, view, view.StatisticsFor(lease.Version));
    }

    // A request-owned forward enumeration avoids repeatedly selecting the next
    // IDs from the whole dictionary. Public resumable cursors remain unchanged.
    internal async IAsyncEnumerable<(IReadOnlyList<GraphCommittedEffect> Effects, bool Complete)> ReadScanPagesAsync(
        GraphSnapshotLease lease, int pageSize, bool includeVertices, bool includeEdges,
        [EnumeratorCancellation] CancellationToken cancellation)
    {
        ValidateSnapshot(lease);
        if (pageSize is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }
        var view = await _kernel.ReadPublishedAsync(_ => _runtime.CaptureReadView(lease.Version), cancellation).ConfigureAwait(false);
        IEnumerable<GraphCommittedEffect> effects = includeVertices
            ? view.Vertices.Values.Select(vertex => (GraphCommittedEffect)new GraphVertexCreated(vertex)) : [];
        if (includeEdges)
        {
            effects = effects.Concat(view.Edges.Values.Select(edge => (GraphCommittedEffect)new GraphEdgeCreated(edge)));
        }
        var page = new List<GraphCommittedEffect>(pageSize);
        foreach (var effect in effects)
        {
            cancellation.ThrowIfCancellationRequested();
            page.Add(effect);
            if (page.Count == pageSize)
            {
                ValidateSnapshot(lease);
                yield return (page, false);
                page = new(pageSize);
            }
        }
        cancellation.ThrowIfCancellationRequested();
        ValidateSnapshot(lease);
        yield return (page, true);
    }
}
