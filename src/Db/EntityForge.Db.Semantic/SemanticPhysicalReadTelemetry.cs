using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace EntityForge.Db.Semantic;

internal static class SemanticPhysicalReadTelemetry
{
    private static readonly Meter Meter = new("EntityForge.Db.Query");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>(
        "entityforge.db.semantic.query.stage.duration", "ms");

    private static readonly Histogram<long> AllocatedBytes = Meter.CreateHistogram<long>(
        "entityforge.db.semantic.query.stage.allocated_bytes", "By");

    internal static Measurement Start(string stage) => new(stage,
        Stopwatch.GetTimestamp(), GC.GetTotalAllocatedBytes(precise: false));

    internal readonly struct Measurement(string stage, long started, long allocatedBefore) : IDisposable
    {
        public void Dispose()
        {
            TagList tags = new() { { "entityforge.db.stage", stage } };
            Duration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds, tags);
            AllocatedBytes.Record(Math.Max(0, GC.GetTotalAllocatedBytes(false) - allocatedBefore), tags);
        }
    }
}
