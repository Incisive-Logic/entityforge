using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace EntityForge.Db.Graph;

internal static class GraphOpenTelemetry
{
    internal const string MeterName = "EntityForge.Db.Graph";
    internal const string PhaseDurationInstrument = "entityforge.db.graph.open.phase.duration";
    private static readonly Meter Meter = new(MeterName);
    private static readonly Histogram<double> PhaseDuration =
        Meter.CreateHistogram<double>(PhaseDurationInstrument, "ms");

    internal static void Phase(string phase, long started) => PhaseDuration.Record(
        Stopwatch.GetElapsedTime(started).TotalMilliseconds,
        new KeyValuePair<string, object?>("entityforge.db.phase", phase));
}
