namespace EntityForge.Db.Kernel;

/// <summary>Identifies the bounded physical action recommended by maintenance policy.</summary>
public enum DatabaseMaintenanceAction
{
    /// <summary>No maintenance is currently recommended.</summary>
    None,
    /// <summary>Compact storage runs using bounded maintenance.</summary>
    Compact,
    /// <summary>Rebuild physical indexes to address structural debt.</summary>
    Reindex,
    /// <summary>Defer maintenance to protect foreground work or resource headroom.</summary>
    Defer
}

/// <summary>Supplies low-cardinality structural and workload evidence to maintenance policy.</summary>
/// <param name="FragmentationRatio">The observed fragmentation fraction, between zero and one.</param>
/// <param name="RunCount">The number of storage runs contributing to structural debt.</param>
/// <param name="RetiredPageCount">The number of retired pages observed by the profile.</param>
/// <param name="ReadAmplification">The measured or estimated read amplification supplied by the profile.</param>
/// <param name="QueuedForegroundCommits">The number of foreground commits waiting for admission.</param>
/// <param name="MemoryPressure">Whether memory pressure should defer maintenance.</param>
/// <param name="DiskPressure">Whether disk pressure should defer maintenance.</param>
/// <param name="EstimatedWorkspaceBytes">The estimated temporary memory workspace needed for maintenance.</param>
/// <param name="AvailableMemoryBytes">The available memory budget against which the workspace estimate is checked.</param>
/// <param name="ReclaimablePageCount">The number of pages eligible for reclamation.</param>
public sealed record DatabaseMaintenanceEvidence(
    double FragmentationRatio,
    int RunCount,
    int RetiredPageCount,
    double ReadAmplification,
    int QueuedForegroundCommits,
    bool MemoryPressure,
    bool DiskPressure,
    long EstimatedWorkspaceBytes = 0,
    long AvailableMemoryBytes = long.MaxValue,
    long ReclaimablePageCount = 0);

/// <summary>Describes an adaptive maintenance decision and its stable reason.</summary>
/// <param name="Action">The recommended bounded maintenance action.</param>
/// <param name="Reason">The stable reason identifier explaining the decision.</param>
public sealed record DatabaseMaintenanceDecision(DatabaseMaintenanceAction Action, string Reason);

/// <summary>
/// Applies conservative hysteretic policy to structural debt while protecting foreground work
/// and resource headroom. It never performs maintenance from a query path.
/// </summary>
public sealed class DatabaseMaintenancePolicy
{
    private const double ReindexFragmentationThreshold = 0.35;
    private const double ReindexExitFragmentationThreshold = 0.25;
    // Page-space currently uses 16-KiB pages. Whole-profile replacement is intentionally reserved
    // for debt large enough to repay its multi-access-path rewrite and durable-flush cost.
    private const int ReindexRetiredPageThreshold = 8192;
    private const int InitialReclaimablePageThreshold = 8192;
    private const int ReindexReclaimableGrowthPageThreshold = 8192;
    private const int CompactRunThreshold = 4;
    private const int CompactExitRunThreshold = 2;
    private const int MaximumForegroundQueueForHeavyWork = 1;
    private DatabaseMaintenanceAction _previous;
    private int _retiredPageBaseline;
    private long _reclaimablePageBaseline;
    private bool _hasPostReindexBaseline;
    private bool _capturePostReindexBaseline;

    /// <summary>Evaluates the next maintenance action without changing database state.</summary>
    public DatabaseMaintenanceDecision Evaluate(DatabaseMaintenanceEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.FragmentationRatio is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(evidence));
        }

        if (evidence.EstimatedWorkspaceBytes < 0 || evidence.AvailableMemoryBytes < 0 ||
            evidence.ReclaimablePageCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(evidence));
        }

        if (evidence.MemoryPressure || evidence.DiskPressure)
        {
            return Remember(DatabaseMaintenanceAction.Defer, "resource-pressure");
        }

        if (evidence.EstimatedWorkspaceBytes > evidence.AvailableMemoryBytes)
        {
            return Remember(DatabaseMaintenanceAction.Defer, "workspace-memory-budget");
        }

        if (evidence.QueuedForegroundCommits > MaximumForegroundQueueForHeavyWork)
        {
            return Remember(DatabaseMaintenanceAction.Defer, "foreground-pressure");
        }

        if (_capturePostReindexBaseline)
        {
            _retiredPageBaseline = evidence.RetiredPageCount;
            _reclaimablePageBaseline = evidence.ReclaimablePageCount;
            _hasPostReindexBaseline = true;
            _capturePostReindexBaseline = false;
        }
        double fragmentationThreshold = _previous is DatabaseMaintenanceAction.Reindex
            ? ReindexExitFragmentationThreshold : ReindexFragmentationThreshold;
        int retiredGrowth = _hasPostReindexBaseline
            ? Math.Max(0, evidence.RetiredPageCount - _retiredPageBaseline)
            : evidence.RetiredPageCount;
        long reclaimableGrowth = _hasPostReindexBaseline
            ? Math.Max(0, evidence.ReclaimablePageCount - _reclaimablePageBaseline)
            : evidence.ReclaimablePageCount;
        if (evidence.FragmentationRatio >= fragmentationThreshold &&
            (retiredGrowth >= ReindexRetiredPageThreshold ||
             reclaimableGrowth >= (_hasPostReindexBaseline
                 ? ReindexReclaimableGrowthPageThreshold : InitialReclaimablePageThreshold)))
        {
            _capturePostReindexBaseline = true;
            return Remember(DatabaseMaintenanceAction.Reindex, "fragmentation");
        }
        int runThreshold = _previous is DatabaseMaintenanceAction.Compact
            ? CompactExitRunThreshold : CompactRunThreshold;
        if (evidence.RunCount >= runThreshold)
        {
            return Remember(DatabaseMaintenanceAction.Compact, "run-amplification");
        }

        return Remember(DatabaseMaintenanceAction.None, "within-budget");
    }

    private DatabaseMaintenanceDecision Remember(DatabaseMaintenanceAction action, string reason)
    {
        _previous = action;
        return new(action, reason);
    }
}
