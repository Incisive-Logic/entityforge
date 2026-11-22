using EntityForge.Db.Storage;

namespace EntityForge.Db.Storage.Pages;

/// <summary>Defines value identity within each key's immutable version history.</summary>
/// <remarks>Returning zero makes differing payloads replacements of one logical value.
/// The access method's persisted version must change when this identity rule changes.
/// Ordinary comparers retain duplicate-preserving key/value identity.</remarks>
public interface IAppendMergeKeyedValueComparer<TKey, TValue> : IComparer<TValue>
{
    /// <summary>Compares two values within the same key's version history.</summary>
    /// <param name="key">Key whose value identities are compared.</param>
    /// <param name="left">First value.</param>
    /// <param name="right">Second value.</param>
    /// <returns>A negative value, zero or a positive value for less than, the same logical identity, or greater than.</returns>
    int Compare(TKey key, TValue left, TValue right);
}

internal static class AppendMergeValueComparison
{
    internal static int CompareAt<TKey, TValue>(this IComparer<TValue> values, TKey key, TValue left, TValue right) =>
        values is IAppendMergeKeyedValueComparer<TKey, TValue> keyed ? keyed.Compare(key, left, right) : values.Compare(left, right);
}

/// <summary>Identifies an append/merge operation stored in an immutable run.</summary>
public enum AppendMergeMutationKind : byte
{
    /// <summary>Adds or replaces the identified value at the mutation's database version.</summary>
    Add = 1,

    /// <summary>Records removal of the identified value at the mutation's database version.</summary>
    Remove = 2
}

/// <summary>Describes one duplicate-preserving composite entry mutation.</summary>
/// <param name="Kind">The append/merge mutation kind.</param>
/// <param name="Key">The ordered index key affected by the mutation.</param>
/// <param name="Value">The associated indexed value.</param>
public sealed record AppendMergeMutation<TKey, TValue>(AppendMergeMutationKind Kind, TKey Key, TValue Value)
    where TKey : notnull;

/// <summary>Contains one access path's sorted and duplicate-resolved mutation input.</summary>
public sealed class AppendMergeMutationPlan<TKey, TValue> where TKey : notnull
{
    internal AppendMergeMutationPlan(object owner,
        AppendMergeRunPlan<TKey, TValue> run,
        TimeSpan planningDuration, long allocatedBytes)
    {
        Owner = owner;
        Run = run;
        PlanningDuration = planningDuration;
        AllocatedBytes = allocatedBytes;
    }

    internal object Owner
    {
        get;
    }

    internal AppendMergeRunPlan<TKey, TValue> Run
    {
        get;
    }

    /// <summary>Gets the number of normalized versioned entries in this plan, including removals.</summary>
    public int Count => Run.Entries.Count;

    /// <summary>Gets the recorded planning duration; this excludes later page materialization and commit work.</summary>
    /// <remarks>Grouped plans report zero because group assembly is not instrumented by this metric.</remarks>
    public TimeSpan PlanningDuration
    {
        get;
    }

    /// <summary>Gets the recorded managed allocation count for planning, not retained memory or total commit allocation.</summary>
    /// <remarks>Single-plan measurements use current-thread allocated bytes. Grouped plans report zero, meaning unmeasured.</remarks>
    public long AllocatedBytes
    {
        get;
    }
}

/// <summary>Describes one prepared append/merge manifest generation.</summary>
/// <param name="TransactionId">The transaction that owns this private stage.</param>
/// <param name="Generation">The prospective access-path generation.</param>
/// <param name="PhysicalMutations">The complete physical page mutations required to persist the stage.</param>
public sealed record AppendMergeStage(Guid TransactionId, long Generation,
    IReadOnlyList<DatabasePhysicalPageMutation> PhysicalMutations);

/// <summary>Identifies the maintenance work represented by a prepared manifest.</summary>
public enum AppendMergeMaintenanceKind
{
    /// <summary>Merges selected immutable runs into a replacement run and manifest.</summary>
    Compaction,

    /// <summary>Reclaims eligible retired pages without merging runs.</summary>
    Reclamation
}

/// <summary>Reports bounded append/merge maintenance debt.</summary>
/// <param name="RunCount">The number of retained append/merge runs.</param>
/// <param name="CompactionDebt">The outstanding compaction work reported by the access path.</param>
/// <param name="RetiredPageCount">The number of retired pages awaiting safe reclamation.</param>
/// <param name="ReclaimablePageCount">The number of pages currently eligible for reclamation.</param>
public sealed record AppendMergeMaintenanceStatus(int RunCount, int CompactionDebt,
    int RetiredPageCount, int ReclaimablePageCount)
{
    /// <summary>Gets whether compaction debt or currently reclaimable pages are present.</summary>
    /// <remarks>Retired pages alone do not imply they can yet be reclaimed.</remarks>
    public bool NeedsMaintenance => CompactionDebt > 0 || ReclaimablePageCount > 0;
}

/// <summary>Describes a prepared compaction or reclamation-only manifest generation.</summary>
/// <param name="Stage">The privately prepared physical maintenance stage.</param>
/// <param name="Kind">The selected maintenance operation.</param>
/// <param name="InputRunCount">The number of runs considered by the maintenance operation.</param>
/// <param name="ReclaimedPageCount">The number of pages reclaimed by the prepared maintenance.</param>
public sealed record AppendMergeMaintenanceStage(AppendMergeStage Stage,
    AppendMergeMaintenanceKind Kind, int InputRunCount, int ReclaimedPageCount);

/// <summary>Wraps failures at the append/merge access-method boundary.</summary>
public class AppendMergeException : PhysicalPageException
{
    /// <summary>Creates an access-method failure with a diagnostic message.</summary>
    public AppendMergeException(string message) : base(message)
    {
    }

    /// <summary>Creates an access-method failure retaining its underlying cause.</summary>
    public AppendMergeException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>Indicates that a replacement no longer matches the published manifest generation.</summary>
public sealed class AppendMergeReplacementStaleException(long expectedGeneration, long actualGeneration) :
    AppendMergeException($"The append/merge replacement expected generation {expectedGeneration}, " +
                         $"but generation {actualGeneration} is published.")
{
    /// <summary>Gets the manifest generation on which the replacement was prepared.</summary>
    public long ExpectedGeneration { get; } = expectedGeneration;

    /// <summary>Gets the manifest generation found when publication was attempted.</summary>
    public long ActualGeneration { get; } = actualGeneration;
}
