using EntityForge.Db.Client;
using EntityForge.Db.Mapping;
using EntityForge.Db.Graph.Contracts;

namespace EntityForge.Db.Graph;

/// <summary>Runs the existing Graph application API against an authenticated HTTP destination.</summary>
public sealed class HttpGraphEntityClient : IGraphEntityClient, IEntityCompleteReadClient
{
    async ValueTask<object> IEntityCompleteReadClient.ReadCompleteAsync(IReadOnlyList<EntityMappingManifest> manifests,
        EntityReadRecipe recipe, Uri? source, Uri? partition, TimeSpan duration, CancellationToken token) =>
        new HttpGraphEntityReadScope(await new HttpEntityReadTransport(http, DatabaseIncarnation, GraphReadRecipePolicy.Instance)
            .CompleteAsync(manifests, recipe, source, partition, duration, token).ConfigureAwait(false));

    private readonly HttpClient http;
    private readonly HttpEntityOperationTransport operations;

    /// <summary>Creates a client for one exact database incarnation. The caller owns the HttpClient and its credentials.</summary>
    public HttpGraphEntityClient(HttpClient http, Guid databaseIncarnation)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (databaseIncarnation == Guid.Empty)
        {
            throw new ArgumentException("A database incarnation is required.", nameof(databaseIncarnation));
        }

        this.http = http;
        DatabaseIncarnation = databaseIncarnation;
        operations = new(http, databaseIncarnation, DatabaseProfileId.PropertyGraph);
    }

    /// <inheritdoc />
    public Guid DatabaseIncarnation
    {
        get;
    }

    /// <inheritdoc />
    public ValueTask<IGraphEntityReadScope> OpenReadAsync(EntityMappingManifest manifest, CancellationToken cancellationToken = default) =>
        HttpGraphEntityReadScope.OpenAsync(http, DatabaseIncarnation, manifest, TimeSpan.FromMinutes(5), cancellationToken);

    /// <inheritdoc />
    public async ValueTask<GraphEntityWrite> PrepareAsync(GraphEntityRequest request, CancellationToken cancellationToken = default) =>
        Prepared(await operations.PrepareAsync(request.OperationId, request.Export(), cancellationToken), request.OperationId, request.Captured.Description.Manifest);

    /// <inheritdoc />
    public async ValueTask<GraphEntityWrite> PrepareBatchAsync(IReadOnlyList<GraphEntityRequest> requests, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count is < 1 or > EntityRecordDeliveryCodec.MaximumBatchOperations || requests.Any(r => r is null))
        {
            throw new ArgumentException("A bounded batch is required.", nameof(requests));
        }

        Guid operation = requests[0].OperationId;
        if (requests.Any(r => r.OperationId != operation))
        {
            throw new ArgumentException("A batch has one operation identity.");
        }

        var envelope = EntityApplicationCommandCodec.Encode(DatabaseIncarnation, DatabaseProfileId.PropertyGraph, operation,
            "preparebatch", requests.Select(r => r.Export()).ToArray());
        return Prepared(await operations.PrepareAsync(operation, envelope, cancellationToken, "preparebatch"), operation, requests[0].Captured.Description.Manifest);
    }

    private GraphEntityWrite Prepared(byte[] bytes, Guid operation, EntityMappingManifest manifest)
    {
        if (EntityPreparationToken.IsToken(bytes))
        {
            _ = EntityPreparationToken.Decode(bytes, DatabaseIncarnation, operation, DatabaseProfileId.PropertyGraph);
            return new(manifest, operation, this,
                token => operations.DeliverAsync(operation, bytes, true, (DatabaseCommitReceipt receipt) => receipt, token),
                () => operations.ReleasePreparationAsync(operation, bytes));
        }
        var write = GraphEntityWrite.Restore(bytes);
        var (metadata, _) = EntityRecordDeliveryCodec.Decode(bytes, DatabaseProfileId.PropertyGraph, GraphTransactionCodec.Version);
        if (metadata.OperationId != operation || metadata.DatabaseIncarnation != DatabaseIncarnation)
        {
            throw new InvalidDataException("The prepared write identifies a different destination or operation.");
        }

        return write;
    }

    /// <inheritdoc />
    public ValueTask<DatabaseCommitReceipt> DeliverAsync(GraphEntityRequest request, CancellationToken cancellationToken = default) =>
        operations.DeliverAsync(request.OperationId, request.Export(), false, (DatabaseCommitReceipt receipt) => receipt, cancellationToken);

    /// <inheritdoc />
    public ValueTask<DatabaseCommitReceipt> DeliverAsync(GraphEntityWrite write, CancellationToken cancellationToken = default) =>
        write.IsPortable ? operations.DeliverAsync(write.Transaction.TransactionId, write.Export(), true, (DatabaseCommitReceipt receipt) => receipt, cancellationToken)
            : write.DeliverRetainedAsync(this, cancellationToken);

    /// <inheritdoc />
    public ValueTask<DatabaseCommitReceipt> ApplyAsync(GraphChangeSession session, CancellationToken cancellationToken = default) =>
        operations.DeliverAsync(session.OperationId, EntityApplicationCommandCodec.Encode(DatabaseIncarnation, DatabaseProfileId.PropertyGraph,
            session.OperationId, "apply", [session.RequestBytes()]), false, (DatabaseCommitReceipt receipt) => receipt, cancellationToken, "apply");

    /// <inheritdoc />
    public ValueTask<EntityOperationResolution<DatabaseCommitReceipt>> ResolveAsync(Guid operationId, CancellationToken cancellationToken = default) =>
        operations.ResolveAsync(operationId, false, (DatabaseCommitReceipt receipt) => receipt, cancellationToken);

    /// <inheritdoc />
    public ValueTask<EntityOperationResolution<DatabaseCommitReceipt>> AbortAsync(Guid operationId, CancellationToken cancellationToken = default) =>
        operations.ResolveAsync(operationId, true, (DatabaseCommitReceipt receipt) => receipt, cancellationToken);
}
