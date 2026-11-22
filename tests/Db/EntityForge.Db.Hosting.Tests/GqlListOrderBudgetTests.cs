using EntityForge.Db.Graph;
using EntityForge.Query.Querying;
using EntityForge.Query.Values;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class GqlListOrderBudgetTests
{
    [TestMethod]
    [DataRow(-1)]
    [DataRow(0)]
    [DataRow(1)]
    public void Gql_order_compares_deep_lists_without_recursive_calls(int expected)
    {
        QueryValue left = Integer(expected > 0 ? 2 : 1);
        QueryValue right = Integer(expected < 0 ? 2 : 1);
        for (int depth = 0; depth < 9999; depth++)
        {
            left = new ListValue([left]);
            right = new ListValue([right]);
        }
        Assert.AreEqual(expected, Math.Sign(GraphScalarEvaluator.CompareForOrder(left, right)));
        Assert.AreEqual(-expected, Math.Sign(GraphScalarEvaluator.CompareForOrder(right, left)));
    }

    [TestMethod]
    public void Gql_list_order_is_lexicographic_with_nested_lengths_and_nulls()
    {
        QueryValue[] ordered = [
            new ListValue([]),
            new ListValue([new ListValue([])]),
            new ListValue([new ListValue([Integer(1)])]),
            new ListValue([new ListValue([Integer(1), Integer(2)])]),
            new ListValue([new ListValue([Integer(1), NullValue.Instance])]),
            new ListValue([Integer(1)]),
            new ListValue([Integer(1), Integer(0)]),
            new ListValue([Integer(2)]),
            new ListValue([NullValue.Instance])];
        for (int first = 0; first < ordered.Length; first++)
        {
            for (int second = 0; second < ordered.Length; second++)
            {
                Assert.AreEqual(Math.Sign(first.CompareTo(second)),
                    Math.Sign(GraphScalarEvaluator.CompareForOrder(ordered[first], ordered[second])));
            }
        }
        Assert.ThrowsExactly<ArgumentException>(() => GraphScalarEvaluator.CompareForOrder(
            new ListValue([new MapValue([])]), new ListValue([new MapValue([])])));
    }

    [TestMethod]
    public void Gql_list_order_bounds_work_without_losing_early_comparison_results()
    {
        var values = Enumerable.Repeat<QueryValue>(Integer(1), 100_000).ToArray();
        var first = new ListValue(values);
        var second = new ListValue(values);
        var error = Assert.ThrowsExactly<ArgumentException>(() => GraphScalarEvaluator.CompareForOrder(first, second));
        StringAssert.Contains(error.Message, "100,000");
        values[0] = Integer(0);
        var early = new ListValue(values);
        Assert.AreEqual(-1, Math.Sign(GraphScalarEvaluator.CompareForOrder(early, second)));
        Assert.AreEqual(1, Math.Sign(GraphScalarEvaluator.CompareForOrder(second, early)));
    }

    private static LiteralValue Integer(int value) => new(value.ToString(System.Globalization.CultureInfo.InvariantCulture), QueryScalarKind.Integer);

    [TestMethod]
    public void Gql_list_order_accepts_exact_work_budget_and_recovers_after_rejection()
    {
        // One traversal step per element, plus the list-completion step.
        var exact = new ListValue(Enumerable.Repeat<QueryValue>(Integer(1), 99_999));
        Assert.AreEqual(0, GraphScalarEvaluator.CompareForOrder(exact, exact));
        var oversized = new ListValue(Enumerable.Repeat<QueryValue>(Integer(1), 100_000));
        Assert.ThrowsExactly<ArgumentException>(() => GraphScalarEvaluator.CompareForOrder(oversized, oversized));
        Assert.AreEqual(0, GraphScalarEvaluator.CompareForOrder(exact, exact));
        Assert.AreEqual(-1, Math.Sign(GraphScalarEvaluator.CompareForOrder(
            new ListValue([Integer(1)]), new ListValue([Integer(2)]))));
    }
}
