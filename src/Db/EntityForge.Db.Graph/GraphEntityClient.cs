using EntityForge.Db.Mapping;
using EntityForge.Db.Graph.Contracts;

namespace EntityForge.Db.Graph;

internal sealed class EmbeddedGraphEntityReadScope(IGraphDatabase database, GraphSnapshotLease lease) : IGraphEntityReadScope
{
    public DatabaseVersion Version => lease.Version;

    public ValueTask ValidateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!lease.IsActive)
        {
            throw new GraphSnapshotLeaseException("The retained entity view has been released.");
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask<GraphVertex?> GetVertexAsync(GraphVertexId identity, CancellationToken cancellationToken = default)
    {
        await ValidateAsync(cancellationToken).ConfigureAwait(false);
        var vertex = await database.GetVertexAsync(identity, Version, cancellationToken).ConfigureAwait(false);
        await ValidateAsync(cancellationToken).ConfigureAwait(false);
        return vertex;
    }

    public async ValueTask<IReadOnlyList<GraphEdge>> OutgoingAsync(GraphVertexId identity, string label, CancellationToken cancellationToken = default)
    {
        await ValidateAsync(cancellationToken).ConfigureAwait(false);
        var edges = await database.OutgoingAsync(identity, Version, label, cancellationToken).ConfigureAwait(false);
        await ValidateAsync(cancellationToken).ConfigureAwait(false);
        return edges;
    }

    public ValueTask DisposeAsync()
    {
        lease.Dispose();
        return ValueTask.CompletedTask;
    }
}
