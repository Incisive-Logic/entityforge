namespace EntityForge.Db.Graph;

// Values belong to one immutable read view. Limit both keys and retained result entries;
// a small key limit alone does not bound arrays describing a large graph.
internal sealed class GraphQueryCache<TKey, TValue>(Func<TValue, int> weight) where TKey : notnull
{
    internal const int MaximumKeys = 32;
    internal const int MaximumEntries = 1_000_000;
    private readonly object _gate = new();
    private readonly Dictionary<TKey, TValue> _values = [];
    private int _entries;

    public TValue GetOrAdd(TKey key, Func<TKey, TValue> create, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        while (!Monitor.TryEnter(_gate, 25))
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_values.TryGetValue(key, out TValue? value))
            {
                return value;
            }
            // Build under the lock so concurrent cold queries do not allocate duplicate indexes.
            value = create(key);
            Store(key, value);
            return value;
        }
        finally
        {
            Monitor.Exit(_gate);
        }
    }

    public void Store(TKey key, TValue value)
    {
        lock (_gate)
        {
            if (_values.ContainsKey(key))
            {
                return;
            }

            int size = weight(value);
            if (size > MaximumEntries)
            {
                return;
            }

            if (_values.Count >= MaximumKeys || size > MaximumEntries - _entries)
            {
                _values.Clear();
                _entries = 0;
            }
            _values.Add(key, value);
            _entries += size;
        }
    }

    internal KeyValuePair<TKey, TValue>[] Snapshot()
    {
        lock (_gate)
        {
            return _values.ToArray();
        }
    }

    // A writer may carry completed immutable indexes into a new view, but must
    // not wait for an unrelated cold query to finish constructing an index.
    internal KeyValuePair<TKey, TValue>[] SnapshotIfAvailable()
    {
        if (!Monitor.TryEnter(_gate))
        {
            return [];
        }
        try
        {
            return _values.ToArray();
        }
        finally
        {
            Monitor.Exit(_gate);
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

    internal int RetainedEntries
    {
        get
        {
            lock (_gate)
            {
                return _entries;
            }
        }
    }
}
