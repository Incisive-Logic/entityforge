using EntityForge.Db.Query.Adapters;
using HotChocolate;
using Microsoft.Extensions.Logging;

namespace EntityForge.Db.Hosting;

internal sealed class GraphQlErrors(ILogger<GraphQlErrors> logger) : IErrorFilter
{
    public IError OnError(IError error)
    {
        if (error.Exception is EntityForge.Db.Mapping.EntityOperationCommittedException<EntityForge.Db.Semantic.SessionCommitResult> committed)
        {
            return error.WithMessage(committed.Message).WithCode("COMMITTED_COMPLETION_FAILED")
                .SetExtension("receipt", new
                {
                    databaseIncarnation = committed.DatabaseIncarnation,
                    transactionId = committed.OperationId,
                    version = committed.Receipt.Version.Value
                });
        }

        if (error.Exception is EntityForge.Db.Semantic.SemanticSessionValidationException invalid)
        {
            return error.WithMessage(invalid.Message).WithCode("SEMANTIC_SESSION_INVALID");
        }

        if (error.Exception is EntityForge.Db.Semantic.SemanticSourceProgressConflictException conflict)
        {
            return error.WithMessage(conflict.Message).WithCode("SOURCE_PROGRESS_CONFLICT");
        }

        if (error.Exception is EntityForgeDbIdempotencyException idempotency)
        {
            return error.WithMessage(idempotency.Message).WithCode("IDEMPOTENCY_CONFLICT");
        }

        if (error.Exception is EntityForgeGraphQlException graphQl)
        {
            return error.WithMessage(graphQl.Message).WithCode(graphQl.Code);
        }

        if (error.Exception is EntityForge.Db.Mapping.EntityOperationCommittedException<DatabaseCommitReceipt> nativeCommitted)
        {
            return error.WithMessage(nativeCommitted.Message).WithCode("COMMITTED_COMPLETION_FAILED")
                .SetExtension("receipt", new
                {
                    databaseIncarnation = nativeCommitted.DatabaseIncarnation,
                    transactionId = nativeCommitted.OperationId,
                    version = nativeCommitted.Receipt.Version.Value
                });
        }

        if (error.Exception is EntityForge.Db.Graph.Contracts.GraphSnapshotTooOldException or EntityForge.Db.Rdf.RdfSnapshotTooOldException)
        {
            return error.WithMessage(error.Exception.Message).WithCode("SNAPSHOT_NOT_RETAINED");
        }

        if (error.Exception is EntityForge.Db.Graph.Contracts.GraphWriteConflictException or EntityForge.Db.Rdf.Contracts.RdfWriteConflictException)
        {
            return error.WithMessage(error.Exception.Message).WithCode("WRITE_CONFLICT");
        }

        if (error.Exception is ArgumentException or FormatException or OverflowException)
        {
            return error.WithMessage(error.Exception.Message).WithCode("GRAPHQL_VALIDATION_FAILED");
        }

        if (error.Exception is not null and not OperationCanceledException)
        {
            logger.LogError(error.Exception, "GraphQL execution failed.");
        }

        return error;
    }
}
