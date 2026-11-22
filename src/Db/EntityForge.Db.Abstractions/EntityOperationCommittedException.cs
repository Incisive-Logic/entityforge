namespace EntityForge.Db.Mapping;

/// <summary>The operation committed, but subsequent delivery or maintenance failed.
/// The receipt establishes commitment, not current database readability. Resolve the operation
/// after restoring database availability; do not submit the write under a new identity.</summary>
public sealed class EntityOperationCommittedException<TReceipt> : EntityForgeDbException
{
    internal EntityOperationCommittedException(TReceipt receipt, DatabaseCommitReceipt nativeReceipt, Exception cause)
        : base("The application operation committed, but completion failed. Resolve the original operation after restoring database availability.", cause)
    {
        Receipt = receipt;
        NativeReceipt = nativeReceipt;
    }

    /// <summary>Gets the original profile-specific result, including Semantic source progress.</summary>
    public TReceipt Receipt
    {
        get;
    }

    /// <summary>Gets the common application commit receipt, including count-free authoritative decisions.</summary>
    public DatabaseCommitReceipt NativeReceipt
    {
        get;
    }

    /// <summary>Gets the application operation identity for resolution.</summary>
    public Guid OperationId => NativeReceipt.TransactionId;

    /// <summary>Gets the destination database incarnation.</summary>
    public Guid DatabaseIncarnation => NativeReceipt.DatabaseIncarnation;
}
