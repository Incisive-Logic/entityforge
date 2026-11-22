using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using System.Globalization;
using System.Numerics;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Rdf;

public static partial class RdfSparql
{
    private sealed record TermPattern(string? Variable, RdfTerm? Constant, QuotedPattern? Quoted);

    private sealed record QuotedPattern(TermPattern Subject, TermPattern Predicate, TermPattern Object);

    private sealed record Pattern(TermPattern Subject, TermPattern? Predicate, TermPattern Object,
        TermPattern? Graph, RdfPathExpression? Path = null);

    private abstract record FilterExpression;

    private abstract record ValueExpression;

    // An execution-local scalar result, including an expression error (null).
    // It is never serialized into a parsed query or retained plan.
    private sealed record EvaluatedValueExpression(RdfTerm? Value) : ValueExpression;

    private sealed record TermValueExpression(TermPattern Term) : ValueExpression;

    private sealed record ValueFunctionExpression(string Name, ValueExpression Argument, string? BaseIri = null) : ValueExpression;

    private sealed record ArithmeticExpression(ValueExpression Left, string Operator, ValueExpression Right) : ValueExpression;

    private sealed record AggregateValueExpression(AggregateProjection Aggregate) : ValueExpression;

    private sealed record CallExpression(string Name, IReadOnlyList<ValueExpression> Arguments) : ValueExpression;

    private sealed record BooleanValueExpression(FilterExpression Filter) : ValueExpression;

    private sealed record ConditionalExpression(FilterExpression Condition, ValueExpression True, ValueExpression False) : ValueExpression;

    private sealed record EffectiveBooleanExpression(ValueExpression Value) : FilterExpression;

    private sealed record ComparisonExpression(ValueExpression Left, string Operator, ValueExpression Right)
        : FilterExpression;

    private sealed record BinaryBooleanExpression(FilterExpression Left, string Operator, FilterExpression Right)
        : FilterExpression;

    private sealed record NotExpression(FilterExpression Operand) : FilterExpression;

    private sealed record FunctionExpression(string Name, ValueExpression First, ValueExpression? Second,
        ValueExpression? Third)
        : FilterExpression;

    private sealed record InExpression(ValueExpression Operand, IReadOnlyList<ValueExpression> Values,
        bool Negated) : FilterExpression;

    private sealed record ExistsExpression(Group Group, bool Negated) : FilterExpression;

    private abstract record GroupElement;

    private sealed record PatternElement(Pattern Pattern) : GroupElement;

    private record NestedGroupElement(Group Group) : GroupElement;

    private sealed record NamedGraphElement(Group Group, TermPattern Graph) : NestedGroupElement(Group);

    private sealed record ServiceElement(Group Group, TermPattern Endpoint, string RemoteBody, bool Silent, Guid Identity) : NestedGroupElement(Group);

    private sealed record OptionalElement(Group Group) : GroupElement;

    private sealed record UnionElement(IReadOnlyList<Group> Groups) : GroupElement;

    private sealed record MinusElement(Group Group) : GroupElement;

    private sealed record SubqueryElement(ParsedSelectQuery Query) : GroupElement;

    private sealed record ValuesElement(IReadOnlyList<string> Variables,
        IReadOnlyList<IReadOnlyList<RdfTerm?>> Rows) : GroupElement;

    private sealed record BindElement(ValueExpression Value, string Variable) : GroupElement;

    private sealed record Group(IReadOnlyList<GroupElement> Elements, IReadOnlyList<FilterExpression> Filters);

    private sealed record SelectProjection(bool SelectAll, IReadOnlyList<string> OutputVariables,
        IReadOnlyList<AggregateProjection> Aggregates, IReadOnlyList<BindElement> Computed);

    private sealed record ParsedSelectQuery(SelectProjection Projection, Group Body,
        Modifiers Modifiers, bool Distinct, QueryDataset? Dataset = null)
    {
        // Includes unbound references, which must not acquire generated bindings
        // when this plan is serialized for a remote SERVICE endpoint.
        public IReadOnlySet<string> SourceVariables { get; init; } = new HashSet<string>(StringComparer.Ordinal);
    }

    private sealed record AggregateProjection(string Function, ValueExpression? Value, string Alias, bool Distinct,
        string Separator = " ")
    {
        public string? Argument => Value is TermValueExpression term ? term.Term.Variable : null;
    }

    private sealed class SimpleCountGroup(RdfTerm? groupValue,
        Dictionary<string, HashSet<RdfTerm>?> distinctValues)
    {
        public RdfTerm? GroupValue { get; } = groupValue;

        public Dictionary<string, HashSet<RdfTerm>?> DistinctValues { get; } = distinctValues;

        public Dictionary<string, long> Counts
        {
            get;
        }
        = distinctValues.Keys.ToDictionary(
            alias => alias, _ => 0L, StringComparer.Ordinal);
    }

    private sealed record OrderCondition(ValueExpression Value, bool Descending);

    private sealed record Modifiers(IReadOnlyList<string> GroupVariables, IReadOnlyList<OrderCondition> Order,
        int Limit, int Offset, FilterExpression? Having)
    {
        public IReadOnlyList<BindElement> GroupExpressions { get; init; } = [];

        public ValuesElement? TrailingValues
        {
            get; init;
        }

        public string? OrderVariable => Order.Count == 1 && Order[0].Value is TermValueExpression term
            ? term.Term.Variable : null;

        public bool Descending => Order.Count == 1 && Order[0].Descending;
    }

    // The algebra consumes one immutable relation. Native snapshots retain their indexed
    // path; distributed exchanges supply an already bounded, deduplicated relation.
}
