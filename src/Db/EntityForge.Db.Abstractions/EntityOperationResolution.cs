namespace EntityForge.Db.Mapping;

/// <summary>The currently observable state of a database-owned captured operation.</summary>
public enum EntityOperationState
{
    /// <summary>No binding was observed. A concurrent submission may still arrive; this is not proof of abort.</summary>
    NotFound,

    /// <summary>An immutable plan is retained, but no committed receipt was observed. This is not a terminal rejection.</summary>
    PlanBound,

    /// <summary>The original native commit is durable and locally readable.</summary>
    Committed,

    /// <summary>A durable decision prevents this bound operation from executing.</summary>
    Aborted,

    /// <summary>The operation is committed, but its resolved readability barrier has not completed.</summary>
    CommittedPendingReadability
}

/// <summary>A read-only lookup result. Native profile receipts retain their profile-specific meaning.</summary>
public sealed class EntityOperationResolution<TReceipt> where TReceipt : class
{
    internal EntityOperationResolution(Guid operationId, bool bound, TReceipt? receipt, bool aborted = false, bool readable = true)
    {
        OperationId = operationId;
        State = receipt is not null ? (readable ? EntityOperationState.Committed : EntityOperationState.CommittedPendingReadability) : aborted ? EntityOperationState.Aborted : bound ? EntityOperationState.PlanBound : EntityOperationState.NotFound;
        Receipt = receipt;
    }

    /// <summary>Gets the queried operation identity within the selected database.</summary>
    public Guid OperationId
    {
        get;
    }

    /// <summary>Gets the observed state; lookup never executes or replans the operation.</summary>
    public EntityOperationState State
    {
        get;
    }

    /// <summary>Gets the original commit receipt, or null when no commit was observed.</summary>
    public TReceipt? Receipt
    {
        get;
    }
}

/// <summary>The database has durably aborted this bound application operation.</summary>
public sealed class EntityOperationAbortedException(Guid operationId, Exception? cause = null)
    : EntityForgeDbException($"Application operation '{operationId}' is durably aborted.", cause)
{
    /// <summary>Gets the aborted operation identity.</summary>
    public Guid OperationId { get; } = operationId;
}
