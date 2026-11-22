using EntityForge.Db.AccessPaths;

namespace EntityForge.Db.Storage.Pages;

/// <summary>Reads one immutable ordered tree generation while loading its leaf pages on demand.</summary>
/// <typeparam name="TKey">The non-null key type.</typeparam>
/// <typeparam name="TValue">The indexed value type.</typeparam>
/// <remarks>Dispose the snapshot to release its generation pin. Already admitted reads retain
/// that pin until they finish; await <see cref="DisposeAsync"/> when reclamation must wait for them.</remarks>
public sealed class DiskResidentOrderedPageTreeSnapshot<TKey, TValue> : IDisposable, IAsyncDisposable where TKey : notnull
{
    private const int DenseBatchLeafCoverageDivisor = 2;
    private readonly IResizablePhysicalPageStore _pages;
    private readonly OrderedPageTreeCodec<TKey, TValue> _codec;
    private readonly IReadOnlyList<DiskResidentLeafDescriptor<TKey, TValue>> _leaves;
    private readonly IComparer<TKey> _keys;
    private readonly IComparer<TValue> _values;
    private readonly SnapshotOperationGate _operations;

    internal DiskResidentOrderedPageTreeSnapshot(Guid accessPathId, OrderedPageTreeGeneration generation,
        long count, IResizablePhysicalPageStore pages, OrderedPageTreeCodec<TKey, TValue> codec,
        IReadOnlyList<DiskResidentLeafDescriptor<TKey, TValue>> leaves,
        IComparer<TKey> keys, IComparer<TValue> values, Action release)
    {
        AccessPathId = accessPathId;
        Generation = generation;
        Count = count;
        _pages = pages;
        _codec = codec;
        _leaves = leaves;
        _keys = keys;
        _values = values;
        _operations = new(release);
    }

    /// <summary>Gets the identity of the tree that owns this snapshot.</summary>
    public Guid AccessPathId
    {
        get;
    }

    /// <summary>Gets the immutable generation pinned by this snapshot.</summary>
    public OrderedPageTreeGeneration Generation
    {
        get;
    }

    /// <summary>Gets the number of key/value entries in the snapshot.</summary>
    public long Count
    {
        get;
    }

    /// <summary>Loads all values whose keys compare equal to the supplied key.</summary>
    /// <param name="key">The key to match using the tree's key comparer.</param>
    /// <param name="cancellationToken">Cancels underlying page reads.</param>
    /// <returns>Matching values in stored order, or an empty list.</returns>
    /// <exception cref="ObjectDisposedException">Snapshot disposal has begun.</exception>
    public async ValueTask<IReadOnlyList<TValue>> FindAsync(TKey key,
        CancellationToken cancellationToken = default)
    {
        using IDisposable operation = _operations.Enter(this);
        if (_leaves.Count == 0)
        {
            return Array.Empty<TValue>();
        }

        int index = FirstPossibleLeaf(key);
        var result = new List<TValue>();
        for (; index < _leaves.Count; index++)
        {
            DiskResidentLeafDescriptor<TKey, TValue> descriptor = _leaves[index];
            if (result.Count != 0 && _keys.Compare(descriptor.FirstKey, key) > 0)
            {
                break;
            }

            OrderedLeaf<TKey, TValue> leaf = await ReadLeafAsync(descriptor.PageId, cancellationToken)
                .ConfigureAwait(false);
            foreach (OrderedLeafEntry<TKey, TValue> entry in leaf.Entries)
            {
                int comparison = _keys.Compare(entry.Key, key);
                if (comparison > 0)
                {
                    return result;
                }

                if (comparison == 0)
                {
                    result.Add(entry.Value);
                }
            }
        }
        return result;
    }

    /// <summary>Loads at most one value for each requested key, rejecting duplicate stored keys.</summary>
    /// <param name="keys">Keys to find; comparer-equal requests are consolidated.</param>
    /// <param name="cancellationToken">Cancels lookup and page reads.</param>
    /// <returns>A dictionary using the tree's key comparer; missing keys are omitted.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="keys"/> is null.</exception>
    /// <exception cref="InvalidDataException">A requested key has multiple stored entries.</exception>
    /// <exception cref="ObjectDisposedException">Snapshot disposal has begun.</exception>
    public async ValueTask<IReadOnlyDictionary<TKey, TValue>> FindUniqueAsync(
        IReadOnlyList<TKey> keys, CancellationToken cancellationToken = default)
    {
        using IDisposable operation = _operations.Enter(this);
        ArgumentNullException.ThrowIfNull(keys);
        var result = new SortedDictionary<TKey, TValue>(_keys);
        if (_leaves.Count == 0 || keys.Count == 0)
        {
            return result;
        }

        IReadOnlyList<TKey> unique = Unique(keys);
        int firstLeaf = FirstPossibleLeaf(unique[0]);
        int lastLeaf = LastPossibleLeaf(unique[^1]);
        if ((long)unique.Count * DenseBatchLeafCoverageDivisor >= lastLeaf - firstLeaf + 1L)
        {
            var requested = new SortedDictionary<TKey, byte>(_keys);
            foreach (TKey key in unique)
            {
                requested.Add(key, 0);
            }

            for (int index = firstLeaf; index <= lastLeaf; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                OrderedLeaf<TKey, TValue> leaf = await ReadLeafAsync(_leaves[index].PageId, cancellationToken)
                    .ConfigureAwait(false);
                foreach (OrderedLeafEntry<TKey, TValue> entry in leaf.Entries)
                {
                    if (!requested.ContainsKey(entry.Key))
                    {
                        continue;
                    }

                    if (!result.TryAdd(entry.Key, entry.Value))
                    {
                        throw new InvalidDataException("A unique ordered lookup encountered duplicate keys.");
                    }
                }
            }
            return result;
        }
        var leaves = new Dictionary<int, OrderedLeaf<TKey, TValue>>();
        foreach (TKey key in unique)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TValue? match = default;
            bool found = false;
            for (int index = FirstPossibleLeaf(key); index < _leaves.Count; index++)
            {
                if (index > 0 && _keys.Compare(_leaves[index].FirstKey, key) > 0)
                {
                    break;
                }

                if (!leaves.TryGetValue(index, out OrderedLeaf<TKey, TValue>? leaf))
                {
                    leaf = await ReadLeafAsync(_leaves[index].PageId, cancellationToken).ConfigureAwait(false);
                    leaves.Add(index, leaf);
                }
                foreach (OrderedLeafEntry<TKey, TValue> entry in leaf.Entries.Where(entry =>
                             _keys.Compare(entry.Key, key) == 0))
                {
                    if (found)
                    {
                        throw new InvalidDataException(
                        "A unique ordered lookup encountered duplicate keys.");
                    }

                    match = entry.Value;
                    found = true;
                }
            }
            if (found)
            {
                result.Add(key, match!);
            }
        }
        return result;
    }

    /// <summary>Loads all matching values for a batch of keys.</summary>
    /// <param name="keys">Keys to find; comparer-equal requests are consolidated.</param>
    /// <param name="cancellationToken">Cancels lookup and page reads.</param>
    /// <returns>A dictionary using the tree's key comparer, with matching values in stored order.
    /// Keys with no values are omitted.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="keys"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">Snapshot disposal has begun.</exception>
    public async ValueTask<IReadOnlyDictionary<TKey, IReadOnlyList<TValue>>> FindManyAsync(
        IReadOnlyList<TKey> keys, CancellationToken cancellationToken = default)
    {
        using IDisposable operation = _operations.Enter(this);
        ArgumentNullException.ThrowIfNull(keys);
        var result = new SortedDictionary<TKey, IReadOnlyList<TValue>>(_keys);
        if (_leaves.Count == 0 || keys.Count == 0)
        {
            return result;
        }

        IReadOnlyList<TKey> unique = Unique(keys);
        int firstLeaf = FirstPossibleLeaf(unique[0]);
        int lastLeaf = LastPossibleLeaf(unique[^1]);
        if ((long)unique.Count * DenseBatchLeafCoverageDivisor >= lastLeaf - firstLeaf + 1L)
        {
            var matches = new SortedDictionary<TKey, List<TValue>>(_keys);
            foreach (TKey key in unique)
            {
                matches.Add(key, []);
            }

            for (int index = firstLeaf; index <= lastLeaf; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                OrderedLeaf<TKey, TValue> leaf = await ReadLeafAsync(_leaves[index].PageId, cancellationToken)
                    .ConfigureAwait(false);
                foreach (OrderedLeafEntry<TKey, TValue> entry in leaf.Entries)
                {
                    if (matches.TryGetValue(entry.Key, out List<TValue>? values))
                    {
                        values.Add(entry.Value);
                    }
                }
            }
            foreach ((TKey key, List<TValue> values) in matches)
            {
                if (values.Count != 0)
                {
                    result.Add(key, values);
                }
            }

            return result;
        }
        var leaves = new Dictionary<int, OrderedLeaf<TKey, TValue>>();
        foreach (TKey key in unique)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var matches = new List<TValue>();
            for (int index = FirstPossibleLeaf(key); index < _leaves.Count; index++)
            {
                if (index > 0 && _keys.Compare(_leaves[index].FirstKey, key) > 0)
                {
                    break;
                }

                if (!leaves.TryGetValue(index, out OrderedLeaf<TKey, TValue>? leaf))
                {
                    leaf = await ReadLeafAsync(_leaves[index].PageId, cancellationToken).ConfigureAwait(false);
                    leaves.Add(index, leaf);
                }
                foreach (OrderedLeafEntry<TKey, TValue> entry in leaf.Entries)
                {
                    if (_keys.Compare(entry.Key, key) == 0)
                    {
                        matches.Add(entry.Value);
                    }
                }
            }
            if (matches.Count != 0)
            {
                result.Add(key, matches);
            }
        }
        return result;
    }

    /// <summary>Reads a bounded page from this pinned generation, loading leaf pages as needed.</summary>
    /// <param name="pageSize">Maximum returned pairs; positive and less than <see cref="int.MaxValue"/>.</param>
    /// <param name="range">Inclusive key bounds, or null for all keys.</param>
    /// <param name="direction">Ascending or descending key/value comparer order.</param>
    /// <param name="cursor">Exclusive continuation position, or null for the first page.</param>
    /// <param name="cancellationToken">Cancels scanning and page reads.</param>
    /// <returns>Up to <paramref name="pageSize"/> entries, with a cursor only when matching entries remain.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pageSize"/> is not positive.</exception>
    /// <exception cref="OverflowException"><paramref name="pageSize"/> is <see cref="int.MaxValue"/>.</exception>
    /// <exception cref="ArgumentException">The cursor has a different tree identity, generation or direction.</exception>
    /// <exception cref="ObjectDisposedException">Snapshot disposal has begun.</exception>
    /// <remarks>Keep the same range for subsequent pages; range bounds are not stored in the cursor.</remarks>
    public async ValueTask<OrderedPageTreePage<TKey, TValue>> ReadPageAsync(int pageSize,
        OrderedIndexRange<TKey>? range = null, OrderedScanDirection direction = OrderedScanDirection.Forward,
        OrderedPageTreeCursor<TKey, TValue>? cursor = null,
        CancellationToken cancellationToken = default)
    {
        using IDisposable operation = _operations.Enter(this);
        if (pageSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }

        if (cursor is not null && (cursor.AccessPathId != AccessPathId ||
                                   cursor.Generation != Generation || cursor.Direction != direction))
        {
            throw new ArgumentException("The cursor does not belong to this access-path snapshot.", nameof(cursor));
        }

        range ??= OrderedIndexRange<TKey>.All;
        var selected = new List<OrderedLeafEntry<TKey, TValue>>(checked(pageSize + 1));
        int firstLeaf = range.HasLowerBound ? FirstPossibleLeaf(range.LowerInclusive!) : 0;
        int lastLeaf = range.HasUpperBound ? LastPossibleLeaf(range.UpperInclusive!) : _leaves.Count - 1;
        if (cursor is not null)
        {
            int low = 0, high = _leaves.Count;
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                var leaf = _leaves[middle];
                if (Compare(leaf.FirstKey, leaf.FirstValue, cursor.Key, cursor.Value) <= 0)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }
            // The preceding leaf may contain the cursor's successor/predecessor.
            if (direction is OrderedScanDirection.Forward)
            {
                firstLeaf = Math.Max(firstLeaf, Math.Max(0, low - 1));
            }
            else
            {
                lastLeaf = Math.Min(lastLeaf, low - 1);
            }
        }
        IEnumerable<int> indices = firstLeaf <= lastLeaf
            ? Enumerable.Range(firstLeaf, lastLeaf - firstLeaf + 1)
            : Enumerable.Empty<int>();
        if (direction is OrderedScanDirection.Reverse)
        {
            indices = indices.Reverse();
        }

        foreach (int index in indices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OrderedLeaf<TKey, TValue> leaf = await ReadLeafAsync(_leaves[index].PageId, cancellationToken)
                .ConfigureAwait(false);
            IEnumerable<OrderedLeafEntry<TKey, TValue>> entries = direction is OrderedScanDirection.Forward
                ? leaf.Entries : leaf.Entries.Reverse();
            foreach (OrderedLeafEntry<TKey, TValue> entry in entries)
            {
                if (range.HasLowerBound && _keys.Compare(entry.Key, range.LowerInclusive!) < 0 ||
                    range.HasUpperBound && _keys.Compare(entry.Key, range.UpperInclusive!) > 0)
                {
                    continue;
                }

                if (cursor is not null)
                {
                    int comparison = Compare(entry.Key, entry.Value, cursor.Key, cursor.Value);
                    if (direction is OrderedScanDirection.Forward ? comparison <= 0 : comparison >= 0)
                    {
                        continue;
                    }
                }
                selected.Add(entry);
                if (selected.Count > pageSize)
                {
                    goto Complete;
                }
            }
        }
    Complete:
        bool more = selected.Count > pageSize;
        if (more)
        {
            selected.RemoveAt(selected.Count - 1);
        }

        OrderedPageTreeCursor<TKey, TValue>? next = more && selected.Count != 0
            ? new(AccessPathId, Generation, selected[^1].Key, selected[^1].Value, direction) : null;
        return new(selected.Select(item => new KeyValuePair<TKey, TValue>(item.Key, item.Value)).ToArray(), next);
    }

    private int FirstPossibleLeaf(TKey key)
    {
        int low = 0, high = _leaves.Count - 1;
        while (low <= high)
        {
            int middle = low + ((high - low) / 2);
            if (_keys.Compare(_leaves[middle].FirstKey, key) < 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }
        int index = Math.Max(0, low - 1);
        while (index > 0 && _keys.Compare(_leaves[index].FirstKey, key) == 0)
        {
            index--;
        }

        return index;
    }

    private int LastPossibleLeaf(TKey key)
    {
        int low = 0, high = _leaves.Count - 1;
        while (low <= high)
        {
            int middle = low + ((high - low) / 2);
            if (_keys.Compare(_leaves[middle].FirstKey, key) <= 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }
        return high;
    }

    private async ValueTask<OrderedLeaf<TKey, TValue>> ReadLeafAsync(PhysicalPageId pageId,
        CancellationToken cancellationToken) => await PhysicalPageDecoding.ReadAsync(_pages, pageId,
            image => _codec.DecodeLeaf(image.Span, pageId), cancellationToken).ConfigureAwait(false);

    private int Compare(TKey leftKey, TValue leftValue, TKey rightKey, TValue rightValue)
    {
        int key = _keys.Compare(leftKey, rightKey);
        return key != 0 ? key : _values.Compare(leftValue, rightValue);
    }

    private IReadOnlyList<TKey> Unique(IReadOnlyList<TKey> keys)
    {
        TKey[] ordered = keys.OrderBy(key => key, _keys).ToArray();
        if (ordered.Length < 2)
        {
            return ordered;
        }

        var unique = new List<TKey>(ordered.Length) { ordered[0] };
        for (int index = 1; index < ordered.Length; index++)
        {
            if (_keys.Compare(ordered[index - 1], ordered[index]) != 0)
            {
                unique.Add(ordered[index]);
            }
        }

        return unique;
    }

    /// <summary>Prevents new reads and waits for admitted reads to finish before releasing the generation pin.</summary>
    /// <returns>A task that completes after the pin has been released.</returns>
    public ValueTask DisposeAsync() => _operations.CloseAsync();

    /// <summary>Prevents new reads and arranges release of the generation pin after admitted reads finish.</summary>
    /// <remarks>This method does not wait for outstanding asynchronous reads. Use
    /// <see cref="DisposeAsync"/> when the caller must observe their completion before proceeding.</remarks>
    public void Dispose() => _operations.Close();
}

internal sealed record DiskResidentLeafDescriptor<TKey, TValue>(
    PhysicalPageId PageId, TKey FirstKey, TValue FirstValue, int EntryCount) where TKey : notnull;
