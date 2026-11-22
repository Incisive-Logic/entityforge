namespace EntityForge.Db.Storage.Pages;

/// <summary>Pure size-tiered selection policy; execution and scheduling remain separate concerns.</summary>
public sealed class AppendMergeCompactionPlanner
{
    private readonly AppendMergeOptions _options;

    /// <summary>Creates a planner from validated append/merge options.</summary>
    public AppendMergeCompactionPlanner(AppendMergeOptions options) =>
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
                pages = checked(pages + CountPages(run));

                smallest = Math.Min(smallest, run.EntryCount);
                largest = Math.Max(largest, run.EntryCount);
            }
            if (pages > maximumInputPages ||
                smallest <= long.MaxValue / _options.MaximumCompactionSizeRatio &&
                largest > smallest * _options.MaximumCompactionSizeRatio)
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
        var scratch = System.Buffers.ArrayPool<Candidate>.Shared.Rent(newestFirst.Count);
        try
        {
            int count = 0;
            for (int index = 0; index < newestFirst.Count; index++)
            {
                var run = newestFirst[index];
                long pages = CountPages(run);

                if (pages <= maximumInputPages)
                {
                    scratch[count++] = new(run, index, pages);
                }
            }
            Array.Sort(scratch, 0, count, SizeOrder);
            for (int start = 0; start <= count - _options.CompactionFanIn; start++)
            {
                long pages = 0;
                int end = start + _options.CompactionFanIn;
                for (int index = start; index < end; index++)
                {
                    pages = checked(pages + scratch[index].Pages);
                }

                long smallest = scratch[start].Run.EntryCount;
                long largest = scratch[end - 1].Run.EntryCount;
                if (pages > maximumInputPages ||
                    smallest <= long.MaxValue / _options.MaximumCompactionSizeRatio &&
                    largest > smallest * _options.MaximumCompactionSizeRatio)
                {
                    continue;
                }

                Array.Sort(scratch, start, _options.CompactionFanIn, IndexOrder);
                var selected = new AppendMergeRunDescriptor[_options.CompactionFanIn];
                for (int index = 0; index < selected.Length; index++)
                {
                    selected[index] = scratch[start + index].Run;
                }

                return selected;
            }
            return [];
        }
        finally
        {
            // Scratch contains references to immutable runs; never retain a database
            // through the pool, and never return pooled storage as the selected result.
            System.Buffers.ArrayPool<Candidate>.Shared.Return(scratch, clearArray: true);
        }
    }

    private static long CountPages(AppendMergeRunDescriptor run)
    {
        long pages = run.InlinePayload is null ? 0 : 1;
        // Index both lists directly: OwnedExtents may materialize their union,
        // and interface enumeration allocates on every overlapping candidate window.
        for (int index = 0; index < run.Extents.Count; index++)
        {
            pages = checked(pages + run.Extents[index].PageCount);
        }
        for (int index = 0; index < run.OverflowExtents.Count; index++)
        {
            pages = checked(pages + run.OverflowExtents[index].PageCount);
        }
        return pages;
    }

    private readonly record struct Candidate(AppendMergeRunDescriptor Run, int Index, long Pages);

    private static readonly IComparer<Candidate> SizeOrder = Comparer<Candidate>.Create((left, right) =>
    {
        int order = left.Run.EntryCount.CompareTo(right.Run.EntryCount);
        if (order == 0)
        {
            order = left.Pages.CompareTo(right.Pages);
        }

        return order != 0 ? order : right.Index.CompareTo(left.Index);
    });

    private static readonly IComparer<Candidate> IndexOrder =
        Comparer<Candidate>.Create((left, right) => left.Index.CompareTo(right.Index));

}

internal sealed class AppendMergeCompactionExecutor<TKey, TValue> where TKey : notnull
{
    private readonly AppendMergeRunReader<TKey, TValue> _reader;
    private readonly AppendMergeRunWriter<TKey, TValue> _writer;
    private readonly IComparer<TKey> _keys;
    private readonly IComparer<TValue> _values;

    public AppendMergeCompactionExecutor(AppendMergeRunReader<TKey, TValue> reader,
        AppendMergeRunWriter<TKey, TValue> writer, IComparer<TKey> keys, IComparer<TValue> values)
    {
        _reader = reader;
        _writer = writer;
        _keys = keys;
        _values = values;
    }

    public async ValueTask<AppendMergeRunImage?> ExecuteAsync(
        IReadOnlyList<AppendMergeRunDescriptor> newestFirst, long replacementGeneration,
        long retentionFloorVersion, bool containsOldestVisibleRun,
        IPhysicalPageAllocator allocation, CancellationToken cancellationToken, Func<TKey, TValue, bool>? retain = null)
    {
        int maximumEntryCount = checked((int)newestFirst.Sum(run => run.EntryCount));
        if (retain is not null && !containsOldestVisibleRun)
        {
            throw new InvalidOperationException("Filtered compaction requires the complete run closure.");
        }

        return await _writer.WriteAsync(FilterAsync(MergeAsync(newestFirst, retentionFloorVersion,
                containsOldestVisibleRun, cancellationToken), retain, cancellationToken), maximumEntryCount,
            replacementGeneration, allocation, cancellationToken).ConfigureAwait(false);
    }

    internal ValueTask<AppendMergeRunImage?> ExecuteWithPendingAsync(
        IReadOnlyList<AppendMergeRunDescriptor> runs, AppendMergeRunPlan<TKey, TValue> pending,
        long generation, IPhysicalPageAllocator allocation, CancellationToken token) =>
        _writer.WriteAsync(MergeAsync(runs, 0, false, token, pending.Entries.Select(entry => entry.Entry).ToArray()),
            checked((int)runs.Sum(run => run.EntryCount) + pending.Entries.Count),
            generation, allocation, token);

    private static async IAsyncEnumerable<AppendMergeRunEntry<TKey, TValue>> FilterAsync(
        IAsyncEnumerable<AppendMergeRunEntry<TKey, TValue>> entries, Func<TKey, TValue, bool>? retain,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        await foreach (var entry in entries.WithCancellation(token).ConfigureAwait(false))
        {
            if (retain is null || retain(entry.Key, entry.Value))
            {
                yield return entry;
            }
        }
    }

    public async ValueTask<AppendMergeRunDescriptor?> ExecuteStreamingAsync(
        IReadOnlyList<AppendMergeRunDescriptor> newestFirst, long replacementGeneration,
        long retentionFloorVersion, bool containsOldestVisibleRun,
        IPhysicalPageAllocator allocation,
        Func<IReadOnlyList<KeyValuePair<PhysicalPageId, ReadOnlyMemory<byte>>>, CancellationToken, ValueTask> writePages,
        CancellationToken cancellationToken, Func<TKey, TValue, bool>? retain = null)
    {
        int maximumEntryCount = checked((int)newestFirst.Sum(run => run.EntryCount));
        if (retain is not null && !containsOldestVisibleRun)
        {
            throw new InvalidOperationException("Filtered compaction requires the complete run closure.");
        }

        return await _writer.WriteStreamingAsync(FilterAsync(MergeAsync(newestFirst, retentionFloorVersion,
                containsOldestVisibleRun, cancellationToken), retain, cancellationToken), maximumEntryCount,
            replacementGeneration, allocation, writePages, cancellationToken).ConfigureAwait(false);
    }

    private async IAsyncEnumerable<AppendMergeRunEntry<TKey, TValue>> MergeAsync(
        IReadOnlyList<AppendMergeRunDescriptor> runs, long retentionFloorVersion,
        bool containsOldestVisibleRun,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken,
        IReadOnlyList<AppendMergeRunEntry<TKey, TValue>>? pending = null)
    {
        var cursors = new List<RunCursor>(runs.Count);
        var queue = new PriorityQueue<int, (AppendMergeRunEntry<TKey, TValue> Entry, int Index)>(
            Comparer<(AppendMergeRunEntry<TKey, TValue> Entry, int Index)>.Create((left, right) =>
            {
                int comparison = Compare(left.Entry, right.Entry);
                // Preserve the existing oldest-to-newest duplicate precedence.
                return comparison != 0 ? comparison : right.Index.CompareTo(left.Index);
            }));
        var matching = new List<int>(runs.Count);
        var versions = new List<AppendMergeRunEntry<TKey, TValue>>(runs.Count);
        var retained = new List<AppendMergeRunEntry<TKey, TValue>>(runs.Count);
        async ValueTask AddAsync(IAsyncEnumerator<AppendMergeRunEntry<TKey, TValue>> enumerator)
        {
            int index = cursors.Count;
            var cursor = new RunCursor(enumerator);
            cursors.Add(cursor); // Own it before the first potentially failing read.
            if (await cursor.MoveNextAsync().ConfigureAwait(false))
            {
                queue.Enqueue(index, (cursor.Current, index));
            }
            else
            {
                await cursor.DisposeAsync().ConfigureAwait(false);
            }
        }
        try
        {
            if (pending is not null)
            {
                var enumerator = PendingEntries(pending, cancellationToken).GetAsyncEnumerator(cancellationToken);
                await AddAsync(enumerator).ConfigureAwait(false);
            }
            foreach (AppendMergeRunDescriptor run in runs)
            {
                IAsyncEnumerator<AppendMergeRunEntry<TKey, TValue>> enumerator =
                    _reader.ReadAsync(run, cancellationToken).GetAsyncEnumerator(cancellationToken);
                await AddAsync(enumerator).ConfigureAwait(false);
            }

            while (queue.TryPeek(out _, out var first))
            {
                cancellationToken.ThrowIfCancellationRequested();
                AppendMergeRunEntry<TKey, TValue> target = first.Entry;
                matching.Clear();
                while (queue.TryPeek(out _, out var candidate) && Compare(candidate.Entry, target) == 0)
                {
                    matching.Add(queue.Dequeue());
                }

                versions.Clear();
                while (matching.Count != 0)
                {
                    for (int index = 0; index < matching.Count;)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        int run = matching[index];
                        RunCursor cursor = cursors[run];
                        versions.Add(cursor.Current);
                        if (!await cursor.MoveNextAsync().ConfigureAwait(false))
                        {
                            await cursor.DisposeAsync().ConfigureAwait(false);
                            matching.RemoveAt(index);
                        }
                        else if (Compare(cursor.Current, target) != 0)
                        {
                            queue.Enqueue(run, (cursor.Current, run));
                            matching.RemoveAt(index);
                        }
                        else
                        {
                            index++;
                        }
                    }
                }

                PrepareRetainedVersions(versions, retained);
                int baseline = -1;
                for (int index = 0; index < retained.Count; index++)
                {
                    if (retained[index].Version <= retentionFloorVersion)
                    {
                        baseline = index;
                    }
                    else
                    {
                        break;
                    }
                }
                if (baseline >= 0 &&
                    (!containsOldestVisibleRun || retained[baseline].Kind is AppendMergeMutationKind.Add))
                {
                    yield return retained[baseline];
                }

                for (int index = baseline + 1; index < retained.Count; index++)
                {
                    yield return retained[index];
                }
            }
        }
        finally
        {
            foreach (RunCursor cursor in cursors)
            {
                await cursor.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static async IAsyncEnumerable<AppendMergeRunEntry<TKey, TValue>> PendingEntries(
        IReadOnlyList<AppendMergeRunEntry<TKey, TValue>> entries,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        foreach (var entry in entries)
        {
            token.ThrowIfCancellationRequested();
            yield return entry;
        }
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static void PrepareRetainedVersions(
        IReadOnlyList<AppendMergeRunEntry<TKey, TValue>> versions,
        List<AppendMergeRunEntry<TKey, TValue>> retained)
    {
        retained.Clear();
        foreach (AppendMergeRunEntry<TKey, TValue> entry in versions)
        {
            int duplicate = -1;
            for (int index = 0; index < retained.Count; index++)
            {
                if (retained[index].Version == entry.Version)
                {
                    duplicate = index;
                    break;
                }
            }

            if (duplicate < 0)
            {
                retained.Add(entry);
            }
            else
            {
                retained[duplicate] = entry;
            }
        }
        retained.Sort(static (left, right) => left.Version.CompareTo(right.Version));
    }

    private int Compare(AppendMergeRunEntry<TKey, TValue> left,
        AppendMergeRunEntry<TKey, TValue> right)
    {
        int key = _keys.Compare(left.Key, right.Key);
        return key != 0 ? key : _values.CompareAt(left.Key, left.Value, right.Value);
    }

    private sealed class RunCursor(IAsyncEnumerator<AppendMergeRunEntry<TKey, TValue>> enumerator) :
        IAsyncDisposable
    {
        private bool _disposed;

        public AppendMergeRunEntry<TKey, TValue> Current => enumerator.Current;

        public ValueTask<bool> MoveNextAsync() => enumerator.MoveNextAsync();

        public ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }

            _disposed = true;
            return enumerator.DisposeAsync();
        }
    }
}
