using EntityForge.Db.Client;
using EntityForge.Db.Mapping;
using EntityForge.Semantics;

namespace EntityForge.Db.Semantic;

/// <summary>Runs the existing Semantic application API against one authenticated HTTP destination.</summary>
public sealed partial class HttpSemanticEntityClient : IEntityCompleteReadClient
{
    async ValueTask<object> IEntityCompleteReadClient.ReadCompleteAsync(IReadOnlyList<EntityMappingManifest> manifests,
        EntityReadRecipe recipe, Uri? source, Uri? partition, TimeSpan duration, CancellationToken token)
    {
        var scope = await new HttpEntityReadTransport(_client, DatabaseIncarnation, SemanticReadRecipePolicy.Instance)
            .CompleteAsync(manifests, recipe, source, partition, duration, token).ConfigureAwait(false);
        try
        {
            return new HttpSemanticEntityReadScope(scope, SemanticEntityReadDecoder.Guard(scope.ContributionGuard), recipe.IncludeContributionGuard);
        }
        catch
        {
            await scope.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private readonly HttpEntityOperationTransport operations;

    /// <summary>Creates a client for an exact database incarnation. The caller owns the HttpClient and credentials.</summary>
    public HttpSemanticEntityClient(HttpClient http, Guid databaseIncarnation)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (databaseIncarnation == Guid.Empty)
        {
            throw new ArgumentException("A database incarnation is required.", nameof(databaseIncarnation));
        }

        _client = http;
        DatabaseIncarnation = databaseIncarnation;
        operations = new(http, databaseIncarnation, DatabaseProfileId.Semantic);
    }

    /// <inheritdoc />
    public Guid DatabaseIncarnation
    {
        get;
    }

    /// <inheritdoc />
    public ISemanticContributionRevisionWriter Revisions => this;

    /// <inheritdoc />
    public ValueTask<ISemanticEntityReadScope> OpenReadAsync(IReadOnlyList<EntityMappingManifest> manifests, Uri sourceSystem,
        Uri sourcePartition, TimeSpan duration, CancellationToken cancellationToken = default) =>
        HttpSemanticEntityReadScope.OpenAsync(_client, DatabaseIncarnation, manifests, sourceSystem, sourcePartition, duration, cancellationToken);

    /// <inheritdoc />
    public async ValueTask<SemanticEntityWrite> PrepareAsync(SemanticEntityRequest request, CancellationToken cancellationToken = default)
    {
        var bytes = await operations.PrepareAsync(request.OperationId, request.Export(), cancellationToken);
        if (EntityPreparationToken.IsToken(bytes))
        {
            _ = EntityPreparationToken.Decode(bytes, DatabaseIncarnation, request.OperationId, DatabaseProfileId.Semantic);
            var application = request._metadata.Application;
            return new(request.OperationId, new(application.GenerationId, application.Version, request.OperationId.ToString("N")), this,
                token => operations.DeliverAsync<SessionCommitResult>(request.OperationId, bytes, true, Receipt, token),
                () => operations.ReleasePreparationAsync(request.OperationId, bytes));
        }
        var write = SemanticEntityWrite.Restore(bytes);
        var (metadata, _) = EntityRecordDeliveryCodec.Decode<SemanticEntityDeliveryCodec.Metadata>(bytes);
        if (write.OperationId != request.OperationId || metadata.DatabaseIncarnation != DatabaseIncarnation)
        {
            throw new InvalidDataException("The prepared write identifies another destination or operation.");
        }

        return write;
    }

    /// <inheritdoc />
    public ValueTask<SessionCommitResult> DeliverAsync(SemanticEntityRequest request, CancellationToken cancellationToken = default) =>
        operations.DeliverAsync<SessionCommitResult>(request.OperationId, request.Export(), false, Receipt, cancellationToken);

    /// <inheritdoc />
    public ValueTask<SessionCommitResult> DeliverAsync(SemanticEntityWrite write, CancellationToken cancellationToken = default) =>
        write.IsPortable ? operations.DeliverAsync<SessionCommitResult>(write.OperationId, write.Export(), true, Receipt, cancellationToken)
            : write.DeliverRetainedAsync(this, cancellationToken);

    /// <inheritdoc />
    public ValueTask<SessionCommitResult> ApplyAsync(SemanticSessionEnvelope session, CancellationToken cancellationToken = default) =>
        SendSession(session, "apply", cancellationToken);

    /// <inheritdoc />
    public ValueTask<SessionCommitResult> BulkLoadAsync(SemanticSessionEnvelope session, CancellationToken cancellationToken = default) =>
        SendSession(session, "bulk", cancellationToken);

    private ValueTask<SessionCommitResult> SendSession(SemanticSessionEnvelope session, string action, CancellationToken cancellation) =>
        operations.DeliverAsync<SessionCommitResult>(session.Start.SessionId, EntityApplicationCommandCodec.Encode(DatabaseIncarnation, DatabaseProfileId.Semantic,
            session.Start.SessionId, action, [SemanticEntityDeliveryCodec.ExportChanges(DatabaseIncarnation, session)]), false, Receipt, cancellation, action);

    private static DatabaseCommitReceipt Receipt(SessionCommitResult receipt)
    {
        if (receipt.SessionId != receipt.TransactionId)
        {
            throw new InvalidDataException("Semantic session receipt identity differs from its operation.");
        }

        return receipt.Receipt;
    }

    /// <inheritdoc />
    public ValueTask<EntityOperationResolution<SessionCommitResult>> ResolveAsync(Guid operationId, CancellationToken cancellationToken = default) =>
        operations.ResolveAsync<SessionCommitResult>(operationId, false, Receipt, cancellationToken);

    /// <inheritdoc />
    public ValueTask<EntityOperationResolution<SessionCommitResult>> AbortAsync(Guid operationId, CancellationToken cancellationToken = default) =>
        operations.ResolveAsync<SessionCommitResult>(operationId, true, Receipt, cancellationToken);
}
