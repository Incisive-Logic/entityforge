using EntityForge.Db.Query.Planning;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class QueryComparisonSortCancellationTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Query_sort_observes_cancellation_between_comparisons_and_preserves_token(bool list)
    {
        using var cancellation = new CancellationTokenSource();
        int comparisons = 0;
        int Compare(int left, int right)
        {
            comparisons++;
            cancellation.Cancel();
            return left.CompareTo(right);
        }
        int[] input = Enumerable.Range(0, 128).Reverse().ToArray();
        var failure = Assert.ThrowsExactly<OperationCanceledException>(() =>
        {
            if (list)
            {
                QueryComparisonSort.Sort(input.ToList(), Compare, cancellation.Token);
            }
            else
            {
                QueryComparisonSort.Sort(input, Compare, cancellation.Token);
            }
        });
        Assert.AreEqual(1, comparisons, "Do not continue invoking the comparer after cancellation.");
        Assert.AreEqual(cancellation.Token, failure.CancellationToken);
        int[] recovery = [3, 1, 2];
        QueryComparisonSort.Sort(recovery, (left, right) => left.CompareTo(right));
        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, recovery);
    }

    [TestMethod]
    public void Query_sort_does_not_report_success_when_its_final_comparison_cancels()
    {
        using var cancellation = new CancellationTokenSource();
        int comparisons = 0;
        var failure = Assert.ThrowsExactly<OperationCanceledException>(() =>
            QueryComparisonSort.Sort(new[] { 2, 1 }, (left, right) =>
            {
                comparisons++;
                cancellation.Cancel();
                return left.CompareTo(right);
            }, cancellation.Token));
        Assert.AreEqual(1, comparisons);
        Assert.AreEqual(cancellation.Token, failure.CancellationToken);
    }

    [TestMethod]
    public void Query_sort_observes_precancellation_even_without_comparison_work()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        foreach (int[] values in new[] { Array.Empty<int>(), new[] { 1 } })
        {
            var failure = Assert.ThrowsExactly<OperationCanceledException>(() =>
                QueryComparisonSort.Sort(values, (_, _) => throw new AssertFailedException("No comparison expected."), cancellation.Token));
            Assert.AreEqual(cancellation.Token, failure.CancellationToken);
        }
    }
}
