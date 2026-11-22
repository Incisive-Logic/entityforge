using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace EntityForge.Db.Query;

internal static class SemanticQueryExecutionTelemetry
{
    internal const string MeterName = "EntityForge.Db.Query";
    internal const string DurationInstrument = "entityforge.db.semantic.query.stage.duration";
    internal const string AllocatedBytesInstrument = "entityforge.db.semantic.query.stage.allocated_bytes";
    internal const string StageTag = "entityforge.db.stage";

    private static readonly ActivitySource Source = new(MeterName);
    private static readonly Meter Meter = new(MeterName);
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>(DurationInstrument, "ms");
    private static readonly Histogram<long> AllocatedBytes = Meter.CreateHistogram<long>(AllocatedBytesInstrument, "By");

    internal static StageMeasurement Start(string stage) => new(stage,
        Stopwatch.GetTimestamp(), GC.GetTotalAllocatedBytes(precise: false), Source.StartActivity("semantic.query." + stage));

    internal readonly struct StageMeasurement(string stage, long started, long allocatedBefore, Activity? activity) : IDisposable
    {
        public void Dispose()
        {
            activity?.Dispose();
            TagList tags = new() { { StageTag, stage } };
            Duration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds, tags);
            AllocatedBytes.Record(Math.Max(0, GC.GetTotalAllocatedBytes(precise: false) - allocatedBefore), tags);
        }
    }
}
