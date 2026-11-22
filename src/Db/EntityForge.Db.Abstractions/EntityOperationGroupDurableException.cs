namespace EntityForge.Db.Mapping;

/// <summary>Every listed member is durable, but group publication is incomplete.
/// Resolve each original identity after recovery. Unlike terminal-projection-only failure,
/// this exception does not establish readable completion for any member.</summary>
public sealed class EntityOperationGroupDurableException : EntityForgeDbException
{
    internal EntityOperationGroupDurableException(IReadOnlyList<DatabaseCommitReceipt> receipts, Exception cause)
        : base("The group is durable but publication requires recovery.", cause)
    {
        Receipts = Array.AsReadOnly(receipts.ToArray());
    }

    /// <summary>Original durable outcomes. None establishes current readability.</summary>
    public IReadOnlyList<DatabaseCommitReceipt> Receipts
    {
        get;
    }
}
