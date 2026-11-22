using EntityForge.Db.Mapping;

namespace EntityForge.Db.Rdf;

/// <summary>Creates the embedded operation client for this profile and database.</summary>
public static class RdfOperationClientExtensions
{
    /// <summary>Gets database-scoped delivery, resolution and abort without requiring an entity map.</summary>
    public static IRdfEntityClient Operations(this IRdfDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        return new EmbeddedRdfOperationClient(database);
    }
}

internal sealed class EmbeddedRdfOperationClient(IRdfDatabase database)
    : IRdfEntityClient
{
    public Guid DatabaseIncarnation => ((IEntityMappingReplayHost)database).DatabaseIncarnation;

    public ValueTask<DatabaseCommitReceipt> ApplyAsync(RdfChangeSession session, CancellationToken cancellationToken = default) =>
        (session ?? throw new ArgumentNullException(nameof(session))).ApplyAsync(database, cancellationToken);

    public ValueTask<RdfEntityWrite> PrepareBatchAsync(IReadOnlyList<RdfEntityRequest> requests, CancellationToken cancellationToken = default) =>
        RdfEntityBatchPreparation.PrepareAsync(database, requests, cancellationToken);

    public ValueTask<RdfEntityWrite> PrepareAsync(RdfEntityRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return request.PrepareCoreAsync(database, cancellationToken);
    }

    public ValueTask<IRdfEntityReadScope> OpenReadAsync(EntityMappingManifest manifest, CancellationToken cancellationToken = default) =>
        database.Mappings().ExecuteAsync<IRdfEntityReadScope>([manifest], async () =>
            new EmbeddedRdfEntityReadScope(database, await database.AcquireSnapshotAsync(
                cancellationToken: cancellationToken).ConfigureAwait(false)), cancellationToken, allowRegistration: false);

    public ValueTask<DatabaseCommitReceipt> DeliverAsync(RdfEntityRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.DeliverCoreAsync(database, cancellationToken);
    }

    public ValueTask<DatabaseCommitReceipt> DeliverAsync(RdfEntityWrite write, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(write);
        return write.DeliverCoreAsync(database, cancellationToken);
    }

    public ValueTask<EntityOperationResolution<DatabaseCommitReceipt>> ResolveAsync(Guid operationId,
        CancellationToken cancellationToken = default) =>
        EntityOperationResolver.ResolveAsync(((IEntityOperationHost)database).Operations, operationId,
            terminal => terminal.ReplayReceipt(),
            (binding, token) => EntityNativeChangeOperation.ResolveAsync(database, binding, DatabaseProfileId.Rdf,
                RdfProfileRuntime.TransactionCodecVersion, token), cancellationToken);

    public ValueTask<EntityOperationResolution<DatabaseCommitReceipt>> AbortAsync(Guid operationId,
        CancellationToken cancellationToken = default) =>
        EntityOperationResolver.AbortAsync(((IEntityOperationHost)database).Operations, operationId, ResolveAsync, cancellationToken);
}
