using EntityForge.Db.Graph;
using EntityForge.Query.Querying;
using EntityForge.Query.Values;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class GraphInternalFlattenBudgetTests
{
    [TestMethod]
    public void Internal_flatten_handles_deep_input_without_recursive_calls()
    {
        QueryValue value = Integer(7);
        for (int depth = 0; depth < 9999; depth++)
        {
            value = new ListValue([value]);
        }
        var result = Flatten(new ListValue([value]), Int64.MaxValue);
        CollectionAssert.AreEqual(new QueryValue[] { Integer(7) }, result.Items.ToArray());
    }

    [TestMethod]
    public void Internal_flatten_preserves_depth_order_nulls_and_empty_lists()
    {
        var nested = new ListValue([Integer(2), new ListValue([Integer(3)]), NullValue.Instance]);
        var input = new ListValue([Integer(1), new ListValue([]), nested, Integer(4)]);
        CollectionAssert.AreEqual(input.Items.ToArray(), Flatten(input, 0).Items.ToArray());
        CollectionAssert.AreEqual(new QueryValue[] { Integer(1), Integer(2), nested.Items[1], NullValue.Instance, Integer(4) },
            Flatten(input, 1).Items.ToArray());
        CollectionAssert.AreEqual(new QueryValue[] { Integer(1), Integer(2), Integer(3), NullValue.Instance, Integer(4) },
            Flatten(input, 2).Items.ToArray());
    }

    [TestMethod]
    public void Internal_flatten_charges_work_even_when_no_values_are_produced()
    {
        var empty = new ListValue([]);
        Assert.AreEqual(0, Flatten(new ListValue(Enumerable.Repeat<QueryValue>(empty, 100_000)), 1).Items.Count);
        var error = Assert.ThrowsExactly<ArgumentException>(() =>
            Flatten(new ListValue(Enumerable.Repeat<QueryValue>(empty, 100_001)), 1));
        StringAssert.Contains(error.Message, "100,000");
    }

    [TestMethod]
    public void Internal_flatten_observes_expression_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var budget = new GraphScalarEvaluator.IterationBudget { Cancellation = cancellation.Token };
        var input = new ListValue([new ListValue([])]);
        Assert.AreEqual(0, Flatten(input, 1, budget).Items.Count);
        cancellation.Cancel();
        var error = Assert.ThrowsExactly<OperationCanceledException>(() => Flatten(input, 1, budget));
        Assert.AreEqual(cancellation.Token, error.CancellationToken);
    }

    private static LiteralValue Integer(long value) => new(value.ToString(System.Globalization.CultureInfo.InvariantCulture), QueryScalarKind.Integer);

    private static ListValue Flatten(ListValue input, long depth, GraphScalarEvaluator.IterationBudget? budget = null)
    {
        var expression = new GraphScalarExpression(null, null, null, "COLL.FLATTEN",
            [new(null, null, input), new(null, null, Integer(depth))]);
        return (ListValue)GraphScalarEvaluator.Evaluate(expression,
            (_, _) => throw new AssertFailedException("Constant expressions must not read graph properties."), budget);
    }
}
