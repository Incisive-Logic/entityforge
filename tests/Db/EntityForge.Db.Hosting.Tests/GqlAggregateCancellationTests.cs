using EntityForge.Db.Graph;
using EntityForge.Query.Querying;
using EntityForge.Query.Values;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class GqlAggregateCancellationTests
{
    [TestMethod]
    [DataRow(GraphPatternAggregateFunction.Count)]
    [DataRow(GraphPatternAggregateFunction.Sum)]
    [DataRow(GraphPatternAggregateFunction.Collect)]
    [DataRow(GraphPatternAggregateFunction.PercentileContinuous)]
    [DataRow(GraphPatternAggregateFunction.StandardDeviationPopulation)]
    public void Gql_aggregate_input_cancellation_disposes_source_and_preserves_request_token(GraphPatternAggregateFunction function)
    {
        foreach (bool distinct in new[] { false, true })
        {
            using var cancellation = new CancellationTokenSource();
            bool disposed = false;
            int yielded = 0;
            IEnumerable<QueryValue> Input()
            {
                try
                {
                    yielded++;
                    yield return new LiteralValue("1", QueryScalarKind.Integer);
                    cancellation.Cancel();
                    yielded++;
                    yield return new LiteralValue("2", QueryScalarKind.Integer);
                    yielded++;
                    yield return new LiteralValue("3", QueryScalarKind.Integer);
                }
                finally
                {
                    disposed = true;
                }
            }
            var aggregate = Plan(function, distinct);
            var error = Assert.ThrowsExactly<OperationCanceledException>(() =>
                BoundedGraphPatternExecutor.AggregateValues(aggregate, Input(), cancellation.Token));
            Assert.AreEqual(cancellation.Token, error.CancellationToken);
            Assert.AreEqual(2, yielded);
            Assert.IsTrue(disposed);
            Assert.IsNotNull(BoundedGraphPatternExecutor.AggregateValues(aggregate,
                [new LiteralValue("1", QueryScalarKind.Integer), new LiteralValue("2", QueryScalarKind.Integer)], default));
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Gql_aggregate_cancellation_on_source_completion_does_not_return_a_partial_result(bool empty)
    {
        using var cancellation = new CancellationTokenSource();
        bool disposed = false;
        IEnumerable<QueryValue> Input()
        {
            try
            {
                if (!empty)
                {
                    yield return new LiteralValue("1", QueryScalarKind.Integer);
                }
                cancellation.Cancel();
            }
            finally
            {
                disposed = true;
            }
        }
        Assert.ThrowsExactly<OperationCanceledException>(() =>
            BoundedGraphPatternExecutor.AggregateValues(Plan(GraphPatternAggregateFunction.Sum, false), Input(), cancellation.Token));
        Assert.IsTrue(disposed);
    }

    [TestMethod]
    public void Gql_precancelled_aggregate_does_not_enumerate_input()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        bool enumerated = false;
        IEnumerable<QueryValue> Input()
        {
            enumerated = true;
            yield return new LiteralValue("1", QueryScalarKind.Integer);
        }
        Assert.ThrowsExactly<OperationCanceledException>(() =>
            BoundedGraphPatternExecutor.AggregateValues(Plan(GraphPatternAggregateFunction.Sum, false), Input(), cancellation.Token));
        Assert.IsFalse(enumerated);
    }

    private static GraphPatternAggregate Plan(GraphPatternAggregateFunction function, bool distinct) =>
        new(function, "result", distinct: distinct,
            expression: new(null, null, new LiteralValue("1", QueryScalarKind.Integer)),
            percentile: function == GraphPatternAggregateFunction.PercentileContinuous ? 0.5m : null);
}
