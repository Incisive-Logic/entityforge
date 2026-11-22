using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using EntityForge.Db.Durability;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [TestMethod]
    public async Task Timestamp_admission_inspects_only_unresolved_preparations_at_retained_catalogue_sizes()
    {
        const int TerminalEntries = 512, ActiveEntries = 8, WarmupAdmissions = 100, MeasuredAdmissions = 2000;
        var authority = new Authority();
        await using var node = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "admission-capacity"),
            DatabaseProfileId.PropertyGraph, authority.Lease, Routing, authority, Options, timestampClock: ReadClock);
        using var protection = await node.Kernel.PartitionTransactions.ProtectTimestampHistoryAsync();
        var observations = new ConcurrentQueue<(string Name, double Milliseconds, int Retained, int Unresolved)>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "EntityForge.Db.Kernel",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.OperationName is "db.timestamp.admission_wait" or "db.timestamp.admission_service" or "db.timestamp.intent_inspection")
                {
                    observations.Enqueue((activity.OperationName, activity.Duration.TotalMilliseconds,
                        activity.GetTagItem("entityforge.retained") as int? ?? 0,
                        activity.GetTagItem("entityforge.unresolved") as int? ?? 0));
                }
            }
        };
        ActivitySource.AddActivityListener(listener);
        var measurements = new List<object>();
        async Task Measure(string phase, int retained, int unresolved)
        {
            var time = PartitionTimestampClock.Read(ReadClock.Domain, ReadClock);
            // A declared unrelated leaf measures candidate inspection, not the
            // required wait for a lower write to that same resource.
            var resources = new HashSet<string> { Guid.NewGuid().ToString("N") };
            for (int i = 0; i < WarmupAdmissions; i++)
            {
                await node.Kernel.CaptureTimestampProjectionAsync(protection, time, resources, default);
            }

            observations.Clear();
            long reservations = node.Kernel.TimestampReservationForces;
            long start = Stopwatch.GetTimestamp();
            for (int i = 0; i < MeasuredAdmissions; i++)
            {
                await node.Kernel.CaptureTimestampProjectionAsync(protection, time, resources, default);
            }

            double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            var inspected = observations.Where(s => s.Name == "db.timestamp.intent_inspection").ToArray();
            Assert.AreEqual(MeasuredAdmissions, inspected.Length);
            Assert.IsTrue(inspected.All(s => s.Retained == retained && s.Unresolved == unresolved));
            Assert.AreEqual(unresolved, node.Kernel.UnresolvedTimestampPreparationCount);
            measurements.Add(new
            {
                phase,
                retained,
                unresolved,
                admissions = MeasuredAdmissions,
                elapsedMs = elapsed,
                reservationForces = node.Kernel.TimestampReservationForces - reservations,
                intervals = observations.GroupBy(s => s.Name).Select(group => new
                {
                    name = group.Key,
                    count = group.Count(),
                    meanMs = group.Average(s => s.Milliseconds),
                    p95Ms = group.Select(s => s.Milliseconds).Order().ElementAt((int)Math.Ceiling(group.Count() * .95) - 1)
                }).ToArray()
            });
        }
        async Task<(Guid Operation, string Descriptor)> Prepare()
        {
            Guid operation = Guid.NewGuid();
            string descriptor = new('D', 64);
            var vertex = GraphVertexId.New();
            await node.Kernel.PartitionTransactions.PrepareAsync(operation, descriptor, node.Kernel.CurrentVersion,
                GraphTransactionCodec.Encode(new(operation, [new CreateVertex(vertex, [])], ReadVersion: node.Kernel.CurrentVersion,
                    ReadPredicates: [new VertexRead(vertex)])));
            return (operation, descriptor);
        }
        await Measure("empty", 0, 0);
        for (int i = 0; i < TerminalEntries; i++)
        {
            var item = await Prepare();
            await node.Kernel.PartitionTransactions.CompleteAsync(item.Operation, item.Descriptor, false);
        }
        await Measure("retained-terminal", TerminalEntries, 0);
        var active = new List<(Guid Operation, string Descriptor)>();
        for (int i = 0; i < ActiveEntries; i++)
        {
            active.Add(await Prepare());
        }

        try
        {
            await Measure("active-unrelated-intents", TerminalEntries + ActiveEntries, ActiveEntries);
        }
        finally
        {
            foreach (var item in active)
            {
                await node.Kernel.PartitionTransactions.CompleteAsync(item.Operation, item.Descriptor, false);
            }
        }
        Assert.AreEqual(0, node.Kernel.UnresolvedTimestampPreparationCount);
        if (Environment.GetEnvironmentVariable("ENTITYFORGE_TIMESTAMP_ADMISSION_MEASUREMENT") is { Length: > 0 } path)
        {
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
            {
                classification = "isolated native qualification, instrumented; not distributed throughput or baseline comparison",
                measurements
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
