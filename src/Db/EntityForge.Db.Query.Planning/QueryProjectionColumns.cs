using System.Collections.Frozen;

namespace EntityForge.Db.Query.Planning;

/// <summary>Immutable live-column interpretation for one query operator boundary.</summary>
/// <remarks>Columns include downstream evaluation dependencies, not just public output fields.
/// An unrestricted layout is explicit; an empty layout retains row occurrences with no values.</remarks>
public sealed class QueryProjectionColumns<T> where T : notnull
{
    private readonly FrozenSet<T>? _columns;

    /// <summary>Creates a bounded live-column layout; null means every column is required.</summary>
    public QueryProjectionColumns(IEnumerable<T>? columns, IEqualityComparer<T>? comparer = null) =>
        _columns = columns?.ToFrozenSet(comparer);

    /// <summary>Whether a column is needed after this operator has completed.</summary>
    public bool Contains(T column) => _columns is null || _columns.Contains(column);

    /// <summary>Whether the boundary requires every source column.</summary>
    public bool IsUnrestricted => _columns is null;

    /// <summary>Combines dependencies when an entity is reached through multiple projection paths.</summary>
    public QueryProjectionColumns<T> Union(QueryProjectionColumns<T> other) =>
        ReferenceEquals(this, other) || IsUnrestricted ? this : other.IsUnrestricted ? other :
        new(_columns!.Union(other._columns!), _columns!.Comparer);

    /// <summary>Projects an owned row without changing its occurrence or mutating its source.</summary>
    public Dictionary<T, TValue> Project<TValue>(IReadOnlyDictionary<T, TValue> values) =>
        values.Where(pair => Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value, _columns?.Comparer);
}
