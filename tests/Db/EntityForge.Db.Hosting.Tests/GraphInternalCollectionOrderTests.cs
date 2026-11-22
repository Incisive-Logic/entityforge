using EntityForge.Db.Graph;
using EntityForge.Query.Querying;
using EntityForge.Query.Values;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class GraphInternalCollectionOrderTests
{
    [TestMethod]
    [DataRow("COLL.MIN")]
    [DataRow("COLL.MAX")]
    [DataRow("COLL.SORT")]
    public void Internal_collection_order_handles_deep_mixed_containers(string function)
    {
        QueryValue first = Integer(1), second = Integer(2);
        int levels = function == "COLL.SORT" ? 2000 : 9999;
        for (int depth = 0; depth < levels; depth++)
        {
            first = Wrap(first);
            second = Wrap(second);
            QueryValue Wrap(QueryValue value) => depth % 2 == 0 ? new ListValue([value]) :
                new MapValue([new KeyValuePair<string, QueryValue>("item", value)]);
        }
        var actual = Evaluate(function, [second, first]);
        if (function == "COLL.SORT")
        {
            var sorted = (ListValue)actual;
            Assert.AreSame(first, sorted.Items[0]);
            Assert.AreSame(second, sorted.Items[1]);
        }
        else
        {
            Assert.AreSame(function == "COLL.MIN" ? first : second, actual);
        }
    }

    [TestMethod]
    public void Internal_collection_order_preserves_map_key_precedence_and_list_prefixes()
    {
        QueryValue[] ordered = [
            new MapValue([]),
            Map(("a", Integer(9))),
            Map(("z", Integer(0))),
            Map(("b", Integer(2)), ("a", Integer(1))),
            Map(("a", Integer(1)), ("b", Integer(3))),
            new ListValue([]),
            new ListValue([Integer(1)]),
            new ListValue([Integer(1), Integer(0)]),
            new ListValue([Integer(2)]),
            Integer(0), NullValue.Instance];
        var sorted = (ListValue)Evaluate("COLL.SORT", ordered.Reverse().ToArray());
        for (int index = 0; index < ordered.Length; index++)
        {
            Assert.AreSame(ordered[index], sorted.Items[index]);
        }
    }

    [TestMethod]
    [DataRow("COLL.MIN")]
    [DataRow("COLL.MAX")]
    public void Internal_collection_extrema_share_work_and_observe_cancellation(string function)
    {
        var wide = new ListValue(Enumerable.Repeat<QueryValue>(Integer(1), 1000));
        var repeated = Enumerable.Repeat<QueryValue>(wide, 102).ToArray();
        var error = Assert.ThrowsExactly<ArgumentException>(() => Evaluate(function, repeated));
        StringAssert.Contains(error.Message, "100,000");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => Evaluate(function, [Integer(1)],
            new GraphScalarEvaluator.IterationBudget { Cancellation = cancellation.Token }));
    }

    [TestMethod]
    public void Internal_collection_sort_checks_result_cardinality_before_comparison_work()
    {
        var budget = new GraphScalarEvaluator.IterationBudget();
        for (int index = 0; index < 100_000; index++)
        {
            budget.Consume();
        }
        var values = Enumerable.Repeat<QueryValue>(Integer(1), 10_001).ToArray();
        var error = Assert.ThrowsExactly<ArgumentException>(() => Evaluate("COLL.SORT", values, budget));
        Assert.AreEqual("2202F", error.Data["EntityForge.GqlStatus"]);
    }

    private static MapValue Map(params (string Key, QueryValue Value)[] pairs) =>
        new(pairs.Select(pair => new KeyValuePair<string, QueryValue>(pair.Key, pair.Value)));

    private static LiteralValue Integer(int value) => new(value.ToString(System.Globalization.CultureInfo.InvariantCulture), QueryScalarKind.Integer);

    private static QueryValue Evaluate(string function, QueryValue[] values, GraphScalarEvaluator.IterationBudget? budget = null) =>
        GraphScalarEvaluator.Evaluate(new GraphScalarExpression(null, null, null, function, [new(null, null, new ListValue(values))]),
            (_, _) => throw new AssertFailedException("Constant expressions must not read graph properties."), budget);
}
