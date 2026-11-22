namespace EntityForge.Db.Rdf;

public static partial class RdfSparql
{
    // Scalar expressions can read graph patterns too. Until their leaves are
    // represented in a fragment plan, evaluate them over the captured relation.
    private static bool UsesValueGraphPatterns(string query)
    {
        if (!query.Contains("EXISTS", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (QueryForm(query) != RdfSparqlQueryForm.Select)
        {
            return true;
        }
        bool Filter(FilterExpression? expression) => expression switch
        {
            ExistsExpression => true,
            ComparisonExpression comparison => Value(comparison.Left) || Value(comparison.Right),
            EffectiveBooleanExpression boolean => Value(boolean.Value),
            BinaryBooleanExpression boolean => Filter(boolean.Left) || Filter(boolean.Right),
            NotExpression not => Filter(not.Operand),
            FunctionExpression function => Value(function.First) || Value(function.Second) || Value(function.Third),
            InExpression member => Value(member.Operand) || member.Values.Any(Value),
            _ => false
        };
        bool Value(ValueExpression? expression) => expression switch
        {
            ArithmeticExpression arithmetic => Value(arithmetic.Left) || Value(arithmetic.Right),
            ValueFunctionExpression function => Value(function.Argument),
            CallExpression call => call.Arguments.Any(Value),
            AggregateValueExpression aggregate => Value(aggregate.Aggregate.Value),
            BooleanValueExpression boolean => Filter(boolean.Filter),
            ConditionalExpression conditional => Filter(conditional.Condition) || Value(conditional.True) || Value(conditional.False),
            _ => false
        };
        bool Options(Modifiers options) => options.Order.Any(order => Value(order.Value)) ||
            options.GroupExpressions.Any(bind => Value(bind.Value));
        bool Projection(SelectProjection projection) => projection.Computed.Any(bind => Value(bind.Value)) ||
            projection.Aggregates.Any(aggregate => Value(aggregate.Value));
        bool FilterValues(FilterExpression filter) => filter switch
        {
            ExistsExpression exists => Body(exists.Group),
            BinaryBooleanExpression binary => FilterValues(binary.Left) || FilterValues(binary.Right),
            NotExpression not => FilterValues(not.Operand),
            _ => Filter(filter)
        };
        bool Body(Group group) => group.Elements.Any(element => element switch
        {
            BindElement bind => Value(bind.Value),
            NestedGroupElement nested => Body(nested.Group),
            OptionalElement optional => Body(optional.Group),
            MinusElement minus => Body(minus.Group),
            UnionElement union => union.Groups.Any(Body),
            SubqueryElement subquery => Plan(subquery.Query),
            _ => false
        }) || group.Filters.Any(FilterValues);
        bool Plan(ParsedSelectQuery plan) => Body(plan.Body) || Projection(plan.Projection) || Options(plan.Modifiers) ||
            plan.Modifiers.Having is { } having && FilterValues(having);
        return Plan(GetSelectPlan(query));
    }
}
