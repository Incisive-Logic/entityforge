using EntityForge.Db.Graph.Contracts;
using EntityForge.Query.Querying;
using EntityForge.Query.Results;
using EntityForge.Query.Values;

namespace EntityForge.Db.Graph;

// Scalar work carries only identity and live values, never a fabricated entity.
internal sealed record GraphFragmentRow(GraphVertexId Id, IReadOnlyDictionary<string, DatabaseValue> Values)
{
    internal static readonly IReadOnlyDictionary<string, DatabaseValue> NoValues = System.Collections.Immutable.ImmutableDictionary<string, DatabaseValue>.Empty;
}

internal sealed partial class BoundedGraphPatternExecutor
{
    // Only operators whose binding multiplicity and enumeration can be reproduced
    // without rebuilding a graph enter this path. Other shapes keep the evaluator.
    internal static bool SupportsFragments(BoundedGraphPatternQuery query)
    {
        if (query.Aggregation is not null || query.Relationships.Any(relationship => relationship.AllowRepeatedRelationships || relationship.PathMode != GraphPatternPathMode.Walk || relationship.Filter is not null || relationship.EdgePredicates.Count != 0) || !FragmentFilterCovered(query, query.Filter) || query.Distinct ||
            query.OrderBy.Count != 0 || query.Relationships.Count > 0 && !SupportsFrontierExchange(query))
        {
            return false;
        }

        var nodes = query.Relationships.Select(r => r.Target.Binding).Prepend(query.Start.Binding).ToHashSet(StringComparer.Ordinal);
        return query.ReturnedBindings.All(nodes.Contains) && query.Relationships.All(r =>
            r.MatchKind == GraphPatternMatchKind.Required && r.PathSelection == GraphPatternPathSelection.All);
    }

    private static bool FragmentFilterCovered(BoundedGraphPatternQuery query, GraphPatternFilter? filter) => filter switch
    {
        null => true,
        GraphPatternBooleanFilter { Operator: GraphPatternBooleanOperator.And } and =>
            FragmentFilterCovered(query, and.Left) && FragmentFilterCovered(query, and.Right),
        GraphPatternPropertyComparison comparison => query.Relationships.Select(r => r.Target).Prepend(query.Start)
            .Any(n => n.Binding == comparison.Binding && n.Predicates.Any(p => p.Property == comparison.Property &&
                p.Operator == comparison.Operator && Equals(p.Value, comparison.Value))),
        _ => false
    };

    internal static IEnumerable<GraphVertex> FragmentVertices(GraphReadView view, BoundedGraphPatternQuery query,
        int stage, IReadOnlyList<Guid>? identities)
    {
        if (!SupportsFragments(query) || stage < 0 || stage > query.Relationships.Count)
        {
            throw new ArgumentException("Unsupported graph fragment.");
        }

        var pattern = stage == 0 ? query.Start : query.Relationships[stage - 1].Target;
        var selected = stage == 0 ? SnapshotCandidates(view, CandidateQuery(query)) :
            (identities ?? throw new ArgumentException("Missing fragment input.")).Select(id => new GraphVertexId(id)).ToArray();
        return selected.Select(view.GetVertex).OfType<GraphVertex>().Where(v => Matches(v, pattern));
    }

    internal static (string Property, DatabaseValue Value)? FragmentRootEquality(BoundedGraphPatternQuery query)
    {
        var candidate = CandidateQuery(query);
        return candidate.Property is { } property && candidate.Value is { } value ? (property, value) : null;
    }

    internal static bool FragmentMatches(GraphVertex vertex, BoundedGraphPatternQuery query, int stage, bool propertiesOnly = false)
    {
        var pattern = stage == 0 ? query.Start : query.Relationships[stage - 1].Target;
        return Matches(vertex, propertiesOnly ? new GraphPatternNode(pattern.Binding, predicates: pattern.Predicates) : pattern);
    }

    internal static bool FragmentMatches(IReadOnlySet<string> labels, IReadOnlyDictionary<string, DatabaseValue> properties,
        BoundedGraphPatternQuery query, int stage, bool propertiesOnly = false)
    {
        var pattern = stage == 0 ? query.Start : query.Relationships[stage - 1].Target;
        return Matches(labels, properties, propertiesOnly ? new GraphPatternNode(pattern.Binding, predicates: pattern.Predicates) : pattern);
    }

    internal static bool FragmentMaterializesVertex(BoundedGraphPatternQuery query, int stage) =>
        query.ReturnedBindings.Contains(stage == 0 ? query.Start.Binding : query.Relationships[stage - 1].Target.Binding);


    internal sealed class FragmentBindings
    {
        private readonly BoundedGraphPatternQuery _query;
        private List<Row> _rows;
        private int _stage;
        private int _expansions;

        // An occurrence owns only its new step. Siblings share their immutable
        // prefix without copying every earlier vertex and edge on expansion.
        private sealed record Row(Row? Parent, GraphVertexId Id, IReadOnlyDictionary<string, DatabaseValue> Values, GraphVertex? Vertex, GraphEdge? Edge, int Depth, GraphEdgeId? EdgeId = null)
        {
            internal bool ContainsEdge(GraphEdgeId id)
            {
                for (Row? row = this; row is not null; row = row.Parent)
                {
                    if (row.EdgeId == id)
                    {
                        return true;
                    }
                }
                return false;
            }

            internal Row ValuesAt(int depth)
            {
                var row = this;
                while (row.Depth > depth)
                {
                    row = row.Parent!;
                }

                return row;
            }

            internal MatchState Materialize(BoundedGraphPatternQuery query)
            {
                var vertices = new GraphVertex[Depth + 1];
                var edges = new GraphEdge[Depth];
                for (Row? row = this; row is not null; row = row.Parent)
                {
                    vertices[row.Depth] = row.Vertex ?? throw new InvalidOperationException("Scalar rows cannot materialize an entity graph.");
                    if (row.Depth > 0)
                    {
                        edges[row.Depth - 1] = row.Edge!;
                    }
                }
                var bindings = new Dictionary<string, GraphVertex?>(StringComparer.Ordinal)
                {
                    [query.Start.Binding] = vertices[0]
                };
                var relationships = new MatchedRelationship[Depth];
                for (int index = 0; index < Depth; index++)
                {
                    var pattern = query.Relationships[index];
                    bindings.Add(pattern.Target.Binding, vertices[index + 1]);
                    relationships[index] = new(pattern, [edges[index]], [vertices[index], vertices[index + 1]]);
                }
                return new(bindings, relationships);
            }
        }

        internal FragmentBindings(BoundedGraphPatternQuery query, IReadOnlyList<GraphVertex> roots)
        {
            if (!SupportsFragments(query))
            {
                throw new ArgumentException("Unsupported fragment plan.");
            }

            _query = query;
            EnsureWithinExpansionLimit(roots.Count, query.MaximumExpansions);
            _expansions = roots.Count;
            _rows = roots.Select(v => new Row(null, v.Id, v.Properties, v, null, 0)).ToList();
        }

        internal FragmentBindings(BoundedGraphPatternQuery query, IReadOnlyList<GraphFragmentRow> roots)
        {
            if (!SupportsFragments(query))
            {
                throw new ArgumentException("Unsupported fragment plan.");
            }

            _query = query;
            EnsureWithinExpansionLimit(roots.Count, query.MaximumExpansions);
            _expansions = roots.Count;
            _rows = roots.Select(v => new Row(null, v.Id, v.Values, null, null, 0)).ToList();
        }

        internal void Advance(IReadOnlyList<GraphEdge> edges, IReadOnlyDictionary<GraphVertexId, GraphVertex> targets,
            CancellationToken token) => AdvanceCore(edges, targets, static v => v.Properties, static v => v, token);

        internal void Advance(IReadOnlyList<GraphEdge> edges, IReadOnlyDictionary<GraphVertexId, GraphFragmentRow> targets,
            CancellationToken token) => AdvanceCore(edges, targets, static v => v.Values, static _ => null, token);

        private void AdvanceCore<T>(IReadOnlyList<GraphEdge> edges, IReadOnlyDictionary<GraphVertexId, T> targets,
            Func<T, IReadOnlyDictionary<string, DatabaseValue>> values, Func<T, GraphVertex?> entity, CancellationToken token)
        {
            if (_stage >= _query.Relationships.Count)
            {
                throw new InvalidOperationException("Fragment already complete.");
            }

            var relationship = _query.Relationships[_stage];
            var adjacency = edges.ToLookup(e => relationship.Direction == GraphPatternDirection.Outgoing ? e.From : e.To);
            var next = new List<Row>();
            foreach (var row in _rows)
            {
                token.ThrowIfCancellationRequested();
                foreach (var edge in adjacency[row.Id])
                {
                    EnsureWithinExpansionLimit(++_expansions, _query.MaximumExpansions);
                    if (row.ContainsEdge(edge.Id))
                    {
                        continue;
                    }
                    var id = relationship.Direction == GraphPatternDirection.Outgoing ? edge.To : edge.From;
                    if (targets.TryGetValue(id, out var target))
                    {
                        var vertex = entity(target);
                        next.Add(new(row, id, values(target), vertex, vertex is null ? null : edge, row.Depth + 1, edge.Id));
                    }
                }
            }
            _rows = next;
            _stage++;
        }

        internal QueryResultEnvelope Complete(CancellationToken token,
            Func<Func<string, string, QueryValue>, GraphPatternMatch>? projection = null, bool preserveGraph = false)
        {
            if (_stage != _query.Relationships.Count && _rows.Count != 0)
            {
                throw new InvalidOperationException("Fragment dependencies are incomplete.");
            }

            if (projection is not null && !preserveGraph)
            {
                var bindings = _query.Relationships.Select(r => r.Target.Binding).Prepend(_query.Start.Binding).ToArray();
                var matches = new List<GraphPatternMatch>();
                foreach (var row in _rows.Skip(_query.Offset).Take(_query.MaximumMatches))
                {
                    token.ThrowIfCancellationRequested();
                    matches.Add(projection((binding, property) =>
                    {
                        int index = Array.IndexOf(bindings, binding);
                        return index >= 0 && index <= row.Depth &&
                            row.ValuesAt(index).Values.TryGetValue(property, out var value)
                            ? QueryValue(value) : UnboundValue.Instance;
                    }));
                }
                // Scalar RETURN owns values, not entity graphs or fabricated evidence.
                // Authoritative traversal and completion obligations remain upstream.
                return new(Guid.NewGuid(), new GraphPatternResult(new GraphResult([], []), matches));
            }
            var selected = new List<MatchState>();
            foreach (var row in _rows.Skip(_query.Offset).Take(_query.MaximumMatches))
            {
                token.ThrowIfCancellationRequested();
                selected.Add(row.Materialize(_query));
            }
            // Uses the same materialiser as embedded evaluation, without a read
            // view, statistics construction, candidate scan or second traversal.
            return new BoundedGraphPatternExecutor(null).MaterializeMatches(_query, selected, default, projection);
        }
    }
}
