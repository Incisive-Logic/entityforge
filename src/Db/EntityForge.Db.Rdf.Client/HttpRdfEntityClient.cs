using EntityForge.Db.Client;
using EntityForge.Db.Mapping;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Rdf;

/// <summary>Runs mapped RDF application operations against an HTTP destination.</summary>
/// <remarks>The caller configures authentication and the base address on the supplied HTTP client. Delivery failures or cancellation can leave the commit outcome uncertain; use operation resolution before issuing a different logical write.</remarks>
public sealed class HttpRdfEntityClient : IRdfEntityClient, IEntityCompleteReadClient
{
    async ValueTask<object> IEntityCompleteReadClient.ReadCompleteAsync(IReadOnlyList<EntityMappingManifest> manifests,
        EntityReadRecipe recipe, Uri? source, Uri? partition, TimeSpan duration, CancellationToken token) =>
        new HttpRdfEntityReadScope(await new HttpEntityReadTransport(http, DatabaseIncarnation, RdfReadRecipePolicy.Instance)
            .CompleteAsync(manifests, recipe, source, partition, duration, token).ConfigureAwait(false));

    private readonly HttpClient http;
    private readonly HttpEntityOperationTransport operations;

    /// <summary>Creates a client for one exact database incarnation. The caller owns the HttpClient and its credentials.</summary>
    /// <param name="http">Caller-owned transport configured with the destination base address and credentials.</param>
    /// <param name="databaseIncarnation">Nonempty incarnation obtained from the intended database, not an application-generated identifier.</param>
    /// <exception cref="ArgumentNullException">The HTTP client is null.</exception>
    /// <exception cref="ArgumentException">The incarnation is empty.</exception>
    public HttpRdfEntityClient(HttpClient http, Guid databaseIncarnation)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (databaseIncarnation == Guid.Empty)
        {
            throw new ArgumentException("A database incarnation is required.", nameof(databaseIncarnation));
        }

        this.http = http;
        DatabaseIncarnation = databaseIncarnation;
        operations = new(http, databaseIncarnation, DatabaseProfileId.Rdf);
    }

    /// <inheritdoc />
    public Guid DatabaseIncarnation
    {
        get;
    }

    /// <inheritdoc />
    public ValueTask<IRdfEntityReadScope> OpenReadAsync(EntityMappingManifest manifest, CancellationToken cancellationToken = default) =>
        HttpRdfEntityReadScope.OpenAsync(http, DatabaseIncarnation, manifest, TimeSpan.FromMinutes(5), cancellationToken);

    /// <inheritdoc />
    public async ValueTask<RdfEntityWrite> PrepareAsync(RdfEntityRequest request, CancellationToken cancellationToken = default) =>
        Prepared(await operations.PrepareAsync(request.OperationId, request.Export(), cancellationToken), request.OperationId, request.Captured.Description.Manifest);

    /// <inheritdoc />
    public async ValueTask<RdfEntityWrite> PrepareBatchAsync(IReadOnlyList<RdfEntityRequest> requests, CancellationToken cancellationToken = default)
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

        var envelope = EntityApplicationCommandCodec.Encode(DatabaseIncarnation, DatabaseProfileId.Rdf, operation,
            "preparebatch", requests.Select(r => r.Export()).ToArray());
        return Prepared(await operations.PrepareAsync(operation, envelope, cancellationToken, "preparebatch"), operation, requests[0].Captured.Description.Manifest);
    }

    private RdfEntityWrite Prepared(byte[] bytes, Guid operation, EntityMappingManifest manifest)
    {
        if (EntityPreparationToken.IsToken(bytes))
        {
            _ = EntityPreparationToken.Decode(bytes, DatabaseIncarnation, operation, DatabaseProfileId.Rdf);
            return new(manifest, operation, this,
                token => operations.DeliverAsync(operation, bytes, true, (DatabaseCommitReceipt receipt) => receipt, token),
                () => operations.ReleasePreparationAsync(operation, bytes));
        }
        var write = RdfEntityWrite.Restore(bytes);
        var (metadata, _) = EntityRecordDeliveryCodec.Decode(bytes, DatabaseProfileId.Rdf, RdfTransactionCodec.Version);
        if (metadata.OperationId != operation || metadata.DatabaseIncarnation != DatabaseIncarnation)
        {
            throw new InvalidDataException("The prepared write identifies a different destination or operation.");
        }

        return write;
    }

    /// <inheritdoc />
    public ValueTask<DatabaseCommitReceipt> DeliverAsync(RdfEntityRequest request, CancellationToken cancellationToken = default) =>
        operations.DeliverAsync(request.OperationId, request.Export(), false, (DatabaseCommitReceipt receipt) => receipt, cancellationToken);

    /// <inheritdoc />
    public ValueTask<DatabaseCommitReceipt> DeliverAsync(RdfEntityWrite write, CancellationToken cancellationToken = default) =>
        write.IsPortable ? operations.DeliverAsync(write.Transaction.TransactionId, write.Export(), true, (DatabaseCommitReceipt receipt) => receipt, cancellationToken)
            : write.DeliverRetainedAsync(this, cancellationToken);

    /// <inheritdoc />
    public ValueTask<DatabaseCommitReceipt> ApplyAsync(RdfChangeSession session, CancellationToken cancellationToken = default) =>
        operations.DeliverAsync(session.OperationId, EntityApplicationCommandCodec.Encode(DatabaseIncarnation, DatabaseProfileId.Rdf,
            session.OperationId, "apply", [session.RequestBytes()]), false, (DatabaseCommitReceipt receipt) => receipt, cancellationToken, "apply");

    /// <inheritdoc />
    public ValueTask<EntityOperationResolution<DatabaseCommitReceipt>> ResolveAsync(Guid operationId, CancellationToken cancellationToken = default) =>
        operations.ResolveAsync(operationId, false, (DatabaseCommitReceipt receipt) => receipt, cancellationToken);

    /// <inheritdoc />
    public ValueTask<EntityOperationResolution<DatabaseCommitReceipt>> AbortAsync(Guid operationId, CancellationToken cancellationToken = default) =>
        operations.ResolveAsync(operationId, true, (DatabaseCommitReceipt receipt) => receipt, cancellationToken);
}
