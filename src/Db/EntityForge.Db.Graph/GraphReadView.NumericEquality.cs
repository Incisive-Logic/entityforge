using EntityForge.Db.Graph.Contracts;

namespace EntityForge.Db.Graph;

internal sealed partial record GraphReadView
{
    private GraphQueryCache<string, NumericEqualityIndex>? _numericEqualityIndexes;

    private GraphQueryCache<string, NumericEqualityIndex> NumericEqualityIndexes =>
        LazyInitializer.EnsureInitialized(ref _numericEqualityIndexes, static () => new(index => index.Count));

    private sealed record NumericEqualityIndex(IReadOnlyDictionary<string, IReadOnlyList<GraphVertexId>> Postings, int Count);

    internal IReadOnlyList<GraphVertexId>? FindScalarNumericEquality(GraphScalarNumericEquality predicate,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Vertices.Count > GraphQueryCache<string, NumericEqualityIndex>.MaximumEntries ||
            GraphScalarEvaluator.NumericKey(predicate.Value) is not { } key)
        {
            return null;
        }
        var index = NumericEqualityIndexes.GetOrAdd(predicate.Property, property =>
        {
            var postings = new Dictionary<string, List<GraphVertexId>>(StringComparer.Ordinal);
            int count = 0;
            foreach (var vertex in Vertices.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!vertex.Properties.TryGetValue(property, out var value) ||
                    GraphScalarEvaluator.NumericKey(value) is not { } numeric)
                {
                    continue;
                }
                if (!postings.TryGetValue(numeric, out var ids))
                {
                    postings.Add(numeric, ids = []);
                }
                ids.Add(vertex.Id);
                count++;
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new(postings.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<GraphVertexId>)pair.Value.ToArray(),
                StringComparer.Ordinal), count);
        }, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        // Use the evaluator's exact rational key across integer/decimal/binary
        // floating values. Residual evaluation still rejects NaN and checks all
        // labels and predicates; this index never changes equality semantics.
        return index.Postings.GetValueOrDefault(key, []);
    }

    private void CarryNumericEqualityIndexes(GraphReadView before, IReadOnlySet<string> changed)
    {
        if (before._numericEqualityIndexes is not { } indexes)
        {
            return;
        }
        foreach (var pair in indexes.SnapshotIfAvailable())
        {
            if (!changed.Contains(pair.Key))
            {
                NumericEqualityIndexes.Store(pair.Key, pair.Value);
            }
        }
    }
}
