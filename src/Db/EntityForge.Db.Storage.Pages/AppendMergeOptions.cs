namespace EntityForge.Db.Storage.Pages;

/// <summary>Controls bounded filtering and explicit size-tiered compaction.</summary>
public sealed record AppendMergeOptions
{
    /// <summary>Default number of filter bits assigned per run entry.</summary>
    public const int DefaultFilterBitsPerKey = 10;

    /// <summary>Default number of independent filter probes.</summary>
    public const int DefaultFilterHashCount = 5;

    /// <summary>Maximum supported persisted filter hash count.</summary>
    public const int MaximumFilterHashCount = 16;

    /// <summary>Default maximum persisted filter size.</summary>
    public const int DefaultMaximumFilterBytes = MaximumSupportedFilterBytes;

    /// <summary>Maximum filter payload supported by the manifest format.</summary>
    public const int MaximumSupportedFilterBytes = 256 * 1024;

    /// <summary>Default run count at which compaction selection begins.</summary>
    public const int DefaultCompactionRunThreshold = 4;

    /// <summary>Default number of similarly sized runs replaced together.</summary>
    public const int DefaultCompactionFanIn = 4;

    /// <summary>Default maximum largest-to-smallest entry-count ratio.</summary>
    public const int DefaultMaximumCompactionSizeRatio = 4;

    /// <summary>Creates validated append/merge configuration.</summary>
    public AppendMergeOptions(int filterBitsPerKey = DefaultFilterBitsPerKey,
        int filterHashCount = DefaultFilterHashCount,
        int maximumFilterBytes = DefaultMaximumFilterBytes,
        int compactionRunThreshold = DefaultCompactionRunThreshold,
        int compactionFanIn = DefaultCompactionFanIn,
        int maximumCompactionSizeRatio = DefaultMaximumCompactionSizeRatio,
        bool compactOnWrite = false)
    {
        if (filterBitsPerKey <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(filterBitsPerKey));
        }

        if (filterHashCount <= 0 || filterHashCount > MaximumFilterHashCount)
        {
            throw new ArgumentOutOfRangeException(nameof(filterHashCount));
        }

        if (maximumFilterBytes <= 0 || maximumFilterBytes > MaximumSupportedFilterBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumFilterBytes));
        }

        if (compactionRunThreshold < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(compactionRunThreshold));
        }

        if (compactionFanIn < 2 || compactionFanIn > compactionRunThreshold)
        {
            throw new ArgumentOutOfRangeException(nameof(compactionFanIn));
        }

        if (maximumCompactionSizeRatio < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCompactionSizeRatio));
        }

        FilterBitsPerKey = filterBitsPerKey;
        FilterHashCount = filterHashCount;
        MaximumFilterBytes = maximumFilterBytes;
        CompactionRunThreshold = compactionRunThreshold;
        CompactionFanIn = compactionFanIn;
        MaximumCompactionSizeRatio = maximumCompactionSizeRatio;
        CompactOnWrite = compactOnWrite;
    }

    /// <summary>Gets filter bits allocated per entry.</summary>
    public int FilterBitsPerKey
    {
        get;
    }

    /// <summary>Gets the persisted filter hash count.</summary>
    public int FilterHashCount
    {
        get;
    }

    /// <summary>Gets the maximum persisted filter payload size.</summary>
    public int MaximumFilterBytes
    {
        get;
    }

    /// <summary>Gets the visible-run threshold for compaction.</summary>
    public int CompactionRunThreshold
    {
        get;
    }

    /// <summary>Gets the number of runs replaced by one compaction.</summary>
    public int CompactionFanIn
    {
        get;
    }

    /// <summary>Gets the maximum size ratio within a selected tier.</summary>
    public int MaximumCompactionSizeRatio
    {
        get;
    }

    /// <summary>Combines one eligible size tier of at most 512 KiB with each new run in the same
    /// physical commit, without pruning versions. Larger tiers require explicit maintenance.</summary>
    public bool CompactOnWrite
    {
        get;
    }
}
