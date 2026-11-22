using EntityForge.Db.Graph.Contracts;

namespace EntityForge.Db.Graph;

internal sealed partial class BoundedGraphPatternExecutor
{
    // A root starts with one immutable binding. Allocate a dictionary only when
    // expanding the match; a general scan can otherwise allocate thousands of
    // hash tables that never contain more than this one entry.
    private sealed class RootVertexBinding(string binding, GraphVertex vertex) : IReadOnlyDictionary<string, GraphVertex?>
    {
        public int Count => 1;

        public IEnumerable<string> Keys => [binding];

        public IEnumerable<GraphVertex?> Values => [vertex];

        public GraphVertex? this[string key] => ContainsKey(key) ? vertex : throw new KeyNotFoundException();

        public bool ContainsKey(string key) => StringComparer.Ordinal.Equals(key, binding);

        public bool TryGetValue(string key, out GraphVertex? value)
        {
            bool found = ContainsKey(key);
            value = found ? vertex : null;
            return found;
        }

        public IEnumerator<KeyValuePair<string, GraphVertex?>> GetEnumerator()
        {
            yield return new(binding, vertex);
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
