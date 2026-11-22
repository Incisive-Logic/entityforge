using EntityForge.Db.AccessPaths;

namespace EntityForge.Db.Storage.Pages;

/// <summary>Exclusive key/value continuation position for an append/merge snapshot.</summary>
/// <param name="Generation">Manifest generation captured by the snapshot.</param>
/// <param name="Key">Last returned key.</param>
/// <param name="Value">Last returned value within that key's ordering.</param>
/// <remarks>Reuse only with the originating snapshot and range. The generation check is not a globally unique
/// snapshot identity and does not authenticate a cursor supplied by an untrusted client.</remarks>
public sealed record AppendMergeCursor<TKey, TValue>(long Generation, TKey Key, TValue Value);

/// <summary>A forward page of visible key/value entries.</summary>
/// <param name="Items">Entries in key/value comparison order.</param>
/// <param name="NextCursor">Exclusive continuation position, or null when the range is exhausted.</param>
public sealed record AppendMergePage<TKey, TValue>(
    IReadOnlyList<KeyValuePair<TKey, TValue>> Items,
    AppendMergeCursor<TKey, TValue>? NextCursor);

/// <summary>Immutable append/merge snapshot that resolves newest visible operations first.</summary>
public sealed class AppendMergeSnapshot<TKey, TValue> : IDisposable, IAsyncDisposable where TKey : notnull
{
    private readonly AppendMergeRunCodec<TKey, TValue> _codec;
    private readonly AppendMergeRunReader<TKey, TValue> _reader;
    private readonly IReadOnlyList<AppendMergeRunDescriptor> _runs;
    private readonly IComparer<TKey> _keys;
    private readonly IComparer<TValue> _values;
    private readonly long? _maximumVersion;
    private long _runProbeCount;
    private readonly SnapshotOperationGate _operations;

    internal AppendMergeSnapshot(AppendMergeRunCodec<TKey, TValue> codec,
        AppendMergeRunReader<TKey, TValue> reader,
        long generation, IReadOnlyList<AppendMergeRunDescriptor> runs,
        IComparer<TKey> keys, IComparer<TValue> values, long? maximumVersion, Action release)
    {
        _codec = codec;
        _reader = reader;
        Generation = generation;
        _runs = runs;
        _keys = keys;
        _values = values;
        _maximumVersion = maximumVersion;
        _operations = new(release);
    }

    /// <summary>Gets the captured manifest generation.</summary>
    public long Generation
    {
        get;
    }

    /// <summary>Gets the optional committed-version ceiling applied by this snapshot.</summary>
    public long? MaximumVersion => _maximumVersion;

    /// <summary>Gets the number of run readers opened by this snapshot.</summary>
    public long RunProbeCount => Interlocked.Read(ref _runProbeCount);

    /// <summary>Streams visible entries with one forward reader per immutable run.
    /// Retains one physical page per run rather than restarting scans for each logical page.</summary>
    public async IAsyncEnumerable<KeyValuePair<TKey, TValue>> ReadVisibleAsync(
        OrderedIndexRange<TKey>? range = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var entry in ReadVisibleCoreAsync(range, null, cancellationToken).ConfigureAwait(false))
        {
            yield return entry;
        }
    }

    private async IAsyncEnumerable<KeyValuePair<TKey, TValue>> ReadVisibleCoreAsync(
        OrderedIndexRange<TKey>? range, AppendMergeCursor<TKey, TValue>? cursor,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken,
        int? logicalPageSize = null)
    {
        using IDisposable operation = _operations.Enter(this);
        cancellationToken.ThrowIfCancellationRequested();
        range ??= OrderedIndexRange<TKey>.All;
        var order = new CompositeKeyComparer(_keys, _values);
        var priority = Comparer<(CompositeKey Key, int Run)>.Create((left, right) =>
        {
            int comparison = order.Compare(left.Key, right.Key);
            return comparison != 0 ? comparison : left.Run.CompareTo(right.Run);
        });
        var queue = new PriorityQueue<int, (CompositeKey Key, int Run)>(priority);
        var readers = new List<IAsyncEnumerator<AppendMergeRunEntry<TKey, TValue>>>();
        try
        {
            var candidates = new List<AppendMergeRunDescriptor>();
            foreach (var run in _runs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (range.HasLowerBound && _keys.Compare(_codec.DecodeKey(run.MaximumKey.Span), range.LowerInclusive!) < 0 ||
                    range.HasUpperBound && _keys.Compare(_codec.DecodeKey(run.MinimumKey.Span), range.UpperInclusive!) > 0)
                {
                    continue;
                }

                if (range.HasLowerBound && range.HasUpperBound && _keys.Compare(range.LowerInclusive!, range.UpperInclusive!) == 0 &&
                    !run.Filter.MightContain(_codec.EncodeKey(range.LowerInclusive!)))
                {
                    continue;
                }
                candidates.Add(run);
            }
            // A logical page merges every intersecting run. Reading 128 entries
            // from each run can discard almost all of that work when the page
            // is small relative to the run count. Keep streaming reads unchanged,
            // and divide bounded-page read-ahead across the eligible runs.
            int readAhead = logicalPageSize is { } size
                ? (int)Math.Clamp(((long)size + candidates.Count - 1) / Math.Max(1, candidates.Count), 8, 128)
                : 128;
            foreach (var run in candidates)
            {
                var reader = (cursor is null
                    ? _reader.ReadStreamAsync(run, range, _keys, _values, cancellationToken)
                    : ReadRunAfterAsync(run, range, cursor, readAhead, cancellationToken)).GetAsyncEnumerator();
                int index = readers.Count;
                readers.Add(reader);
                Interlocked.Increment(ref _runProbeCount);
                AppendMergeLookupTelemetry.Add(LookupWork.RunProbes);
                AppendMergeLookupTelemetry.Add(LookupWork.SequentialRuns);
                if (await reader.MoveNextAsync().ConfigureAwait(false))
                {
                    queue.Enqueue(index, (new(reader.Current.Key, reader.Current.Value), index));
                }
            }
            AppendMergeRunEntry<TKey, TValue>? newestVisibleOperation = null;
            CompositeKey? currentIdentity = null;
            // All runs must advance past a key/value pair before its winner can be emitted.
            // A newer tombstone suppresses older additions, even when a page is nearly full.
            while (queue.TryDequeue(out int index, out var next))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var reader = readers[index];
                var entry = reader.Current;
                if (currentIdentity is { } current && order.Compare(current, next.Key) != 0)
                {
                    if (newestVisibleOperation is { Kind: AppendMergeMutationKind.Add } visible)
                    {
                        yield return new(visible.Key, visible.Value);
                    }

                    newestVisibleOperation = null;
                }
                currentIdentity = next.Key;
                if ((_maximumVersion is null || entry.Version <= _maximumVersion) &&
                    (newestVisibleOperation is null || entry.Version > newestVisibleOperation.Value.Version))
                {
                    newestVisibleOperation = entry;
                }

                if (await reader.MoveNextAsync().ConfigureAwait(false))
                {
                    queue.Enqueue(index, (new(reader.Current.Key, reader.Current.Value), index));
                }
            }
            if (newestVisibleOperation is { Kind: AppendMergeMutationKind.Add } last)
            {
                yield return new(last.Key, last.Value);
            }
        }
        finally
        {
            foreach (var reader in readers)
            {
                await reader.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>Streams stored additions from every retained run for conservative
    /// reference tracing, including superseded values and duplicates. This is not
    /// a current-state query; tombstones are omitted and no history is discarded.</summary>
    public async IAsyncEnumerable<KeyValuePair<TKey, TValue>> ReadStoredAdditionsAsync(
        OrderedIndexRange<TKey> range,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(range);
        using IDisposable operation = _operations.Enter(this);
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var run in _runs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (range.HasLowerBound && _keys.Compare(_codec.DecodeKey(run.MaximumKey.Span), range.LowerInclusive!) < 0 ||
                range.HasUpperBound && _keys.Compare(_codec.DecodeKey(run.MinimumKey.Span), range.UpperInclusive!) > 0)
            {
                continue;
            }

            Interlocked.Increment(ref _runProbeCount);
            AppendMergeLookupTelemetry.Add(LookupWork.RunProbes);
            AppendMergeLookupTelemetry.Add(LookupWork.SequentialRuns);
            await foreach (var entry in _reader.ReadStreamAsync(run, range, _keys, _values, cancellationToken).ConfigureAwait(false))
            {
                if (entry.Kind == AppendMergeMutationKind.Add && (_maximumVersion is null || entry.Version <= _maximumVersion))
                {
                    yield return new(entry.Key, entry.Value);
                }
            }
        }
    }

    /// <summary>Reads one forward page of currently visible entries in key/value order.</summary>
    /// <param name="pageSize">Positive maximum returned entry count; the caller must bound this allocation.</param>
    /// <param name="range">Inclusive key bounds, or null for the entire index. Keep unchanged between pages.</param>
    /// <param name="cursor">Position from a previous page of this snapshot, or null to begin.</param>
    /// <param name="cancellationToken">Cancels reading.</param>
    /// <returns>A page with a continuation only when another visible entry exists.</returns>
    public async ValueTask<AppendMergePage<TKey, TValue>> ReadPageAsync(int pageSize,
        OrderedIndexRange<TKey>? range = null, AppendMergeCursor<TKey, TValue>? cursor = null,
        CancellationToken cancellationToken = default)
    {
        using IDisposable operation = _operations.Enter(this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);
        if (cursor is not null && cursor.Generation != Generation)
        {
            throw new ArgumentException("The cursor does not belong to this append/merge snapshot.", nameof(cursor));
        }

        range ??= OrderedIndexRange<TKey>.All;
        var page = new List<KeyValuePair<TKey, TValue>>(pageSize);
        await foreach (var entry in ReadVisibleCoreAsync(range, cursor, cancellationToken, pageSize).ConfigureAwait(false))
        {
            if (page.Count == pageSize)
            {
                return new(page, new(Generation, page[^1].Key, page[^1].Value));
            }

            page.Add(entry);
        }
        return new(page, null);
    }

    private async IAsyncEnumerable<AppendMergeRunEntry<TKey, TValue>> ReadRunAfterAsync(
        AppendMergeRunDescriptor run, OrderedIndexRange<TKey> range, AppendMergeCursor<TKey, TValue> after,
        int readAhead,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        AppendMergeRunReadCursor? cursor = null;
        do
        {
            var (entries, nextCursor) = await _reader.ReadRangeAsync(run, range, true, after.Key, after.Value,
                _keys, _values, cursor, readAhead, token).ConfigureAwait(false);
            foreach (var entry in entries)
            {
                yield return entry;
            }

            cursor = nextCursor;
        } while (cursor is not null);
    }

    /// <summary>Finds currently visible values for an exact key in value comparison order.</summary>
    /// <param name="key">Key compared using the index's key comparer.</param>
    /// <param name="cancellationToken">Cancels reading.</param>
    /// <param name="maximumResults">Optional positive result limit. Null returns all visible values.</param>
    /// <returns>Visible values, limited when requested. A limited result does not indicate whether more values exist;
    /// use <see cref="ReadPageAsync"/> when a continuation is needed.</returns>
    public async ValueTask<IReadOnlyList<TValue>> FindAsync(TKey key,
        CancellationToken cancellationToken = default, int? maximumResults = null)
    {
        using IDisposable operation = _operations.Enter(this);
        if (maximumResults is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumResults));
        }

        if (maximumResults is null)
        {
            return (await FindCoreAsync(key, int.MaxValue, cancellationToken).ConfigureAwait(false)).Values;
        }

        var result = new List<TValue>(maximumResults.Value);
        await foreach (var entry in ReadVisibleCoreAsync(new(key, true, key, true), null, cancellationToken).ConfigureAwait(false))
        {
            result.Add(entry.Value);
            if (result.Count == maximumResults.Value)
            {
                break;
            }
        }
        return result;
    }

    private async ValueTask<(IReadOnlyList<TValue> Values, bool MayHaveMore)> FindCoreAsync(
        TKey key, int perRunLimit, CancellationToken cancellationToken)
    {
        var resolved = new SortedSet<AppendMergeRunEntry<TKey, TValue>>(
            Comparer<AppendMergeRunEntry<TKey, TValue>>.Create((left, right) =>
                _values.CompareAt(key, left.Value, right.Value)));
        bool mayHaveMore = false;
        byte[] encodedKey = _codec.EncodeKey(key);
        foreach (AppendMergeRunDescriptor run in _runs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_keys.Compare(key, _codec.DecodeKey(run.MinimumKey.Span)) < 0 ||
                _keys.Compare(key, _codec.DecodeKey(run.MaximumKey.Span)) > 0 ||
                !run.Filter.MightContain(encodedKey))
            {
                continue;
            }

            Interlocked.Increment(ref _runProbeCount);
            IReadOnlyList<AppendMergeRunEntry<TKey, TValue>> matches = await _reader.FindAsync(
                run, key, _keys, _values, cancellationToken, perRunLimit).ConfigureAwait(false);
            mayHaveMore |= matches.Count >= perRunLimit;
            foreach (AppendMergeRunEntry<TKey, TValue> entry in matches)
            {
                if (_maximumVersion is long maximum && entry.Version > maximum)
                {
                    continue;
                }

                if (_keys.Compare(entry.Key, key) != 0)
                {
                    continue;
                }

                if (!resolved.TryGetValue(entry, out AppendMergeRunEntry<TKey, TValue> existing))
                {
                    resolved.Add(entry);
                }
                else if (entry.Version > existing.Version)
                {
                    resolved.Remove(existing);
                    resolved.Add(entry);
                }
            }
        }
        return (resolved.Where(entry => entry.Kind is AppendMergeMutationKind.Add)
            .Select(entry => entry.Value).ToArray(), mayHaveMore);
    }

    /// <summary>Finds all currently visible duplicate values for each requested exact key.</summary>
    public async ValueTask<IReadOnlyDictionary<TKey, IReadOnlyList<TValue>>> FindManyAsync(
        IReadOnlyList<TKey> keys, CancellationToken cancellationToken = default)
    {
        using IDisposable operation = _operations.Enter(this);
        ArgumentNullException.ThrowIfNull(keys);
        var resolved = new SortedDictionary<TKey,
            SortedDictionary<ValueKey, AppendMergeRunEntry<TKey, TValue>>>(_keys);
        foreach (TKey key in keys)
        {
            if (!resolved.ContainsKey(key))
            {
                resolved.Add(key,
                new SortedDictionary<ValueKey, AppendMergeRunEntry<TKey, TValue>>(
                    new ValueKeyComparer(key, _values)));
            }
        }

        if (resolved.Count == 0)
        {
            return new SortedDictionary<TKey, IReadOnlyList<TValue>>(_keys);
        }

        TKey[] requested = resolved.Keys.ToArray();
        var encoded = new byte[requested.Length][];
        TKey first = requested[0];
        TKey last = requested[^1];
        foreach (AppendMergeRunDescriptor run in _runs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TKey minimum = _codec.DecodeKey(run.MinimumKey.Span);
            TKey maximumKey = _codec.DecodeKey(run.MaximumKey.Span);
            if (_keys.Compare(last, minimum) < 0 || _keys.Compare(first, maximumKey) > 0)
            {
                continue;
            }

            var candidates = new List<TKey>();
            for (int index = 0; index < requested.Length; index++)
            {
                TKey key = requested[index];
                if (_keys.Compare(key, minimum) < 0)
                {
                    continue;
                }

                if (_keys.Compare(key, maximumKey) > 0)
                {
                    break;
                }
                // Encode only keys that intersect a run, and reuse their bytes within this lookup.
                byte[] bytes = encoded[index] ??= _codec.EncodeKey(key);
                if (run.Filter.MightContain(bytes))
                {
                    candidates.Add(key);
                }
            }
            if (candidates.Count == 0)
            {
                continue;
            }

            AppendMergeLookupTelemetry.Add(LookupWork.RunProbes);
            AppendMergeLookupTelemetry.Add(LookupWork.CandidateKeys, candidates.Count);
            Interlocked.Increment(ref _runProbeCount);
            IReadOnlyDictionary<TKey, IReadOnlyList<AppendMergeRunEntry<TKey, TValue>>> runMatches =
                await _reader.FindManyAsync(run, candidates, _keys, cancellationToken).ConfigureAwait(false);
            foreach ((TKey key, IReadOnlyList<AppendMergeRunEntry<TKey, TValue>> entries) in runMatches)
            {
                SortedDictionary<ValueKey, AppendMergeRunEntry<TKey, TValue>> matches = resolved[key];
                foreach (AppendMergeRunEntry<TKey, TValue> entry in entries)
                {
                    if (_maximumVersion is long maximum && entry.Version > maximum)
                    {
                        continue;
                    }

                    var identity = new ValueKey(entry.Value);
                    if (!matches.TryGetValue(identity, out AppendMergeRunEntry<TKey, TValue> existing) ||
                        entry.Version > existing.Version)
                    {
                        matches[identity] = entry;
                    }
                }
            }
        }
        var result = new SortedDictionary<TKey, IReadOnlyList<TValue>>(_keys);
        foreach ((TKey key, SortedDictionary<ValueKey, AppendMergeRunEntry<TKey, TValue>> matches) in resolved)
        {
            result.Add(key, matches.Values.Where(entry => entry.Kind is AppendMergeMutationKind.Add)
                .Select(entry => entry.Value).ToArray());
        }

        return result;
    }

    /// <summary>Finds at most one currently visible value for every requested key.</summary>
    public async ValueTask<IReadOnlyDictionary<TKey, TValue>> FindUniqueAsync(
        IReadOnlyList<TKey> keys, CancellationToken cancellationToken = default)
    {
        IReadOnlyDictionary<TKey, IReadOnlyList<TValue>> matches = await FindManyAsync(
            keys, cancellationToken).ConfigureAwait(false);
        var result = new SortedDictionary<TKey, TValue>(_keys);
        foreach ((TKey key, IReadOnlyList<TValue> values) in matches)
        {
            if (values.Count > 1)
            {
                throw new AppendMergeException("An append/merge key has more than one visible value.");
            }

            if (values.Count == 1)
            {
                result.Add(key, values[0]);
            }
        }
        return result;
    }

    /// <summary>Prevents new reads and waits for admitted operations to drain before releasing snapshot ownership.</summary>
    /// <remarks>Finish or dispose active asynchronous enumerators before awaiting disposal.</remarks>
    public ValueTask DisposeAsync() => _operations.CloseAsync();

    /// <summary>Prevents new reads. Releases snapshot ownership immediately if idle, or after admitted reads drain;
    /// this call does not wait for those reads.</summary>
    public void Dispose() => _operations.Close();

    private readonly record struct CompositeKey(TKey Key, TValue Value);

    private readonly record struct ValueKey(TValue Value);

    private sealed class CompositeKeyComparer(IComparer<TKey> keys, IComparer<TValue> values)
        : IComparer<CompositeKey>
    {
        public int Compare(CompositeKey left, CompositeKey right)
        {
            int key = keys.Compare(left.Key, right.Key);
            return key != 0 ? key : values.CompareAt(left.Key, left.Value, right.Value);
        }
    }

    private sealed class ValueKeyComparer(TKey key, IComparer<TValue> values) : IComparer<ValueKey>
    {
        public int Compare(ValueKey left, ValueKey right) => values.CompareAt(key, left.Value, right.Value);
    }

}
