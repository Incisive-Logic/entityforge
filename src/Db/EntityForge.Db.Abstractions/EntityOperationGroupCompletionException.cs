namespace EntityForge.Db.Mapping;

// All native members have published. Only their in-memory terminal projections failed;
// retain individual receipts so one failure cannot hide commitment or poison its peers.
internal sealed class EntityOperationGroupCompletionException(
    IReadOnlyList<DatabaseCommitReceipt> receipts, IReadOnlyDictionary<Guid, Exception> failures)
    : Exception("Committed group terminal projection failed.", new AggregateException(failures.Values))
{
    internal IReadOnlyList<DatabaseCommitReceipt> Receipts { get; } = receipts;

    internal IReadOnlyDictionary<Guid, Exception> Failures { get; } = failures;
}
