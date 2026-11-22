using EntityForge.Db.Graph.Contracts;
using EntityForge.Query.Querying;

namespace EntityForge.Db.Graph;

internal sealed record GraphNumericBound(string Property, DatabaseValue Value, GraphPatternComparisonOperator Operator);

internal sealed partial record GraphReadView
{
    private readonly object _numericGate = new();
    private readonly Dictionary<(string Property, Type Type), NumericEntry[]> _numericIndexes = new();
    private int _numericEntries;

    internal int NumericIndexBuilds
    {
        get; private set;
    }

    private sealed record NumericEntry(DatabaseValue Value, GraphVertexId Vertex);

    private void CarryNumericIndexes(GraphReadView before, IReadOnlySet<string> changed)
    {
        lock (before._numericGate)
        {
            foreach (var pair in before._numericIndexes)
            {
                if (!changed.Contains(pair.Key.Property))
                {
                    _numericIndexes.Add(pair.Key, pair.Value);
                    _numericEntries += pair.Value.Length;
                }
            }
        }
    }

    // Graph comparisons require identical scalar types. RDF and Semantic retain
    // their own coercion/evidence semantics; all three seek immutable columns and
    // still evaluate the complete residual predicate against the selected view.
    internal IReadOnlyList<GraphVertexId>? FindNumericCandidates(
        IReadOnlyList<GraphNumericBound> bounds, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (bounds.Count == 0)
        {
            return null;
        }

        var key = (bounds[0].Property, bounds[0].Value.GetType());
        if (bounds.Any(b => b.Property != key.Property || b.Value.GetType() != key.Item2 ||
            b.Value is not (DatabaseValue.Int64 or DatabaseValue.Double)))
        {
            return null;
        }

        NumericEntry[]? entries;
        lock (_numericGate)
        {
            if (!_numericIndexes.TryGetValue(key, out entries) &&
                (_numericIndexes.Count >= MaximumPropertyPostingIndexes ||
                 Vertices.Count > MaximumPropertyPostingEntries - _numericEntries))
            {
                return null;
            }
        }
        if (entries is null)
        {
            // Build outside the publication lock. A concurrent new view
            // skips an unfinished index rather than stalling a write for it.
            var selected = new List<NumericEntry>();
            foreach (var vertex in Vertices.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (vertex.Properties.TryGetValue(key.Property, out var value) && value.GetType() == key.Item2)
                {
                    selected.Add(new(value, vertex.Id));
                }
            }
            selected.Sort(static (a, b) => GraphDatabaseValueComparer.Instance.Compare(a.Value, b.Value));
            cancellationToken.ThrowIfCancellationRequested();
            entries = selected.ToArray();
            lock (_numericGate)
            {
                if (_numericIndexes.TryGetValue(key, out var winner))
                {
                    entries = winner;
                }
                else if (_numericIndexes.Count < MaximumPropertyPostingIndexes &&
                    entries.Length <= MaximumPropertyPostingEntries - _numericEntries)
                {
                    _numericIndexes.Add(key, entries);
                    _numericEntries += entries.Length;
                    NumericIndexBuilds++;
                }
            }
        }
        int first = 0, last = entries.Length;
        foreach (var bound in bounds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool lower = bound.Operator is GraphPatternComparisonOperator.GreaterThan or GraphPatternComparisonOperator.GreaterThanOrEqual;
            bool afterEqual = bound.Operator is GraphPatternComparisonOperator.GreaterThan or GraphPatternComparisonOperator.LessThanOrEqual;
            int low = 0, high = entries.Length;
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                int comparison = GraphDatabaseValueComparer.Instance.Compare(entries[middle].Value, bound.Value);
                if (comparison < 0 || afterEqual && comparison == 0)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }
            if (lower)
            {
                first = Math.Max(first, low);
            }
            else
            {
                last = Math.Min(last, low);
            }
        }
        var result = new GraphVertexId[Math.Max(0, last - first)];
        for (int i = 0; i < result.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result[i] = entries[first + i].Vertex;
        }
        return result;
    }
}
