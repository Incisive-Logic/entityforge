using System.Globalization;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Query.Values;

namespace EntityForge.Db.Graph;

internal sealed partial record GraphReadView
{
    private GraphQueryCache<string, ScalarRangeIndex>? _scalarRangeIndexes;

    private GraphQueryCache<string, ScalarRangeIndex> ScalarRangeIndexes =>
        LazyInitializer.EnsureInitialized(ref _scalarRangeIndexes, static () => new(index => index.Entries?.Length ?? 0));

    private sealed record ScalarRangeEntry(LiteralValue Value, GraphVertexId Vertex);

    private sealed record ScalarRangeIndex(ScalarRangeEntry[]? Entries);

    internal IReadOnlyList<GraphVertexId>? FindScalarNumericRange(IReadOnlyList<GraphScalarNumericBound> bounds,
        CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (bounds.Count == 0 || Vertices.Count > GraphQueryCache<string, ScalarRangeIndex>.MaximumEntries ||
            bounds.Any(b => b.Property != bounds[0].Property || b.Operator is not ("<" or "<=" or ">" or ">=") ||
                !GraphScalarEvaluator.FloatingNumber(b.Value, out double number) || Double.IsNaN(number)))
        {
            return null;
        }
        var index = ScalarRangeIndexes.GetOrAdd(bounds[0].Property, property =>
        {
            var entries = new List<ScalarRangeEntry>();
            foreach (var vertex in Vertices.Values)
            {
                cancellation.ThrowIfCancellationRequested();
                if (!vertex.Properties.TryGetValue(property, out var value))
                {
                    continue;
                }
                LiteralValue? numeric = value switch
                {
                    DatabaseValue.Int64 v => new(v.Value.ToString(CultureInfo.InvariantCulture), QueryScalarKind.Integer),
                    DatabaseValue.Decimal v => new(v.Value.ToString(CultureInfo.InvariantCulture), QueryScalarKind.Decimal),
                    DatabaseValue.Double v => GraphScalarEvaluator.FloatingValue(v.Value),
                    _ => null
                };
                // Incompatible ordering operands may raise an error. Do not silently
                // prune them; leave that entire column to normal residual execution.
                if (numeric is null)
                {
                    return new ScalarRangeIndex(null);
                }
                if (!Double.IsNaN(value is DatabaseValue.Double d ? d.Value : 0))
                {
                    entries.Add(new(numeric, vertex.Id));
                }
            }
            entries.Sort(static (a, b) => GraphScalarEvaluator.CompareForOrder(a.Value, b.Value));
            cancellation.ThrowIfCancellationRequested();
            return new ScalarRangeIndex(entries.ToArray());
        }, cancellation);
        if (index.Entries is not { } sorted)
        {
            return null;
        }
        int first = 0, last = sorted.Length;
        foreach (var bound in bounds)
        {
            cancellation.ThrowIfCancellationRequested();
            int low = 0, high = sorted.Length;
            bool upper = bound.Operator is ">" or "<=";
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                int compared = GraphScalarEvaluator.CompareForOrder(sorted[middle].Value, bound.Value);
                if (compared < 0 || upper && compared == 0)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }
            if (bound.Operator is ">" or ">=")
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
            cancellation.ThrowIfCancellationRequested();
            result[i] = sorted[first + i].Vertex;
        }
        return result;
    }

    private void CarryScalarNumericRangeIndexes(GraphReadView before, IReadOnlySet<string> changed)
    {
        if (before._scalarRangeIndexes is not { } indexes)
        {
            return;
        }
        foreach (var pair in indexes.SnapshotIfAvailable())
        {
            if (!changed.Contains(pair.Key))
            {
                ScalarRangeIndexes.Store(pair.Key, pair.Value);
            }
        }
    }
}
