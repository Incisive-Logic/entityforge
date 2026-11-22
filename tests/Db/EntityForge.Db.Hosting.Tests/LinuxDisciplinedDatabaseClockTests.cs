using EntityForge.Db.Kernel;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class LinuxDisciplinedDatabaseClockTests
{
    private sealed class Time : TimeProvider
    {
        internal long Now;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Now;
    }

    [TestMethod]
    [DataRow(0, 123456L)]
    [DataRow(0x2000, 123456789L)]
    public void Kernel_bounds_include_reported_error_syscall_duration_and_fraction_rounding(int status, long fraction)
    {
        var time = new Time();
        const long seconds = 1_800_000_000;
        var clock = new LinuxDisciplinedDatabaseClock(Guid.NewGuid(), TimeSpan.FromMilliseconds(10), 100, time,
            () =>
            {
                time.Now += 100;
                return new(0, status, seconds, fraction, 100);
            });
        var bound = clock.Read();
        decimal ticks = DateTime.UnixEpoch.Ticks + seconds * TimeSpan.TicksPerSecond +
            (decimal)fraction * TimeSpan.TicksPerSecond / (status == 0 ? 1_000_000 : 1_000_000_000);
        Assert.IsTrue(bound.EarliestUtcTicks <= ticks - 1000);
        Assert.IsTrue(bound.LatestUtcTicks >= ticks + 1000 + 100);
    }

    [TestMethod]
    [DataRow(5, 0, 10L)]
    [DataRow(1, 0, 10L)]
    [DataRow(0, 0x40, 10L)]
    [DataRow(0, 0x1000, 10L)]
    [DataRow(0, 0, -1L)]
    [DataRow(0, 0, 1_000_000L)]
    public void Unsynchronised_leaping_or_excessively_uncertain_clock_is_rejected(int state, int status, long error)
    {
        var clock = new LinuxDisciplinedDatabaseClock(Guid.NewGuid(), TimeSpan.FromMilliseconds(10), 100,
            new Time(), () => new(state, status, 1_800_000_000, 0, error));
        Assert.ThrowsExactly<InvalidOperationException>(() => clock.Read());
    }

    [TestMethod]
    public void Provider_reobserves_health_on_every_read_and_recovers_without_changing_domain()
    {
        int state = 0;
        Guid domain = Guid.NewGuid();
        var clock = new LinuxDisciplinedDatabaseClock(domain, TimeSpan.FromMilliseconds(10), 100,
            new Time(), () => new(state, 0, 1_800_000_000, 0, 10));
        clock.Read();
        state = 5;
        Assert.ThrowsExactly<InvalidOperationException>(() => clock.Read());
        state = 0;
        clock.Read();
        Assert.AreEqual(domain, clock.Domain);
    }
}
