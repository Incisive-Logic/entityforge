// Frozen selection oracle (a5833fc3).
namespace EntityForge.Db.Storage.Pages;

/// <summary>Pure size-tiered selection policy; execution and scheduling remain separate concerns.</summary>
public sealed class LegacyAppendMergeCompactionPlanner
{
    private readonly AppendMergeOptions _options;

    /// <summary>Creates a planner from validated append/merge options.</summary>
    public LegacyAppendMergeCompactionPlanner(AppendMergeOptions options) =>
        _options = options ?? throw new ArgumentNullException(nameof(options));

    /// <summary>Returns whether the visible run count meets the configured compaction threshold.</summary>
    public bool MeetsRunCountThreshold(int visibleRunCount) =>
        visibleRunCount >= _options.CompactionRunThreshold;

    internal IReadOnlyList<AppendMergeRunDescriptor> Select(
        IReadOnlyList<AppendMergeRunDescriptor> newestFirst, long maximumInputPages = long.MaxValue)
    {
        if (!MeetsRunCountThreshold(newestFirst.Count))
        {
            return [];
        }

        for (int start = newestFirst.Count - _options.CompactionFanIn; start >= 0; start--)
        {
            long pages = 0, smallest = long.MaxValue, largest = 0;
            int end = start + _options.CompactionFanIn;
            for (int index = start; index < end; index++)
            {
                AppendMergeRunDescriptor run = newestFirst[index];
                for (int extentIndex = 0; extentIndex < run.Extents.Count; extentIndex++)
                {
                    pages = checked(pages + run.Extents[extentIndex].PageCount);
                }

                smallest = Math.Min(smallest, run.EntryCount);
                largest = Math.Max(largest, run.EntryCount);
            }
            if (pages > maximumInputPages ||
                largest > checked(smallest * _options.MaximumCompactionSizeRatio))
            {
                continue;
            }

            var candidate = new AppendMergeRunDescriptor[_options.CompactionFanIn];
            for (int index = 0; index < candidate.Length; index++)
            {
                candidate[index] = newestFirst[start + index];
            }

            return candidate;
        }
        // Preserve the cheap oldest-adjacent policy when it can make progress. Mixed
        // mutation sizes (for example, progress-only and entity runs) can otherwise
        // strand an entire size tier between larger runs indefinitely.
        var bySize = newestFirst.Select((run, index) => (Run: run, Index: index,
                Pages: run.Extents.Sum(extent => extent.PageCount)))
            .Where(item => item.Pages <= maximumInputPages)
            .OrderBy(item => item.Run.EntryCount)
            .ThenBy(item => item.Pages)
            .ThenByDescending(item => item.Index).ToArray();
        for (int start = 0; start <= bySize.Length - _options.CompactionFanIn; start++)
        {
            long pages = 0;
            int end = start + _options.CompactionFanIn;
            for (int index = start; index < end; index++)
            {
                pages = checked(pages + bySize[index].Pages);
            }

            long smallest = bySize[start].Run.EntryCount;
            long largest = bySize[end - 1].Run.EntryCount;
            if (pages > maximumInputPages ||
                smallest <= long.MaxValue / _options.MaximumCompactionSizeRatio &&
                largest > smallest * _options.MaximumCompactionSizeRatio)
            {
                continue;
            }
            // Merge consumes newest-first runs to retain the existing duplicate
            // version precedence. Size ordering is only a selection policy.
            return bySize.Skip(start).Take(_options.CompactionFanIn)
                .OrderBy(item => item.Index).Select(item => item.Run).ToArray();
        }
        return [];
    }
}
