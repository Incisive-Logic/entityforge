using EntityForge.Db.Graph;
using EntityForge.Query.Querying;
using EntityForge.Query.Values;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class GqlAggregateListOrderBudgetTests
{
    [TestMethod]
    [DataRow(GraphPatternAggregateFunction.Minimum)]
    [DataRow(GraphPatternAggregateFunction.Maximum)]
    public void Gql_aggregate_extrema_compare_deep_lists_without_recursion(GraphPatternAggregateFunction function)
    {
        QueryValue first = Integer(1), second = Integer(2);
        for (int depth = 0; depth < 9999; depth++)
        {
            first = new ListValue([first]);
            second = new ListValue([second]);
        }
        Assert.AreEqual(-1, Math.Sign(BoundedGraphPatternExecutor.CompareAggregateValues(first, second)));
        Assert.AreEqual(1, Math.Sign(BoundedGraphPatternExecutor.CompareAggregateValues(second, first)));
        Assert.AreEqual(0, BoundedGraphPatternExecutor.CompareAggregateValues(first, first));
        var aggregate = new BoundedGraphPatternExecutor.StreamingAggregate(function);
        aggregate.Add(second);
        aggregate.Add(first);
        Assert.AreSame(function == GraphPatternAggregateFunction.Minimum ? first : second, aggregate.Finish());
    }

    [TestMethod]
    public void Gql_aggregate_list_order_preserves_prefix_null_and_mixed_rank_semantics()
    {
        QueryValue[] ordered = [
            new ListValue([]),
            new ListValue([new ListValue([])]),
            new ListValue([new ListValue([Integer(1)])]),
            new ListValue([new ListValue([Integer(1), NullValue.Instance])]),
            new ListValue([new LiteralValue("a")]),
            new ListValue([Integer(1)]),
            new ListValue([Integer(1), Integer(0)]),
            new ListValue([Integer(2)]),
            new ListValue([NullValue.Instance])];
        for (int left = 0; left < ordered.Length; left++)
        {
            for (int right = 0; right < ordered.Length; right++)
            {
                Assert.AreEqual(Math.Sign(left.CompareTo(right)),
                    Math.Sign(BoundedGraphPatternExecutor.CompareAggregateValues(ordered[left], ordered[right])));
            }
        }
        Assert.ThrowsExactly<ArgumentException>(() => BoundedGraphPatternExecutor.CompareAggregateValues(
            new ListValue([new MapValue([])]), new ListValue([new MapValue([])])));
    }

    [TestMethod]
    public void Gql_aggregate_list_comparison_bounds_work_and_preserves_early_results()
    {
        var exact = new ListValue(Enumerable.Repeat<QueryValue>(Integer(1), 99_999));
        Assert.AreEqual(0, BoundedGraphPatternExecutor.CompareAggregateValues(exact, exact));
        var oversized = new ListValue(Enumerable.Repeat<QueryValue>(Integer(1), 100_000));
        var error = Assert.ThrowsExactly<ArgumentException>(() =>
            BoundedGraphPatternExecutor.CompareAggregateValues(oversized, oversized));
        StringAssert.Contains(error.Message, "100,000");
        Assert.AreEqual(-1, Math.Sign(BoundedGraphPatternExecutor.CompareAggregateValues(new ListValue([Integer(0)]), oversized)));
        Assert.AreEqual(0, BoundedGraphPatternExecutor.CompareAggregateValues(exact, exact));
        var aggregate = new BoundedGraphPatternExecutor.StreamingAggregate(GraphPatternAggregateFunction.Minimum);
        aggregate.Add(oversized);
        Assert.ThrowsExactly<ArgumentException>(() => aggregate.Add(oversized));
    }

    private static LiteralValue Integer(int value) => new(value.ToString(System.Globalization.CultureInfo.InvariantCulture), QueryScalarKind.Integer);
}
