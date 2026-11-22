using EntityForge.Query.Querying;
using EntityForge.Query.Results;
using EntityForge.Query.Values;

namespace EntityForge.Db.Gql;

internal sealed partial record GqlRowPipeline
{
    private static CompiledGraphGqlQuery RestrictUncorrelatedRootPredicate(CompiledGraphGqlQuery plan,
        GqlRowStage stage, IReadOnlyList<GraphPatternMatch> input)
    {
        var query = plan.Query;
        if (stage.Operation != "MATCH" || stage.MatchComponents is not null || stage.PathBinding is not null ||
            stage.ValueSubqueries is { Count: > 0 } || stage.Expression is null ||
            input.Count != 1 || input[0].Bindings.Count != 0 || input[0].Paths.Count != 0 ||
            plan.Pipeline is not null || plan.Union is not null || plan.Projection is not null || plan.ClockRequest is not null ||
            query.Relationships.Count != 0 || query.Aggregation is not null || query.Filter is not null ||
            query.Offset != 0 || query.OrderBy.Count != 0 || query.Distinct)
        {
            return plan;
        }
        GraphScalarExpression? Lower(GraphScalarExpression expression)
        {
            if (expression.Function == "AND" && expression.Arguments is [var first, var second])
            {
                var left = Lower(first);
                var right = Lower(second);
                return left is not null && right is not null ? new(null, null, null, "AND", [left, right]) : null;
            }
            if (expression.Function is not ("=" or "<" or "<=" or ">" or ">=") ||
                expression.Arguments is not [var a, var b])
            {
                return null;
            }
            bool Property(GraphScalarExpression item) => item.Binding == "__entity_" + query.Start.Binding && item.Property is not null;
            bool Number(GraphScalarExpression item) => item.Literal is LiteralValue
            { ScalarKind: QueryScalarKind.Integer or QueryScalarKind.Decimal or QueryScalarKind.FloatingPoint };
            bool String(GraphScalarExpression item) => item.Literal is LiteralValue { ScalarKind: QueryScalarKind.String };
            if (!(Property(a) && Number(b) || Number(a) && Property(b) || expression.Function == "=" &&
                (Property(a) && String(b) || String(a) && Property(b))))
            {
                return null;
            }
            GraphScalarExpression Operand(GraphScalarExpression item) => Property(item)
                ? new(query.Start.Binding, item.Property, null) : item;
            return new(null, null, null, expression.Function, [Operand(a), Operand(b)]);
        }
        var filter = Lower(stage.Expression);
        if (filter is null)
        {
            return plan;
        }
        // No imported row values or optional null extension participate here.
        // Retain the row-stage predicate as a residual correctness check; native
        // candidate indexes retain their mixed-type/error-preserving fallback,
        // including binary values' string projection for equality.
        return plan with
        {
            Query = new(query.Start, query.Relationships, query.MaximumMatches, query.MaximumExpansions,
                query.ReturnedBindings, query.OrderBy, query.Distinct, query.Offset, new GraphPatternExpressionFilter(filter))
        };
    }
}
