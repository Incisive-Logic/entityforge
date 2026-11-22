using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Distributed.Hosting;

// Pure routing under the query's immutable binding. Each receiver still checks
// ownership independently; these buckets contain no serving-authority evidence.
internal static class PartitionGraphCandidateRouting
{
    internal static List<Guid>?[] Group(IEnumerable<Guid> candidates,
        IReadOnlyDictionary<LogicalShardId, int> owners, Func<Guid, LogicalShardId> route)
    {
        var buckets = new List<Guid>?[owners.Count];
        foreach (var identity in candidates)
        {
            if (!owners.TryGetValue(route(identity), out int index))
            {
                throw new InvalidDataException("Graph candidate has no query owner.");
            }

            (buckets[index] ??= []).Add(identity);
        }
        return buckets;
    }
}
