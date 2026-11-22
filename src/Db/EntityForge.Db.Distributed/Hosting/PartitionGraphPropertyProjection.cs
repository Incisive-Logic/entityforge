using EntityForge.Db.Gql;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Query.Querying;
using EntityForge.Db.Query.Planning;
using System.Runtime.CompilerServices;

namespace EntityForge.Db.Distributed.Hosting;

internal sealed partial class PartitionOwnedNode
{
    private sealed record GraphProjectionLayout(QueryProjectionColumns<string>[] Output, QueryProjectionColumns<string>[] Checked,
        QueryProjectionColumns<string> Evaluation);

    // Query interpretation owns these plans; neither a layout nor this weak
    // association retains a snapshot, participant job or historical generation.
    private static readonly ConditionalWeakTable<BoundedGraphPatternQuery, ConditionalWeakTable<GqlPropertyProjection, GraphProjectionLayout>> GraphProjectionLayouts = new();

    private static GraphProjectionLayout GetGraphProjectionLayout(BoundedGraphPatternQuery query, GqlPropertyProjection projection)
    {
        var layouts = GraphProjectionLayouts.GetValue(query, static _ => new());
        if (!layouts.TryGetValue(projection, out var layout))
        {
            var output = Enumerable.Range(0, query.Relationships.Count + 1).Select(i => BuildGraphProjectionColumns(query, i, projection, false)).ToArray();
            var checkedColumns = Enumerable.Range(0, query.Relationships.Count + 1).Select(i => BuildGraphProjectionColumns(query, i, projection, true)).ToArray();
            var required = output.SelectMany((_, stage) => (stage == 0 ? query.Start : query.Relationships[stage - 1].Target).Predicates.Select(p => p.Property))
                .Concat(projection.Terms.SelectMany(t => t.Expression.Leaves).Select(t => t.Property).OfType<string>())
                .Concat(query.OrderBy.SelectMany(term => term.Expression is { } expression
                    ? expression.Leaves.Select(leaf => leaf.Property).OfType<string>() : [term.Property])).ToHashSet(StringComparer.Ordinal);
            void Filter(GraphPatternFilter? filter)
            {
                switch (filter)
                {
                    case GraphPatternExpressionFilter expression:
                        required.UnionWith(expression.Expression.Leaves.Select(leaf => leaf.Property).OfType<string>());
                        break;
                    case GraphPatternNullTest test:
                        if (test.Property is not null)
                        {
                            required.Add(test.Property);
                        }
                        break;
                    case GraphPatternPropertyComparison comparison:
                        required.Add(comparison.Property);
                        break;
                    case GraphPatternBooleanFilter boolean:
                        Filter(boolean.Left);
                        Filter(boolean.Right);
                        break;
                    case GraphPatternNotFilter not:
                        Filter(not.Operand);
                        break;
                    case null:
                        break;
                    default:
                        throw new NotSupportedException("Unknown graph evaluation dependency.");
                }
            }
            Filter(query.Filter);
            foreach (var relationship in query.Relationships)
            {
                Filter(relationship.Filter);
                required.UnionWith(relationship.EdgePredicates.Select(predicate => predicate.Property));
            }
            if (query.Aggregation is { } aggregation)
            {
                required.UnionWith(aggregation.Aggregates.Select(value => value.ValueProperty).OfType<string>());
                foreach (var expression in aggregation.Aggregates.Select(value => value.Expression)
                    .Concat(aggregation.GroupBy.Select(key => key.Expression)).OfType<GraphScalarExpression>())
                {
                    required.UnionWith(expression.Leaves.Select(leaf => leaf.Property).OfType<string>());
                }
            }

            var built = new GraphProjectionLayout(output, checkedColumns, new(required, StringComparer.Ordinal));
            layout = layouts.GetValue(projection, _ => built);
        }
        return layout;
    }

    internal static QueryProjectionColumns<string> GraphProjectionColumns(BoundedGraphPatternQuery query, int stage,
        GqlPropertyProjection projection, bool keepPredicates)
    {
        var layout = GetGraphProjectionLayout(query, projection);
        return (keepPredicates ? layout.Checked : layout.Output)[stage];
    }

    private static QueryProjectionColumns<string> GraphEvaluationColumns(BoundedGraphPatternQuery query, GqlPropertyProjection projection) =>
        GetGraphProjectionLayout(query, projection).Evaluation;

    private static QueryProjectionColumns<string> BuildGraphProjectionColumns(BoundedGraphPatternQuery query, int stage,
        GqlPropertyProjection projection, bool keepPredicates)
    {
        var node = stage == 0 ? query.Start : query.Relationships[stage - 1].Target;
        var required = projection.Terms.SelectMany(t => t.Expression.Leaves)
            .Where(operand => operand.Binding == node.Binding && operand.Property is not null)
            .Select(operand => operand.Property!).ToHashSet(StringComparer.Ordinal);
        // Split topology/property owners validate the joined predicate at the home.
        if (keepPredicates)
        {
            required.UnionWith(node.Predicates.Select(p => p.Property));
        }

        return new(required, StringComparer.Ordinal);
    }
}
