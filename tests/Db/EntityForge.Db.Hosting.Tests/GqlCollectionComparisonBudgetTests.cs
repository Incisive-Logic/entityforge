using EntityForge.Db.Graph;
using EntityForge.Query.Querying;
using EntityForge.Query.Values;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class GqlCollectionComparisonBudgetTests
{
    [TestMethod]
    [DataRow(false, "equal")]
    [DataRow(true, "equal")]
    [DataRow(false, "different")]
    [DataRow(true, "different")]
    [DataRow(false, "unknown")]
    [DataRow(true, "unknown")]
    public void Gql_deep_collection_comparison_preserves_three_valued_equality(bool records, string comparison)
    {
        QueryValue left = comparison == "unknown" ? NullValue.Instance : Integer(1);
        QueryValue right = comparison == "unknown" ? NullValue.Instance : Integer(comparison == "equal" ? 1 : 2);
        for (int depth = 0; depth < 9999; depth++)
        {
            left = Wrap(left);
            right = Wrap(right);
            QueryValue Wrap(QueryValue value) => records && depth % 2 == 0
                ? new MapValue([new KeyValuePair<string, QueryValue>("item", value)])
                : new ListValue([value]);
        }
        var result = Evaluate(Call("=", left, right));
        if (comparison == "unknown")
        {
            Assert.AreSame(UnboundValue.Instance, result);
        }
        else
        {
            Assert.AreEqual(Boolean(comparison == "equal"), result);
        }
        Assert.AreEqual(Boolean(comparison == "equal"), Evaluate(Call("ISNULL", Evaluate(Call("NULLIF", left, right)))));
    }

    [TestMethod]
    [DataRow("IN")]
    [DataRow("COLL.INDEXOF")]
    public void Gql_collection_membership_shares_work_budget_across_candidates(string function)
    {
        var needle = new ListValue(Enumerable.Repeat<QueryValue>(Integer(1), 1000));
        var miss = new ListValue(needle.Items.Take(999).Append(Integer(2)));
        var candidates = new ListValue(Enumerable.Repeat<QueryValue>(miss, 101));
        GraphScalarExpression Search(ListValue list) => function == "IN"
            ? Call(function, needle, list) : Call(function, list, needle);
        var error = Assert.ThrowsExactly<ArgumentException>(() => Evaluate(Search(candidates)));
        StringAssert.Contains(error.Message, "100,000");
        var earlyMatch = new ListValue(candidates.Items.Prepend(needle));
        Assert.AreEqual(function == "IN" ? Boolean(true) : Integer(0), Evaluate(Search(earlyMatch)));
    }

    [TestMethod]
    public void Gql_collection_comparisons_share_one_expression_budget()
    {
        var list = new ListValue(Enumerable.Repeat<QueryValue>(Integer(1), 60_000));
        var equality = Call("=", list, list);
        Assert.AreEqual(Boolean(true), Evaluate(equality));
        var combined = new GraphScalarExpression(null, null, null, "AND", [equality, equality]);
        var error = Assert.ThrowsExactly<ArgumentException>(() => Evaluate(combined));
        StringAssert.Contains(error.Message, "100,000");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Gql_collection_inequality_dominates_unknown_members(bool unknownFirst)
    {
        QueryValue[] left = [NullValue.Instance, Integer(1)];
        QueryValue[] right = [NullValue.Instance, Integer(2)];
        if (!unknownFirst)
        {
            Array.Reverse(left);
            Array.Reverse(right);
        }
        Assert.AreEqual(Boolean(false), Evaluate(Call("=", new ListValue(left), new ListValue(right))));
        Assert.AreEqual(Boolean(true), Evaluate(Call("<>", new ListValue(left), new ListValue(right))));
    }

    private static LiteralValue Integer(int value) => new(value.ToString(System.Globalization.CultureInfo.InvariantCulture), QueryScalarKind.Integer);

    private static LiteralValue Boolean(bool value) => new(value ? "true" : "false", QueryScalarKind.Boolean);

    private static GraphScalarExpression Call(string function, params QueryValue[] arguments) =>
        new(null, null, null, function, arguments.Select(value => new GraphScalarExpression(null, null, value)).ToArray());

    private static QueryValue Evaluate(GraphScalarExpression expression) => GraphScalarEvaluator.Evaluate(expression,
        (_, _) => throw new AssertFailedException("Constant expressions must not read graph properties."));
}
