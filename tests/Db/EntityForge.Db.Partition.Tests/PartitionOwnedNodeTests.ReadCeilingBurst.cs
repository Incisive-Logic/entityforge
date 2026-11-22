using System.Diagnostics;
using EntityForge.Db.Durability;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Kernel;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [TestMethod]
    [DataRow(1, 16)]
    [DataRow(250, 13)]
    [DataRow(450, 3)]
    [DataRow(500, 0)]
    public async Task Timestamp_append_read_burst_preserves_exact_durable_ceiling(int ageMilliseconds, int expectedImmediate)
    {
        using var visibility = TimestampVisibility();
        long now = Stopwatch.GetElapsedTime(0).Ticks;
        DatabaseTimestampCostOptions.QualificationOverride.Value = new(Guid.NewGuid(),
            () => Interlocked.Read(ref now), () => true, TimeSpan.FromMilliseconds(500))
        {
            Visibility = true
        };
        using var observer = new PausedReadAdmissionWal();
        var opened = await OpenCohortStore(DatabaseProfileId.PropertyGraph,
            Path.Combine(_root, "ceiling-burst"), observer);
        await using var database = opened.Database;
        var kernel = opened.Kernel;
        var a = new GraphVertexId(Guid.NewGuid());
        var b = new GraphVertexId(Guid.NewGuid());
        Guid seed = Guid.NewGuid();
        await kernel.CommitAsync(seed, GraphTransactionCodec.Encode(new(seed, [new CreateVertex(a, []), new CreateVertex(b, [])])));
        using var protection = await kernel.PartitionTransactions.ProtectTimestampHistoryAsync();
        var options = kernel.TimestampCostOptions!;
        Interlocked.Add(ref now, TimeSpan.FromMilliseconds(1).Ticks);
        await kernel.CaptureTimestampProjectionAsync(protection, new(options.Domain, now, 0, 0, true), [b.Value.ToString("N")], default);
        long ceiling = kernel.TimestampCostState!.ReservedReadThrough;
        long forces = kernel.TimestampReservationForces;
        long baseline = ceiling - TimeSpan.FromMilliseconds(500).Ticks;
        Interlocked.Exchange(ref now, baseline + TimeSpan.FromMilliseconds(ageMilliseconds).Ticks);
        Guid operation = Guid.NewGuid();
        observer.Armed = true;
        var writing = Task.Run(async () => await kernel.CommitAsync(operation,
            GraphTransactionCodec.Encode(new(operation, [new SetVertexProperty(a, "value", new DatabaseValue.String("changed"))]))));
        var reads = new List<Task<object>>();
        try
        {
            await observer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (int i = 0; i < 16; i++)
            {
                Interlocked.Exchange(ref now, baseline + TimeSpan.FromMilliseconds(ageMilliseconds + 20 * i).Ticks);
                reads.Add(kernel.CaptureTimestampProjectionAsync(protection,
                    new(options.Domain, now, 0, 0, true), [b.Value.ToString("N")], default).AsTask());
                if (i != 15)
                {
                    await Task.Delay(20);
                }
            }
            Assert.IsFalse(writing.IsCompleted);
            Assert.AreEqual(expectedImmediate, reads.Count(read => read.IsCompletedSuccessfully));
            Assert.AreEqual(ceiling, kernel.TimestampCostState!.ReservedReadThrough);
            Assert.AreEqual(forces, kernel.TimestampReservationForces);
            Console.WriteLine($"CeilingAgeMs={ageMilliseconds}; ImmediateReads={expectedImmediate}; BlockedReads={16 - expectedImmediate}; ReservationForcesDuringStall=0");
        }
        finally
        {
            observer.Release.Set();
        }
        await writing.WaitAsync(TimeSpan.FromSeconds(5));
        foreach (object view in await Task.WhenAll(reads).WaitAsync(TimeSpan.FromSeconds(5)))
        {
            using (kernel.EnterTimestampProjection(view))
            {
                Assert.IsNotNull(await ((GraphDatabaseEngine)database).GetVertexAsync(b, kernel.CurrentVersion));
            }
        }
    }
}
