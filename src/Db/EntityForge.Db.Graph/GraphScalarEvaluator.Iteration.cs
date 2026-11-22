using EntityForge.Query.Querying;
using EntityForge.Query.Values;

namespace EntityForge.Db.Graph;

internal static partial class GraphScalarEvaluator
{
    private static QueryValue EvaluateReduction(GraphScalarExpression expression,
        Func<string, string, QueryValue> property, IterationBudget budget) =>
        // This callback is synchronous: the ValueTask completes before returning.
        EvaluateReductionAsync(expression, property, budget, (item, scope) => new(EvaluateCore(item, scope, budget))).GetAwaiter().GetResult();

    internal static async ValueTask<QueryValue> EvaluateReductionAsync(GraphScalarExpression expression,
        Func<string, string, QueryValue> property, IterationBudget budget,
        Func<GraphScalarExpression, Func<string, string, QueryValue>, ValueTask<QueryValue>> evaluate)
    {
        var arguments = expression.Arguments!;
        bool all = expression.Function == "ALLREDUCE";
        if (arguments.Count != (all ? 6 : 5) || arguments[2].Literal is not LiteralValue accumulatorName ||
            arguments[3].Literal is not LiteralValue itemName)
        {
            throw new ArgumentException("REDUCE has an invalid argument shape.");
        }
        QueryValue input = await evaluate(arguments[0], property).ConfigureAwait(false);
        if (input is NullValue or UnboundValue)
        {
            return UnboundValue.Instance;
        }
        if (input is not ListValue list)
        {
            throw new ArgumentException("REDUCE requires a list.");
        }
        QueryValue accumulator = await evaluate(arguments[1], property).ConfigureAwait(false);
        QueryValue initial = accumulator;
        bool unknown = false;
        foreach (var item in list.Items)
        {
            budget.Consume();
            QueryValue ScopedProperty(string binding, string name) => binding != "$iteration" ? property(binding, name) :
                name == accumulatorName.LexicalValue ? accumulator : name == itemName.LexicalValue ? item : property(binding, name);
            accumulator = await evaluate(arguments[4], ScopedProperty).ConfigureAwait(false);
            if (all)
            {
                if (initial is not (UnboundValue or NullValue) && accumulator is not (UnboundValue or NullValue) &&
                    (initial.GetType() != accumulator.GetType() || initial is LiteralValue first && accumulator is LiteralValue next && first.ScalarKind != next.ScalarKind))
                {
                    throw new ArgumentException("ALLREDUCE must preserve the accumulator's value type.");
                }
                QueryValue predicate = await evaluate(arguments[5], ScopedProperty).ConfigureAwait(false);
                if (predicate is not (NullValue or UnboundValue or LiteralValue { ScalarKind: QueryScalarKind.Boolean }))
                {
                    throw new ArgumentException("ALLREDUCE requires a Boolean or null predicate.");
                }
                bool? accepted = Boolean(predicate);
                if (accepted == false)
                {
                    return new LiteralValue("false", QueryScalarKind.Boolean);
                }
                unknown |= accepted is null;
            }
        }
        return !all ? accumulator : unknown ? UnboundValue.Instance : new LiteralValue("true", QueryScalarKind.Boolean);
    }

    internal sealed class IterationBudget
    {
        private int _items;

        internal Func<GraphElementIdentity, QueryValue>? ResolveNode
        {
            get; init;
        }

        internal Action<long>? ChargePathWork
        {
            get; init;
        }

        internal CancellationToken Cancellation
        {
            get; init;
        }

        internal void Consume()
        {
            Cancellation.ThrowIfCancellationRequested();
            if (++_items > 100_000)
            {
                throw new ArgumentException("List iteration exceeds 100,000 examined elements per expression.");
            }
        }
    }

    private static QueryValue EvaluateIteration(GraphScalarExpression expression,
        Func<string, string, QueryValue> property, IterationBudget budget) =>
        // Share the iteration rules with captured asynchronous read subqueries.
        EvaluateIterationAsync(expression, property, budget, (item, scope) => new(EvaluateCore(item, scope, budget))).GetAwaiter().GetResult();

    internal static async ValueTask<QueryValue> EvaluateIterationAsync(GraphScalarExpression expression,
        Func<string, string, QueryValue> property, IterationBudget budget,
        Func<GraphScalarExpression, Func<string, string, QueryValue>, ValueTask<QueryValue>> evaluate)
    {
        var arguments = expression.Arguments!;
        bool comprehension = expression.Function == "COMPREHENSION";
        if (arguments.Count != (comprehension ? 4 : 3) || arguments[1].Literal is not LiteralValue variable)
        {
            throw new ArgumentException("A list iteration has an invalid argument shape.");
        }
        QueryValue input = await evaluate(arguments[0], property).ConfigureAwait(false);
        if (input is NullValue or UnboundValue)
        {
            return UnboundValue.Instance;
        }
        if (input is not ListValue list)
        {
            throw new ArgumentException("List iteration requires a list.");
        }
        var output = new List<QueryValue>();
        bool unknown = false;
        int matched = 0;
        QueryValue BooleanResult(bool value) => new LiteralValue(value ? "true" : "false", QueryScalarKind.Boolean);
        foreach (var item in list.Items)
        {
            budget.Consume();
            QueryValue ScopedProperty(string binding, string name) => binding == "$iteration" && name == variable.LexicalValue
                ? item : property(binding, name);
            QueryValue predicate = await evaluate(arguments[2], ScopedProperty).ConfigureAwait(false);
            if (predicate is not (NullValue or UnboundValue or LiteralValue { ScalarKind: QueryScalarKind.Boolean }))
            {
                throw new ArgumentException("A list predicate must evaluate to a Boolean or null.");
            }
            bool? accepted = Boolean(predicate);
            unknown |= accepted is null;
            if (accepted == true)
            {
                matched++;
                if (comprehension)
                {
                    output.Add(await evaluate(arguments[3], ScopedProperty).ConfigureAwait(false));
                }
                else if (expression.Function is "ANY" or "NONE")
                {
                    return BooleanResult(expression.Function == "ANY");
                }
                else if (expression.Function == "SINGLE" && matched > 1)
                {
                    return BooleanResult(false);
                }
            }
            else if (accepted == false && expression.Function == "ALL")
            {
                return BooleanResult(false);
            }
        }
        if (comprehension)
        {
            return BoundedList(output);
        }
        return unknown ? UnboundValue.Instance : BooleanResult(expression.Function switch
        {
            "ALL" or "NONE" => true,
            "SINGLE" => matched == 1,
            _ => false
        });
    }
}
