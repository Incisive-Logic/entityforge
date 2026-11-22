namespace EntityForge.Db.Rdf;

public static partial class RdfSparql
{
    // Scan expressions may use the bound solution and statement functions, but
    // must not accidentally answer EXISTS against an empty scalar-only view.
    private static bool IsScanLocal(ValueExpression? expression) => expression switch
    {
        null or TermValueExpression or EvaluatedValueExpression => true,
        ValueFunctionExpression function => IsScanLocal(function.Argument),
        ArithmeticExpression arithmetic => IsScanLocal(arithmetic.Left) && IsScanLocal(arithmetic.Right),
        CallExpression call => call.Arguments.All(IsScanLocal),
        BooleanValueExpression boolean => IsScanLocal(boolean.Filter),
        ConditionalExpression conditional => IsScanLocal(conditional.Condition) && IsScanLocal(conditional.True) && IsScanLocal(conditional.False),
        _ => false
    };

    private static bool IsScanLocal(FilterExpression expression) => expression switch
    {
        EffectiveBooleanExpression boolean => IsScanLocal(boolean.Value),
        ComparisonExpression comparison => IsScanLocal(comparison.Left) && IsScanLocal(comparison.Right),
        BinaryBooleanExpression boolean => IsScanLocal(boolean.Left) && IsScanLocal(boolean.Right),
        NotExpression not => IsScanLocal(not.Operand),
        FunctionExpression function => IsScanLocal(function.First) && IsScanLocal(function.Second) && IsScanLocal(function.Third),
        InExpression member => IsScanLocal(member.Operand) && member.Values.All(IsScanLocal),
        _ => false
    };
}
