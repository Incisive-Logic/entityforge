using EntityForge.Db.Graph.Contracts;
using EntityForge.Query.Querying;
using EntityForge.Query.Values;

namespace EntityForge.Db.Graph;

internal sealed partial class BoundedGraphPatternExecutor
{
    private static IEnumerable<GraphScalarNumericBound> ScalarNumericBounds(GraphPatternFilter? filter, string binding)
    {
        if (filter is GraphPatternBooleanFilter { Operator: GraphPatternBooleanOperator.And } both)
        {
            return ScalarNumericBounds(both.Left, binding).Concat(ScalarNumericBounds(both.Right, binding));
        }
        return filter is GraphPatternExpressionFilter scalar ? ScalarNumericBounds(scalar.Expression, binding) : [];
    }

    private static IEnumerable<GraphScalarNumericBound> ScalarNumericBounds(GraphScalarExpression expression, string binding)
    {
        if (expression is { Function: "AND", Arguments: [var left, var right] })
        {
            return ScalarNumericBounds(left, binding).Concat(ScalarNumericBounds(right, binding));
        }
        if (expression is not { Function: "<" or "<=" or ">" or ">=", Arguments: [var first, var second] })
        {
            return [];
        }
        string op = expression.Function;
        if (second.Binding == binding && second.Property is not null)
        {
            (first, second) = (second, first);
            op = op switch
            {
                "<" => ">",
                "<=" => ">=",
                ">" => "<",
                _ => "<="
            };
        }
        if (first.Binding != binding || first.Property is not { } property || NumericConstant(second) is not { } value)
        {
            return [];
        }
        return [new(property, value, op)];
    }

    private static LiteralValue? NumericConstant(GraphScalarExpression expression)
    {
        if (expression.Literal is LiteralValue { ScalarKind: QueryScalarKind.Integer or QueryScalarKind.Decimal or QueryScalarKind.FloatingPoint } literal)
        {
            return literal;
        }
        // Only deterministic numeric arithmetic; never fold clocks, random values,
        // user calls or binding-dependent expressions while choosing candidates.
        if (expression.Function is not ("+" or "-" or "*" or "/" or "%" or "unary+" or "unary-") ||
            expression.Arguments is not { } args || args.Any(arg => NumericConstant(arg) is null))
        {
            return null;
        }
        try
        {
            return GraphScalarEvaluator.Evaluate(expression, (_, _) => UnboundValue.Instance) as LiteralValue;
        }
        catch (Exception error) when (error is ArgumentException or ArithmeticException)
        {
            // Preserve normal expression errors (including the empty-input case).
            return null;
        }
    }

    private static bool HasValueCandidates(GraphCandidateQuery query) => query.Property is not null ||
        query.ScalarNumericEquality is not null || query.ScalarStringEquality is not null ||
        query.ScalarNumericBounds is not null || query.NumericBounds is not null;

    private static bool TryAnchoredCandidates(BoundedGraphPatternQuery query, GraphReadView view,
        GraphCandidateQuery root, out IReadOnlyList<GraphVertexId> roots, CancellationToken cancellation = default)
    {
        roots = [];
        // A semijoin narrows the original start set; it does not reverse path values,
        // bindings, edge order or optional-match semantics. Keep more complex shapes
        // on the existing executor until their necessary conditions are established.
        if (HasValueCandidates(root) || query.Relationships.Count != 1)
        {
            return false;
        }
        var step = query.Relationships[0];
        if (step.SourceBinding != query.Start.Binding || step.Target.Binding == query.Start.Binding ||
            step.MatchKind != GraphPatternMatchKind.Required || !step.Hops.IsFixed ||
            step.PathSelection != GraphPatternPathSelection.All || step.Direction == GraphPatternDirection.Undirected)
        {
            return false;
        }
        var target = CandidateQuery(step.Target, query.Filter);
        if (!HasValueCandidates(target) || !view.HasCompleteCandidateAdjacency)
        {
            return false;
        }
        var targets = SnapshotCandidates(view, target, cancellation);
        if (targets.Count > 256 || targets.Count >= view.Vertices.Count)
        {
            return false;
        }
        var selected = new HashSet<GraphVertexId>();
        int inspected = 0;
        foreach (var id in targets)
        {
            cancellation.ThrowIfCancellationRequested();
            var edges = step.Direction == GraphPatternDirection.Outgoing ? view.Incoming(id, step.Label) : view.Outgoing(id, step.Label);
            foreach (var edge in edges)
            {
                cancellation.ThrowIfCancellationRequested();
                if (++inspected > Math.Min(query.MaximumExpansions, 10_000))
                {
                    return false;
                }
                var source = step.Direction == GraphPatternDirection.Outgoing ? edge.From : edge.To;
                if (view.Vertices.ContainsKey(source))
                {
                    selected.Add(source);
                }
            }
        }
        roots = selected.ToArray();
        return true;
    }
}
