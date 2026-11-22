using EntityForge.Db.Graph.Contracts;

namespace EntityForge.Db.Graph;

// Only adapts existing synchronous embedded snapshots. Every operation completes synchronously;
// remote/distributed callers use the async planner directly with their retained scope.
internal sealed class SynchronousGraphPlanningScope(IGraphMappingSnapshot snapshot, DatabaseVersion version)
    : IGraphEntityReadScope
{
    public DatabaseVersion Version => version;

    public ValueTask<GraphVertex?> GetVertexAsync(GraphVertexId identity, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(snapshot.GetVertex(identity));
    }

    public ValueTask<IReadOnlyList<GraphEdge>> OutgoingAsync(GraphVertexId identity, string label, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(snapshot.Outgoing(identity, label));
    }

    public ValueTask ValidateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
