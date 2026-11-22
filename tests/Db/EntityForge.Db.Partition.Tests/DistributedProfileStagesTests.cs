using System.Collections.Concurrent;
using System.Diagnostics.Tracing;

namespace EntityForge.Db.Partition.Tests;

[TestClass]
public sealed class DistributedProfileStagesTests
{
    private sealed class Listener : EventListener
    {
        internal readonly ConcurrentQueue<(int Event, long Id, string? Stage)> Events = new();

        protected override void OnEventWritten(EventWrittenEventArgs e)
        {
            if (e.EventSource.Name != "EntityForge-ProfileStages")
            {
                return;
            }

            if (e.EventId == 0)
            {
                throw new InvalidOperationException("Invalid profiling event schema.");
            }

            Events.Enqueue((e.EventId, Convert.ToInt64(e.Payload![0]), e.EventId == 1 ? (string)e.Payload[1]! : null));
        }
    }

    [TestMethod]
    public async Task Profile_markers_pair_across_await_and_exception_without_collecting_unselected_stages()
    {
        using var listener = new Listener();
        listener.EnableEvents(DistributedProfileStages.Log, EventLevel.Informational);
        using (DistributedReadTelemetry.Measure("partition.application.combined-selection"))
        {
            await Task.Yield();
            try
            {
                using var inner = DistributedReadTelemetry.Measure("partition.selection.encode");
                throw new InvalidOperationException();
            }
            catch (InvalidOperationException)
            {

            }
            using var excluded = DistributedReadTelemetry.Measure("unselected-stage");
        }
        var events = listener.Events.ToArray();
        Assert.AreEqual(4, events.Length);
        Assert.AreEqual(2, events.Select(e => e.Id).Distinct().Count());
        foreach (var group in events.GroupBy(e => e.Id))
        {
            CollectionAssert.AreEqual(new[] { 1, 2 }, group.Select(e => e.Event).ToArray());
        }

        Assert.IsTrue(events.Where(e => e.Event == 1).All(e => e.Stage is
            "partition.application.combined-selection" or "partition.selection.encode"));
        listener.DisableEvents(DistributedProfileStages.Log);
        Assert.AreEqual(0L, DistributedProfileStages.Log.Enter("partition.selection.encode"));
    }
}
