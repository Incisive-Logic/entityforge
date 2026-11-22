namespace EntityForge.Db.Kernel;

/// <summary>Owns compiled logical effects while the kernel owns durable preparation.</summary>
/// <remarks>Effects are associated with the exact durable payload and reservation. Scoped
/// effects merge only reserved keys; whole-partition effects verify their predecessor.
/// Physical after-images are never retained: allocation and page staging use the current tree.
/// Recovery reconstructs an evicted plan from the durable payload under the same reservation.</remarks>
public interface IDatabaseProfileLogicalPreparation
{
    /// <summary>Captures logical effects before native durability when the profile supports this optional preparation step.</summary>
    void CaptureLogicalEffects(Guid operation, ReadOnlyMemory<byte> payload)
    {
    }

    /// <summary>Retains the operation's logical preparation for the exact payload until publication or abandonment.</summary>
    void RetainLogicalPreparation(Guid operation, ReadOnlyMemory<byte> payload);

    /// <summary>Releases retained logical preparation for one operation.</summary>
    void ReleaseLogicalPreparation(Guid operation);

    /// <summary>Clears all retained logical preparations owned by the profile.</summary>
    void ClearLogicalPreparations();
}

/// <summary>A reusable immutable map delta; applying it cannot replace unrelated keys.</summary>
public sealed class DatabaseMapEffects<TKey, TValue> where TKey : notnull
{
    private readonly KeyValuePair<TKey, TValue>[] _set;
    private readonly TKey[] _remove;
    private readonly System.Collections.Immutable.ImmutableDictionary<TKey, TValue>? _before, _after;

    /// <summary>Computes dictionary additions, replacements and removals between the supplied before and after views.</summary>
    public DatabaseMapEffects(IReadOnlyDictionary<TKey, TValue> before, IReadOnlyDictionary<TKey, TValue> after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        _before = before as System.Collections.Immutable.ImmutableDictionary<TKey, TValue>;
        _after = after as System.Collections.Immutable.ImmutableDictionary<TKey, TValue>;
        if (ReferenceEquals(before, after))
        {
            _set = [];
            _remove = [];
            return;
        }
        _set = after.Where(pair => !before.TryGetValue(pair.Key, out var old) ||
            !EqualityComparer<TValue>.Default.Equals(old, pair.Value)).ToArray();
        _remove = before.Keys.Where(key => !after.ContainsKey(key)).ToArray();
    }

    /// <summary>Captures only keys identified by complete committed effects.</summary>
    public DatabaseMapEffects(IReadOnlyDictionary<TKey, TValue> before, IReadOnlyDictionary<TKey, TValue> after,
        IEnumerable<TKey> touched)
    {
        // Preserve the addressed-key contract even if a caller supplies an after-map
        // containing other changes. Whole-successor reuse belongs to a full-map capture.
        var set = new List<KeyValuePair<TKey, TValue>>();
        var remove = new List<TKey>();
        foreach (var key in touched.Distinct())
        {
            bool existed = before.TryGetValue(key, out var old);
            if (after.TryGetValue(key, out var value))
            {
                if (!existed || !EqualityComparer<TValue>.Default.Equals(old, value))
                {
                    set.Add(new(key, value));
                }
            }
            else if (existed)
            {
                remove.Add(key);
            }
        }
        _set = set.ToArray();
        _remove = remove.ToArray();
    }

    /// <summary>Applies the captured dictionary changes to an immutable current view.</summary>
    public System.Collections.Immutable.ImmutableDictionary<TKey, TValue> Apply(
        System.Collections.Immutable.ImmutableDictionary<TKey, TValue> current)
    {
        if (_after is not null && ReferenceEquals(current, _before))
        {
            return _after;
        }

        if (_set.Length == 0 && _remove.Length == 0)
        {
            return current;
        }

        var result = current.ToBuilder();
        foreach (var key in _remove)
        {
            result.Remove(key);
        }

        foreach (var pair in _set)
        {
            result[pair.Key] = pair.Value;
        }

        return result.ToImmutable();
    }
}

/// <summary>Captures the set membership additions and removals between two views.</summary>
public sealed class DatabaseSetEffects<T> where T : notnull
{
    private readonly T[] _added, _removed;

    /// <summary>Computes the membership additions and removals between the supplied before and after sets.</summary>
    public DatabaseSetEffects(IEnumerable<T> before, IEnumerable<T> after)
    {
        var old = before.ToHashSet();
        var next = after.ToHashSet();
        _added = next.Where(value => !old.Contains(value)).ToArray();
        _removed = old.Where(value => !next.Contains(value)).ToArray();
    }

    /// <summary>Applies the captured membership changes to an immutable current set.</summary>
    public System.Collections.Immutable.ImmutableHashSet<T> Apply(System.Collections.Immutable.ImmutableHashSet<T> current) =>
        current.Except(_removed).Union(_added);
}

/// <summary>Exact-byte-associated, evictable decoded intents. Access requires kernel admission.</summary>
public sealed class DatabaseLogicalPreparationCache<T> where T : class
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, (byte[] Payload, T Intent)> _entries = [];

    /// <summary>Compiles only on a miss, using the same owned bytes retained for identity checks.</summary>
    public void RetainCompiled(Guid operation, ReadOnlyMemory<byte> payload, Func<ReadOnlyMemory<byte>, T> compile)
    {
        ArgumentNullException.ThrowIfNull(compile);
        lock (_gate)
        {
            if (_entries.TryGetValue(operation, out var prior))
            {
                if (!payload.Span.SequenceEqual(prior.Payload))
                {
                    throw new EntityForgeDbIdempotencyException(operation);
                }

                return;
            }
            var owned = payload.ToArray();
            var intent = compile(owned);
            _entries.Add(operation, (owned, intent));
        }
    }

    /// <summary>Retains an operation's intent together with its exact encoded payload.</summary>
    public void Retain(Guid operation, ReadOnlyMemory<byte> payload, T intent)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(operation, out var prior))
            {
                if (!payload.Span.SequenceEqual(prior.Payload))
                {
                    throw new EntityForgeDbIdempotencyException(operation);
                }

                return;
            }
            _entries.Add(operation, (payload.ToArray(), intent));
        }
    }

    /// <summary>Retrieves a retained intent only when both the operation identity and payload match.</summary>
    public bool TryGet(Guid operation, ReadOnlySpan<byte> payload, out T intent)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(operation, out var entry))
            {
                if (!payload.SequenceEqual(entry.Payload))
                {
                    throw new EntityForgeDbIdempotencyException(operation);
                }

                intent = entry.Intent;
                return true;
            }
            intent = null!;
            return false;
        }
    }

    /// <summary>Removes the retained intent for one operation.</summary>
    public void Remove(Guid operation)
    {
        lock (_gate)
        {
            _entries.Remove(operation);
        }
    }

    /// <summary>Removes all retained logical intents.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
        }
    }
}
