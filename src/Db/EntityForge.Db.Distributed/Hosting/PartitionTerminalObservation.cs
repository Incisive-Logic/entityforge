namespace EntityForge.Db.Distributed.Hosting;

// These are immutable authority observations, not cached placement permission.
// A failed member must not prevent another member's exact terminal delivery.
internal sealed record PartitionTerminalObservation(Guid Operation, PartitionApplicationBinding? Application,
    PartitionTransactionPlan? Plan, PartitionTransactionDecision? Decision, Exception? Failure = null)
{
    internal const int MaximumBatch = 8;

    internal static Dictionary<Guid, string> CopySelections(IReadOnlyList<Guid> operations, IReadOnlyDictionary<Guid, string>? selected)
    {
        var result = selected is null ? new Dictionary<Guid, string>() : new(selected);
        if (result.Any(pair => !operations.Contains(pair.Key) || pair.Value.Length != 64 || !pair.Value.All(Uri.IsHexDigit)))
        {
            throw new ArgumentException("Invalid local selection association.");
        }

        return result;
    }

    internal static void RequireBatch(IReadOnlyList<Guid> operations)
    {
        if (operations.Count is < 1 or > MaximumBatch || operations.Any(id => id == Guid.Empty) ||
            operations.Distinct().Count() != operations.Count)
        {
            throw new ArgumentException("Invalid bounded terminal observation batch.");
        }
    }
}
