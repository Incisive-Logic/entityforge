using EntityForge.Db.Graph.Contracts;

namespace EntityForge.Db.Graph;

// Mapping needs complete adjacency, not authoritative edge properties. Keep that distinction explicit.
internal interface IGraphEntityPlanningScope
{
    DatabaseVersion Version
    {
        get;
    }

    ValueTask<GraphVertex?> GetVertexAsync(GraphVertexId identity, CancellationToken cancellationToken);

    ValueTask PrefetchVerticesAsync(IReadOnlyList<GraphVertexId> identities, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    ValueTask<IReadOnlyList<GraphEdgeReference>> OutgoingAsync(GraphVertexId identity, string label, CancellationToken cancellationToken);

    ValueTask ValidateAsync(CancellationToken cancellationToken);
}

internal sealed class GraphEntityPlanningScope(IGraphEntityReadScope view) : IGraphEntityPlanningScope
{
    public DatabaseVersion Version => view.Version;

    public ValueTask<GraphVertex?> GetVertexAsync(GraphVertexId identity, CancellationToken cancellationToken) =>
        view.GetVertexAsync(identity, cancellationToken);

    public async ValueTask<IReadOnlyList<GraphEdgeReference>> OutgoingAsync(GraphVertexId identity, string label, CancellationToken cancellationToken) =>
        (await view.OutgoingAsync(identity, label, cancellationToken).ConfigureAwait(false))
            .Select(edge => new GraphEdgeReference(edge.Id, edge.From, edge.To, edge.Label)).ToArray();

    public ValueTask ValidateAsync(CancellationToken cancellationToken) => view.ValidateAsync(cancellationToken);
}
