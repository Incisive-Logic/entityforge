using System.Collections.Frozen;
using System.Runtime.CompilerServices;

namespace EntityForge.Db.Graph;

internal sealed partial class GraphPropertyShard
{
    // Derived from immutable bags: no independent durability or recovery ordering.
    // Weak ownership keeps an old index alive only while its snapshot is retained.
    private static readonly ConditionalWeakTable<object, Lazy<FrozenDictionary<(bool Edge, string Property, DatabaseValue Value), Guid[]>>> Indexes = new();

    internal static Guid[] Find(State state, bool edge, string property, DatabaseValue value, int maximum)
    {
        var bags = state.Bags;
        var index = Indexes.GetValue(bags, _ => new(() => bags
            .SelectMany(bag => bag.Value.Select(p => (Key: (bag.Key.Edge, p.Key, p.Value), bag.Key.Id)))
            .GroupBy(p => p.Key).ToFrozenDictionary(g => g.Key, g => g.Select(p => p.Id).Order().ToArray()))).Value;
        if (!index.TryGetValue((edge, property, value), out var matches))
        {
            return [];
        }

        if (matches.Length > maximum)
        {
            throw new InvalidDataException("Property query exceeds its result bound.");
        }

        return matches.ToArray();
    }
}
