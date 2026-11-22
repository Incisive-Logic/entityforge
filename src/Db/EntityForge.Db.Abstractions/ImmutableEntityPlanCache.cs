using System.Security.Cryptography;

namespace EntityForge.Db.Mapping;

// Bounded pure compilation cache. Callers supply complete immutable policy keys
// and retain no database, request, authority or snapshot in the compiled value.
internal sealed class ImmutableEntityPlanCache<T>
{
    internal const int MaximumEntries = 64;
    internal const int MaximumKeyBytes = 64 * 1024;
    internal const int MaximumRetainedKeyBytes = 1024 * 1024;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private int _keyBytes;

    private sealed record Entry(byte[] Key, T Plan);

    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    internal int RetainedKeyBytes
    {
        get
        {
            lock (_gate)
            {
                return _keyBytes;
            }
        }
    }

    internal T Get(byte[] key, Func<T> create)
    {
        if (key.Length > MaximumKeyBytes)
        {
            return create();
        }

        string digest = Convert.ToHexString(SHA256.HashData(key));
        lock (_gate)
        {
            if (_entries.TryGetValue(digest, out var existing) && key.AsSpan().SequenceEqual(existing.Key))
            {
                return existing.Plan;
            }
        }
        var plan = create(); // Failed/invalid definitions never enter the cache.
        lock (_gate)
        {
            if (_entries.TryGetValue(digest, out var existing))
            {
                return key.AsSpan().SequenceEqual(existing.Key) ? existing.Plan : plan; // Hash collision bypasses admission.
            }

            while (_entries.Count >= MaximumEntries || _keyBytes + key.Length > MaximumRetainedKeyBytes)
            {
                string oldest = _order.Dequeue();
                _keyBytes -= _entries[oldest].Key.Length;
                _entries.Remove(oldest);
            }
            _entries.Add(digest, new(key.ToArray(), plan));
            _order.Enqueue(digest);
            _keyBytes += key.Length;
            return plan;
        }
    }
}
