using EntityForge.Query.Querying;
using EntityForge.Query.Values;

namespace EntityForge.Db.Graph;

internal sealed partial class BoundedGraphPatternExecutor
{
    private static GraphScalarStringEquality? FindScalarStringEquality(GraphPatternFilter? filter, string binding) => filter switch
    {
        GraphPatternBooleanFilter { Operator: GraphPatternBooleanOperator.And } both =>
            FindScalarStringEquality(both.Left, binding) ?? FindScalarStringEquality(both.Right, binding),
        GraphPatternExpressionFilter expression => FindScalarStringEquality(expression.Expression, binding),
        _ => null
    };

    private static GraphScalarStringEquality? FindScalarStringEquality(GraphScalarExpression expression, string binding)
    {
        if (expression is { Function: "AND", Arguments: [var left, var right] })
        {
            return FindScalarStringEquality(left, binding) ?? FindScalarStringEquality(right, binding);
        }
        if (expression is not { Function: "=", Arguments: [var first, var second] })
        {
            return null;
        }
        if (first.Literal is not null)
        {
            (first, second) = (second, first);
        }
        return first.Binding == binding && first.Property is { } property && second.Literal is LiteralValue
        { ScalarKind: QueryScalarKind.String } value ? new(property, value.LexicalValue) : null;
    }
}
