using EntityForge.Db.Mapping;
using EntityForge.Db.Graph.Contracts;

namespace EntityForge.Db.Graph;

/// <summary>Explicit embedded delivery conveniences; ordinary applications use the profile client.</summary>
public static class GraphEmbeddedEntityExecution
{
    internal static GraphEntityWrite PrepareCore(this GraphEntityRequest request, IGraphDatabase database)
    {
        EntityRecordDeliveryCodec.RequireTarget(database, request.Captured.Description.DatabaseIncarnation);
        return GraphEntityInputCompiler.FromManifest(request.Captured.Description.Manifest).PrepareCaptured(database, request.Captured);
    }

    /// <summary>Durably binds one native plan before execution; retries reuse that exact plan.</summary>
    public static ValueTask<DatabaseCommitReceipt> DeliverAsync(this GraphEntityRequest request, IGraphDatabase database, CancellationToken cancellationToken = default) =>
        database.Operations().DeliverAsync(request, cancellationToken);

    internal static async ValueTask<DatabaseCommitReceipt> DeliverCoreAsync(this GraphEntityRequest request, IGraphDatabase database, CancellationToken cancellationToken,
        Func<CancellationToken, ValueTask<GraphEntityWrite>>? prepare = null, Action<GraphEntityWrite>? validate = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        async ValueTask<byte[]> PrepareAsync()
        {
            var write = prepare is null
                ? await GraphEntityInputCompiler.FromManifest(request.Captured.Description.Manifest)
                    .PrepareCapturedAsync(database, request.Captured, cancellationToken).ConfigureAwait(false)
                : await prepare(cancellationToken).ConfigureAwait(false);
            validate?.Invoke(write);
            return write.Export();
        }
        EntityRecordDeliveryCodec.RequireTarget(database, request.Captured.Description.DatabaseIncarnation);
        if (database is not IEntityOperationHost host)
        {
            throw new NotSupportedException("The database does not provide durable request binding.");
        }

        if (database is GraphDatabaseEngine { SupportsBoundCommit: true } engine)
        {
            var receipt = await host.Operations.TryExecuteCapturedAsync(request.OperationId, request.Export(),
                PrepareAsync,
                engine.TryCommitBoundAsync, cancellationToken, request.Captured.Description.DatabaseIncarnation)
                .ConfigureAwait(false);
            if (receipt is not null)
            {
                return receipt;
            }
        }
        var binding = await host.Operations.GetOrBindOperationAsync(request.OperationId, request.Export(),
            PrepareAsync,
            cancellationToken, request.Captured.Description.DatabaseIncarnation).ConfigureAwait(false);
        try
        {
            if (binding.Committed is { } terminal)
            {
                return await host.Operations.ReadCommittedAsync(terminal, terminal => terminal.ReplayReceipt(), cancellationToken).ConfigureAwait(false);
            }

            var selected = GraphEntityWrite.Restore(binding.Plan);
            validate?.Invoke(selected);
            return await selected.DeliverCoreAsync(database, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not EntityOperationCommittedException<DatabaseCommitReceipt>)
        {
            EntityOperationSubmissionFailure.Attach(failure, request.OperationId, request.Captured.Description.DatabaseIncarnation);
            throw;
        }
    }

    /// <summary>Registers the exact map, then commits or replays the frozen native transaction against a local database.</summary>
    public static ValueTask<DatabaseCommitReceipt> CommitAsync(this GraphEntityWrite write, IGraphDatabase database, CancellationToken cancellationToken = default) =>
        database.Operations().DeliverAsync(write, cancellationToken);

    internal static ValueTask<DatabaseCommitReceipt> DeliverCoreAsync(this GraphEntityWrite write, IGraphDatabase database, CancellationToken cancellationToken)
    {
        var (delivery, payload) = EntityRecordDeliveryCodec.Decode(write.Export(), DatabaseProfileId.PropertyGraph, GraphTransactionCodec.Version);
        var operationId = delivery.OperationId;
        EntityRecordDeliveryCodec.RequireTarget(database, delivery.DatabaseIncarnation);
        return
        ((IEntityOperationHost)database).Operations.ExecutePreparedAsync(operationId,
            retained => payload.AsSpan().SequenceEqual(GraphTransactionCodec.Encode(GraphEntityWrite.Restore(retained).Transaction)),
            () => database.Mappings().ExecutePreparedAsync(write.Manifest,
            EntityRecordDeliveryCodec.RequireTarget(database, delivery.DatabaseIncarnation),
            operationId, payload, () => database.CommitWithReceiptAsync(write.Transaction, cancellationToken), cancellationToken), cancellationToken,
            write.Export(), EntityRecordDeliveryCodec.PreparedIdentity(delivery, payload), terminal => terminal.ReplayReceipt(), receipt => receipt, delivery.DatabaseIncarnation);
    }
}
