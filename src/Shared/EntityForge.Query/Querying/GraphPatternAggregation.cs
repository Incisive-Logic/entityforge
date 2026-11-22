using EntityForge.Query.Projection;

namespace EntityForge.Query.Querying;

/// <summary>Defines one aggregate result column.</summary>
public sealed record GraphPatternAggregate
{
    /// <summary>Creates an aggregate over rows, a binding, a property or a scalar expression.</summary>
    /// <param name="function">Aggregate operation.</param>
    /// <param name="alias">Unique output column name.</param>
    /// <param name="valueBinding">Node or relationship binding; null for COUNT(*) or an expression.</param>
    /// <param name="valueProperty">Property on the value binding, or null.</param>
    /// <param name="distinct">Whether to eliminate equal input values before aggregation.</param>
    /// <param name="expression">Scalar input expression, mutually exclusive with binding/property input.</param>
    /// <param name="percentile">Constant percentile in [0,1], required only for percentile aggregates.</param>
    public GraphPatternAggregate(GraphPatternAggregateFunction function, string alias,
        string? valueBinding = null, string? valueProperty = null, bool distinct = false,
        GraphScalarExpression? expression = null, decimal? percentile = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);
        if (!Enum.IsDefined(function))
        {
            throw new ArgumentOutOfRangeException(nameof(function));
        }

        ValueBinding = String.IsNullOrWhiteSpace(valueBinding) ? null : valueBinding;
        ValueProperty = String.IsNullOrWhiteSpace(valueProperty) ? null : valueProperty;
        if (expression is not null && (ValueBinding is not null || ValueProperty is not null))
        {
            throw new ArgumentException("Use either an expression or a binding/property input.", nameof(expression));
        }

        if (ValueProperty is not null && ValueBinding is null)
        {
            throw new ArgumentException("A property input requires a binding.", nameof(valueProperty));
        }

        if (function != GraphPatternAggregateFunction.Count && expression is null && ValueProperty is null)
        {
            throw new ArgumentException("This aggregate requires a scalar input.", nameof(expression));
        }

        if (distinct && expression is null && ValueBinding is null)
        {
            throw new ArgumentException("COUNT(DISTINCT *) is not supported.", nameof(distinct));
        }

        bool percentileFunction = function is GraphPatternAggregateFunction.PercentileContinuous or GraphPatternAggregateFunction.PercentileDiscrete;
        if (percentileFunction != percentile.HasValue || percentile is < 0 or > 1)
        {
            throw new ArgumentException("Percentile aggregates require a percentile in [0,1]; other aggregates cannot specify one.", nameof(percentile));
        }

        Function = function;
        Alias = alias;
        Distinct = distinct;
        Expression = expression;
        Percentile = percentile;
    }

    /// <summary>Gets the aggregate operation.</summary>
    public GraphPatternAggregateFunction Function
    {
        get;
    }

    /// <summary>Gets the result column name.</summary>
    public string Alias
    {
        get;
    }

    /// <summary>Gets the input binding, or null.</summary>
    public string? ValueBinding
    {
        get;
    }

    /// <summary>Gets the input property, or null.</summary>
    public string? ValueProperty
    {
        get;
    }

    /// <summary>Gets whether equal non-null inputs are counted once.</summary>
    public bool Distinct
    {
        get;
    }

    /// <summary>Gets the scalar input expression, or null.</summary>
    public GraphScalarExpression? Expression
    {
        get;
    }

    /// <summary>Gets the percentile, or null for other aggregate functions.</summary>
    public decimal? Percentile
    {
        get;
    }
}

/// <summary>Defines a node identity or scalar value used as a grouping key.</summary>
public sealed record GraphPatternGroupingKey
{
    /// <summary>Creates a grouping key with exactly one binding or expression.</summary>
    /// <param name="alias">Unique output column name.</param>
    /// <param name="binding">Node binding to group by identity, or null.</param>
    /// <param name="expression">Scalar grouping expression, or null.</param>
    public GraphPatternGroupingKey(string alias, string? binding = null, GraphScalarExpression? expression = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);
        if ((binding is null) == (expression is null) || binding is not null && String.IsNullOrWhiteSpace(binding))
        {
            throw new ArgumentException("A grouping key requires exactly one node binding or scalar expression.");
        }

        Alias = alias;
        Binding = binding;
        Expression = expression;
    }

    /// <summary>Gets the result column name.</summary>
    public string Alias
    {
        get;
    }

    /// <summary>Gets the node binding, or null for a scalar key.</summary>
    public string? Binding
    {
        get;
    }

    /// <summary>Gets the scalar key expression, or null for a node identity.</summary>
    public GraphScalarExpression? Expression
    {
        get;
    }
}

/// <summary>Orders grouped results by an output column.</summary>
/// <param name="Alias">Grouping or aggregate output column.</param>
/// <param name="Direction">Ascending or descending order.</param>
public sealed record GraphPatternAggregateOrder(string Alias, GraphPatternSortDirection Direction = GraphPatternSortDirection.Ascending);

/// <summary>Defines immutable grouped aggregation, ordering and paging over complete bounded input.</summary>
public sealed record GraphPatternAggregation
{
    /// <summary>Creates an aggregate plan with at most 32 result columns.</summary>
    /// <param name="aggregates">One or more aggregate result columns.</param>
    /// <param name="groupBy">Grouping keys; empty means one global group, even for empty input.</param>
    /// <param name="orderBy">Output-column ordering, applied before paging.</param>
    /// <param name="limit">Maximum output rows, including zero.</param>
    /// <param name="offset">Output rows to skip.</param>
    /// <param name="maximumInputRows">Input-row work bound; exceeding it fails instead of truncating aggregates.</param>
    public GraphPatternAggregation(IEnumerable<GraphPatternAggregate> aggregates,
        IEnumerable<GraphPatternGroupingKey>? groupBy = null, IEnumerable<GraphPatternAggregateOrder>? orderBy = null,
        int limit = 100, int offset = 0, int maximumInputRows = EntityTraversalLimits.DefaultMaximumExpandedEntities)
    {
        ArgumentNullException.ThrowIfNull(aggregates);
        Aggregates = Array.AsReadOnly(aggregates.ToArray());
        GroupBy = Array.AsReadOnly((groupBy ?? []).ToArray());
        OrderBy = Array.AsReadOnly((orderBy ?? []).ToArray());
        if (Aggregates.Count == 0 || Aggregates.Count + GroupBy.Count > 32 ||
            Aggregates.Any(item => item is null) || GroupBy.Any(item => item is null))
        {
            throw new ArgumentException("An aggregate plan requires 1-32 result columns, including at least one aggregate.");
        }

        var aliases = Aggregates.Select(item => item.Alias).Concat(GroupBy.Select(item => item.Alias)).ToHashSet(StringComparer.Ordinal);
        if (aliases.Count != Aggregates.Count + GroupBy.Count)
        {
            throw new ArgumentException("Aggregate and grouping aliases must be unique.");
        }

        if (OrderBy.Count > 32 || OrderBy.Any(item => item is null || !aliases.Contains(item.Alias) || !Enum.IsDefined(item.Direction)))
        {
            throw new ArgumentException("Aggregate ordering requires known output aliases and valid directions.", nameof(orderBy));
        }

        if (limit < 0 || limit > EntityTraversalLimits.MaximumExpandedEntities)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        if (offset < 0 || offset > EntityTraversalLimits.MaximumExpandedEntities - limit)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        if (maximumInputRows <= 0 || maximumInputRows > EntityTraversalLimits.MaximumExpandedEntities)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumInputRows));
        }

        Limit = limit;
        Offset = offset;
        MaximumInputRows = maximumInputRows;
    }

    /// <summary>Gets aggregate result columns.</summary>
    public IReadOnlyList<GraphPatternAggregate> Aggregates
    {
        get;
    }

    /// <summary>Gets grouping result columns.</summary>
    public IReadOnlyList<GraphPatternGroupingKey> GroupBy
    {
        get;
    }

    /// <summary>Gets output ordering terms.</summary>
    public IReadOnlyList<GraphPatternAggregateOrder> OrderBy
    {
        get;
    }

    /// <summary>Gets the output row limit.</summary>
    public int Limit
    {
        get;
    }

    /// <summary>Gets the output offset.</summary>
    public int Offset
    {
        get;
    }

    /// <summary>Gets the complete-input work bound.</summary>
    public int MaximumInputRows
    {
        get;
    }
}
