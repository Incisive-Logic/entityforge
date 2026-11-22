using System.Collections.ObjectModel;

namespace EntityForge.Query.Values;

/// <summary>A query map with ordinal string keys and typed query values.</summary>
public sealed record MapValue : QueryValue
{
    /// <summary>Copies map entries into an immutable, ordinal-keyed collection.</summary>
    /// <param name="values">Unique keys and non-null query values. Represent a query null using <see cref="NullValue.Instance"/> or <see cref="UnboundValue.Instance"/>.</param>
    /// <exception cref="ArgumentNullException">The entries, a key or a value are null.</exception>
    /// <exception cref="ArgumentException">A key occurs more than once.</exception>
    public MapValue(IEnumerable<KeyValuePair<string, QueryValue>> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var copy = new Dictionary<string, QueryValue>(StringComparer.Ordinal);
        foreach (var pair in values)
        {
            ArgumentNullException.ThrowIfNull(pair.Key);
            ArgumentNullException.ThrowIfNull(pair.Value);
            copy.Add(pair.Key, pair.Value);
        }
        Values = new ReadOnlyDictionary<string, QueryValue>(copy);
    }

    /// <summary>Gets the immutable map entries, compared by ordinal key.</summary>
    public IReadOnlyDictionary<string, QueryValue> Values
    {
        get;
    }
}
