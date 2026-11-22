using System.Globalization;
using EntityForge.Db.Graph;
using EntityForge.Query.Querying;
using EntityForge.Query.Values;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class StreamingGraphAggregateTests
{
    [TestMethod]
    [DataRow(GraphPatternAggregateFunction.Sum)]
    [DataRow(GraphPatternAggregateFunction.Average)]
    public void Stored_integers_match_lexical_accumulation_with_extremes_and_late_type_changes(GraphPatternAggregateFunction function)
    {
        var random = new Random(2917);
        QueryValue[] tails = [UnboundValue.Instance, new LiteralValue("0.5", QueryScalarKind.Decimal),
            new LiteralValue("0", QueryScalarKind.FloatingPoint), new LiteralValue("NaN", QueryScalarKind.FloatingPoint),
            new LiteralValue("P1M", QueryScalarKind.Duration), new LiteralValue("invalid")];
        foreach (var tail in tails)
        {
            var direct = new BoundedGraphPatternExecutor.StreamingAggregate(function);
            var lexical = new BoundedGraphPatternExecutor.StreamingAggregate(function);
            long[] extremes = [Int64.MaxValue, 1, -1, Int64.MinValue, 9007199254740993, -9007199254740992, 0];
            foreach (long value in extremes.Concat(Enumerable.Range(0, 1000).Select(_ => random.NextInt64())))
            {
                direct.AddInteger(value);
                lexical.Add(new LiteralValue(value.ToString(CultureInfo.InvariantCulture), QueryScalarKind.Integer));
            }
            direct.Add(tail);
            lexical.Add(tail);
            // Exercise integers both before and after a non-integer input.
            direct.AddInteger(-17);
            lexical.Add(new LiteralValue("-17", QueryScalarKind.Integer));
            static string Outcome(BoundedGraphPatternExecutor.StreamingAggregate state)
            {
                try
                {
                    return GraphScalarEvaluator.DistinctKey(state.Finish());
                }
                catch (ArgumentException error)
                {
                    return error.GetType().Name + ":" + error.Message;
                }
            }
            Assert.AreEqual(Outcome(lexical), Outcome(direct));
            Assert.AreEqual(lexical.NullEliminated, direct.NullEliminated);
        }
    }

    [TestMethod]
    public void Streaming_sum_keeps_exact_decimal_track_after_large_integer_cancellation()
    {
        var state = new BoundedGraphPatternExecutor.StreamingAggregate(GraphPatternAggregateFunction.Sum);
        state.Add(new LiteralValue("9223372036854775807", QueryScalarKind.Integer));
        state.Add(new LiteralValue("-9223372036854775807", QueryScalarKind.Integer));
        state.Add(new LiteralValue("0.5", QueryScalarKind.Decimal));
        var result = (LiteralValue)state.Finish();
        Assert.AreEqual(QueryScalarKind.Decimal, result.ScalarKind);
        Assert.AreEqual("0.5", result.LexicalValue);
    }

    [TestMethod]
    public void Streaming_sum_keeps_existing_floating_rounding_when_a_float_arrives_late()
    {
        var state = new BoundedGraphPatternExecutor.StreamingAggregate(GraphPatternAggregateFunction.Sum);
        state.Add(new LiteralValue("9007199254740993", QueryScalarKind.Integer));
        state.Add(new LiteralValue("-9007199254740992", QueryScalarKind.Integer));
        state.Add(new LiteralValue("0", QueryScalarKind.FloatingPoint));
        var result = (LiteralValue)state.Finish();
        Assert.AreEqual(QueryScalarKind.FloatingPoint, result.ScalarKind);
        Assert.AreEqual("0", result.LexicalValue);
    }

    [TestMethod]
    public void Streaming_decimal_track_retains_values_outside_int64_and_average_signed_zero()
    {
        var sum = new BoundedGraphPatternExecutor.StreamingAggregate(GraphPatternAggregateFunction.Sum);
        sum.Add(new LiteralValue("9223372036854775808", QueryScalarKind.Integer));
        sum.Add(new LiteralValue("0.5", QueryScalarKind.Decimal));
        Assert.AreEqual("9223372036854775808.5", ((LiteralValue)sum.Finish()).LexicalValue);
        var average = new BoundedGraphPatternExecutor.StreamingAggregate(GraphPatternAggregateFunction.Average);
        average.Add(new LiteralValue("-0", QueryScalarKind.Integer));
        Assert.AreEqual("-0", ((LiteralValue)average.Finish()).LexicalValue);
    }

    [TestMethod]
    [DataRow(GraphPatternAggregateFunction.Count, "100000")]
    [DataRow(GraphPatternAggregateFunction.Sum, "5000050000")]
    [DataRow(GraphPatternAggregateFunction.Average, "50000.5")]
    [DataRow(GraphPatternAggregateFunction.Minimum, "1")]
    [DataRow(GraphPatternAggregateFunction.Maximum, "100000")]
    public void Common_aggregate_consumes_100000_values_with_null_diagnostics(GraphPatternAggregateFunction function, string expected)
    {
        var state = new BoundedGraphPatternExecutor.StreamingAggregate(function);
        for (int value = 1; value <= 100000; value++)
        {
            state.Add(new LiteralValue(value.ToString(CultureInfo.InvariantCulture), QueryScalarKind.Integer));
        }
        state.Add(UnboundValue.Instance);
        state.Add(NullValue.Instance);
        Assert.AreEqual(expected, ((LiteralValue)state.Finish()).LexicalValue);
        Assert.IsTrue(state.NullEliminated);
    }

    [TestMethod]
    public void Streaming_sum_preserves_integer_promotion_and_late_floating_type_selection()
    {
        var integer = new BoundedGraphPatternExecutor.StreamingAggregate(GraphPatternAggregateFunction.Sum);
        integer.Add(new LiteralValue(Int64.MaxValue.ToString(CultureInfo.InvariantCulture), QueryScalarKind.Integer));
        integer.Add(new LiteralValue("1", QueryScalarKind.Integer));
        integer.Add(new LiteralValue("-1", QueryScalarKind.Integer));
        Assert.AreEqual(QueryScalarKind.FloatingPoint, ((LiteralValue)integer.Finish()).ScalarKind);
        var floating = new BoundedGraphPatternExecutor.StreamingAggregate(GraphPatternAggregateFunction.Sum);
        floating.Add(new LiteralValue(Decimal.MaxValue.ToString(CultureInfo.InvariantCulture), QueryScalarKind.Decimal));
        floating.Add(new LiteralValue(Decimal.MaxValue.ToString(CultureInfo.InvariantCulture), QueryScalarKind.Decimal));
        floating.Add(new LiteralValue("0", QueryScalarKind.FloatingPoint));
        var result = (LiteralValue)floating.Finish();
        Assert.AreEqual(QueryScalarKind.FloatingPoint, result.ScalarKind);
        Assert.AreEqual(2 * (double)Decimal.MaxValue, Double.Parse(result.LexicalValue, CultureInfo.InvariantCulture));
    }

    [TestMethod]
    public void Streaming_average_avoids_large_floating_sum_overflow()
    {
        var state = new BoundedGraphPatternExecutor.StreamingAggregate(GraphPatternAggregateFunction.Average);
        state.Add(new LiteralValue("1E308", QueryScalarKind.FloatingPoint));
        state.Add(new LiteralValue("1E308", QueryScalarKind.FloatingPoint));
        Assert.AreEqual(1E308, Double.Parse(((LiteralValue)state.Finish()).LexicalValue, CultureInfo.InvariantCulture));
    }

    [TestMethod]
    public void Streaming_aggregate_preserves_duration_types_and_rejects_mixed_inputs()
    {
        var state = new BoundedGraphPatternExecutor.StreamingAggregate(GraphPatternAggregateFunction.Sum);
        state.Add(new LiteralValue("P1M", QueryScalarKind.Duration));
        state.Add(new LiteralValue("P2M", QueryScalarKind.Duration));
        Assert.AreEqual("P3M", ((LiteralValue)state.Finish()).LexicalValue);
        state.Add(new LiteralValue("1", QueryScalarKind.Integer));
        Assert.ThrowsExactly<ArgumentException>(() => state.Finish());
    }

    [TestMethod]
    public void Streaming_decimal_sum_keeps_overflow_failure_and_empty_sum_is_null()
    {
        var state = new BoundedGraphPatternExecutor.StreamingAggregate(GraphPatternAggregateFunction.Sum);
        Assert.IsInstanceOfType<UnboundValue>(state.Finish());
        state.Add(new LiteralValue(Decimal.MaxValue.ToString(CultureInfo.InvariantCulture), QueryScalarKind.Decimal));
        state.Add(new LiteralValue("1", QueryScalarKind.Decimal));
        Assert.ThrowsExactly<ArgumentException>(() => state.Finish());
    }
}
