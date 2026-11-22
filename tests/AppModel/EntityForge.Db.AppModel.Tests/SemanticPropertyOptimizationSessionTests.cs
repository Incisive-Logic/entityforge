using EntityForge.Db.AppModel.Optimizations;
using EntityForge.Db.Semantic;
using EntityForge.Semantics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EntityForge.Db.AppModel.Tests;

[TestClass]
public sealed class SemanticPropertyOptimizationSessionTests
{
    [TestMethod]
    public async Task Session_exposes_analysis_and_refreshes_after_one_bounded_change()
    {
        var client = new StubClient();
        var session = new SemanticPropertyOptimizationSession();
        session.Bind(client);

        await session.RefreshAsync();
        Assert.AreEqual(SemanticPropertyOptimizationState.Ready, session.State);
        Assert.AreEqual(1, session.Report!.Recommendations.Count);

        await session.ApplyNextAsync();
        Assert.AreEqual(1, client.MaximumChanges);
        Assert.AreEqual(0, session.Report!.Recommendations.Count);
        Assert.AreEqual(2, client.AnalysisCount);
    }

    [TestMethod]
    public async Task Reset_clears_evidence_and_reloads_the_report()
    {
        var client = new StubClient();
        var session = new SemanticPropertyOptimizationSession();
        session.Bind(client);

        await session.RefreshAsync();
        await session.ResetObservationsAsync();

        Assert.IsTrue(client.WasReset);
        Assert.AreEqual(0, session.Report!.Observations.Count);
    }

    [TestMethod]
    public async Task Old_report_is_ignored_after_rebinding()
    {
        using var session = new SemanticPropertyOptimizationSession();
        var delayed = new StubClient { PendingAnalysis = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        session.Bind(delayed);
        Task pending = session.RefreshAsync().AsTask();
        session.Bind(null);
        delayed.PendingAnalysis.SetResult(await new StubClient().AnalyzeAsync());
        await pending;
        Assert.AreEqual(SemanticPropertyOptimizationState.Unavailable, session.State);
        Assert.IsNull(session.Report);
    }

    [TestMethod]
    public async Task Old_mutation_completion_cannot_refresh_or_change_a_new_session()
    {
        using var session = new SemanticPropertyOptimizationSession();
        var client = new StubClient { PendingApply = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        session.Bind(client);
        await session.RefreshAsync();
        Task pending = session.ApplyNextAsync().AsTask();
        session.Bind(new StubClient());
        await session.RefreshAsync();
        var expected = session.Report;
        client.PendingApply.SetResult(new SemanticPropertyOptimizationResult(expected!, []));
        await pending;
        Assert.AreSame(expected, session.Report);
        Assert.AreEqual(OptimizationMutationOutcome.None, session.MutationOutcome);
        Assert.AreEqual(1, client.AnalysisCount);
    }

    [TestMethod]
    public async Task Confirmed_mutation_is_not_reported_as_failed_when_refresh_fails()
    {
        using var session = new SemanticPropertyOptimizationSession();
        var client = new StubClient { FailRefreshAfterApply = true };
        session.Bind(client);
        await session.RefreshAsync();
        await session.ApplyNextAsync();
        Assert.AreEqual(OptimizationMutationOutcome.Confirmed, session.MutationOutcome);
        Assert.AreEqual(SemanticPropertyOptimizationState.Failed, session.State);
        StringAssert.Contains(session.Error!, "server confirmed");
        Assert.IsNull(session.Report);
        Assert.IsFalse(session.CanMutate);
    }

    [TestMethod]
    public async Task Unknown_mutation_cannot_be_retried_even_after_report_refresh()
    {
        using var session = new SemanticPropertyOptimizationSession();
        var client = new StubClient { CancelApply = true };
        session.Bind(client);
        await session.RefreshAsync();
        await session.ApplyNextAsync();
        Assert.AreEqual(OptimizationMutationOutcome.Unknown, session.MutationOutcome);
        Assert.AreEqual(SemanticPropertyOptimizationState.Cancelled, session.State);
        await session.RefreshAsync();
        await session.ApplyNextAsync();
        Assert.AreEqual(1, client.ApplyCount);
        Assert.IsFalse(session.CanMutate);
        session.Bind(null, preserveUnknownOutcome: true);
        session.Bind(new StubClient(), preserveUnknownOutcome: true);
        await session.RefreshAsync();
        Assert.AreEqual(OptimizationMutationOutcome.Unknown, session.MutationOutcome);
        Assert.IsFalse(session.CanMutate, "Rechecking the connection must not silently authorize retrying an uncertain mutation.");
    }

    [TestMethod]
    public async Task Mutation_requires_a_successful_analysis_and_cancelled_analysis_discards_late_result()
    {
        using var session = new SemanticPropertyOptimizationSession();
        var client = new StubClient { PendingAnalysis = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        session.Bind(client);
        await session.ApplyNextAsync();
        Assert.AreEqual(0, client.ApplyCount);
        Task pending = session.RefreshAsync().AsTask();
        session.Cancel();
        client.PendingAnalysis.SetResult(await new StubClient().AnalyzeAsync());
        await pending;
        Assert.AreEqual(SemanticPropertyOptimizationState.Cancelled, session.State);
        Assert.IsNull(session.Report);
    }

    private sealed class StubClient : ISemanticPropertyOptimizationClient
    {
        private bool _applied;

        public int AnalysisCount
        {
            get; private set;
        }

        public int MaximumChanges
        {
            get; private set;
        }

        public bool WasReset
        {
            get; private set;
        }

        public bool FailRefreshAfterApply
        {
            get; init;
        }

        public bool CancelApply
        {
            get; init;
        }

        public int ApplyCount
        {
            get; private set;
        }

        public TaskCompletionSource<SemanticPropertyOptimizationReport>? PendingAnalysis
        {
            get; init;
        }

        public TaskCompletionSource<SemanticPropertyOptimizationResult>? PendingApply
        {
            get; init;
        }

        public ValueTask<SemanticPropertyOptimizationReport> AnalyzeAsync(
            CancellationToken cancellationToken = default)
        {
            AnalysisCount++;
            if (PendingAnalysis is not null)
            {
                return new(PendingAnalysis.Task);
            }

            if (_applied && FailRefreshAfterApply)
            {
                throw new HttpRequestException("refresh failed");
            }

            SemanticPropertyWorkloadObservation[] observations = WasReset ? [] : [Observation()];
            SemanticPropertyOptimizationRecommendation[] recommendations = _applied || WasReset ? [] :
                [new(Property(), SemanticPropertyStorageKind.ColdJson,
                    SemanticPropertyStorageKind.IndexedJson, 0.9, "Frequently selective equality queries.", Observation())];
            return ValueTask.FromResult(new SemanticPropertyOptimizationReport(
                DateTimeOffset.UnixEpoch, observations, recommendations, observations.Length, 4096, 0, false));
        }

        public ValueTask<SemanticPropertyOptimizationResult> ApplyAsync(int maximumChanges = 1,
            CancellationToken cancellationToken = default)
        {
            MaximumChanges = maximumChanges;
            ApplyCount++;
            if (PendingApply is not null)
            {
                return new(PendingApply.Task);
            }

            if (CancelApply)
            {
                throw new OperationCanceledException("timeout after submission");
            }

            _applied = true;
            return ValueTask.FromResult(new SemanticPropertyOptimizationResult(
                new(DateTimeOffset.UnixEpoch, [Observation()], [], 1, 4096, 0, false), []));
        }

        public ValueTask ResetObservationsAsync(CancellationToken cancellationToken = default)
        {
            WasReset = true;
            return ValueTask.CompletedTask;
        }

        private static SemanticTerm Property() => new(new Uri("urn:entityforge:test:name"), SemanticTermRole.Property);

        private static SemanticPropertyWorkloadObservation Observation() => new(Property(), 50, 0, 2,
            5000, 50, TimeSpan.FromMilliseconds(12), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
    }
}
