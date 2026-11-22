using EntityForge.Db.Mapping;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Rdf;

/// <summary>Explicit embedded delivery conveniences; ordinary applications use the profile client.</summary>
public static class RdfEmbeddedEntityExecution
{
    internal static ValueTask<RdfEntityWrite> PrepareCoreAsync(this RdfEntityRequest request, IRdfDatabase database,
        CancellationToken cancellationToken)
    {
        EntityRecordDeliveryCodec.RequireTarget(database, request.Captured.Description.DatabaseIncarnation);
        return RdfEntityInputCompiler.FromManifest(request.Captured.Description.Manifest)
            .PrepareCapturedAsync(database, request.Captured, cancellationToken);
    }

    /// <summary>Durably binds one native plan before execution; retries reuse that exact plan.</summary>
    public static ValueTask<DatabaseCommitReceipt> DeliverAsync(this RdfEntityRequest request, IRdfDatabase database, CancellationToken cancellationToken = default) =>
        database.Operations().DeliverAsync(request, cancellationToken);

    internal static async ValueTask<DatabaseCommitReceipt> DeliverCoreAsync(this RdfEntityRequest request, IRdfDatabase database, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        EntityRecordDeliveryCodec.RequireTarget(database, request.Captured.Description.DatabaseIncarnation);
        if (database is not IEntityOperationHost host)
        {
            throw new NotSupportedException("The database does not provide durable request binding.");
        }

        if (database is RdfDatabaseEngine { SupportsBoundCommit: true } engine)
        {
            var receipt = await host.Operations.TryExecuteCapturedAsync(request.OperationId, request.Export(),
                async () => (await request.PrepareCoreAsync(database, cancellationToken).ConfigureAwait(false)).Export(),
                engine.TryCommitBoundAsync, cancellationToken, request.Captured.Description.DatabaseIncarnation)
                .ConfigureAwait(false);
            if (receipt is not null)
            {
                return receipt;
            }
        }
        var binding = await host.Operations.GetOrBindOperationAsync(request.OperationId, request.Export(),
            async () => (await request.PrepareCoreAsync(database, cancellationToken).ConfigureAwait(false)).Export(),
            cancellationToken, request.Captured.Description.DatabaseIncarnation).ConfigureAwait(false);
        try
        {
            if (binding.Committed is { } terminal)
            {
                return await host.Operations.ReadCommittedAsync(terminal, terminal => terminal.ReplayReceipt(), cancellationToken).ConfigureAwait(false);
            }

            return await RdfEntityWrite.Restore(binding.Plan).DeliverCoreAsync(database, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not EntityOperationCommittedException<DatabaseCommitReceipt>)
        {
            EntityOperationSubmissionFailure.Attach(failure, request.OperationId, request.Captured.Description.DatabaseIncarnation);
            throw;
        }
    }

    /// <summary>Registers the exact map, then commits or replays the frozen native transaction against a local database.</summary>
    public static ValueTask<DatabaseCommitReceipt> CommitAsync(this RdfEntityWrite write, IRdfDatabase database, CancellationToken cancellationToken = default) =>
        database.Operations().DeliverAsync(write, cancellationToken);

    internal static ValueTask<DatabaseCommitReceipt> DeliverCoreAsync(this RdfEntityWrite write, IRdfDatabase database, CancellationToken cancellationToken)
    {
        var (delivery, payload) = EntityRecordDeliveryCodec.Decode(write.Export(), DatabaseProfileId.Rdf, RdfTransactionCodec.Version);
        var operationId = delivery.OperationId;
        EntityRecordDeliveryCodec.RequireTarget(database, delivery.DatabaseIncarnation);
        return
        ((IEntityOperationHost)database).Operations.ExecutePreparedAsync(operationId,
            retained => payload.AsSpan().SequenceEqual(RdfTransactionCodec.Encode(RdfEntityWrite.Restore(retained).Transaction)),
            () => database.Mappings().ExecutePreparedAsync(write.Manifest,
            EntityRecordDeliveryCodec.RequireTarget(database, delivery.DatabaseIncarnation),
            operationId, payload, () => database.CommitWithReceiptAsync(write.Transaction, cancellationToken), cancellationToken), cancellationToken,
            write.Export(), EntityRecordDeliveryCodec.PreparedIdentity(delivery, payload), terminal => terminal.ReplayReceipt(), receipt => receipt, delivery.DatabaseIncarnation);
    }
}
