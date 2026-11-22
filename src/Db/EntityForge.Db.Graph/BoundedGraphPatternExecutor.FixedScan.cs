using EntityForge.Db.Graph.Contracts;
using EntityForge.Query.Querying;
using EntityForge.Query.Results;
using EntityForge.Query.Values;

namespace EntityForge.Db.Graph;

internal sealed partial class BoundedGraphPatternExecutor
{
    // The scan coordinator owns global ordering, aggregation, windows and budgets.
    // This source retains only the current depth-first match, never a frontier of results.
    internal IEnumerable<GraphPatternMatch> ScanFixedMatches(BoundedGraphPatternQuery query,
        GraphQuerySnapshot snapshot, Action chargeCandidate,
        Func<Func<string, string, QueryValue>, GraphPatternMatch>? projection = null,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(chargeCandidate);
        if (query.Relationships.Count is < 1 or > 64 || query.Relationships.Any(relationship =>
            !relationship.Hops.IsFixed || relationship.MatchKind != GraphPatternMatchKind.Required) ||
            query.Aggregation is not null || query.Distinct || query.OrderBy.Count != 0)
        {
            throw new NotSupportedException("Streaming graph matching requires fixed required relationships without global row operators.");
        }
        var view = snapshot.View;
        var plan = PlanRelationships(query, snapshot.Statistics);
        var bound = new HashSet<string>(StringComparer.Ordinal) { query.Start.Binding };
        var introduces = new bool[plan.Count];
        for (int index = 0; index < plan.Count; index++)
        {
            if (!bound.Contains(plan[index].Pattern.SourceBinding))
            {
                throw new InvalidDataException("A streaming relationship plan references an unbound source.");
            }
            introduces[index] = bound.Add(plan[index].Pattern.Target.Binding);
        }
        foreach (var root in view.Vertices.Values)
        {
            cancellation.ThrowIfCancellationRequested();
            chargeCandidate();
            if (!Matches(root, query.Start))
            {
                continue;
            }
            foreach (var state in Expand(0, MatchState.Start(query.Start.Binding, root)))
            {
                cancellation.ThrowIfCancellationRequested();
                if (query.Filter is not null && !Matches(state, query.Filter))
                {
                    continue;
                }
                yield return projection is not null ? projection(state.PropertyValue) :
                    ((GraphPatternResult)MaterializeMatches(query, [state], snapshot.Version).Result).Matches[0];
            }
        }

        IEnumerable<MatchState> Expand(int depth, MatchState state)
        {
            cancellation.ThrowIfCancellationRequested();
            if (depth == plan.Count)
            {
                yield return state;
                yield break;
            }
            var relationship = plan[depth].Pattern;
            var source = state.Vertex(relationship.SourceBinding);
            var requiredTarget = introduces[depth] ? null : state.VertexOrDefault(relationship.Target.Binding);
            IEnumerable<GraphEdge> candidates = relationship.Direction switch
            {
                GraphPatternDirection.Outgoing => view.Outgoing(source.Id, relationship.Label),
                GraphPatternDirection.Incoming => view.Incoming(source.Id, relationship.Label),
                // Only self-loops occur in both lists. Avoid a degree-sized DISTINCT set.
                GraphPatternDirection.Undirected => view.Outgoing(source.Id, relationship.Label)
                    .Concat(view.Incoming(source.Id, relationship.Label).Where(edge => edge.From != source.Id)),
                _ => throw new ArgumentOutOfRangeException(nameof(relationship.Direction))
            };
            foreach (var edge in candidates)
            {
                cancellation.ThrowIfCancellationRequested();
                chargeCandidate();
                if (!MatchesEdgeProperties(edge, relationship.EdgePredicates) ||
                    !relationship.AllowRepeatedRelationships && state.Relationships.Any(prior => prior.Edges.Any(item => item.Id == edge.Id)))
                {
                    continue;
                }
                var targetId = relationship.Direction is GraphPatternDirection.Outgoing ||
                    relationship.Direction is GraphPatternDirection.Undirected && edge.From == source.Id ? edge.To : edge.From;
                if (requiredTarget is not null && targetId != requiredTarget.Id ||
                    relationship.PathMode == GraphPatternPathMode.Acyclic && targetId == source.Id)
                {
                    continue;
                }
                chargeCandidate();
                var target = view.GetVertex(targetId) ?? throw new InvalidDataException("A graph relationship references a missing endpoint.");
                if (!Matches(target, relationship.Target))
                {
                    continue;
                }
                var next = state.Append(relationship, [edge], [source, target], introduces[depth]);
                foreach (var match in Expand(depth + 1, next))
                {
                    yield return match;
                }
            }
        }
    }
}
