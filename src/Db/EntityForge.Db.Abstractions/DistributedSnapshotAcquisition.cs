namespace EntityForge.Db;

// A bounded acquisition window, not a lease or a promise that an arbitrarily delayed
// timestamp remains readable. Explicit checkpoint/retention may discard this cache.
// Embedded databases retain their existing one-version policy.
internal static class DistributedSnapshotAcquisition
{
    internal const int MaximumVersions = 64;
}
