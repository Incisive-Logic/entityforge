namespace EntityForge.Db.Distributed.Hosting;

// Only detached immutable operator values live here. A view is an identity key,
// not a snapshot lease or serving capability. Admission and final authority
// proof remain mandatory outside this cache, including on a hit.
internal sealed class PartitionSnapshotOperatorCache<TKey, TValue>(int maximumEntries, long maximumBytes)
    where TKey : notnull
{
    private readonly int _maximumEntries = maximumEntries > 0 ? maximumEntries : throw new ArgumentOutOfRangeException(nameof(maximumEntries));
    private readonly long _maximumBytes = maximumBytes > 0 ? maximumBytes : throw new ArgumentOutOfRangeException(nameof(maximumBytes));
    private readonly object _gate = new();
    private readonly Dictionary<TKey, (TValue Value, long Bytes)> _values = [];
    private readonly Queue<TKey> _insertionOrder = new();
    private WeakReference<object>? _view;
    private long _bytes;
    private long _hits;

    internal long Hits
    {
        get
        {
            lock (_gate)
            {
                return _hits;
            }
        }
    }

    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _values.Count;
            }
        }
    }

    internal long RetainedBytes
    {
        get
        {
            lock (_gate)
            {
                return _bytes;
            }
        }
    }

    private void SelectView(object view)
    {
        if (_view is not null && _view.TryGetTarget(out var prior) && ReferenceEquals(prior, view))
        {
            return;
        }

        _values.Clear();
        _insertionOrder.Clear();
        _bytes = 0;
        _view = new(view);
    }

    internal bool TryGet(object view, TKey key, out TValue value)
    {
        lock (_gate)
        {
            SelectView(view);
            if (_values.TryGetValue(key, out var entry))
            {
                _hits++;
                value = entry.Value;
                return true;
            }
            value = default!;
            return false;
        }
    }

    internal void Add(object view, TKey key, TValue value, long bytes)
    {
        if (bytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bytes));
        }

        if (bytes > _maximumBytes)
        {
            return;
        }

        lock (_gate)
        {
            SelectView(view);
            if (_values.ContainsKey(key))
            {
                return;
            }

            while (_values.Count >= _maximumEntries || bytes > _maximumBytes - _bytes)
            {
                var oldest = _insertionOrder.Dequeue();
                _bytes -= _values[oldest].Bytes;
                _values.Remove(oldest);
            }
            _values.Add(key, (value, bytes));
            _insertionOrder.Enqueue(key);
            _bytes += bytes;
        }
    }

    internal void Clear()
    {
        lock (_gate)
        {
            _values.Clear();
            _insertionOrder.Clear();
            _bytes = 0;
            _view = null;
        }
    }
}

internal sealed record PartitionOperatorInterpretation(string Language, string? Statement, string? Entity,
    bool CompleteQuery, bool GraphFragments, bool ProfileFragments, bool IncludeSupportingGraph = false, bool ContinueLocal = false);

internal sealed record PartitionOperatorKey(PartitionOperatorInterpretation Interpretation, PartitionQueryWorkKey Work)
{
    internal long InterpretationBytes => checked(2L * ((Interpretation.Language?.Length ?? 0) +
        (Interpretation.Statement?.Length ?? 0) + (Interpretation.Entity?.Length ?? 0)) + Work.IdentityBytes);
}

internal sealed record PartitionCachedOperator(PartitionQueryRelation Relation, long RelationBytes, PartitionProjectionValue Page);

internal sealed partial class PartitionOwnedNode
{
    private const int MaximumCachedOperators = 1024;
    private const long MaximumCachedOperatorBytes = 4L * 1024 * 1024;
    private readonly PartitionSnapshotOperatorCache<PartitionOperatorKey, PartitionCachedOperator> _operatorCache =
        new(MaximumCachedOperators, MaximumCachedOperatorBytes);

    private readonly AsyncLocal<object?> _operatorView = new();

    internal long CachedOperatorHits => _operatorCache.Hits;
}
