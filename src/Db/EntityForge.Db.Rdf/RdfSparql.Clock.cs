using System.Globalization;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Rdf;

public static partial class RdfSparql
{
    // Cached syntax remains immutable. Each execution binds NOW once, including
    // nested subqueries, aggregates and correlated EXISTS expressions.
    private sealed class QueryFunctionBindings
    {
        private readonly ValueExpression? _now;
        private readonly string? _baseIri;

        internal QueryFunctionBindings(bool bindClock = true, string? baseIri = null)
        {
            _baseIri = baseIri;
            _now = bindClock ? new TermValueExpression(new(null,
                new RdfLiteral(DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture), Xsd + "dateTime"), null)) : null;
        }

        private ValueExpression Value(ValueExpression value) => value switch
        {
            CallExpression { Name: "NOW" } when _now is not null => _now,
            CallExpression call => call with { Arguments = call.Arguments.Select(Value).ToArray() },
            ArithmeticExpression arithmetic => arithmetic with { Left = Value(arithmetic.Left), Right = Value(arithmetic.Right) },
            ValueFunctionExpression function => function with { Argument = Value(function.Argument), BaseIri = function.BaseIri ?? _baseIri },
            AggregateValueExpression aggregate => aggregate with { Aggregate = Aggregate(aggregate.Aggregate) },
            BooleanValueExpression boolean => boolean with { Filter = Filter(boolean.Filter)! },
            ConditionalExpression conditional => conditional with { Condition = Filter(conditional.Condition)!, True = Value(conditional.True), False = Value(conditional.False) },
            _ => value
        };

        private AggregateProjection Aggregate(AggregateProjection aggregate) =>
            aggregate with
            {
                Value = aggregate.Value is null ? null : Value(aggregate.Value)
            };

        private FilterExpression? Filter(FilterExpression? filter) => filter switch
        {
            ExistsExpression exists => exists with { Group = Body(exists.Group) },
            BinaryBooleanExpression binary => binary with { Left = Filter(binary.Left)!, Right = Filter(binary.Right)! },
            NotExpression not => not with { Operand = Filter(not.Operand)! },
            _ => MapFilterValues(filter, Value)
        };

        public Group Body(Group group) => group with
        {
            Filters = group.Filters.Select(filter => Filter(filter)!).ToArray(),
            Elements = group.Elements.Select(element => element switch
            {
                ServiceElement service => (GroupElement)service,
                NamedGraphElement named => (GroupElement)new NamedGraphElement(Body(named.Group), named.Graph),
                NestedGroupElement nested => new NestedGroupElement(Body(nested.Group)),
                OptionalElement optional => optional with { Group = Body(optional.Group) },
                MinusElement minus => minus with { Group = Body(minus.Group) },
                UnionElement union => union with { Groups = union.Groups.Select(Body).ToArray() },
                SubqueryElement subquery => subquery with { Query = Plan(subquery.Query) },
                BindElement bind => bind with { Value = Value(bind.Value) },
                _ => element
            }).ToArray()
        };

        public Modifiers Options(Modifiers modifiers) => modifiers with
        {
            Having = Filter(modifiers.Having),
            GroupExpressions = modifiers.GroupExpressions.Select(bind => bind with { Value = Value(bind.Value) }).ToArray(),
            Order = modifiers.Order.Select(order => order with { Value = Value(order.Value) }).ToArray()
        };

        public ParsedSelectQuery Plan(ParsedSelectQuery plan) => plan with
        {
            Body = Body(plan.Body),
            Modifiers = Options(plan.Modifiers),
            Projection = plan.Projection with
            {
                Aggregates = plan.Projection.Aggregates.Select(Aggregate).ToArray(),
                Computed = plan.Projection.Computed.Select(bind => bind with { Value = Value(bind.Value) }).ToArray()
            }
        };
    }

    private static bool UsesQueryClock(string query) => Token().Matches(NormalizeQueryLexicalForm(query))
        .Any(token => token.Value.Equals("NOW", StringComparison.OrdinalIgnoreCase));
}
