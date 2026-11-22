using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace EntityForge.Db;

// A native unit is profile-specific (Graph mutation/effect, RDF quad mutation,
// Semantic entity change/evidence effect); counts are not equivalent user entities.
// Durations include child work and waits. Never sum them with kernel parent spans.
internal static class DatabaseProfileWorkTelemetry
{
    internal const string MeterName = "EntityForge.Db.ProfileWork";
    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Units = Meter.CreateCounter<long>("entityforge.db.profile.work.units", "{unit}");
    private static readonly Counter<long> Operations = Meter.CreateCounter<long>("entityforge.db.profile.work.operations", "{operation}");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("entityforge.db.profile.work.duration", "ms");

    internal static bool IsEnabled => Units.Enabled || Operations.Enabled || Duration.Enabled;

    internal static long Start() => Duration.Enabled ? Stopwatch.GetTimestamp() : 0;

    internal static void Record(string profile, string stage, string unit, long count, long started = 0)
    {
        if (!Units.Enabled && !Operations.Enabled && !Duration.Enabled)
        {
            return;
        }

        TagList tags = new()
        {
            { "profile", profile }, { "stage", stage }, { "unit", unit },
            { "parent", "kernel.commit" }, { "timing", "inclusive-wall" }
        };
        Units.Add(count, tags);
        Operations.Add(1, tags);
        if (started != 0)
        {
            Duration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds, tags);
        }
    }
}
