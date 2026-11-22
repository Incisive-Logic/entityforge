namespace EntityForge.Db.Semantic;

internal sealed class SemanticAtomCache
{
    // Tier M contains 50,000 entity identities and 50,000 provenance atoms. Keep both shared
    // sets resident so repeated field batches do not continually evict and rehydrate each other.
    private const int DefaultCapacity = 131_072;
    private readonly object _gate = new();
    private readonly int _capacity;
    private long _payloadBytes;

    internal (int Count, long PayloadBytes) Observe()
    {
        lock (_gate)
        {
            return (_entries.Count, _payloadBytes);
        }
    }

    private readonly Dictionary<SemanticAtomId, CacheEntry> _entries = [];
    private readonly LinkedList<SemanticAtomId> _recency = [];

    public SemanticAtomCache(int capacity = DefaultCapacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
    }

    public bool TryGet(SemanticAtomId id, out SemanticAtomRecord? atom)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out CacheEntry? entry))
            {
                atom = null;
                return false;
            }
            _recency.Remove(entry.Recency);
            _recency.AddLast(entry.Recency);
            atom = entry.Atom;
            return true;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _recency.Clear();
            _payloadBytes = 0;
        }
    }

    public void Add(SemanticAtomRecord atom)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(atom.Id, out CacheEntry? existing))
            {
                _recency.Remove(existing.Recency);
                _recency.AddLast(existing.Recency);
                return;
            }
            while (_entries.Count >= _capacity)
            {
                LinkedListNode<SemanticAtomId> oldest = _recency.First!;
                _recency.RemoveFirst();
                var removed = _entries[oldest.Value].Atom;
                _payloadBytes -= (long)removed.CanonicalBytes.Length + removed.FullHash.Length;
                _entries.Remove(oldest.Value);
            }
            LinkedListNode<SemanticAtomId> recency = _recency.AddLast(atom.Id);
            _entries.Add(atom.Id, new(atom, recency));
            _payloadBytes += (long)atom.CanonicalBytes.Length + atom.FullHash.Length;
        }
    }

    private sealed record CacheEntry(SemanticAtomRecord Atom, LinkedListNode<SemanticAtomId> Recency);
}
