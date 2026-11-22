using EntityForge.Query.Projection;
using EntityForge.Query.Values;

namespace EntityForge.Query.Querying;

/// <summary>Specifies which incident edge orientations a graph pattern traverses.</summary>
public enum GraphPatternDirection
{
    /// <summary>Traverses edges from their source to their target.</summary>
    Outgoing,

    /// <summary>Traverses edges from their target to their source.</summary>
    Incoming,

    /// <summary>Traverses either orientation, counting a self-loop once per starting vertex.</summary>
    Undirected
}

/// <summary>Compares a graph property with a query operand using the executor's supported value types.</summary>
public enum GraphPatternComparisonOperator
{
    /// <summary>Tests equality.</summary>
    Equal,

    /// <summary>Tests inequality.</summary>
    NotEqual,

    /// <summary>Tests whether the property value sorts before the operand.</summary>
    LessThan,

    /// <summary>Tests whether the property value equals or sorts before the operand.</summary>
    LessThanOrEqual,

    /// <summary>Tests whether the property value sorts after the operand.</summary>
    GreaterThan,

    /// <summary>Tests whether the property value equals or sorts after the operand.</summary>
    GreaterThanOrEqual
}

/// <summary>Specifies the direction of a graph-pattern ordering key.</summary>
public enum GraphPatternSortDirection
{
    /// <summary>Orders lesser values before greater values.</summary>
    Ascending,

    /// <summary>Orders greater values before lesser values.</summary>
    Descending
}

/// <summary>Specifies whether an unmatched pattern eliminates or preserves its input row.</summary>
public enum GraphPatternMatchKind
{
    /// <summary>Eliminates an input row with no matching traversal.</summary>
    Required,

    /// <summary>Preserves an unmatched input row with absent introduced bindings.</summary>
    Optional
}

/// <summary>Selects paths from the candidates admitted by a traversal.</summary>
public enum GraphPatternPathSelection
{
    /// <summary>Returns all admitted paths within execution limits.</summary>
    All,

    /// <summary>Selects the requested number of shortest admitted paths per endpoint pair; defaults to one.</summary>
    AnyShortest,

    /// <summary>Selects all paths in the requested number of shortest distinct length groups per endpoint pair; defaults to one group.</summary>
    AllShortest
}

/// <summary>Constrains uniqueness within one relationship traversal's complete path.</summary>
public enum GraphPatternPathMode
{
    /// <summary>Adds no uniqueness restriction beyond the match's relationship reuse policy.</summary>
    Walk,

    /// <summary>Forbids repeating a physical relationship.</summary>
    Trail,

    /// <summary>Forbids repeating a vertex.</summary>
    Acyclic,

    /// <summary>Forbids repeating a vertex except for closing the path at its starting vertex.</summary>
    Simple
}

/// <summary>Combines two graph-pattern filter truth values.</summary>
public enum GraphPatternBooleanOperator
{
    /// <summary>Requires both operands to be true.</summary>
    And,

    /// <summary>Requires at least one operand to be true.</summary>
    Or,

    /// <summary>Requires exactly one operand to be true.</summary>
    Xor
}

/// <summary>Specifies an operation over the non-null values in an aggregate group.</summary>
public enum GraphPatternAggregateFunction
{
    /// <summary>Counts rows, bound entities or non-null expression results.</summary>
    Count,

    /// <summary>Adds compatible non-null inputs, returning null when the group has no non-null inputs.</summary>
    Sum,

    /// <summary>Selects the least input value, or null for an empty group.</summary>
    Minimum,

    /// <summary>Selects the greatest input value, or null for an empty group.</summary>
    Maximum,

    /// <summary>Returns the arithmetic mean, or null for an empty group.</summary>
    Average,

    /// <summary>Collects inputs into a bounded list with unspecified element order.</summary>
    Collect,

    /// <summary>Returns sample standard deviation using a denominator of n-1, or null for fewer than two non-null inputs.</summary>
    StandardDeviationSample,

    /// <summary>Returns population standard deviation using a denominator of n.</summary>
    StandardDeviationPopulation,

    /// <summary>Interpolates between sorted numeric values at the supplied percentile.</summary>
    PercentileContinuous,

    /// <summary>Selects a sorted numeric value at the supplied percentile.</summary>
    PercentileDiscrete
}

/// <summary>Base predicate applied to bindings produced by a typed graph pattern.</summary>
public abstract record GraphPatternFilter;

/// <summary>Tests whether a node binding or one of its properties is absent.</summary>
/// <param name="Binding">Node binding to inspect.</param>
/// <param name="Property">Property name, or null to test the node binding itself.</param>
/// <param name="Negated">True for IS NOT NULL; false for IS NULL.</param>
public sealed record GraphPatternNullTest(string Binding, string? Property = null, bool Negated = false) : GraphPatternFilter
{
    /// <summary>Gets the node binding to inspect.</summary>
    public string Binding
    {
        get;
    }
    = String.IsNullOrWhiteSpace(Binding)
        ? throw new ArgumentException("A null-test binding is required.", nameof(Binding)) : Binding;
}

/// <summary>Compares a named binding's property with a supplied query value.</summary>
/// <param name="Binding">The nonblank binding name.</param>
/// <param name="Property">The nonblank property name.</param>
/// <param name="Value">The nonnull comparison operand.</param>
/// <param name="Operator">The requested comparison operation.</param>
public sealed record GraphPatternPropertyComparison(
    string Binding,
    string Property,
    QueryValue Value,
    GraphPatternComparisonOperator Operator = GraphPatternComparisonOperator.Equal) : GraphPatternFilter
{
    /// <summary>Gets the binding supplying the property.</summary>
    public string Binding
    {
        get;
    }
    = String.IsNullOrWhiteSpace(Binding)
        ? throw new ArgumentException("A comparison binding is required.", nameof(Binding)) : Binding;

    /// <summary>Gets the property name to compare.</summary>
    public string Property
    {
        get;
    }
    = String.IsNullOrWhiteSpace(Property)
        ? throw new ArgumentException("A comparison property is required.", nameof(Property)) : Property;

    /// <summary>Gets the retained comparison operand.</summary>
    public QueryValue Value { get; } = Value ?? throw new ArgumentNullException(nameof(Value));
}

/// <summary>Combines two nonnull graph-pattern filters with a Boolean operation.</summary>
/// <param name="Left">The left filter.</param>
/// <param name="Operator">The combining Boolean operation.</param>
/// <param name="Right">The right filter.</param>
public sealed record GraphPatternBooleanFilter(
    GraphPatternFilter Left,
    GraphPatternBooleanOperator Operator,
    GraphPatternFilter Right) : GraphPatternFilter
{
    /// <summary>Gets the left filter.</summary>
    public GraphPatternFilter Left { get; } = Left ?? throw new ArgumentNullException(nameof(Left));

    /// <summary>Gets the right filter.</summary>
    public GraphPatternFilter Right { get; } = Right ?? throw new ArgumentNullException(nameof(Right));
}

/// <summary>Negates a nonnull graph-pattern filter.</summary>
/// <param name="Operand">The filter to negate.</param>
public sealed record GraphPatternNotFilter(GraphPatternFilter Operand) : GraphPatternFilter
{
    /// <summary>Gets the filter to negate.</summary>
    public GraphPatternFilter Operand { get; } = Operand ?? throw new ArgumentNullException(nameof(Operand));
}

/// <summary>Describes finite or open-ended trail lengths independently of execution work budgets.</summary>
public sealed record GraphPatternHopRange
{
    /// <summary>Gets a single-relationship pattern.</summary>
    public static GraphPatternHopRange Fixed { get; } = new(1, 1);

    /// <summary>Creates a finite range or an open-ended range when maximum is null.</summary>
    /// <param name="minimum">Minimum number of relationships, including zero.</param>
    /// <param name="maximum">Finite upper bound, or null for complete trail enumeration under execution work limits.</param>
    public GraphPatternHopRange(int minimum, int? maximum)
    {
        if (minimum < 0 || minimum > GraphPatternLimits.MaximumSpecifiedHops)
        {
            throw new ArgumentOutOfRangeException(nameof(minimum));
        }

        if (maximum < minimum || maximum > GraphPatternLimits.MaximumSpecifiedHops)
        {
            throw new ArgumentOutOfRangeException(nameof(maximum));
        }

        Minimum = minimum;
        Maximum = maximum;
    }

    /// <summary>Gets the minimum number of traversed relationships.</summary>
    public int Minimum
    {
        get;
    }

    /// <summary>Gets the finite upper bound, or null for an open-ended range.</summary>
    public int? Maximum
    {
        get;
    }

    /// <summary>Gets whether this range contains exactly one relationship.</summary>
    public bool IsFixed => Minimum == 1 && Maximum == 1;
}

/// <summary>Defines one graph-pattern ordering key from a direct property or scalar expression.</summary>
public sealed record GraphPatternOrderTerm
{
    /// <summary>Defines an ordering key by binding and property name.</summary>
    /// <param name="binding">The nonblank binding name.</param>
    /// <param name="property">The nonblank property name.</param>
    /// <param name="direction">The ordering direction.</param>
    /// <exception cref="ArgumentException">A name is null, empty or whitespace.</exception>
    public GraphPatternOrderTerm(string binding, string property,
        GraphPatternSortDirection direction = GraphPatternSortDirection.Ascending)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(binding);
        ArgumentException.ThrowIfNullOrWhiteSpace(property);
        Binding = binding;
        Property = property;
        Direction = direction;
    }

    /// <summary>Creates an ordering key evaluated from a scalar expression before result paging.</summary>
    /// <param name="expression">The scalar key, referencing nodes or fixed-length edges introduced by the pattern.</param>
    /// <param name="direction">The ascending or descending ordering direction.</param>
    public GraphPatternOrderTerm(GraphScalarExpression expression,
        GraphPatternSortDirection direction = GraphPatternSortDirection.Ascending)
    {
        Expression = expression ?? throw new ArgumentNullException(nameof(expression));
        Binding = String.Empty;
        Property = String.Empty;
        Direction = direction;
    }

    /// <summary>Gets the computed ordering key, or null when this term directly references a property.</summary>
    public GraphScalarExpression? Expression
    {
        get;
    }

    /// <summary>Gets the direct property binding, or an empty string for an expression key.</summary>
    public string Binding
    {
        get;
    }

    /// <summary>Gets the direct property name, or an empty string for an expression key.</summary>
    public string Property
    {
        get;
    }

    /// <summary>Gets the ordering direction.</summary>
    public GraphPatternSortDirection Direction
    {
        get;
    }
}

/// <summary>A property comparison attached directly to a node or relationship pattern.</summary>
public sealed record GraphPatternPropertyPredicate
{
    /// <summary>Defines a comparison against a nonnull query value.</summary>
    /// <param name="property">The nonblank property name.</param>
    /// <param name="value">The nonnull operand; retained without conversion.</param>
    /// <param name="operator">The comparison operation.</param>
    /// <exception cref="ArgumentException">The property is blank or an argument is null.</exception>
    public GraphPatternPropertyPredicate(string property, QueryValue value,
        GraphPatternComparisonOperator @operator = GraphPatternComparisonOperator.Equal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(property);
        Property = property;
        Value = value ?? throw new ArgumentNullException(nameof(value));
        Operator = @operator;
    }

    /// <summary>Gets the property name to compare.</summary>
    public string Property
    {
        get;
    }

    /// <summary>Gets the retained operand.</summary>
    public QueryValue Value
    {
        get;
    }

    /// <summary>Gets the comparison operation.</summary>
    public GraphPatternComparisonOperator Operator
    {
        get;
    }
}

/// <summary>Introduces a node binding with required labels and property predicates.</summary>
public sealed record GraphPatternNode
{
    /// <summary>Captures a node pattern and copies its label and predicate sequences.</summary>
    /// <param name="binding">The nonblank node binding name.</param>
    /// <param name="labels">Optional required labels, deduplicated using ordinal case-sensitive equality.</param>
    /// <param name="predicates">Optional property predicates, retained in input order.</param>
    /// <exception cref="ArgumentException">The binding is null, empty or whitespace.</exception>
    public GraphPatternNode(string binding, IEnumerable<string>? labels = null,
        IEnumerable<GraphPatternPropertyPredicate>? predicates = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(binding);
        Binding = binding;
        Labels = Array.AsReadOnly((labels ?? []).Distinct(StringComparer.Ordinal).ToArray());
        Predicates = Array.AsReadOnly((predicates ?? []).ToArray());
    }

    /// <summary>Gets the node binding name.</summary>
    public string Binding
    {
        get;
    }

    /// <summary>Gets the captured distinct required labels.</summary>
    public IReadOnlyList<string> Labels
    {
        get;
    }

    /// <summary>Gets the captured property predicates.</summary>
    public IReadOnlyList<GraphPatternPropertyPredicate> Predicates
    {
        get;
    }
}

/// <summary>One directed or undirected traversal from a previously introduced node to a target node pattern.</summary>
public sealed record GraphPatternRelationship
{
    /// <summary>Describes a fixed or quantified relationship traversal in a bounded graph pattern.</summary>
    /// <param name="binding">The relationship or path binding introduced by this step.</param>
    /// <param name="sourceBinding">The previously bound source node.</param>
    /// <param name="label">The required relationship label, or null for any label.</param>
    /// <param name="direction">The permitted edge orientation.</param>
    /// <param name="target">The target node pattern.</param>
    /// <param name="matchKind">Whether a missing match preserves its input row.</param>
    /// <param name="hops">The hop range; null selects one fixed hop.</param>
    /// <param name="pathSelection">The path selection applied after traversal.</param>
    /// <param name="filter">An optional condition for a fixed optional relationship.</param>
    /// <param name="edgePredicates">At most 32 property conditions applied to each traversed edge, before path selection.</param>
    /// <param name="allowRepeatedRelationships">Whether a physical relationship can be reused within this match. An unbounded traversal requires a uniqueness-constraining path mode or shortest-path selection.</param>
    /// <param name="pathMode">Uniqueness within this traversal, applied before path selection. This does not constrain other relationship steps.</param>
    /// <param name="pathSelectionCount">Number of shortest paths, or distinct length groups for AllShortest, per endpoint. Between zero and 10000; ignored for All.</param>
    public GraphPatternRelationship(string binding, string sourceBinding, string? label,
        GraphPatternDirection direction, GraphPatternNode target,
        GraphPatternMatchKind matchKind = GraphPatternMatchKind.Required,
        GraphPatternHopRange? hops = null,
        GraphPatternPathSelection pathSelection = GraphPatternPathSelection.All,
        GraphPatternFilter? filter = null,
        IEnumerable<GraphPatternPropertyPredicate>? edgePredicates = null,
        bool allowRepeatedRelationships = false, GraphPatternPathMode pathMode = GraphPatternPathMode.Walk,
        int pathSelectionCount = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(binding);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceBinding);
        Binding = binding;
        SourceBinding = sourceBinding;
        Label = String.IsNullOrWhiteSpace(label) ? null : label;
        Direction = direction;
        Target = target ?? throw new ArgumentNullException(nameof(target));
        MatchKind = matchKind;
        Hops = hops ?? GraphPatternHopRange.Fixed;
        if (!Enum.IsDefined(pathMode))
        {
            throw new ArgumentOutOfRangeException(nameof(pathMode));
        }

        if (allowRepeatedRelationships && Hops.Maximum is null && pathMode == GraphPatternPathMode.Walk &&
            pathSelection == GraphPatternPathSelection.All)
        {
            throw new ArgumentException("Repeatable walks require a finite maximum hop count.", nameof(hops));
        }

        PathMode = pathMode;
        AllowRepeatedRelationships = allowRepeatedRelationships;
        if (pathSelection is not GraphPatternPathSelection.All && Hops.IsFixed)
        {
            throw new ArgumentException("Shortest-path selection requires a quantified relationship.",
                nameof(pathSelection));
        }

        PathSelection = pathSelection;
        if (pathSelectionCount is < 0 or > 10000)
        {
            throw new ArgumentOutOfRangeException(nameof(pathSelectionCount));
        }

        PathSelectionCount = pathSelectionCount;
        if (filter is not null && (matchKind != GraphPatternMatchKind.Optional || !Hops.IsFixed))
        {
            throw new ArgumentException("A relationship-scoped filter requires a fixed optional relationship.", nameof(filter));
        }

        Filter = filter;
        EdgePredicates = Array.AsReadOnly((edgePredicates ?? []).Take(33).ToArray());
        if (EdgePredicates.Count > 32 || EdgePredicates.Any(predicate => predicate is null))
        {
            throw new ArgumentException("A relationship accepts at most 32 non-null edge predicates.", nameof(edgePredicates));
        }
    }

    /// <summary>Gets the relationship or quantified traversal binding.</summary>
    public string Binding
    {
        get;
    }

    /// <summary>Gets the previously introduced source node binding.</summary>
    public string SourceBinding
    {
        get;
    }

    /// <summary>Gets the required relationship label, or null to accept any label.</summary>
    public string? Label
    {
        get;
    }

    /// <summary>Gets the permitted orientation of traversed edges.</summary>
    public GraphPatternDirection Direction
    {
        get;
    }

    /// <summary>Gets the target node pattern.</summary>
    public GraphPatternNode Target
    {
        get;
    }

    /// <summary>Gets whether failure to match eliminates or preserves the input row.</summary>
    public GraphPatternMatchKind MatchKind
    {
        get;
    }

    /// <summary>Gets the allowed number of traversed relationships.</summary>
    public GraphPatternHopRange Hops
    {
        get;
    }

    /// <summary>Gets the selection applied to admitted paths.</summary>
    public GraphPatternPathSelection PathSelection
    {
        get;
    }

    /// <summary>Gets the number of shortest paths or distinct length groups selected per endpoint.</summary>
    public int PathSelectionCount
    {
        get;
    }

    /// <summary>Gets whether physical relationships may be reused within this match.</summary>
    public bool AllowRepeatedRelationships
    {
        get;
    }

    /// <summary>Gets the uniqueness constraint applied within this traversal before path selection.</summary>
    public GraphPatternPathMode PathMode
    {
        get;
    }

    /// <summary>Gets the optional-match condition, evaluated before preserving an unmatched input row.</summary>
    public GraphPatternFilter? Filter
    {
        get;
    }

    /// <summary>Gets property predicates applied to every traversed edge before path selection.
    /// Zero-length paths satisfy these predicates without requiring an edge.</summary>
    public IReadOnlyList<GraphPatternPropertyPredicate> EdgePredicates
    {
        get;
    }
}

/// <summary>Structural and materialization bounds for typed graph-pattern execution.</summary>
public static class GraphPatternLimits
{
    /// <summary>Maximum number of relationship pattern steps, independently of each step's hop count.</summary>
    public const int MaximumRelationships = 16;

    /// <summary>Maximum numeric hop bound accepted in a quantified pattern.</summary>
    public const int MaximumSpecifiedHops = 10_000;

    /// <summary>Maximum cumulative vertex/edge elements materialized while enumerating paths.</summary>
    public const int MaximumMaterializedPathElements = 1_000_000;
}

/// <summary>A validated typed graph pattern with explicit result and traversal budgets.</summary>
/// <remarks>This model represents a bounded execution primitive, not every construct of the text GQL language.</remarks>
public sealed record BoundedGraphPatternQuery
{
    /// <summary>Captures and validates pattern topology, binding references, ordering and execution budgets.</summary>
    /// <param name="start">The nonnull initial node pattern.</param>
    /// <param name="relationships">Traversal steps in binding order, copied; each source must already be introduced.</param>
    /// <param name="maximumMatches">Maximum result count, including zero, within the shared result bound.</param>
    /// <param name="maximumExpansions">Positive traversal-work budget within the shared expansion bound.</param>
    /// <param name="returnedBindings">Unique introduced bindings to return; null uses their introduction order.</param>
    /// <param name="orderBy">Ordering keys, copied in precedence order.</param>
    /// <param name="distinct">Whether to eliminate duplicate results before paging.</param>
    /// <param name="offset">Nonnegative number of results to skip within the combined result bound.</param>
    /// <param name="filter">Optional predicate over introduced bindings.</param>
    /// <param name="aggregation">Optional grouping and aggregation, which owns its ordering, distinctness and paging.</param>
    /// <exception cref="ArgumentException">Inputs contain invalid topology or bindings, or incompatible aggregation options.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A structural or execution budget is outside its supported bounds.</exception>
    public BoundedGraphPatternQuery(GraphPatternNode start,
        IEnumerable<GraphPatternRelationship>? relationships = null,
        int maximumMatches = 100,
        int maximumExpansions = EntityTraversalLimits.DefaultMaximumExpandedEntities,
        IEnumerable<string>? returnedBindings = null,
        IEnumerable<GraphPatternOrderTerm>? orderBy = null,
        bool distinct = false,
        int offset = 0,
        GraphPatternFilter? filter = null,
        GraphPatternAggregation? aggregation = null)
    {
        Start = start ?? throw new ArgumentNullException(nameof(start));
        Relationships = Array.AsReadOnly((relationships ?? []).ToArray());
        if (Relationships.Count > GraphPatternLimits.MaximumRelationships)
        {
            throw new ArgumentOutOfRangeException(nameof(relationships),
                $"A bounded graph pattern supports at most {GraphPatternLimits.MaximumRelationships} relationships.");
        }

        if (maximumMatches < 0 || maximumMatches > EntityTraversalLimits.MaximumExpandedEntities)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumMatches));
        }

        if (maximumExpansions <= 0 || maximumExpansions > EntityTraversalLimits.MaximumExpandedEntities)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumExpansions));
        }

        if (offset < 0 || offset > EntityTraversalLimits.MaximumExpandedEntities - maximumMatches)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        (HashSet<string> bindings, HashSet<string> nodeBindings) = ValidateTopology(Start, Relationships);
        ReturnedBindings = Array.AsReadOnly((returnedBindings ?? BindingOrder(Start, Relationships)).ToArray());
        if (ReturnedBindings.Count == 0 || ReturnedBindings.Distinct(StringComparer.Ordinal).Count() != ReturnedBindings.Count ||
            ReturnedBindings.Any(binding => !bindings.Contains(binding)))
        {
            throw new ArgumentException("Returned bindings must be unique bindings introduced by the pattern.",
                nameof(returnedBindings));
        }

        OrderBy = Array.AsReadOnly((orderBy ?? []).ToArray());
        var scalarBindings = nodeBindings.Concat(Relationships.Where(relationship => relationship.Hops.IsFixed)
            .Select(relationship => relationship.Binding)).ToHashSet(StringComparer.Ordinal);
        if (OrderBy.Any(term => term.Expression is { } expression
            ? expression.Bindings.Any(binding => !scalarBindings.Contains(binding)) : !nodeBindings.Contains(term.Binding)))
        {
            throw new ArgumentException("Graph ordering requires a node property or a scalar expression over known nodes and fixed-length edges.",
                nameof(orderBy));
        }

        ValidateFilter(filter, scalarBindings, bindings);
        var filterBindings = new HashSet<string>(StringComparer.Ordinal) { Start.Binding };
        var scopedBindings = new HashSet<string>(StringComparer.Ordinal) { Start.Binding };
        foreach (GraphPatternRelationship relationship in Relationships)
        {
            filterBindings.Add(relationship.Target.Binding);
            scopedBindings.Add(relationship.Target.Binding);
            scopedBindings.Add(relationship.Binding);
            if (relationship.Hops.IsFixed)
            {
                filterBindings.Add(relationship.Binding);
            }

            ValidateFilter(relationship.Filter, filterBindings, scopedBindings);
        }
        if (aggregation is not null)
        {
            foreach (var key in aggregation.GroupBy)
            {
                if (key.Binding is not null && !nodeBindings.Contains(key.Binding) ||
                    key.Expression is { } expression && expression.Bindings.Any(binding => !scalarBindings.Contains(binding)))
                {
                    throw new ArgumentException("A grouping key references an unknown node or fixed edge.", nameof(aggregation));
                }
            }
            foreach (var aggregate in aggregation.Aggregates)
            {
                if (aggregate.ValueBinding is not null && !bindings.Contains(aggregate.ValueBinding) ||
                    aggregate.ValueProperty is not null && !scalarBindings.Contains(aggregate.ValueBinding!) ||
                    aggregate.Expression is { } expression && expression.Bindings.Any(binding => !scalarBindings.Contains(binding)))
                {
                    throw new ArgumentException("An aggregate references an unknown binding or non-scalar edge property.", nameof(aggregation));
                }
            }
            if (orderBy is not null && OrderBy.Count > 0 || distinct || offset != 0)
            {
                throw new ArgumentException("Aggregation owns ordering, distinctness, and paging.", nameof(aggregation));
            }
        }
        MaximumMatches = maximumMatches;
        MaximumExpansions = maximumExpansions;
        Distinct = distinct;
        Offset = offset;
        Filter = filter;
        Aggregation = aggregation;
    }

    /// <summary>Gets the initial node pattern.</summary>
    public GraphPatternNode Start
    {
        get;
    }

    /// <summary>Gets the captured traversal steps in binding order.</summary>
    public IReadOnlyList<GraphPatternRelationship> Relationships
    {
        get;
    }

    /// <summary>Gets the maximum requested result count.</summary>
    public int MaximumMatches
    {
        get;
    }

    /// <summary>Gets the work budget; exhaustion fails execution rather than silently truncating traversal.</summary>
    public int MaximumExpansions
    {
        get;
    }

    /// <summary>Gets the captured output bindings in result order.</summary>
    public IReadOnlyList<string> ReturnedBindings
    {
        get;
    }

    /// <summary>Gets the captured ordering keys in precedence order.</summary>
    public IReadOnlyList<GraphPatternOrderTerm> OrderBy
    {
        get;
    }

    /// <summary>Gets whether duplicate results are eliminated before paging.</summary>
    public bool Distinct
    {
        get;
    }

    /// <summary>Gets the number of results to skip.</summary>
    public int Offset
    {
        get;
    }

    /// <summary>Gets the optional predicate over the completed pattern bindings.</summary>
    public GraphPatternFilter? Filter
    {
        get;
    }

    /// <summary>Gets the optional aggregate operation and its own output controls.</summary>
    public GraphPatternAggregation? Aggregation
    {
        get;
    }

    private static void ValidateFilter(GraphPatternFilter? filter, IReadOnlySet<string> nodeBindings, IReadOnlySet<string> bindings)
    {
        switch (filter)
        {
            case null:
                return;
            case GraphPatternExpressionFilter expression:
                if (expression.Expression.Bindings.Any(binding => !nodeBindings.Contains(binding)))
                {
                    throw new ArgumentException("A scalar graph filter references an unknown node or fixed-length edge binding.", nameof(filter));
                }
                return;
            case GraphPatternNullTest test when !(test.Property is null ? bindings : nodeBindings).Contains(test.Binding):
                throw new ArgumentException($"Graph filter references unknown node binding '{test.Binding}'.", nameof(filter));
            case GraphPatternNullTest:
                return;
            case GraphPatternPropertyComparison comparison when !nodeBindings.Contains(comparison.Binding):
                throw new ArgumentException($"Graph filter references unknown node binding '{comparison.Binding}'.",
                    nameof(filter));
            case GraphPatternPropertyComparison:
                return;
            case GraphPatternBooleanFilter boolean:
                ValidateFilter(boolean.Left, nodeBindings, bindings);
                ValidateFilter(boolean.Right, nodeBindings, bindings);
                return;
            case GraphPatternNotFilter not:
                ValidateFilter(not.Operand, nodeBindings, bindings);
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(filter));
        }
    }

    private static (HashSet<string> Bindings, HashSet<string> NodeBindings) ValidateTopology(GraphPatternNode start,
        IReadOnlyList<GraphPatternRelationship> relationships)
    {
        var nodeBindings = new HashSet<string>(StringComparer.Ordinal) { start.Binding };
        var relationshipBindings = new HashSet<string>(StringComparer.Ordinal);
        var possiblyUnboundNodes = new HashSet<string>(StringComparer.Ordinal);
        foreach (GraphPatternRelationship relationship in relationships)
        {
            if (!nodeBindings.Contains(relationship.SourceBinding))
            {
                throw new ArgumentException(
                    $"Relationship '{relationship.Binding}' references unbound source '{relationship.SourceBinding}'.",
                    nameof(relationships));
            }

            if (!relationshipBindings.Add(relationship.Binding) || nodeBindings.Contains(relationship.Binding) ||
                relationshipBindings.Contains(relationship.Target.Binding))
            {
                throw new ArgumentException("Node and relationship bindings must be unique.", nameof(relationships));
            }

            if (possiblyUnboundNodes.Contains(relationship.SourceBinding) &&
                relationship.MatchKind is GraphPatternMatchKind.Required)
            {
                throw new ArgumentException("A relationship from an optional node must also be optional.",
                    nameof(relationships));
            }

            bool introducesTarget = !nodeBindings.Contains(relationship.Target.Binding);
            if (!introducesTarget && possiblyUnboundNodes.Contains(relationship.Target.Binding))
            {
                throw new ArgumentException("Convergence requires an already definitely bound target.",
                    nameof(relationships));
            }

            if (introducesTarget)
            {
                nodeBindings.Add(relationship.Target.Binding);
                if (relationship.MatchKind is GraphPatternMatchKind.Optional ||
                    possiblyUnboundNodes.Contains(relationship.SourceBinding))
                {
                    possiblyUnboundNodes.Add(relationship.Target.Binding);
                }
            }
        }
        return ([.. nodeBindings, .. relationshipBindings], nodeBindings);
    }

    private static IEnumerable<string> BindingOrder(GraphPatternNode start,
        IEnumerable<GraphPatternRelationship> relationships) =>
        new[] { start.Binding }.Concat(relationships.SelectMany(item =>
            new[] { item.Binding, item.Target.Binding })).Distinct(StringComparer.Ordinal);
}

/// <summary>Indicates that graph traversal exhausted its work budget without returning a complete result.</summary>
/// <param name="maximumExpansions">The exhausted expansion-step budget.</param>
public sealed class GraphPatternExpansionLimitException(int maximumExpansions)
    : Exception($"The bounded graph pattern exceeded its bound of {maximumExpansions} expansion steps.")
{
    /// <summary>Gets the expansion-step budget that was exceeded.</summary>
    public int MaximumExpansions { get; } = maximumExpansions;
}
