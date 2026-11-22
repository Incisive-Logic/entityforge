using System.Collections;
using System.Collections.Immutable;

namespace EntityForge.Db.Semantic.Engine;

// Timestamp versions share immutable assertion roots. Enumeration is the query interface;
// the list indexer is retained for the public state contract, not used for lookup.
internal sealed class SemanticAssertionCollection<T>(ImmutableDictionary<Guid, T> items) : IReadOnlyList<T>
{
    internal ImmutableDictionary<Guid, T> Items { get; } = items;

    private readonly Lazy<T[]> _indexed = new(() => items.Values.ToArray());

    public int Count => Items.Count;

    public T this[int index] => _indexed.Value[index];

    public IEnumerator<T> GetEnumerator() => Items.Values.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    internal static ImmutableDictionary<Guid, T> Index(IReadOnlyList<T> values, Func<T, Guid> identity) =>
        values is SemanticAssertionCollection<T> indexed ? indexed.Items : values.ToImmutableDictionary(identity);
}
