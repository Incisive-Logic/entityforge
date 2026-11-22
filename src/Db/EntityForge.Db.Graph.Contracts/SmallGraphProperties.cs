using System.Collections;

namespace EntityForge.Db.Graph.Contracts;

// Small records are common in both native writes and projected semantic evidence.
// Own the entries, never expose their array, and retain ordinal lookup semantics.
internal sealed class SmallGraphProperties : IReadOnlyDictionary<string, DatabaseValue>, IDictionary<string, DatabaseValue>
{
    internal const int MaximumCount = 8;
    private readonly KeyValuePair<string, DatabaseValue>[] _entries;

    private SmallGraphProperties(KeyValuePair<string, DatabaseValue>[] ownedEntries) => _entries = ownedEntries;

    // Used only with validated immutable graph property collections. A null value
    // removes the key; non-null values replace or append a stored property.
    internal static IReadOnlyDictionary<string, DatabaseValue> Change(
        IReadOnlyDictionary<string, DatabaseValue> prior, string key, DatabaseValue? value)
    {
        GraphContractNames.Required(key);
        bool exists = prior.TryGetValue(key, out var previous);
        if (exists ? previous == value : value is null)
        {
            return prior;
        }
        int count = prior.Count + (value is null ? -1 : exists ? 0 : 1);
        if (count <= MaximumCount)
        {
            var entries = new KeyValuePair<string, DatabaseValue>[count];
            int index = 0;
            foreach (var entry in prior)
            {
                if (!StringComparer.Ordinal.Equals(entry.Key, key))
                {
                    entries[index++] = entry;
                }
                else if (value is not null)
                {
                    entries[index++] = new(key, value);
                }
            }
            if (!exists && value is not null)
            {
                entries[index] = new(key, value);
            }
            return new SmallGraphProperties(entries);
        }
        var properties = new Dictionary<string, DatabaseValue>(prior, StringComparer.Ordinal);
        if (value is null)
        {
            properties.Remove(key);
        }
        else
        {
            properties[key] = value;
        }
        return GraphContractNames.CopyProperties(properties);
    }

    internal SmallGraphProperties(IReadOnlyDictionary<string, DatabaseValue> source)
    {
        _entries = source.ToArray();
        for (int i = 0; i < _entries.Length; i++)
        {
            GraphContractNames.Required(_entries[i].Key);
            if (_entries[i].Value is null)
            {
                throw new ArgumentException("A graph property value cannot be null.", nameof(source));
            }
            for (int previous = 0; previous < i; previous++)
            {
                if (StringComparer.Ordinal.Equals(_entries[previous].Key, _entries[i].Key))
                {
                    throw new ArgumentException("Ordinal graph property names must be unique.", nameof(source));
                }
            }
        }
    }

    public int Count => _entries.Length;

    public IEnumerable<string> Keys => _entries.Select(entry => entry.Key);

    public IEnumerable<DatabaseValue> Values => _entries.Select(entry => entry.Value);

    ICollection<string> IDictionary<string, DatabaseValue>.Keys => Keys.ToArray();

    ICollection<DatabaseValue> IDictionary<string, DatabaseValue>.Values => Values.ToArray();

    bool ICollection<KeyValuePair<string, DatabaseValue>>.IsReadOnly => true;

    DatabaseValue IDictionary<string, DatabaseValue>.this[string key]
    {
        get => this[key];
        set => throw new NotSupportedException();
    }

    public DatabaseValue this[string key] => TryGetValue(key, out var value)
        ? value : throw new KeyNotFoundException();

    public bool ContainsKey(string key) => TryGetValue(key, out _);

    public bool TryGetValue(string key, out DatabaseValue value)
    {
        ArgumentNullException.ThrowIfNull(key);
        foreach (var entry in _entries)
        {
            if (StringComparer.Ordinal.Equals(entry.Key, key))
            {
                value = entry.Value;
                return true;
            }
        }
        value = default!;
        return false;
    }

    public IEnumerator<KeyValuePair<string, DatabaseValue>> GetEnumerator() =>
        ((IEnumerable<KeyValuePair<string, DatabaseValue>>)_entries).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    void IDictionary<string, DatabaseValue>.Add(string key, DatabaseValue value) => throw new NotSupportedException();

    bool IDictionary<string, DatabaseValue>.Remove(string key) => throw new NotSupportedException();

    void ICollection<KeyValuePair<string, DatabaseValue>>.Add(KeyValuePair<string, DatabaseValue> item) => throw new NotSupportedException();

    void ICollection<KeyValuePair<string, DatabaseValue>>.Clear() => throw new NotSupportedException();

    bool ICollection<KeyValuePair<string, DatabaseValue>>.Remove(KeyValuePair<string, DatabaseValue> item) => throw new NotSupportedException();

    bool ICollection<KeyValuePair<string, DatabaseValue>>.Contains(KeyValuePair<string, DatabaseValue> item) =>
        TryGetValue(item.Key, out var value) && EqualityComparer<DatabaseValue>.Default.Equals(value, item.Value);

    void ICollection<KeyValuePair<string, DatabaseValue>>.CopyTo(KeyValuePair<string, DatabaseValue>[] array, int arrayIndex) =>
        _entries.CopyTo(array, arrayIndex);
}
