using EntityForge.Db.Kernel;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class DatabaseBoundedClockTests
{
    private sealed class ClockTime : TimeProvider
    {
        internal long Ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Ticks;
    }

    [TestMethod]
    public void Monitored_clock_expands_uncertainty_with_age_and_never_rounds_drift_down()
    {
        var time = new ClockTime { Ticks = 10 };
        var clock = new MonitoredDatabaseClock(Guid.NewGuid(), time,
            () => new(1000, 1100, 0, true), TimeSpan.FromSeconds(1), TimeSpan.FromTicks(200), 1);
        Assert.AreEqual(new DatabaseClockInterval(1008, 1112), clock.Read());
    }

    [TestMethod]
    public void Monitored_clock_rejects_stale_unhealthy_future_and_excessive_error_samples()
    {
        var time = new ClockTime();
        var sample = new DatabaseClockObservation(1000, 1100, 0, true);
        var clock = new MonitoredDatabaseClock(Guid.NewGuid(), time, () => sample,
            TimeSpan.FromTicks(100), TimeSpan.FromTicks(200), 1);
        time.Ticks = 101;
        Assert.ThrowsExactly<InvalidOperationException>(() => clock.Read());
        time.Ticks = 0;
        sample = sample with
        {
            Synchronized = false
        };
        Assert.ThrowsExactly<InvalidOperationException>(() => clock.Read());
        sample = sample with
        {
            Synchronized = true,
            MonotonicTimestamp = 1
        };
        Assert.ThrowsExactly<InvalidOperationException>(() => clock.Read());
        sample = sample with
        {
            MonotonicTimestamp = 0,
            LatestUtcTicks = 1201
        };
        Assert.ThrowsExactly<InvalidOperationException>(() => clock.Read());
    }
}
