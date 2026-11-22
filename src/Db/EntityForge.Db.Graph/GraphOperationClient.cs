using EntityForge.Db.Mapping;

namespace EntityForge.Db.Graph;

/// <summary>Creates the embedded operation client for this profile and database.</summary>
public static class GraphOperationClientExtensions
{
    /// <summary>Gets database-scoped delivery, resolution and abort without requiring an entity map.</summary>
    public static IGraphEntityClient Operations(this IGraphDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        return new EmbeddedGraphOperationClient(database);
    }
}

internal sealed class EmbeddedGraphOperationClient(IGraphDatabase database)
    : IGraphEntityClient
{
    public Guid DatabaseIncarnation => ((IEntityMappingReplayHost)database).DatabaseIncarnation;

    public ValueTask<DatabaseCommitReceipt> ApplyAsync(GraphChangeSession session, CancellationToken cancellationToken = default) =>
        (session ?? throw new ArgumentNullException(nameof(session))).ApplyAsync(database, cancellationToken);

    public ValueTask<GraphEntityWrite> PrepareBatchAsync(IReadOnlyList<GraphEntityRequest> requests, CancellationToken cancellationToken = default) =>
        GraphEntityBatchPreparation.PrepareAsync(database, requests, cancellationToken);

    public ValueTask<GraphEntityWrite> PrepareAsync(GraphEntityRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        EntityRecordDeliveryCodec.RequireTarget(database, request.Captured.Description.DatabaseIncarnation);
        return GraphEntityInputCompiler.FromManifest(request.Captured.Description.Manifest)
            .PrepareCapturedAsync(database, request.Captured, cancellationToken);
    }

    public ValueTask<IGraphEntityReadScope> OpenReadAsync(EntityMappingManifest manifest, CancellationToken cancellationToken = default) =>
        database.Mappings().ExecuteAsync<IGraphEntityReadScope>([manifest], async () =>
            new EmbeddedGraphEntityReadScope(database, await database.AcquireSnapshotAsync(
                cancellationToken: cancellationToken).ConfigureAwait(false)), cancellationToken, allowRegistration: false);

    public ValueTask<DatabaseCommitReceipt> DeliverAsync(GraphEntityRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.DeliverCoreAsync(database, cancellationToken);
    }

    public ValueTask<DatabaseCommitReceipt> DeliverAsync(GraphEntityWrite write, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(write);
        return write.DeliverCoreAsync(database, cancellationToken);
    }

    public ValueTask<EntityOperationResolution<DatabaseCommitReceipt>> ResolveAsync(Guid operationId,
        CancellationToken cancellationToken = default) =>
        EntityOperationResolver.ResolveAsync(((IEntityOperationHost)database).Operations, operationId,
            terminal => terminal.ReplayReceipt(),
            (binding, token) => EntityNativeChangeOperation.ResolveAsync(database, binding, DatabaseProfileId.PropertyGraph,
                GraphProfileRuntime.ProfileCodecVersion, token), cancellationToken);

    public ValueTask<EntityOperationResolution<DatabaseCommitReceipt>> AbortAsync(Guid operationId,
        CancellationToken cancellationToken = default) =>
        EntityOperationResolver.AbortAsync(((IEntityOperationHost)database).Operations, operationId, ResolveAsync, cancellationToken);
}
