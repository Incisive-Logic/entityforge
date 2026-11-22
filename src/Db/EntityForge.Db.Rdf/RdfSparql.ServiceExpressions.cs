using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Rdf;

public static partial class RdfSparql
{
    private static async ValueTask<RdfTerm?> EvaluateServiceValueAsync(RdfSparqlReadView database,
        ValueExpression expression, IReadOnlyDictionary<string, RdfTerm> row, ServiceExecution execution)
    {
        database.CheckCancellation();
        if (!Services(expression).Any())
        {
            return EvaluateValue(expression, row, database);
        }
        async ValueTask<ValueExpression> Value(ValueExpression value) => new EvaluatedValueExpression(
            await EvaluateServiceValueAsync(database, value, row, execution).ConfigureAwait(false));
        switch (expression)
        {
            case BooleanValueExpression boolean:
                return BooleanTerm(await EvaluateServiceFilterAsync(database, boolean.Filter, row, execution).ConfigureAwait(false));
            case ConditionalExpression conditional:
                var condition = await EvaluateServiceFilterAsync(database, conditional.Condition, row, execution).ConfigureAwait(false);
                return condition == FilterResult.Error ? null : await EvaluateServiceValueAsync(database,
                    condition == FilterResult.True ? conditional.True : conditional.False, row, execution).ConfigureAwait(false);
            case CallExpression { Name: "COALESCE" } coalesce:
                foreach (var argument in coalesce.Arguments)
                {
                    if (await EvaluateServiceValueAsync(database, argument, row, execution).ConfigureAwait(false) is { } value)
                    {
                        return value;
                    }
                }
                return null;
            case CallExpression call:
                var arguments = new List<ValueExpression>(call.Arguments.Count);
                foreach (var argument in call.Arguments)
                {
                    arguments.Add(await Value(argument).ConfigureAwait(false));
                }
                return EvaluateValue(call with
                {
                    Arguments = arguments
                }, row, database);
            case ValueFunctionExpression function:
                return EvaluateValue(function with
                {
                    Argument = await Value(function.Argument).ConfigureAwait(false)
                }, row, database);
            case ArithmeticExpression arithmetic:
                return EvaluateValue(arithmetic with
                {
                    Left = await Value(arithmetic.Left).ConfigureAwait(false),
                    Right = await Value(arithmetic.Right).ConfigureAwait(false)
                }, row, database);
            default:
                throw new InvalidOperationException("A federated scalar expression was not lowered before execution.");
        }
    }

    private static async ValueTask<FilterResult> EvaluateServiceFilterAsync(RdfSparqlReadView database,
        FilterExpression expression, IReadOnlyDictionary<string, RdfTerm> row, ServiceExecution execution)
    {
        database.CheckCancellation();
        if (!Services(expression).Any())
        {
            return Evaluate(database, expression, row);
        }
        async ValueTask<ValueExpression> Value(ValueExpression value) => new EvaluatedValueExpression(
            await EvaluateServiceValueAsync(database, value, row, execution).ConfigureAwait(false));
        switch (expression)
        {
            case ExistsExpression exists:
                var found = await EvaluateServiceGroupAsync(database, exists.Group,
                    [new Dictionary<string, RdfTerm>(row, StringComparer.Ordinal)], row, execution).ConfigureAwait(false);
                return Result((found.Count > 0) != exists.Negated);
            case EffectiveBooleanExpression boolean:
                return EffectiveBoolean(await EvaluateServiceValueAsync(database, boolean.Value, row, execution).ConfigureAwait(false));
            case NotExpression not:
                return (await EvaluateServiceFilterAsync(database, not.Operand, row, execution).ConfigureAwait(false)) switch
                {
                    FilterResult.True => FilterResult.False,
                    FilterResult.False => FilterResult.True,
                    _ => FilterResult.Error
                };
            case BinaryBooleanExpression binary:
                var left = await EvaluateServiceFilterAsync(database, binary.Left, row, execution).ConfigureAwait(false);
                if (binary.Operator == "&&" && left == FilterResult.False || binary.Operator == "||" && left == FilterResult.True)
                {
                    return left;
                }
                var right = await EvaluateServiceFilterAsync(database, binary.Right, row, execution).ConfigureAwait(false);
                return binary.Operator == "&&" ? And(left, right) : Or(left, right);
            case ComparisonExpression comparison:
                return EvaluateComparison(comparison with
                {
                    Left = await Value(comparison.Left).ConfigureAwait(false),
                    Right = await Value(comparison.Right).ConfigureAwait(false)
                }, row, database);
            case FunctionExpression function:
                return EvaluateFunction(function with
                {
                    First = await Value(function.First).ConfigureAwait(false),
                    Second = function.Second is null ? null : await Value(function.Second).ConfigureAwait(false),
                    Third = function.Third is null ? null : await Value(function.Third).ConfigureAwait(false)
                }, row, database);
            case InExpression membership:
                var operand = await Value(membership.Operand).ConfigureAwait(false);
                if (operand is EvaluatedValueExpression { Value: null })
                {
                    return FilterResult.Error;
                }
                bool error = false;
                foreach (var candidate in membership.Values)
                {
                    var candidateComparison = EvaluateComparison(new(operand, "=", await Value(candidate).ConfigureAwait(false)), row, database);
                    if (candidateComparison == FilterResult.True)
                    {
                        return Result(!membership.Negated);
                    }
                    error |= candidateComparison == FilterResult.Error;
                }
                return error ? FilterResult.Error : Result(membership.Negated);
            default:
                throw new InvalidOperationException("Unsupported federated filter expression.");
        }
    }

    private static async ValueTask<List<Dictionary<string, RdfTerm>>> FilterServiceRowsAsync(RdfSparqlReadView database,
        IReadOnlyList<Dictionary<string, RdfTerm>> rows, IReadOnlyList<FilterExpression> filters, ServiceExecution execution, SelectProjection? havingProjection = null)
    {
        var selected = new List<Dictionary<string, RdfTerm>>();
        foreach (var row in rows)
        {
            bool include = true;
            foreach (var filter in filters)
            {
                if (await EvaluateServiceFilterAsync(database, filter, havingProjection is null ? row : HavingRow(row, havingProjection), execution).ConfigureAwait(false) != FilterResult.True)
                {
                    include = false;
                    break;
                }
            }
            if (include)
            {
                AddBounded(selected, row);
            }
        }
        return selected;
    }

    private static async ValueTask<List<Dictionary<string, RdfTerm>>> BindServiceRowsAsync(RdfSparqlReadView database,
        BindElement bind, IReadOnlyList<Dictionary<string, RdfTerm>> rows, ServiceExecution execution)
    {
        var results = new List<Dictionary<string, RdfTerm>>(rows.Count);
        foreach (var row in rows)
        {
            if (row.ContainsKey(bind.Variable))
            {
                throw new InvalidDataException("BIND cannot replace an in-scope variable.");
            }
            var bound = new Dictionary<string, RdfTerm>(row, StringComparer.Ordinal);
            PreserveBlankNodeScope(row, bound);
            if (await EvaluateServiceValueAsync(database, bind.Value, row, execution).ConfigureAwait(false) is { } value)
            {
                bound[bind.Variable] = value;
            }
            AddBounded(results, bound);
        }
        return results;
    }
}
