using EntityForge.Db.Graph;
using EntityForge.Query.Querying;
using EntityForge.Query.Values;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class GqlDistinctKeyBudgetTests
{
    [TestMethod]
    public void Gql_structured_distinct_keys_preserve_lengths_order_and_numeric_equivalence()
    {
        var first = new MapValue([
            new("b", new ListValue([NullValue.Instance, new LiteralValue("x")])),
            new("a", new LiteralValue("1", QueryScalarKind.Integer))]);
        var reordered = new MapValue([
            new("a", new LiteralValue("1.0", QueryScalarKind.Decimal)),
            new("b", new ListValue([UnboundValue.Instance, new LiteralValue("x")]))]);
        const string expected = "map:1:a10:number:1/11:b21:list:4:null8:String:x";
        Assert.AreEqual(expected, GraphScalarEvaluator.DistinctKey(first));
        Assert.AreEqual(expected, GraphScalarEvaluator.DistinctKey(reordered));
        Assert.AreEqual("list:5:list:4:map:", GraphScalarEvaluator.DistinctKey(new ListValue([new ListValue([]), new MapValue([])])));
        Assert.AreEqual("map:2:😀4:null", GraphScalarEvaluator.DistinctKey(new MapValue([new("😀", NullValue.Instance)])));
        Assert.AreNotEqual(GraphScalarEvaluator.DistinctKey(new ListValue([new LiteralValue("a"), new LiteralValue("bc")])),
            GraphScalarEvaluator.DistinctKey(new ListValue([new LiteralValue("ab"), new LiteralValue("c")])));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Gql_distinct_keys_encode_deep_values_without_recursive_copying(bool records)
    {
        QueryValue nested = NullValue.Instance;
        for (int depth = 0; depth < 9999; depth++)
        {
            nested = records ? new MapValue([new("x", nested)]) : new ListValue([nested]);
        }
        string key = GraphScalarEvaluator.DistinctKey(nested);
        string prefix = records ? "map:1:x" : "list:";
        int offset = 0;
        for (int depth = 0; depth < 9999; depth++)
        {
            Assert.AreEqual(prefix, key.Substring(offset, prefix.Length));
            offset += prefix.Length;
            int colon = key.IndexOf(':', offset);
            int length = Int32.Parse(key.AsSpan(offset, colon - offset), System.Globalization.CultureInfo.InvariantCulture);
            offset = colon + 1;
            Assert.AreEqual(key.Length - offset, length);
        }
        Assert.AreEqual("null", key[offset..]);
    }

    [TestMethod]
    public void Gql_distinct_key_work_is_bounded_and_shared_by_collection_distinct()
    {
        var item = new LiteralValue("1", QueryScalarKind.Integer);
        var oversized = new ListValue(Enumerable.Repeat<QueryValue>(item, 100_000));
        var error = Assert.ThrowsExactly<ArgumentException>(() => GraphScalarEvaluator.DistinctKey(oversized));
        StringAssert.Contains(error.Message, "100,000");
        var nested = new ListValue(Enumerable.Repeat<QueryValue>(item, 1000));
        var repeated = new ListValue(Enumerable.Repeat<QueryValue>(nested, 101));
        var expression = new GraphScalarExpression(null, null, null, "COLL.DISTINCT", [new(null, null, repeated)]);
        var shared = Assert.ThrowsExactly<ArgumentException>(() => GraphScalarEvaluator.Evaluate(expression,
            (_, _) => throw new AssertFailedException("No property reads expected.")));
        StringAssert.Contains(shared.Message, "100,000");
    }
}
