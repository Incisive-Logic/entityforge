using System.Diagnostics.Metrics;

namespace EntityForge.Db.AccessPaths;

// Test-oracle instrumentation; production selected-format telemetry lives in Storage.Pages.
internal static class OrderedAccessPathTelemetry
{
    internal const string MeterName = "EntityForge.Db.AccessPaths";
    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Splits = Meter.CreateCounter<long>("entityforge.db.access_path.splits");
    private static readonly Counter<long> Merges = Meter.CreateCounter<long>("entityforge.db.access_path.merges");
    private static readonly Counter<long> Seeks = Meter.CreateCounter<long>("entityforge.db.access_path.seeks");
    private static readonly Counter<long> Pages = Meter.CreateCounter<long>("entityforge.db.access_path.pages_returned");
    private static readonly Histogram<long> PageItems = Meter.CreateHistogram<long>(
        "entityforge.db.access_path.page_items", unit: "{item}");

    internal static void Split() => Splits.Add(1);

    internal static void Merge() => Merges.Add(1);

    internal static void Seek() => Seeks.Add(1);

    internal static void Page(int itemCount)
    {
        Pages.Add(1);
        PageItems.Record(itemCount);
    }
}
