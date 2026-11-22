using EntityForge.Db.Storage;
using EntityForge.Db.AccessPaths;

namespace EntityForge.Db.Storage.Pages;

/// <summary>Converts an ordered index key or value to and from its stored byte representation.</summary>
/// <typeparam name="T">The key or value type.</typeparam>
public interface IOrderedIndexCodec<T>
{
    /// <summary>Encodes a value for storage in an ordered page.</summary>
    /// <param name="value">The value to encode.</param>
    /// <returns>The encoded bytes.</returns>
    byte[] Encode(T value);

    /// <summary>Decodes a stored value, rejecting an invalid representation.</summary>
    /// <param name="value">Encoded bytes, which must not be retained by the decoded result.</param>
    /// <returns>A value that owns any data needed after the input span expires.</returns>
    T Decode(ReadOnlySpan<byte> value);
}

/// <summary>A codec that can validate an encoded value without constructing it.</summary>
/// <remarks>
/// Validate must accept exactly the inputs accepted by Decode and perform all of its
/// corruption checks, without retaining the supplied bytes. Both operations must be
/// deterministic and independent of previous calls. Decode still owns its result.
/// Codecs without this explicit contract continue to decode every visited record.
/// </remarks>
public interface IValidatingOrderedIndexCodec<T> : IOrderedIndexCodec<T>
{
    /// <summary>Performs the same representation and corruption checks as decoding without creating a value.</summary>
    /// <param name="value">Encoded bytes to validate; the implementation must not retain them.</param>
    void Validate(ReadOnlySpan<byte> value);
}

/// <summary>Specifies whether an ordered key/value pair is added or removed.</summary>
public enum OrderedPageTreeMutationKind
{
    /// <summary>Adds a key/value pair.</summary>
    Add,

    /// <summary>Removes a key/value pair.</summary>
    Remove
}

/// <summary>Describes one pair-level change to an ordered page tree.</summary>
/// <typeparam name="TKey">The non-null key type.</typeparam>
/// <typeparam name="TValue">The indexed value type.</typeparam>
/// <param name="Kind">Whether to add or remove the pair.</param>
/// <param name="Key">Key of the affected pair.</param>
/// <param name="Value">Value of the affected pair.</param>
public sealed record OrderedPageTreeMutation<TKey, TValue>(OrderedPageTreeMutationKind Kind,
    TKey Key, TValue Value) where TKey : notnull;

/// <summary>Identifies an ordered page tree generation for snapshot and publication checks.</summary>
/// <param name="Value">The generation number.</param>
public sealed record OrderedPageTreeGeneration(long Value)
{
    /// <summary>Gets generation zero, before the first published change.</summary>
    public static OrderedPageTreeGeneration Initial { get; } = new(0);
}

/// <summary>Records an exclusive continuation position within one ordered tree snapshot.</summary>
/// <typeparam name="TKey">The index key type.</typeparam>
/// <typeparam name="TValue">The indexed value type.</typeparam>
/// <param name="AccessPathId">Identity of the tree that issued the cursor.</param>
/// <param name="Generation">Snapshot generation that issued the cursor.</param>
/// <param name="Key">Last returned key.</param>
/// <param name="Value">Last returned value within that key.</param>
/// <param name="Direction">Traversal direction used to issue the cursor.</param>
public sealed record OrderedPageTreeCursor<TKey, TValue>(Guid AccessPathId,
    OrderedPageTreeGeneration Generation, TKey Key, TValue Value, OrderedScanDirection Direction);

/// <summary>Contains a page of ordered pairs and its optional continuation.</summary>
/// <typeparam name="TKey">The index key type.</typeparam>
/// <typeparam name="TValue">The indexed value type.</typeparam>
/// <param name="Items">Returned pairs in the requested traversal order.</param>
/// <param name="NextCursor">Position for the next page, or null when no matching pairs remain.</param>
public sealed record OrderedPageTreePage<TKey, TValue>(
    IReadOnlyList<KeyValuePair<TKey, TValue>> Items,
    OrderedPageTreeCursor<TKey, TValue>? NextCursor);

/// <summary>An immutable reader over one published or staged ordered access-path generation.</summary>
public sealed class OrderedPageTreeSnapshot<TKey, TValue> where TKey : notnull
{
    private readonly IReadOnlyList<OrderedLeaf<TKey, TValue>> _leaves;
    private readonly IComparer<TKey> _keys;
    private readonly IComparer<TValue> _values;

    internal OrderedPageTreeSnapshot(Guid accessPathId, OrderedPageTreeGeneration generation,
        IReadOnlyList<OrderedLeaf<TKey, TValue>> leaves, IComparer<TKey> keys, IComparer<TValue> values)
    {
        AccessPathId = accessPathId;
        Generation = generation;
        _leaves = leaves;
        _keys = keys;
        _values = values;
        Count = leaves.Sum(item => item.Entries.Count);
    }

    /// <summary>Gets the identity of the tree from which this snapshot was captured.</summary>
    public Guid AccessPathId
    {
        get;
    }

    /// <summary>Gets the immutable generation represented by this snapshot.</summary>
    public OrderedPageTreeGeneration Generation
    {
        get;
    }

    /// <summary>Gets the number of key/value entries in the snapshot.</summary>
    public int Count
    {
        get;
    }

    /// <summary>Finds all values whose keys compare equal to the supplied key.</summary>
    /// <param name="key">The key to match using the tree's key comparer.</param>
    /// <returns>Matching values in stored order, or an empty list when no key matches.</returns>
    public IReadOnlyList<TValue> Find(TKey key)
    {
        if (_leaves.Count == 0)
        {
            return [];
        }

        int leafIndex = FindLeaf(key);
        while (leafIndex > 0 && _keys.Compare(_leaves[leafIndex - 1].Entries[^1].Key, key) >= 0)
        {
            leafIndex--;
        }

        var result = new List<TValue>();
        for (int index = leafIndex; index < _leaves.Count; index++)
        {
            foreach (OrderedLeafEntry<TKey, TValue> item in _leaves[index].Entries)
            {
                int comparison = _keys.Compare(item.Key, key);
                if (comparison > 0)
                {
                    return result;
                }

                if (comparison == 0)
                {
                    result.Add(item.Value);
                }
            }
        }

        return result;
    }

    /// <summary>Reads a bounded page from this immutable snapshot.</summary>
    /// <param name="pageSize">Maximum returned pairs; must be positive and less than <see cref="int.MaxValue"/>.</param>
    /// <param name="range">Inclusive key bounds, or null for all keys.</param>
    /// <param name="direction">Ascending or descending key/value comparer order.</param>
    /// <param name="cursor">Last returned pair to exclude, or null to begin the scan.</param>
    /// <returns>Up to <paramref name="pageSize"/> pairs and a cursor only if more matching pairs remain.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pageSize"/> is not positive.</exception>
    /// <exception cref="OverflowException"><paramref name="pageSize"/> is <see cref="int.MaxValue"/>.</exception>
    /// <exception cref="ArgumentException">The cursor belongs to a different tree, generation or direction.</exception>
    /// <remarks>Keep the range unchanged between pages; it is not encoded in the cursor.
    /// Ordering compares keys first and values second. The snapshot is not advanced by this call.</remarks>
    public OrderedPageTreePage<TKey, TValue> ReadPage(int pageSize,
        OrderedIndexRange<TKey>? range = null, OrderedScanDirection direction = OrderedScanDirection.Forward,
        OrderedPageTreeCursor<TKey, TValue>? cursor = null)
    {
        if (pageSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }

        if (cursor is not null && (cursor.AccessPathId != AccessPathId || cursor.Generation != Generation ||
                                   cursor.Direction != direction))
        {
            throw new ArgumentException("The cursor does not belong to this access-path snapshot.", nameof(cursor));
        }

        range ??= OrderedIndexRange<TKey>.All;
        IEnumerable<KeyValuePair<TKey, TValue>> query = _leaves.SelectMany(leaf => leaf.Entries)
            .Where(item => (!range.HasLowerBound || _keys.Compare(item.Key, range.LowerInclusive!) >= 0) &&
                           (!range.HasUpperBound || _keys.Compare(item.Key, range.UpperInclusive!) <= 0))
            .Select(item => new KeyValuePair<TKey, TValue>(item.Key, item.Value));
        if (direction is OrderedScanDirection.Reverse)
        {
            query = query.Reverse();
        }

        if (cursor is not null)
        {
            query = query.Where(item => direction is OrderedScanDirection.Forward
            ? Compare(item, cursor.Key, cursor.Value) > 0 : Compare(item, cursor.Key, cursor.Value) < 0);
        }

        KeyValuePair<TKey, TValue>[] selected = query.Take(checked(pageSize + 1)).ToArray();
        bool more = selected.Length > pageSize;
        KeyValuePair<TKey, TValue>[] visible = more ? selected[..pageSize] : selected;
        OrderedPageTreeCursor<TKey, TValue>? next = more && visible.Length != 0
            ? new(AccessPathId, Generation, visible[^1].Key, visible[^1].Value, direction) : null;
        return new(visible, next);
    }

    private int Compare(KeyValuePair<TKey, TValue> item, TKey key, TValue value)
    {
        int keyResult = _keys.Compare(item.Key, key);
        return keyResult != 0 ? keyResult : _values.Compare(item.Value, value);
    }

    private int FindLeaf(TKey key)
    {
        int low = 0, high = _leaves.Count - 1;
        while (low <= high)
        {
            int middle = low + ((high - low) / 2);
            if (_keys.Compare(_leaves[middle].Entries[0].Key, key) <= 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }
        return Math.Max(0, high);
    }
}

/// <summary>Describes prepared physical changes for a transaction's new tree generation.</summary>
/// <param name="TransactionId">Transaction that owns the prepared changes.</param>
/// <param name="Generation">Generation to publish after the transaction commits.</param>
/// <param name="PhysicalMutations">Page changes to submit through the physical commit log.</param>
/// <remarks>Preparing a stage alone does not durably commit or publish it.</remarks>
public sealed record OrderedPageTreeStage(
    Guid TransactionId,
    OrderedPageTreeGeneration Generation,
    IReadOnlyList<DatabasePhysicalPageMutation> PhysicalMutations);

/// <summary>Reports an ordered page tree storage or consistency failure.</summary>
public class OrderedPageTreeException : PhysicalPageException
{
    /// <summary>Creates a failure with a diagnostic message.</summary>
    /// <param name="message">Description of the failure.</param>
    public OrderedPageTreeException(string message) : base(message)
    {
    }

    /// <summary>Creates a failure preserving its underlying cause.</summary>
    /// <param name="message">Description of the failure.</param>
    /// <param name="inner">Exception that caused the failure.</param>
    public OrderedPageTreeException(string message, Exception inner) : base(message, inner)
    {
    }
}

/// <summary>Reports that a staged replacement was based on a generation that is no longer current.</summary>
/// <param name="expectedGeneration">Generation against which the replacement was prepared.</param>
/// <param name="actualGeneration">Generation found when attempting to prepare publication.</param>
public sealed class OrderedPageTreeReplacementStaleException(long expectedGeneration, long actualGeneration) :
    OrderedPageTreeException(
        $"The ordered replacement expected generation {expectedGeneration}, but generation {actualGeneration} is live.")
{
    /// <summary>Gets the generation against which the replacement was prepared.</summary>
    public long ExpectedGeneration { get; } = expectedGeneration;

    /// <summary>Gets the live generation that prevented publication.</summary>
    public long ActualGeneration { get; } = actualGeneration;
}
