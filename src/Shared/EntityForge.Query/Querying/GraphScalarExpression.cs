using EntityForge.Query.Values;

namespace EntityForge.Query.Querying;

/// <summary>A bounded scalar expression over graph properties, literal values and named operations.</summary>
public sealed record GraphScalarExpression
{
    /// <summary>Creates one property reference, literal, or operation with immutable argument expressions.</summary>
    /// <param name="binding">The matched node or edge variable for a property reference.</param>
    /// <param name="property">The property name for a property reference.</param>
    /// <param name="literal">The constant value; use <see cref="UnboundValue.Instance"/> for null.</param>
    /// <param name="function">An operation or function name interpreted by the execution engine.</param>
    /// <param name="arguments">The ordered operands of an operation.</param>
    public GraphScalarExpression(string? binding, string? property, QueryValue? literal,
        string? function = null, IReadOnlyList<GraphScalarExpression>? arguments = null)
    {
        bool reference = binding is not null || property is not null;
        if ((reference ? 1 : 0) + (literal is not null ? 1 : 0) + (function is not null ? 1 : 0) != 1 ||
            reference && (String.IsNullOrWhiteSpace(binding) || String.IsNullOrWhiteSpace(property)) ||
            function is null && arguments is not null || function is not null && (String.IsNullOrWhiteSpace(function) || arguments is null))
        {
            throw new ArgumentException("An expression must contain exactly one complete property, literal, or operation.");
        }
        Binding = binding;
        Property = property;
        Literal = literal;
        Function = function;
        Arguments = arguments is null ? null : Array.AsReadOnly(arguments.ToArray());
        if (Arguments?.Any(argument => argument is null) == true)
        {
            throw new ArgumentException("Expression arguments cannot be null.", nameof(arguments));
        }
        Depth = Arguments is { Count: > 0 } ? Arguments.Max(argument => argument.Depth) + 1 : 0;
        if (Depth > 32)
        {
            throw new ArgumentException("Scalar expression trees cannot exceed 32 operation levels.");
        }
    }

    /// <summary>Gets the matched variable referenced by a property leaf.</summary>
    public string? Binding
    {
        get;
    }

    /// <summary>Gets the property name referenced by a property leaf.</summary>
    public string? Property
    {
        get;
    }

    /// <summary>Gets the value of a literal leaf.</summary>
    public QueryValue? Literal
    {
        get;
    }

    /// <summary>Gets the function or operator name for a non-leaf expression.</summary>
    public string? Function
    {
        get;
    }

    /// <summary>Gets the immutable ordered operation arguments, or null for a leaf.</summary>
    public IReadOnlyList<GraphScalarExpression>? Arguments
    {
        get;
    }

    /// <summary>Gets the longest path of operation nodes beneath this expression.</summary>
    public int Depth
    {
        get;
    }

    /// <summary>Enumerates property bindings needed to evaluate the expression, including repeated references.</summary>
    public IEnumerable<string> Bindings => Binding is not null ? [Binding] :
        Arguments?.SelectMany(argument => argument.Bindings) ?? [];

    /// <summary>Enumerates constant and property leaves for dependency planning.</summary>
    public IEnumerable<GraphScalarExpression> Leaves => Function is null ? [this] : Arguments!.SelectMany(argument => argument.Leaves);
}

/// <summary>Retains graph matches whose scalar expression evaluates to true; false or null excludes a match.</summary>
public sealed record GraphPatternExpressionFilter : GraphPatternFilter
{
    /// <summary>Creates a scalar-expression predicate.</summary>
    /// <param name="expression">The boolean expression to evaluate for each match.</param>
    public GraphPatternExpressionFilter(GraphScalarExpression expression)
    {
        Expression = expression ?? throw new ArgumentNullException(nameof(expression));
    }

    /// <summary>Gets the expression evaluated against each candidate match.</summary>
    public GraphScalarExpression Expression
    {
        get;
    }
}
