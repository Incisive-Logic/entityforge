using System.Collections;
using EntityForge.Db.Query.Planning;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class CandidatePlannerCancellationTests
{
    [TestMethod]
    public async Task Candidate_planner_precancellation_does_not_select_or_acquire()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var source = new Source();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            new EntityCandidatePlanner<int, int, int>().ExecuteAsync(0, source, cancellationToken: cancellation.Token).AsTask());
        Assert.AreEqual(0, source.Selections);
        Assert.AreEqual(0, source.Acquisitions);
    }

    [TestMethod]
    public async Task Candidate_planner_empty_restriction_skips_acquisition_but_retains_plan()
    {
        var source = new Source();
        var result = await new EntityCandidatePlanner<int, int, int>().ExecuteAsync(0, source, new HashSet<int>());
        Assert.AreEqual(0, source.Acquisitions);
        Assert.AreEqual(0, result.Candidates.Count);
        Assert.AreEqual(0, result.AcquiredCandidateCounts.Count);
        CollectionAssert.AreEqual(new[] { "first", "second" }, result.AccessPaths.ToArray());
        CollectionAssert.AreEqual(new long?[] { 4, 5 }, result.EstimatedCandidateCounts.ToArray());
    }

    [TestMethod]
    public async Task Candidate_planner_cancellation_after_acquisition_does_not_return_success()
    {
        using var cancellation = new CancellationTokenSource();
        var source = new Source { Acquired = cancellation.Cancel };
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            new EntityCandidatePlanner<int, int, int>().ExecuteAsync(0, source, cancellationToken: cancellation.Token).AsTask());
        Assert.AreEqual(1, source.Acquisitions);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Candidate_planner_cancels_during_candidate_enumeration_and_disposes_iterator(bool restricted)
    {
        using var cancellation = new CancellationTokenSource();
        var candidates = new CancellingCandidates(cancellation);
        var source = new Source { Candidates = candidates };
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            new EntityCandidatePlanner<int, int, int>().ExecuteAsync(0, source,
                restricted ? new HashSet<int> { 1, 2, 3, 4 } : null, cancellation.Token).AsTask());
        Assert.IsTrue(candidates.Disposed);
        Assert.AreEqual(1, source.Acquisitions);
    }

    [TestMethod]
    public async Task Candidate_planner_preserves_intersection_and_raw_counts_without_cancellation()
    {
        var source = new Source { Candidates = new[] { 1, 2, 2, 3 } };
        var result = await new EntityCandidatePlanner<int, int, int>().ExecuteAsync(0, source, new HashSet<int> { 2, 3, 9 });
        CollectionAssert.AreEquivalent(new[] { 2, 3 }, result.Candidates.ToArray());
        CollectionAssert.AreEqual(new long[] { 4, 4 }, result.AcquiredCandidateCounts.ToArray());
        Assert.AreEqual(2, source.Acquisitions);
    }

    private sealed class Source : IEntityCandidateSource<int, int, int>
    {
        public int Selections;
        public int Acquisitions;
        public Action? Acquired;
        public IReadOnlyList<int> Candidates = new[] { 1, 2, 3, 4 };

        public IReadOnlyList<EntityCandidateAccess<int>> Select(int query)
        {
            Selections++;
            return [new("first", 0, EstimatedCandidates: 4), new("second", 1, EstimatedCandidates: 5)];
        }

        public EntityCandidateAccess<int> Fallback(int query) => throw new AssertFailedException("Unexpected fallback.");

        public ValueTask<IReadOnlyList<int>> AcquireAsync(int query, EntityCandidateAccess<int> access,
            IReadOnlySet<int>? constraint, CancellationToken cancellationToken = default)
        {
            Acquisitions++;
            Acquired?.Invoke();
            return ValueTask.FromResult(Candidates);
        }
    }

    private sealed class CancellingCandidates(CancellationTokenSource cancellation) : IReadOnlyList<int>
    {
        public bool Disposed;

        public int Count => 4;

        public int this[int index] => index + 1;

        public IEnumerator<int> GetEnumerator()
        {
            try
            {
                yield return 1;
                cancellation.Cancel();
                yield return 2;
                yield return 3;
                yield return 4;
            }
            finally
            {
                Disposed = true;
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
