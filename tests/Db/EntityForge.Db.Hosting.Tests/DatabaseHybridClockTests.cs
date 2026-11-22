using EntityForge.Db.Kernel;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class DatabaseHybridClockTests
{
    private sealed class ManualTime : TimeProvider
    {
        internal long Now = 0;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Now;
    }

    [TestMethod]
    public void Tightened_independent_interval_preserves_previous_upper_bound_for_commit_wait()
    {
        var time = new ManualTime();
        var sample = new DatabaseClockSample(100, 200, 0, true);
        var clock = new DatabaseHybridClock(time, () => sample, TimeSpan.FromTicks(10),
            TimeSpan.FromTicks(100), default, useUpperBound: true);
        var first = clock.Next();
        sample = new(120, 150, 0, true);
        var second = clock.Next();
        Assert.AreEqual(200L, second.PhysicalTicks);
        Assert.IsTrue(second.CompareTo(first) > 0);
    }

    [TestMethod]
    public void BackwardPhysicalMovementAndRecoveryNeverReuseTime()
    {
        var time = new ManualTime();
        var sample = new DatabaseClockSample(100, 200, 0, true);
        DatabaseHybridClock Open(DatabaseHybridTime recovered = default) =>
            new(time, () => sample, TimeSpan.FromTicks(10), TimeSpan.FromTicks(200), recovered);
        var clock = Open();
        DatabaseHybridTime first = clock.Next();
        sample = new(90, 190, 0, true);
        DatabaseHybridTime second = clock.Next();
        Assert.IsTrue(second.CompareTo(first) > 0);
        Assert.IsTrue(Open(second).Next().CompareTo(second) > 0);
    }

    [TestMethod]
    public void UnhealthyStaleFutureSamplesAndUnboundedIntervalsFailClosed()
    {
        var time = new ManualTime();
        var sample = new DatabaseClockSample(100, 200, 0, true);
        var clock = new DatabaseHybridClock(time, () => sample,
            TimeSpan.FromTicks(10), TimeSpan.FromTicks(100), default);
        foreach (var invalid in new[]
        {
            sample with { Healthy = false }, sample with { SampledAt = -11 },
            sample with { SampledAt = 1 }, sample with { LatestUtcTicks = 201 },
            sample with { EarliestUtcTicks = -1 }, sample with { LatestUtcTicks = 99 }
        })
        {
            sample = invalid;
            Assert.ThrowsExactly<InvalidOperationException>(() => clock.Next());
        }
        sample = new(100, 200, 0, true);
        Assert.ThrowsExactly<InvalidOperationException>(() => clock.Next(new(201, 0)));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            new DatabaseHybridClock(time, () => sample, TimeSpan.FromTicks(10),
                TimeSpan.FromTicks(100), new(201, 0)).Next());
    }

    [TestMethod]
    public void CausalBoundsAndConcurrentIssuanceAreStrictlyMonotonic()
    {
        var time = new ManualTime();
        var clock = new DatabaseHybridClock(time, () => new(100, 200, 0, true),
            TimeSpan.FromTicks(10), TimeSpan.FromTicks(100), default);
        var causal = new DatabaseHybridTime(180, 30);
        Assert.IsTrue(clock.Next(causal).CompareTo(causal) > 0);
        var issued = new System.Collections.Concurrent.ConcurrentBag<DatabaseHybridTime>();
        Parallel.For(0, 1024, _ => issued.Add(clock.Next()));
        Assert.AreEqual(1024, issued.Distinct().Count());
        Assert.IsTrue(issued.All(value => value.CompareTo(causal) > 0));
    }

    [TestMethod]
    public void LogicalOverflowCannotWrapIntoAnEarlierTimestamp()
    {
        var time = new ManualTime();
        var clock = new DatabaseHybridClock(time, () => new(100, 200, 0, true),
            TimeSpan.FromTicks(10), TimeSpan.FromTicks(100), new(180, int.MaxValue));
        Assert.ThrowsExactly<OverflowException>(() => clock.Next());
    }

    [TestMethod]
    public void ExpiredClockCertificateDoesNotBecomeFreshThroughLogicalAdvancement()
    {
        var time = new ManualTime();
        var clock = new DatabaseHybridClock(time, () => new(100, 200, 0, true),
            TimeSpan.FromTicks(10), TimeSpan.FromTicks(100), default);
        clock.Next();
        time.Now = 11;
        Assert.ThrowsExactly<InvalidOperationException>(() => clock.Next());
    }
}
