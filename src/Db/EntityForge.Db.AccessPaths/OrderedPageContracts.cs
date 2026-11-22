namespace EntityForge.Db.AccessPaths;

/// <summary>Chooses ascending or descending traversal of an ordered key/value index.</summary>
public enum OrderedScanDirection
{
    /// <summary>Visits keys, then values within each key, in ascending comparer order.</summary>
    Forward,

    /// <summary>Visits keys, then values within each key, in descending comparer order.</summary>
    Reverse
}

/// <summary>Identifies the last returned pair when resuming a scan of one index generation.</summary>
/// <typeparam name="TKey">The index key type.</typeparam>
/// <typeparam name="TValue">The value type used to distinguish entries with the same key.</typeparam>
/// <param name="AccessPathId">Identity of the index that issued the cursor.</param>
/// <param name="Generation">Index generation to which the cursor belongs.</param>
/// <param name="Key">Last returned key.</param>
/// <param name="Value">Last returned value within that key.</param>
/// <param name="Direction">Traversal direction used to issue the cursor.</param>
/// <remarks>The cursor is an exclusive continuation position. It does not encode range bounds;
/// retain the original range when requesting subsequent pages.</remarks>
public sealed record OrderedIndexCursor<TKey, TValue>(Guid AccessPathId, long Generation,
    TKey Key, TValue Value, OrderedScanDirection Direction);

/// <summary>Contains ordered entries and an optional continuation position.</summary>
/// <typeparam name="TKey">The index key type.</typeparam>
/// <typeparam name="TValue">The indexed value type.</typeparam>
/// <param name="Items">Entries returned in the requested traversal order.</param>
/// <param name="NextCursor">Continuation for remaining entries, or null when this scan has ended.</param>
public sealed record OrderedIndexPage<TKey, TValue>(
    IReadOnlyList<KeyValuePair<TKey, TValue>> Items,
    OrderedIndexCursor<TKey, TValue>? NextCursor);

/// <summary>Specifies optional inclusive key bounds using the index's key comparer.</summary>
/// <typeparam name="TKey">The index key type.</typeparam>
/// <param name="LowerInclusive">Lower key bound; ignored when <paramref name="HasLowerBound"/> is false.</param>
/// <param name="HasLowerBound">Whether the lower bound participates in filtering.</param>
/// <param name="UpperInclusive">Upper key bound; ignored when <paramref name="HasUpperBound"/> is false.</param>
/// <param name="HasUpperBound">Whether the upper bound participates in filtering.</param>
/// <remarks>Bound flags distinguish an unbounded range from one whose bound is a default key value.
/// Construction does not validate the relative order of the bounds.</remarks>
public sealed record OrderedIndexRange<TKey>(TKey? LowerInclusive, bool HasLowerBound,
    TKey? UpperInclusive, bool HasUpperBound)
{
    /// <summary>Gets a range without lower or upper bounds.</summary>
    public static OrderedIndexRange<TKey> All { get; } = new(default, false, default, false);

    /// <summary>Creates a range including both supplied bounds.</summary>
    /// <param name="lowerInclusive">The least key to include.</param>
    /// <param name="upperInclusive">The greatest key to include.</param>
    /// <returns>A range with both bounds enabled.</returns>
    public static OrderedIndexRange<TKey> Between(TKey lowerInclusive, TKey upperInclusive) =>
        new(lowerInclusive, true, upperInclusive, true);

    /// <summary>Creates a range starting at the supplied key with no upper bound.</summary>
    /// <param name="lowerInclusive">The least key to include.</param>
    /// <returns>A range with only its lower bound enabled.</returns>
    public static OrderedIndexRange<TKey> From(TKey lowerInclusive) =>
        new(lowerInclusive, true, default, false);

    /// <summary>Creates a range ending at the supplied key with no lower bound.</summary>
    /// <param name="upperInclusive">The greatest key to include.</param>
    /// <returns>A range with only its upper bound enabled.</returns>
    public static OrderedIndexRange<TKey> Through(TKey upperInclusive) =>
        new(default, false, upperInclusive, true);
}
