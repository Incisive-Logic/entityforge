using System.Collections.Immutable;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Rdf;

internal sealed partial class RdfProfileRuntime
{
    private sealed partial record RdfReadView
    {
        // Same union as the full-relation fingerprint: all candidate-subject
        // quads (including named graphs), plus newly matching default anchors.
        // Existing immutable indexes avoid visiting unrelated subjects.
        internal IEnumerable<RdfQuad> QueryEvidence(RdfQueryStateRead query)
        {
            var seen = new HashSet<RdfQuad>();
            foreach (var subject in query.Subjects)
            {
                if (BySubject.TryGetValue(subject, out var quads))
                {
                    foreach (var quad in quads)
                    {
                        if (seen.Add(quad))
                        {
                            yield return quad;
                        }
                    }
                }
            }

            var anchor = new RdfQuadPattern(Predicate: query.Predicate, Object: query.Object, MatchDefaultGraphOnly: true);
            foreach (var quad in Candidates(anchor))
            {
                if (Matches(quad, anchor) && seen.Add(quad))
                {
                    yield return quad;
                }
            }
        }

        // Builders own only the changed paths. No mutable collection from an earlier view is reused.
        public RdfReadView Apply(IEnumerable<RdfMutation> mutations)
        {
            var quads = Quads.ToBuilder();
            var subjects = new IndexChanges<RdfTerm>(BySubject);
            var predicates = new IndexChanges<RdfIri>(ByPredicate);
            var objects = new IndexChanges<RdfTerm>(ByObject);
            var graphs = new IndexChanges<RdfTerm>(ByGraph);
            // DefaultGraph is a subset of Quads. Equal counts therefore mean
            // both indexes represent the same immutable set. Build it once;
            // a named insertion below separates the two before mutation.
            ImmutableHashSet<RdfQuad>.Builder? defaultGraph = Quads.Count == DefaultGraph.Count
                ? null : DefaultGraph.ToBuilder();
            // Only completed immutable indexes may cross a version boundary. A build
            // still running belongs to the old view and is never forced by a writer.
            var partitions = _predicatePartitions.Where(p => p.Value.IsValueCreated && p.Value.Value.Available)
                .ToDictionary(p => p.Key, p => p.Value.Value);
            var partitionChanges = new Dictionary<RdfIri, (IndexChanges<RdfTerm> Subjects, IndexChanges<RdfTerm> Objects)>();
            bool changed = false;
            foreach (RdfMutation mutation in mutations)
            {
                bool add = mutation switch
                {
                    AssertRdfQuad => true,
                    RetractRdfQuad => false,
                    _ => throw new InvalidDataException($"Unsupported RDF mutation {mutation.GetType().Name}.")
                };
                RdfQuad quad = mutation.Quad;
                if (add)
                {
                    // Reuse equal immutable terms already owned by these indexes.
                    // This has no global intern table: term lifetime remains tied
                    // to live read views and the current mutation batch.
                    var subject = subjects.CanonicalKey(quad.Subject);
                    var predicate = predicates.CanonicalKey(quad.Predicate);
                    var value = objects.CanonicalKey(quad.Object);
                    var graphName = quad.Graph is null ? null : graphs.CanonicalKey(quad.Graph);
                    if (!ReferenceEquals(subject, quad.Subject) || !ReferenceEquals(predicate, quad.Predicate) ||
                        !ReferenceEquals(value, quad.Object) || !ReferenceEquals(graphName, quad.Graph))
                    {
                        quad = new(subject, predicate, value, graphName);
                    }
                }
                if (add && quad.Graph is not null && defaultGraph is null)
                {
                    defaultGraph = quads.ToImmutable().ToBuilder();
                }
                if (!(add ? quads.Add(quad) : quads.Remove(quad)))
                {
                    continue;
                }

                changed = true;
                if (partitions.TryGetValue(quad.Predicate, out var partition))
                {
                    if (!partitionChanges.TryGetValue(quad.Predicate, out var indexes))
                    {
                        indexes = (new(partition.BySubject), new(partition.ByObject));
                        partitionChanges.Add(quad.Predicate, indexes);
                    }
                    indexes.Subjects.Apply(quad.Subject, quad, add);
                    indexes.Objects.Apply(quad.Object, quad, add);
                }
                subjects.Apply(quad.Subject, quad, add);
                predicates.Apply(quad.Predicate, quad, add);
                objects.Apply(quad.Object, quad, add);
                if (quad.Graph is { } graph)
                {
                    graphs.Apply(graph, quad, add);
                }
                else if (defaultGraph is not null && add)
                {
                    defaultGraph.Add(quad);
                }
                else if (defaultGraph is not null)
                {
                    defaultGraph.Remove(quad);
                }
            }
            if (!changed)
            {
                return this;
            }

            var nextQuads = quads.ToImmutable();
            var nextDefaultGraph = defaultGraph is null || defaultGraph.Count == nextQuads.Count
                ? nextQuads : defaultGraph.ToImmutable();
            var next = new RdfReadView(nextQuads, subjects.Finish(), predicates.Finish(),
                objects.Finish(), graphs.Finish(), nextDefaultGraph);
            foreach (var (predicate, partition) in partitions)
            {
                int entries = checked(Get(next.ByPredicate, predicate).Count * 2);
                if (entries == 0 || entries > MaximumPredicatePartitionEntries - next._predicatePartitionEntries)
                {
                    continue;
                }

                var updated = partitionChanges.TryGetValue(predicate, out var indexes)
                    ? new RdfPredicatePartitions(indexes.Subjects.Finish(), indexes.Objects.Finish(), true)
                    : partition;
                var retained = new Lazy<RdfPredicatePartitions>(() => updated);
                _ = retained.Value;
                next._predicatePartitions[predicate] = retained;
                next._predicatePartitionEntries += entries;
            }
            return next;
        }

        private sealed class IndexChanges<TKey>(ImmutableDictionary<TKey, ImmutableHashSet<RdfQuad>> source)
            where TKey : notnull
        {
            private readonly Dictionary<TKey, (TKey Key, ImmutableHashSet<RdfQuad>.Builder Values)> _changed = [];

            public TKey CanonicalKey(TKey key) => source.TryGetKey(key, out var existing)
                ? existing : _changed.TryGetValue(key, out var changed) ? changed.Key : key;

            public void Apply(TKey key, RdfQuad quad, bool add)
            {
                if (!_changed.TryGetValue(key, out var changed))
                {
                    changed = (key, source.GetValueOrDefault(key, ImmutableHashSet<RdfQuad>.Empty).ToBuilder());
                    _changed.Add(key, changed);
                }
                var values = changed.Values;
                if (add)
                {
                    values.Add(quad);
                }
                else
                {
                    values.Remove(quad);
                }
            }

            public ImmutableDictionary<TKey, ImmutableHashSet<RdfQuad>> Finish()
            {
                if (_changed.Count == 0)
                {
                    return source;
                }

                var result = source.ToBuilder();
                foreach (var (key, changed) in _changed)
                {
                    var values = changed.Values;
                    if (values.Count == 0)
                    {
                        result.Remove(key);
                    }
                    else
                    {
                        result[key] = values.ToImmutable();
                    }
                }

                return result.ToImmutable();
            }
        }
    }
}
