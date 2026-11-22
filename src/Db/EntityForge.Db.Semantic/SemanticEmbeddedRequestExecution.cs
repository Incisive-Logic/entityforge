using EntityForge.Db.Mapping;
using EntityForge.Semantics;
using EntityForge.Semantics.Protobuf;

namespace EntityForge.Db.Semantic;

/// <summary>Explicit embedded delivery for captured Semantic requests.</summary>
public static class SemanticEmbeddedRequestExecution
{
    internal static SemanticEntityWrite PrepareCore(this SemanticEntityRequest request, ISemanticDatabase database)
    {
        if (((ISemanticMappedExecution)database).DatabaseIncarnation != request._metadata.DatabaseIncarnation)
        {
            throw new InvalidOperationException("The captured request belongs to another database incarnation.");
        }

        var plan = SemanticEntityPlan.FromDefinition(request._metadata.Definition,
            request._metadata.Application, request._metadata.Limits, request._metadata.RootBinding?.ToBinding());
        return plan.PrepareCaptured(request._metadata.DatabaseIncarnation, EntityValueCapture.Decode(request._payload)!, request._metadata.Operation,
            request._metadata.Predecessor, request._metadata.Guarded, request._metadata.Delete);
    }

    /// <summary>Plans and delivers the captured replacement to its original database.
    /// The exact captured request binds one durable plan; expanded source replacement remains database-owned.</summary>
    public static ValueTask<SessionCommitResult> DeliverAsync(this SemanticEntityRequest request, ISemanticDatabase database, CancellationToken cancellationToken = default) =>
        database.Operations().DeliverAsync(request, cancellationToken);

    internal static async ValueTask<SessionCommitResult> DeliverCoreAsync(this SemanticEntityRequest request, ISemanticDatabase database, CancellationToken cancellationToken,
        Func<SemanticSessionEnvelope, Action<IReadOnlyList<SemanticEntityChange>>, CancellationToken, ValueTask<SessionCommitResult>>? apply = null,
        EntityForge.Db.Semantic.Durability.SemanticOwnerCoverage? localCoverage = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        cancellationToken.ThrowIfCancellationRequested();
        if (database is not ISemanticMappedExecution execution || execution.DatabaseIncarnation != request._metadata.DatabaseIncarnation)
        {
            throw new InvalidOperationException("The captured request belongs to another database incarnation.");
        }

        var plan = SemanticEntityPlan.FromDefinition(request._metadata.Definition,
            request._metadata.Application, request._metadata.Limits, request._metadata.RootBinding?.ToBinding());
        if (database is not IEntityOperationHost host)
        {
            throw new NotSupportedException("Durable request binding is unavailable.");
        }
        // Definitions exclude diagnostic names from their fingerprint. All other captured options bind this operation.
        byte[] identity = request.CanonicalIdentity();
        var binding = await host.Operations.GetOrBindOperationAsync(request.OperationId, identity,
            () => SemanticEntityDeliveryCodec.WithLocalCoverage(plan.PrepareCaptured(request._metadata.DatabaseIncarnation, EntityValueCapture.Decode(request._payload)!, request._metadata.Operation,
                request._metadata.Predecessor, request._metadata.Guarded, request._metadata.Delete).Export(), localCoverage), cancellationToken, request._metadata.DatabaseIncarnation).ConfigureAwait(false);
        try
        {
            if (binding.Committed is { } terminal)
            {
                return await host.Operations.ReadCommittedAsync(terminal, SemanticEntityDeliveryCodec.Replay, cancellationToken).ConfigureAwait(false);
            }

            return await new SemanticEmbeddedWriteExecution(database, SemanticEntityWrite.Restore(binding.Plan), apply).DeliverCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not EntityOperationCommittedException<SessionCommitResult>)
        {
            EntityOperationSubmissionFailure.Attach(failure, request.OperationId, request._metadata.DatabaseIncarnation);
            throw;
        }
    }
}
