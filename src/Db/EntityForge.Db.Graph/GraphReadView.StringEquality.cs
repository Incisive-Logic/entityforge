using EntityForge.Db.Graph.Contracts;

namespace EntityForge.Db.Graph;

internal sealed partial record GraphReadView
{
    private GraphQueryCache<string, StringEqualityIndex>? _stringEqualityIndexes;

    private GraphQueryCache<string, StringEqualityIndex> StringEqualityIndexes =>
        LazyInitializer.EnsureInitialized(ref _stringEqualityIndexes, static () => new(index => index.Weight));

    private sealed record StringEqualityIndex(IReadOnlyDictionary<string, IReadOnlyList<GraphVertexId>>? Postings, int Weight);

    internal IReadOnlyList<GraphVertexId>? FindScalarStringEquality(GraphScalarStringEquality predicate,
        CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        const int maximum = GraphQueryCache<string, StringEqualityIndex>.MaximumEntries;
        if (Vertices.Count > maximum)
        {
            return null;
        }
        var index = StringEqualityIndexes.GetOrAdd(predicate.Property, property =>
        {
            var postings = new Dictionary<string, List<GraphVertexId>>(StringComparer.Ordinal);
            int weight = 0;
            foreach (var vertex in Vertices.Values)
            {
                cancellation.ThrowIfCancellationRequested();
                if (!vertex.Properties.TryGetValue(property, out var value))
                {
                    continue;
                }
                // Binary graph values currently project as Base64 string literals.
                // Include them so indexing preserves scalar equality's mixed-value behaviour.
                if (value is DatabaseValue.Binary large && large.Value.Length > maximum / 4 * 3)
                {
                    return new(null, 0);
                }
                string? key = value switch
                {
                    DatabaseValue.String text => text.Value,
                    DatabaseValue.Binary bytes => Convert.ToBase64String(bytes.Value.Span),
                    _ => null
                };
                if (key is null)
                {
                    continue;
                }
                bool existing = postings.TryGetValue(key, out var ids);
                long addition = 1L + (existing ? 0 : key.Length);
                if (addition > maximum - weight)
                {
                    // Cache unavailability, never an incomplete posting list.
                    return new(null, 0);
                }
                weight += (int)addition;
                if (!existing)
                {
                    postings.Add(key, ids = []);
                }
                ids!.Add(vertex.Id);
            }
            return new(postings.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<GraphVertexId>)pair.Value.ToArray(),
                StringComparer.Ordinal), weight);
        }, cancellation);
        cancellation.ThrowIfCancellationRequested();
        // Codepoint equality is exact string equality, including UTF-16 surrogate
        // sequences: no normalization, padding, culture or case folding. The full
        // predicate and labels are still checked on every selected vertex.
        return index.Postings?.GetValueOrDefault(predicate.Value, []);
    }

    private void CarryStringEqualityIndexes(GraphReadView before, IReadOnlySet<string> changed)
    {
        if (before._stringEqualityIndexes is not { } indexes)
        {
            return;
        }
        foreach (var pair in indexes.SnapshotIfAvailable())
        {
            if (!changed.Contains(pair.Key))
            {
                StringEqualityIndexes.Store(pair.Key, pair.Value);
            }
        }
    }
}
