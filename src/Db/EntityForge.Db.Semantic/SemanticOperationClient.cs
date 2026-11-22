using EntityForge.Db.Mapping;
using EntityForge.Semantics.Protobuf;
using EntityForge.Semantics;

namespace EntityForge.Db.Semantic;

/// <summary>Creates the embedded operation client for this profile and database.</summary>
public static class SemanticOperationClientExtensions
{
    /// <summary>Gets database-scoped delivery, resolution and abort without requiring an entity map.</summary>
    public static ISemanticEntityClient Operations(this ISemanticDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        return new EmbeddedSemanticOperationClient(database);
    }
}

internal sealed class EmbeddedSemanticOperationClient(ISemanticDatabase database)
    : ISemanticEntityClient
{
    public Guid DatabaseIncarnation => ((ISemanticMappedExecution)database).DatabaseIncarnation;

    public ISemanticContributionRevisionWriter Revisions => database;

    public ValueTask<SessionCommitResult> ApplyAsync(SemanticSessionEnvelope session, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[] envelope = SemanticEntityDeliveryCodec.ExportChanges(DatabaseIncarnation, session);
        var frozen = SemanticEntityDeliveryCodec.Session(envelope);
        var codec = new SemanticChangeSessionTransportCodec();
        if (!new SemanticChangeSessionValidator().Validate(frozen.Start, frozen.Pages, frozen.Close).IsValid)
        {
            throw new InvalidDataException("The change session has inconsistent framing or hashes.");
        }

        var hashes = frozen.Close.OperationHash is null
            ? (ContentHash: codec.ComputeContentHash(frozen.Pages), OperationHash: (string?)null)
            : codec.ComputeHashes(frozen.Start, frozen.Pages);
        if (hashes.ContentHash != frozen.Close.ContentHash ||
            (frozen.Close.OperationHash is not null && hashes.OperationHash != frozen.Close.OperationHash))
        {
            throw new InvalidDataException("The change session has inconsistent framing or hashes.");
        }

        return ((IEntityOperationHost)database).Operations.ExecutePreparedAsync(frozen.Start.SessionId,
            retained => retained.AsSpan().SequenceEqual(envelope),
            () => database.ApplyAsync(frozen, cancellationToken), cancellationToken,
            envelope, SemanticEntityDeliveryCodec.PreparedIdentity(envelope), SemanticEntityDeliveryCodec.Replay,
            receipt => receipt.Receipt, DatabaseIncarnation);
    }

    public ValueTask<SessionCommitResult> BulkLoadAsync(SemanticSessionEnvelope session, CancellationToken cancellationToken = default) =>
        database.BulkLoadAsync(session, cancellationToken);

    public ValueTask<SemanticEntityWrite> PrepareAsync(SemanticEntityRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(request.PrepareCore(database));
    }

    public ValueTask<ISemanticEntityReadScope> OpenReadAsync(IReadOnlyList<EntityMappingManifest> manifests,
        Uri sourceSystem, Uri sourcePartition, TimeSpan duration, CancellationToken cancellationToken = default) =>
        database.Mappings().ExecuteAsync(manifests, () =>
            ((ISemanticMappedExecution)database).AcquireMappedReadAsync(sourceSystem, sourcePartition, duration, cancellationToken),
            cancellationToken, allowRegistration: false);

    public ValueTask<SessionCommitResult> DeliverAsync(SemanticEntityRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.DeliverCoreAsync(database, cancellationToken);
    }

    public ValueTask<SessionCommitResult> DeliverAsync(SemanticEntityWrite write, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(write);
        return new SemanticEmbeddedWriteExecution(database, write).DeliverCoreAsync(cancellationToken);
    }

    public ValueTask<EntityOperationResolution<SessionCommitResult>> ResolveAsync(Guid operationId,
        CancellationToken cancellationToken = default) =>
        EntityOperationResolver.ResolveAsync(((IEntityOperationHost)database).Operations, operationId,
            SemanticEntityDeliveryCodec.Replay,
            (binding, token) => ((ISemanticMappedExecution)database).ResolveMappedReplayAsync(
                SemanticEntityDeliveryCodec.Session(binding.Plan), token), cancellationToken);

    public ValueTask<EntityOperationResolution<SessionCommitResult>> AbortAsync(Guid operationId,
        CancellationToken cancellationToken = default) =>
        EntityOperationResolver.AbortAsync(((IEntityOperationHost)database).Operations, operationId, ResolveAsync, cancellationToken);
}
