using EntityForge.Db.Graph;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class GqlTopKCancellationTests
{
    [TestMethod]
    [DataRow(1)]
    [DataRow(100)]
    public void Gql_group_order_cancellation_stops_heap_and_full_sort_comparisons(int limit)
    {
        using var cancellation = new CancellationTokenSource();
        int comparisons = 0;
        int[] values = Enumerable.Range(0, 128).Reverse().ToArray();
        var error = Assert.ThrowsExactly<OperationCanceledException>(() =>
            BoundedGraphPatternExecutor.SelectOrdered(values, 0, limit, (left, right) =>
            {
                comparisons++;
                cancellation.Cancel();
                return left.CompareTo(right);
            }, cancellation.Token));
        Assert.AreEqual(cancellation.Token, error.CancellationToken);
        Assert.AreEqual(1, comparisons);
        CollectionAssert.AreEqual(Enumerable.Range(0, limit).ToArray(),
            BoundedGraphPatternExecutor.SelectOrdered(values, 0, limit, (left, right) => left.CompareTo(right), default).ToArray());
    }

    [TestMethod]
    public void Gql_group_order_precancellation_is_observed_even_for_an_empty_window()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() =>
            BoundedGraphPatternExecutor.SelectOrdered(Array.Empty<int>(), 0, 0,
                (left, right) => left.CompareTo(right), cancellation.Token));
    }

    [TestMethod]
    [DataRow(3, 7)]
    [DataRow(17, 70)]
    public void Gql_group_order_preserves_duplicate_multiplicity_and_window(int offset, int limit)
    {
        int[] values = Enumerable.Range(0, 128).Select(value => value % 19).Reverse().ToArray();
        CollectionAssert.AreEqual(values.Order().Skip(offset).Take(limit).ToArray(),
            BoundedGraphPatternExecutor.SelectOrdered(values, offset, limit, (left, right) => left.CompareTo(right), default).ToArray());
    }
}
