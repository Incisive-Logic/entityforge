using EntityForge.Db.Mapping;
using EntityForge.Db.Graph.Contracts;

namespace EntityForge.Db.Graph;

/// <summary>Provides conventional mapped-entity operations without hiding native Graph semantics.</summary>
public sealed partial class GraphEntityStore<T>
{
    /// <summary>Prepares a save through the profile client.</summary>
    public ValueTask<GraphEntityWrite> PrepareSaveAsync(T entity, Guid operationId, CancellationToken cancellationToken = default) =>
        PrepareSaveAsync(entity, new GraphEntityWriteOptions(operationId), cancellationToken);

    /// <summary>Prepares deletion through the profile client.</summary>
    public ValueTask<GraphEntityWrite> PrepareDeleteAsync(object identity, Guid operationId, CancellationToken cancellationToken = default) =>
        PrepareDeleteAsync(identity, new GraphEntityWriteOptions(operationId), cancellationToken);

    /// <summary>Captures locally, then selects a frozen native write through the receiving client.</summary>
    public ValueTask<GraphEntityWrite> PrepareSaveAsync(T entity, GraphEntityWriteOptions options, CancellationToken cancellationToken = default) =>
        _client.PrepareAsync(CaptureSave(entity, options), cancellationToken);

    /// <summary>Captures deletion, then selects a frozen native write through the receiving client.</summary>
    public ValueTask<GraphEntityWrite> PrepareDeleteAsync(object identity, GraphEntityWriteOptions options, CancellationToken cancellationToken = default) =>
        _client.PrepareAsync(CaptureDelete(identity, options), cancellationToken);

    /// <summary>Gets the exact mapping interpretation.</summary>
    public GraphResolvedEntityMapping<T> Mapping
    {
        get;
    }

    private readonly IGraphEntityClient _client;
    private readonly GraphEntityMap<T> map;

    internal GraphEntityStore(IGraphEntityClient client, GraphResolvedEntityMapping<T> resolved)
    {
        _client = client;
        Mapping = resolved;
        map = resolved.Map;
    }

    /// <summary>Creates an atomic write batch. Dispose the batch to release any retained preparation.</summary>
    public GraphEntityWriteBatch<T> CreateWriteBatch(Guid? operationId = null, DatabaseVersion? expectedVersion = null) => new(_client, this, operationId ?? EntityOperationIds.New(), expectedVersion);

    /// <summary>Saves the complete map-owned representation and returns its durable outcome.</summary>
    /// <remarks>The operation identity binds immutable captured values to one durable plan. Retain CaptureSave for delivery across processes.</remarks>
    public ValueTask<DatabaseCommitReceipt> SaveAsync(T entity, Guid? transactionId = null,
        CancellationToken cancellationToken = default) =>
        CaptureSave(entity, new(transactionId ?? EntityOperationIds.New())).DeliverAsync(_client, cancellationToken);

    /// <summary>Plans a new save with an operation identity and optional concurrency guard.</summary>
    /// <remarks>The operation identity binds immutable captured values to one durable plan. Retain CaptureSave for delivery across processes.</remarks>
    public ValueTask<DatabaseCommitReceipt> SaveAsync(T entity, GraphEntityWriteOptions options,
        CancellationToken cancellationToken = default) =>
        CaptureSave(entity, options).DeliverAsync(_client, cancellationToken);

    /// <summary>Removes the map-owned representation while preserving unrelated native state.</summary>
    /// <remarks>The operation identity binds one durable deletion plan. Retain CaptureDelete for delivery across processes.</remarks>
    public ValueTask<DatabaseCommitReceipt> DeleteAsync(object identity, Guid? transactionId = null,
        CancellationToken cancellationToken = default) =>
        CaptureDelete(identity, new(transactionId ?? EntityOperationIds.New())).DeliverAsync(_client, cancellationToken);

    /// <summary>Removes mapped state with explicit replay identity and optional optimistic concurrency.</summary>
    /// <remarks>The operation identity binds one durable deletion plan. Retain CaptureDelete for delivery across processes.</remarks>
    public ValueTask<DatabaseCommitReceipt> DeleteAsync(object identity, GraphEntityWriteOptions options,
        CancellationToken cancellationToken = default) =>
        CaptureDelete(identity, options).DeliverAsync(_client, cancellationToken);

    /// <summary>Finds one entity using the same retained asynchronous read as ReadAsync.</summary>
    public async ValueTask<T?> FindAsync(object identity, CancellationToken cancellationToken = default) =>
        (await ReadAsync(identity, cancellationToken).ConfigureAwait(false)) is { } read ? read.Value : default;

    /// <summary>Materializes the complete mapped entity through one retained profile view.</summary>
    public async ValueTask<EntityReadResult<T>?> ReadAsync(object identity, CancellationToken cancellationToken = default)
    {
        var scope = _client is IEntityCompleteReadClient complete
            ? (IGraphEntityReadScope)await complete.ReadCompleteAsync([Mapping.Manifest], GraphReadRecipeBuilder.Create(map, identity),
                null, null, TimeSpan.FromMinutes(5), cancellationToken).ConfigureAwait(false)
            : _client is IEntityReadRecipeProvider targeted
                ? (IGraphEntityReadScope)await targeted.OpenRecipeAsync([Mapping.Manifest], GraphReadRecipeBuilder.Create(map, identity),
                    null, null, TimeSpan.FromMinutes(5), cancellationToken).ConfigureAwait(false)
                : await _client.OpenReadAsync(Mapping.Manifest, cancellationToken).ConfigureAwait(false);
        return await EntityReadScopeLifetime.RunAsync<EntityReadResult<T>?>(scope, async () =>
        {
            if (scope is IEntityReadRecipeScope recipeScope)
            {
                await recipeScope.PrefetchRecipeAsync(GraphReadRecipeBuilder.Create(map, identity), cancellationToken).ConfigureAwait(false);
            }

            var result = await GraphEntityMaterialization.ReadEntityAsync(scope, map, identity, cancellationToken).ConfigureAwait(false);
            return result;
        }, () => scope.ValidateAsync(cancellationToken)).ConfigureAwait(false);
    }
}

/// <summary>Captures an ordered batch and prepares it through the receiving profile client.</summary>
public sealed class GraphEntityWriteBatch<T> : IAsyncDisposable
{
    /// <summary>Maximum operations in one atomic mapped batch.</summary>
    public const int MaximumOperations = EntityRecordDeliveryCodec.MaximumBatchOperations;

    /// <summary>Maximum encoded batch size.</summary>
    public const int MaximumEncodedBytes = EntityRecordDeliveryCodec.MaximumPayloadBytes;
    private readonly IGraphEntityClient _client;
    private readonly GraphEntityStore<T> _store;
    private readonly GraphEntityWriteOptions _options;
    private readonly List<GraphEntityRequest> _requests = [];
    private GraphEntityRequest[]? _frozen;
    private GraphEntityWrite? _prepared;
    private bool _preparing;
    private bool _disposed;

    /// <summary>Releases the selected preparation without aborting the logical operation.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_preparing)
        {
            throw new InvalidOperationException("A batch does not support disposal during preparation.");
        }

        _disposed = true;
        if (_prepared is not null)
        {
            await _prepared.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal GraphEntityWriteBatch(IGraphEntityClient client, GraphEntityStore<T> store,
        Guid operationId, DatabaseVersion? expectedVersion = null)
    {
        _client = client;
        _store = store;
        _options = new(operationId, expectedVersion);
    }

    /// <summary>Captures a complete mapped replacement.</summary>
    public GraphEntityWriteBatch<T> Save(T entity)
    {
        Add(_store.CaptureSave(entity, _options));
        return this;
    }

    /// <summary>Captures removal of one mapped representation.</summary>
    public GraphEntityWriteBatch<T> Delete(object identity)
    {
        Add(_store.CaptureDelete(identity, _options));
        return this;
    }

    /// <summary>Selects one native transaction under one receiving-side snapshot without executing it.</summary>
    public async ValueTask<GraphEntityWrite> PrepareAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_preparing)
        {
            throw new InvalidOperationException("A batch does not support concurrent preparation.");
        }

        if (_prepared is not null)
        {
            return _prepared;
        }

        if (_requests.Count == 0)
        {
            throw new InvalidOperationException("A mapped write batch cannot be empty.");
        }

        _frozen ??= _requests.ToArray();
        _preparing = true;
        try
        {
            return _prepared = await _client.PrepareBatchAsync(_frozen, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _preparing = false;
        }
    }

    /// <summary>Delivers the selected transaction atomically through the client; retries reuse its frozen plan.</summary>
    public async ValueTask<DatabaseCommitReceipt> CommitAsync(CancellationToken cancellationToken = default) =>
        await _client.DeliverAsync(await PrepareAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

    private void Add(GraphEntityRequest request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_frozen is not null)
        {
            throw new InvalidOperationException("A frozen mapped write batch cannot be changed.");
        }

        if (_requests.Count == MaximumOperations)
        {
            throw new InvalidOperationException("The mapped batch exceeds its operation limit.");
        }

        _requests.Add(request);
    }
}
