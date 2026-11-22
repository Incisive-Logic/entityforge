using System.Diagnostics;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Kernel;
using EntityForge.Db.Mapping;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class TimestampEnduranceTests
{
    private sealed class Clock : IDatabaseBoundedClock
    {
        public Guid Domain { get; } = Guid.NewGuid();

        public DatabaseClockInterval Read()
        {
            long now = DateTime.UtcNow.Ticks;
            return new(now, now);
        }
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2048)]
    public async Task Timestamp_history_survives_repeated_pinned_writes_checkpoint_reclamation_and_reopen(int vertices)
    {
        const int epochs = 64, writesPerEpoch = 128;
        string path = Path.Combine(Path.GetTempPath(), "ef-timestamp-endurance-" + Guid.NewGuid().ToString("N"));
        var clock = new Clock();
        var id = GraphVertexId.New();
        long peakHeap = 0, peakWorkingSet = 0;
        long started = Stopwatch.GetTimestamp(), allocated = GC.GetTotalAllocatedBytes(true);
        try
        {
            for (int cycle = 0; cycle < 4; cycle++)
            {
                await using var graph = await GraphDatabase.OpenLocalAsync(new(path)
                {
                    VersioningMode = DatabaseVersioningMode.PartitionLocal,
                    TimestampClock = clock
                });
                var kernel = ((GraphDatabaseEngine)graph).PartitionKernel;
                async Task Commit(params GraphMutation[] mutations)
                {
                    Guid operation = EntityOperationIds.New();
                    await kernel.CommitWithReceiptAsync(operation, GraphTransactionCodec.Encode(new(operation, mutations)));
                }
                if (cycle == 0)
                {
                    var seed = new List<GraphMutation> { new CreateVertex(id, []), new SetVertexProperty(id, "value", new DatabaseValue.String("initial")) };
                    for (int item = 1; item < vertices; item++)
                    {
                        var other = GraphVertexId.New();
                        seed.Add(new CreateVertex(other, ["base"]));
                        seed.Add(new SetVertexProperty(other, "padding", new DatabaseValue.String(new string('x', 256))));
                    }
                    await Commit(seed.ToArray());
                }
                for (int epoch = cycle * (epochs / 4); epoch < (cycle + 1) * (epochs / 4); epoch++)
                {
                    string prior = epoch == 0 ? "initial" : $"{epoch - 1}:{writesPerEpoch - 1}";
                    using (var pin = await kernel.PartitionTransactions.CaptureSnapshotAsync())
                    {
                        var time = DatabaseTimestampReadClock.Read(clock.Domain, clock);
                        await kernel.PartitionTransactions.CaptureTimestampProjectionAsync(pin, time, null);
                        for (int index = 0; index < writesPerEpoch; index++)
                        {
                            await Commit(new SetVertexProperty(id, "value", new DatabaseValue.String($"{epoch}:{index}")));
                        }

                        var view = await kernel.PartitionTransactions.CaptureTimestampProjectionAsync(pin, time, null);
                        using (kernel.PartitionTransactions.EnterTimestampProjection(view))
                        {
                            Assert.AreEqual(new DatabaseValue.String(prior), (await graph.GetVertexAsync(id, graph.CurrentVersion))!.Properties["value"]);
                        }
                    }
                    var cutoff = DateTimeOffset.UtcNow;
                    Assert.AreEqual(cutoff.ToUnixTimeMilliseconds(), await kernel.PartitionTransactions.RetainAsync(cutoff));
                    peakHeap = Math.Max(peakHeap, GC.GetTotalMemory(false));
                    using (var process = Process.GetCurrentProcess())
                    {
                        peakWorkingSet = Math.Max(peakWorkingSet, process.WorkingSet64);
                    }

                    Assert.AreEqual(0, kernel.TimestampProtectionCount);
                }
                Assert.AreEqual(new DatabaseValue.String($"{(cycle + 1) * (epochs / 4) - 1}:{writesPerEpoch - 1}"),
                    (await graph.GetVertexAsync(id, graph.CurrentVersion))!.Properties["value"]);
            }
            Console.WriteLine($"vertices={vertices};epochs={epochs};writes={epochs * writesPerEpoch};reopen_cycles=4;peak_managed_bytes={peakHeap};peak_working_set_bytes={peakWorkingSet};allocated_bytes={GC.GetTotalAllocatedBytes(true) - allocated};elapsed_ms={Stopwatch.GetElapsedTime(started).TotalMilliseconds}");
        }
        finally
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
    }

    [TestMethod]
    public async Task Native_history_capacity_rejection_preserves_old_reader_and_releases_after_drainage()
    {
        string path = Path.Combine(Path.GetTempPath(), "ef-timestamp-capacity-" + Guid.NewGuid().ToString("N"));
        var clock = new Clock();
        var id = GraphVertexId.New();
        try
        {
            await using var graph = await GraphDatabase.OpenLocalAsync(new(path)
            {
                VersioningMode = DatabaseVersioningMode.PartitionLocal,
                TimestampClock = clock
            });
            var kernel = ((GraphDatabaseEngine)graph).PartitionKernel;
            Guid seed = Guid.NewGuid();
            await kernel.CommitAsync(seed, GraphTransactionCodec.Encode(new(seed, [new CreateVertex(id, [])])));
            using var pin = await kernel.PartitionTransactions.CaptureSnapshotAsync();
            var time = DatabaseTimestampReadClock.Read(clock.Domain, clock);
            await kernel.PartitionTransactions.CaptureTimestampProjectionAsync(pin, time, null);
            for (int index = 0; index < DatabaseLogicalHistory<object, object>.MaximumVersions; index++)
            {
                Guid operation = EntityOperationIds.New();
                await kernel.CommitAsync(operation, GraphTransactionCodec.Encode(new(operation,
                    [new SetVertexProperty(id, "value", new DatabaseValue.String(index.ToString()))])));
            }
            Guid rejected = Guid.NewGuid();
            var payload = GraphTransactionCodec.Encode(new(rejected, [new SetVertexProperty(id, "value", new DatabaseValue.String("after-drain"))]));
            var before = kernel.CurrentVersion;
            var failure = await Assert.ThrowsExceptionAsync<EntityForgeDbCommitException>(async () => await kernel.CommitAsync(rejected, payload));
            Assert.IsInstanceOfType<InvalidOperationException>(failure.InnerException);
            Assert.AreEqual(before, kernel.CurrentVersion);
            Assert.AreEqual(DatabaseKernelState.Ready, kernel.State);
            var old = await kernel.PartitionTransactions.CaptureTimestampProjectionAsync(pin, time, null);
            using (kernel.PartitionTransactions.EnterTimestampProjection(old))
            {
                Assert.IsFalse((await graph.GetVertexAsync(id, graph.CurrentVersion))!.Properties.ContainsKey("value"));
            }

            pin.Dispose();
            await kernel.CommitAsync(rejected, payload);
            Assert.AreEqual(new DatabaseValue.String("after-drain"), (await graph.GetVertexAsync(id, graph.CurrentVersion))!.Properties["value"]);
        }
        finally
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
    }

}
