namespace EntityForge.Db.Mapping;

/// <summary>Identifies an operation whose submission was attempted but whose outcome must be resolved.
/// This is not evidence of commitment or abort. Reused causes may require a submission-specific wrapper.</summary>
/// <param name="OperationId">Operation identity to retain when resolving an ambiguous submission.</param>
/// <param name="DatabaseIncarnation">Database incarnation against which the submission was attempted.</param>
public sealed record EntityOperationSubmissionFailure(Guid OperationId, Guid DatabaseIncarnation)
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Exception, EntityOperationSubmissionFailure> Contexts = new();

    /// <summary>Reads resolution context attached at the application submission boundary.</summary>
    public static EntityOperationSubmissionFailure? FromException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception is EntityOperationSubmissionException submission ? submission.Context
            : Contexts.TryGetValue(exception, out var context) ? context : null;
    }

    internal static void Attach(Exception exception, Guid operationId, Guid incarnation)
    {
        // Weak keys preserve the existing exception type without retaining completed failures or
        // depending on a custom exception's mutable Data dictionary. Transport maps this explicitly.
        if (incarnation == Guid.Empty)
        {
            return;
        }

        var requested = new EntityOperationSubmissionFailure(operationId, incarnation);
        lock (Contexts)
        {
            var existing = FromException(exception);
            if (existing == requested)
            {
                return;
            }
            // Never change the context still observed by an earlier caller of the same exception.
            if (existing is not null)
            {
                throw new EntityOperationSubmissionException(requested, exception);
            }

            Contexts.Add(exception, requested);
        }
    }
}

/// <summary>Separates a submission from an underlying exception already associated with another operation.
/// The original cause remains available; this exception does not assert commitment or abort.</summary>
public sealed class EntityOperationSubmissionException : EntityForgeDbException
{
    internal EntityOperationSubmissionException(EntityOperationSubmissionFailure context, Exception cause)
        : base("The submission failed; resolve the operation identified by Context.", cause) => Context = context;

    /// <summary>Gets the immutable resolution context for this submission.</summary>
    public EntityOperationSubmissionFailure Context
    {
        get;
    }
}
