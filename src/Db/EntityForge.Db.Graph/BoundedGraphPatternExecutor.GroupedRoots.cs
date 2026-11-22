using System.Diagnostics;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Query.Querying;
using EntityForge.Query.Values;

namespace EntityForge.Db.Graph;

internal sealed partial class BoundedGraphPatternExecutor
{
    private bool TryExecuteGroupedRootScalars(BoundedGraphPatternQuery query, GraphReadView view,
        DatabaseVersion snapshot, IReadOnlyList<GraphVertexId> roots, TimeSpan candidateDuration,
        CancellationToken cancellationToken, out BoundedGraphPatternExecution? execution)
    {
        execution = null;
        if (query.Relationships.Count != 0 || query.Filter is not null || query.Aggregation is not { } plan ||
            plan.GroupBy is not [{ Expression: { Binding: { } binding, Property: { } property } }] ||
            binding != query.Start.Binding)
        {
            return false;
        }
        var properties = new string?[plan.Aggregates.Count];
        for (int i = 0; i < properties.Length; i++)
        {
            var aggregate = plan.Aggregates[i];
            bool countStar = aggregate.Function == GraphPatternAggregateFunction.Count &&
                aggregate.Expression is null && aggregate.ValueBinding is null && aggregate.ValueProperty is null;
            string? valueBinding = aggregate.Expression?.Binding ?? (aggregate.Expression is null ? aggregate.ValueBinding : null);
            properties[i] = aggregate.Expression?.Property ?? (aggregate.Expression is null ? aggregate.ValueProperty : null);
            if (aggregate.Distinct || !countStar &&
                (aggregate.Function is not (GraphPatternAggregateFunction.Sum or GraphPatternAggregateFunction.Average) ||
                 valueBinding != binding || properties[i] is null))
            {
                return false;
            }
        }
        // Match and check both input budgets before evaluating any aggregate,
        // matching general execution's failure precedence. Keep only vertex
        // references, not per-row match states, bindings and grouped row arrays.
        var vertices = new List<GraphVertex>(roots.Count);
        IEnumerable<GraphVertex?> candidates = view.TryDenseCandidates(roots, cancellationToken) ?? roots.Select(view.GetVertex);
        foreach (var vertex in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (vertex is not null && Matches(vertex, query.Start))
            {
                vertices.Add(vertex);
            }
        }
        EnsureWithinExpansionLimit(vertices.Count, query.MaximumExpansions);
        if (vertices.Count > plan.MaximumInputRows)
        {
            throw new GraphPatternExpansionLimitException(plan.MaximumInputRows);
        }
        long started = Stopwatch.GetTimestamp();
        var keyFor = CanonicalGroupKeySelector();
        var groups = new Dictionary<string, (QueryValue Key, StreamingAggregate[] Values)>(StringComparer.Ordinal);
        foreach (var vertex in vertices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var groupValue = vertex.Properties.GetValueOrDefault(property);
            string key = keyFor(groupValue);
            if (!groups.TryGetValue(key, out var group))
            {
                group = (groupValue is null ? UnboundValue.Instance : QueryValue(groupValue),
                    plan.Aggregates.Select(item => new StreamingAggregate(item.Function)).ToArray());
                groups.Add(key, group);
            }
            for (int i = 0; i < properties.Length; i++)
            {
                if (properties[i] is not { } valueProperty)
                {
                    group.Values[i].AddCount(1);
                    continue;
                }
                var value = vertex.Properties.GetValueOrDefault(valueProperty);
                if (value is EntityForge.Db.DatabaseValue.Int64 integer)
                {
                    group.Values[i].AddInteger(integer.Value);
                }
                else
                {
                    group.Values[i].Add(value is null ? UnboundValue.Instance : QueryValue(value));
                }
            }
        }
        bool nullEliminated = false;
        var rows = new List<(string Key, Dictionary<string, QueryValue> Values)>(groups.Count);
        foreach (var pair in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var values = new Dictionary<string, QueryValue>(StringComparer.Ordinal) { [plan.GroupBy[0].Alias] = pair.Value.Key };
            for (int i = 0; i < properties.Length; i++)
            {
                nullEliminated |= pair.Value.Values[i].NullEliminated;
                values.Add(plan.Aggregates[i].Alias, pair.Value.Values[i].Finish());
            }
            rows.Add((pair.Key, values));
        }
        execution = CompleteAggregation(plan, rows, [], nullEliminated, snapshot, roots.Count, candidateDuration,
            [], vertices.Count, vertices.Count, started, cancellationToken);
        return true;
    }
}
