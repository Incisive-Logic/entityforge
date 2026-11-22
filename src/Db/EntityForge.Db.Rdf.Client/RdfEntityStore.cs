using EntityForge.Db.Mapping;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Rdf;

/// <summary>Provides conventional mapped-entity operations over map-owned default-graph statements.</summary>
public sealed partial class RdfEntityStore<T>
{
    /// <summary>Prepares a save through the profile client.</summary>
    /// <param name="entity">Entity whose current mapped values are captured.</param>
    /// <param name="operationId">Nonempty identity for the logical write.</param>
    /// <param name="cancellationToken">Cancels preparation.</param>
    /// <returns>A selected write that has not yet been delivered; dispose it after use.</returns>
    public ValueTask<RdfEntityWrite> PrepareSaveAsync(T entity, Guid operationId, CancellationToken cancellationToken = default) =>
        PrepareSaveAsync(entity, new RdfEntityWriteOptions(operationId), cancellationToken);

    /// <summary>Prepares deletion through the profile client.</summary>
    /// <param name="identity">Mapped identity whose map-owned statements should be removed.</param>
    /// <param name="operationId">Nonempty identity for the logical write.</param>
    /// <param name="cancellationToken">Cancels preparation.</param>
    /// <returns>A selected deletion that has not yet been delivered; dispose it after use.</returns>
    public ValueTask<RdfEntityWrite> PrepareDeleteAsync(object identity, Guid operationId, CancellationToken cancellationToken = default) =>
        PrepareDeleteAsync(identity, new RdfEntityWriteOptions(operationId), cancellationToken);

    /// <summary>Captures locally, then selects a frozen native write through the receiving client.</summary>
    /// <param name="entity">Entity whose current mapped values are captured.</param>
    /// <param name="options">Operation identity and optional complete subject-state concurrency guard.</param>
    /// <param name="cancellationToken">Cancels preparation.</param>
    /// <returns>A caller-owned selected write that has not yet been delivered.</returns>
    /// <remarks>Dispose the write to release any retained preparation. Preparation and disposal do not themselves commit or durably abort the operation.</remarks>
    public ValueTask<RdfEntityWrite> PrepareSaveAsync(T entity, RdfEntityWriteOptions options, CancellationToken cancellationToken = default) =>
        _client.PrepareAsync(CaptureSave(entity, options), cancellationToken);

    /// <summary>Captures deletion, then selects a frozen native write through the receiving client.</summary>
    /// <param name="identity">Mapped identity whose map-owned statements should be removed.</param>
    /// <param name="options">Operation identity and optional complete subject-state concurrency guard.</param>
    /// <param name="cancellationToken">Cancels preparation.</param>
    /// <returns>A caller-owned selected deletion that has not yet been delivered.</returns>
    /// <remarks>Dispose the write to release any retained preparation. Unrelated RDF statements remain outside the mapped deletion.</remarks>
    public ValueTask<RdfEntityWrite> PrepareDeleteAsync(object identity, RdfEntityWriteOptions options, CancellationToken cancellationToken = default) =>
        _client.PrepareAsync(CaptureDelete(identity, options), cancellationToken);

    /// <summary>Gets the exact mapping interpretation.</summary>
    public RdfResolvedEntityMapping<T> Mapping
    {
        get;
    }

    private readonly IRdfEntityClient _client;
    private readonly RdfEntityMap<T> map;

    internal RdfEntityStore(IRdfEntityClient client, RdfResolvedEntityMapping<T> resolved)
    {
        _client = client;
        Mapping = resolved;
        map = resolved.Map;
    }

    /// <summary>Creates an atomic mapped write batch.</summary>
    /// <param name="operationId">Shared identity for the batch, or null to generate a new one.</param>
    /// <returns>A caller-owned batch that must be disposed after use.</returns>
    /// <remarks>Capture save/delete operations before preparing. Preparation freezes the batch; it cannot subsequently be extended.</remarks>
    public RdfEntityWriteBatch<T> CreateWriteBatch(Guid? operationId = null) => new(_client, this, operationId ?? EntityOperationIds.New());

    /// <summary>Saves the complete map-owned default-graph representation.</summary>
    /// <remarks>The operation identity binds immutable captured values to one durable plan. Retain CaptureSave for delivery across processes.</remarks>
    /// <param name="entity">Entity whose current mapped values replace its map-owned state.</param>
    /// <param name="transactionId">Identity of this logical operation, or null to generate a new one.</param>
    /// <param name="cancellationToken">Cancels delivery; cancellation does not establish that no commit occurred.</param>
    /// <returns>The destination's commit receipt.</returns>
    public ValueTask<DatabaseCommitReceipt> SaveAsync(T entity, Guid? transactionId = null,
        CancellationToken cancellationToken = default) =>
        CaptureSave(entity, new(transactionId ?? EntityOperationIds.New())).DeliverAsync(_client, cancellationToken);

    /// <summary>Plans a new save with an operation identity and optional concurrency guard.</summary>
    /// <remarks>The operation identity binds immutable captured values to one durable plan. Retain CaptureSave for delivery across processes.</remarks>
    /// <param name="entity">Entity whose current mapped values replace its map-owned state.</param>
    /// <param name="options">Operation identity and optional complete subject-state concurrency guard.</param>
    /// <param name="cancellationToken">Cancels delivery; use operation resolution after an uncertain outcome.</param>
    /// <returns>The destination's commit receipt.</returns>
    public ValueTask<DatabaseCommitReceipt> SaveAsync(T entity, RdfEntityWriteOptions options,
        CancellationToken cancellationToken = default) =>
        CaptureSave(entity, options).DeliverAsync(_client, cancellationToken);

    /// <summary>Removes map-owned statements while preserving unrelated RDF statements.</summary>
    /// <remarks>The operation identity binds one durable deletion plan. Retain CaptureDelete for delivery across processes.</remarks>
    /// <param name="identity">Mapped identity whose representation should be removed.</param>
    /// <param name="transactionId">Identity of this logical operation, or null to generate a new one.</param>
    /// <param name="cancellationToken">Cancels delivery; cancellation does not establish that no commit occurred.</param>
    /// <returns>The destination's commit receipt.</returns>
    public ValueTask<DatabaseCommitReceipt> DeleteAsync(object identity, Guid? transactionId = null,
        CancellationToken cancellationToken = default) =>
        CaptureDelete(identity, new(transactionId ?? EntityOperationIds.New())).DeliverAsync(_client, cancellationToken);

    /// <summary>Removes mapped state with explicit replay identity and optional optimistic concurrency.</summary>
    /// <remarks>The operation identity binds one durable deletion plan. Retain CaptureDelete for delivery across processes.</remarks>
    /// <param name="identity">Mapped identity whose representation should be removed.</param>
    /// <param name="options">Operation identity and optional complete subject-state concurrency guard.</param>
    /// <param name="cancellationToken">Cancels delivery; use operation resolution after an uncertain outcome.</param>
    /// <returns>The destination's commit receipt.</returns>
    public ValueTask<DatabaseCommitReceipt> DeleteAsync(object identity, RdfEntityWriteOptions options,
        CancellationToken cancellationToken = default) =>
        CaptureDelete(identity, options).DeliverAsync(_client, cancellationToken);

    /// <summary>Finds one entity using the same retained asynchronous read as ReadAsync.</summary>
    /// <param name="identity">Mapped identity to look up in the default graph.</param>
    /// <param name="cancellationToken">Cancels the read and materialization.</param>
    /// <returns>The mapped entity, or the default value of <typeparamref name="T"/> if absent.</returns>
    /// <remarks>The retained read view is validated and released before returning. Use <see cref="ReadAsync"/> when the snapshot version and concurrency token are required for a conditional write.</remarks>
    public async ValueTask<T?> FindAsync(object identity, CancellationToken cancellationToken = default) =>
        (await ReadAsync(identity, cancellationToken).ConfigureAwait(false)) is { } read ? read.Value : default;

    /// <summary>Materializes the complete mapped entity through one retained profile view.</summary>
    /// <param name="identity">Mapped identity to look up in the default graph.</param>
    /// <param name="cancellationToken">Cancels the read, materialization and final view validation.</param>
    /// <returns>The entity and its observed version/concurrency token, or null when absent.</returns>
    /// <remarks>The retained view is released on success or failure. An unavailable view is an error, not an absent entity. Pass a returned read result to <see cref="RdfEntityWriteOptions.FromRead{T}"/> for conditional updates.</remarks>
    public async ValueTask<EntityReadResult<T>?> ReadAsync(object identity, CancellationToken cancellationToken = default)
    {
        var scope = _client is IEntityCompleteReadClient complete
            ? (IRdfEntityReadScope)await complete.ReadCompleteAsync([Mapping.Manifest], RdfEntityMaterialization.ReadRecipe(map, identity),
                null, null, TimeSpan.FromMinutes(5), cancellationToken).ConfigureAwait(false)
            : _client is IEntityReadRecipeProvider targeted
                ? (IRdfEntityReadScope)await targeted.OpenRecipeAsync([Mapping.Manifest], RdfEntityMaterialization.ReadRecipe(map, identity),
                    null, null, TimeSpan.FromMinutes(5), cancellationToken).ConfigureAwait(false)
                : await _client.OpenReadAsync(Mapping.Manifest, cancellationToken).ConfigureAwait(false);
        return await EntityReadScopeLifetime.RunAsync<EntityReadResult<T>?>(scope, async () =>
        {
            var result = await RdfEntityMaterialization.ReadEntityAsync(scope, map, identity, cancellationToken).ConfigureAwait(false);
            return result;
        }, () => scope.ValidateAsync(cancellationToken)).ConfigureAwait(false);
    }
}

/// <summary>Captures an ordered batch and prepares it through the receiving profile client.</summary>
public sealed class RdfEntityWriteBatch<T> : IAsyncDisposable
{
    /// <summary>Maximum operations in one atomic mapped batch.</summary>
    public const int MaximumOperations = EntityRecordDeliveryCodec.MaximumBatchOperations;

    /// <summary>Maximum encoded batch size.</summary>
    public const int MaximumEncodedBytes = EntityRecordDeliveryCodec.MaximumPayloadBytes;
    private readonly IRdfEntityClient _client;
    private readonly RdfEntityStore<T> _store;
    private readonly RdfEntityWriteOptions _options;
    private readonly List<RdfEntityRequest> _requests = [];
    private RdfEntityRequest[]? _frozen;
    private RdfEntityWrite? _prepared;
    private bool _preparing;
    private bool _disposed;

    /// <summary>Releases the selected preparation without aborting the logical operation.</summary>
    /// <returns>A task that completes when retained preparation resources have been released.</returns>
    /// <remarks>Disposal does not undo a commit or establish an abort outcome. The batch is unusable after disposal.</remarks>
    /// <exception cref="InvalidOperationException">Preparation is currently in progress.</exception>
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

    internal RdfEntityWriteBatch(IRdfEntityClient client, RdfEntityStore<T> store,
        Guid operationId, DatabaseVersion? expectedVersion = null)
    {
        _client = client;
        _store = store;
        _options = new(operationId, expectedVersion);
    }

    /// <summary>Captures a complete mapped replacement.</summary>
    /// <param name="entity">Entity whose mapped values are captured at this call.</param>
    /// <returns>This batch for further captures.</returns>
    /// <exception cref="InvalidOperationException">The batch has been frozen or has reached its operation limit.</exception>
    /// <exception cref="ObjectDisposedException">The batch has been disposed.</exception>
    public RdfEntityWriteBatch<T> Save(T entity)
    {
        Add(_store.CaptureSave(entity, _options));
        return this;
    }

    /// <summary>Captures removal of one mapped representation.</summary>
    /// <param name="identity">Mapped entity identity to remove.</param>
    /// <returns>This batch for further captures.</returns>
    /// <exception cref="InvalidOperationException">The batch has been frozen or has reached its operation limit.</exception>
    /// <exception cref="ObjectDisposedException">The batch has been disposed.</exception>
    public RdfEntityWriteBatch<T> Delete(object identity)
    {
        Add(_store.CaptureDelete(identity, _options));
        return this;
    }

    /// <summary>Selects one native transaction under one receiving-side snapshot without executing it.</summary>
    /// <param name="cancellationToken">Cancels preparation; a started attempt leaves the captured batch frozen for retries.</param>
    /// <returns>The selected write, reused by subsequent successful preparation calls.</returns>
    /// <remarks>Preparation does not commit. The batch owns and releases the selected preparation on disposal; keep the batch alive while using the returned write. Concurrent preparation is unsupported.</remarks>
    /// <exception cref="InvalidOperationException">The batch is empty or another preparation is in progress.</exception>
    /// <exception cref="ObjectDisposedException">The batch has been disposed.</exception>
    public async ValueTask<RdfEntityWrite> PrepareAsync(CancellationToken cancellationToken = default)
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
    /// <param name="cancellationToken">Cancels preparation or delivery, without proving that the logical operation was aborted.</param>
    /// <returns>The destination's commit receipt for the frozen batch.</returns>
    /// <remarks>After an uncertain response, retry this frozen batch or resolve its operation identity. Do not reconstruct a different batch under the same identity.</remarks>
    public async ValueTask<DatabaseCommitReceipt> CommitAsync(CancellationToken cancellationToken = default) =>
        await _client.DeliverAsync(await PrepareAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

    private void Add(RdfEntityRequest request)
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
