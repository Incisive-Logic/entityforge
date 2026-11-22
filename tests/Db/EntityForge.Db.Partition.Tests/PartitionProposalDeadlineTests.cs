namespace EntityForge.Db.Partition.Tests;

[TestClass]
public sealed class PartitionProposalDeadlineTests
{
    [TestMethod]
    public void Proposal_budget_uses_timer_without_reading_or_comparing_wall_clocks()
    {
        var clock = new ManualTimerProvider();
        using var budget = new PartitionProposalDeadline(PartitionReadCuts.MaximumDuration, default, clock);
        Assert.AreEqual(TimeSpan.FromSeconds(30), clock.Due);
        Assert.IsFalse(budget.Token.IsCancellationRequested);
        clock.Fire();
        Assert.IsTrue(budget.Token.IsCancellationRequested);
    }

    [TestMethod]
    public void Invalid_or_expired_budgets_are_rejected_without_starting_a_timer()
    {
        var clock = new ManualTimerProvider();
        foreach (var duration in new[]
        {
            TimeSpan.Zero, TimeSpan.FromTicks(-1),
            PartitionReadCuts.MaximumDuration + TimeSpan.FromTicks(1), TimeSpan.MaxValue
        })
        {
            Assert.ThrowsExactly<InvalidDataException>(() => new PartitionProposalDeadline(duration, default, clock));
        }

        Assert.IsFalse(clock.Created);
    }

    [TestMethod]
    public void Caller_cancellation_propagates_and_disposal_releases_timer()
    {
        var clock = new ManualTimerProvider();
        using var caller = new CancellationTokenSource();
        using (var budget = new PartitionProposalDeadline(TimeSpan.FromSeconds(1), caller.Token, clock))
        {
            caller.Cancel();
            Assert.IsTrue(budget.Token.IsCancellationRequested);
        }
        Assert.IsTrue(clock.Disposed);
        Assert.ThrowsExactly<OperationCanceledException>(() =>
            new PartitionProposalDeadline(TimeSpan.FromSeconds(1), caller.Token, clock));
    }

    private sealed class ManualTimerProvider : TimeProvider
    {
        private TimerCallback? _callback;
        private object? _state;
        internal TimeSpan Due;
        internal bool Created, Disposed;

        public override DateTimeOffset GetUtcNow() => throw new AssertFailedException("Proposal duration must not depend on wall-clock offset or steps.");

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _callback = callback;
            _state = state;
            Due = dueTime;
            Created = true;
            return new ManualTimer(this);
        }

        internal void Fire() => _callback!(_state);

        private sealed class ManualTimer(ManualTimerProvider owner) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                owner.Due = dueTime;
                return true;
            }

            public void Dispose() => owner.Disposed = true;

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
