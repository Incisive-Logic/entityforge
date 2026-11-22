using System.Globalization;

namespace EntityForge.Db.Gql;

internal static partial class GraphGqlStandardSyntax
{
    private static readonly GqlPreparedSourceCache PreparedSources = new();
}

// Only validated, parameter-independent text and completion metadata are retained.
// No parse trees, request objects, bound parameter values, plans or results are cached.
internal sealed class GqlPreparedSourceCache
{
    internal const int MaximumEntries = 128;
    internal const int MaximumCharacters = 1_048_576;
    private readonly object _gate = new();
    private readonly Dictionary<(string Source, string Culture), Prepared> _entries = [];
    private readonly Queue<(string Source, string Culture)> _order = [];
    private int _characters;

    internal sealed record Prepared(string Source, bool OmitsResult);

    internal bool TryGet(string source, out Prepared prepared)
    {
        lock (_gate)
        {
            return _entries.TryGetValue((source, CultureInfo.CurrentCulture.Name), out prepared!);
        }
    }

    internal void Store(string source, Prepared prepared)
    {
        int weight = source.Length + prepared.Source.Length;
        if (weight > MaximumCharacters)
        {
            return;
        }
        var key = (source, CultureInfo.CurrentCulture.Name);
        lock (_gate)
        {
            if (_entries.ContainsKey(key))
            {
                return;
            }
            while (_entries.Count >= MaximumEntries || _characters + weight > MaximumCharacters)
            {
                var removed = _order.Dequeue();
                _characters -= removed.Source.Length + _entries[removed].Source.Length;
                _entries.Remove(removed);
            }
            _entries.Add(key, prepared);
            _order.Enqueue(key);
            _characters += weight;
        }
    }
}
