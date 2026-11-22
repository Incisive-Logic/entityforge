namespace EntityForge.Db.Storage.Pages;

internal sealed class AppendMergeReclaimer
{
    internal static long? OldestProtectedGeneration(IEnumerable<long> protectedSnapshotGenerations)
    {
        long? oldest = null;
        foreach (long generation in protectedSnapshotGenerations)
        {
            if (oldest is null || generation < oldest.Value)
            {
                oldest = generation;
            }
        }

        return oldest;
    }

    internal static bool CanReclaim(AppendMergeRetiredPages item, long? oldestProtectedGeneration) =>
        oldestProtectedGeneration is null || item.LastVisibleGeneration < oldestProtectedGeneration.Value;

    public IReadOnlyList<AppendMergeRetiredPages> Select(
        IReadOnlyList<AppendMergeRetiredPages> retired,
        IReadOnlyCollection<long> protectedSnapshotGenerations)
    {
        long? oldest = OldestProtectedGeneration(protectedSnapshotGenerations);
        return retired.Where(item => CanReclaim(item, oldest)).ToArray();
    }
}
