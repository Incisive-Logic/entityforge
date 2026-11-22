using System.Diagnostics;
using EntityForge.Db.Durability;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Kernel;
using EntityForge.Db.Storage.Pages;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    private sealed class PausedReadAdmissionWal : IRedoDurabilityObserver, IDisposable
    {
        internal bool Armed;
        internal bool FailOnRelease;
        internal Func<bool>? ShouldPause;
        internal TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ManualResetEventSlim Release = new();

        public void Reached(RedoDurabilityStage stage)
        {
            if (!Armed || stage != RedoDurabilityStage.BeforeTailPublication || ShouldPause?.Invoke() == false)
            {
                return;
            }

            Armed = false;
            Entered.TrySetResult();
            if (!Release.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("Paused WAL was not released.");
            }

            if (FailOnRelease)
            {
                throw new IOException("Injected append failure after concurrent read.");
            }
        }

        public void Dispose() => Release.Dispose();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Timestamp_read_admission_bypasses_unrelated_append_but_preserves_conflict_and_ceiling_guards(bool fail)
    {
        using var visibility = TimestampVisibility();
        long offset = 0;
        DatabaseTimestampCostOptions.QualificationOverride.Value = new(Guid.NewGuid(), () => Stopwatch.GetElapsedTime(0).Ticks + Interlocked.Read(ref offset), () => true, TimeSpan.FromSeconds(1))
        {
            Visibility = true
        };
        using var observer = new PausedReadAdmissionWal { FailOnRelease = fail };
        var opened = await OpenCohortStore(DatabaseProfileId.PropertyGraph, Path.Combine(_root, "read-admission-isolation"), observer);
        await using var database = opened.Database;
        var kernel = opened.Kernel;
        var a = new GraphVertexId(Guid.NewGuid());
        var b = new GraphVertexId(Guid.NewGuid());
        Guid seed = Guid.NewGuid();
        await kernel.CommitAsync(seed, GraphTransactionCodec.Encode(new(seed, [new CreateVertex(a, []), new CreateVertex(b, [])])));
        using var protection = await kernel.PartitionTransactions.ProtectTimestampHistoryAsync();
        var options = kernel.TimestampCostOptions!;
        var before = new DurableTimestampState(options.Domain, options.Now(), 0, 0, true);
        object oldView = await kernel.CaptureTimestampProjectionAsync(protection, before, [b.Value.ToString("N")], default);
        Guid write = Guid.NewGuid();
        observer.Armed = true;
        var pending = Task.Run(async () => await kernel.CommitAsync(write,
            GraphTransactionCodec.Encode(new(write, [new SetVertexProperty(a, "name", new DatabaseValue.String("after"))]))));
        try
        {
            await observer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(pending.IsCompleted, "A write must not acknowledge before its force.");
            using (kernel.EnterTimestampProjection(oldView))
            {
                Assert.IsNotNull(await ((GraphDatabaseEngine)database).GetVertexAsync(b, kernel.CurrentVersion));
            }
            // Historical unscoped views are safe only strictly before every append.
            Assert.IsNotNull(await kernel.CaptureTimestampProjectionAsync(protection, before, null, default));
            var appendTimes = (DurableTimestampState?[])typeof(DatabaseKernel).GetField("_appendReadTimes", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(kernel)!;
            var equalTime = appendTimes.Single()!;
            using (var equalCancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(75)))
            {
                await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                    await kernel.CaptureTimestampProjectionAsync(protection, equalTime, null, equalCancel.Token));
            }
            // Exercise the admission decision for every member, including mixed
            // timestamp groups. The physical writer stays paused throughout.
            var timeField = typeof(DatabaseKernel).GetField("_appendReadTimes", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            var footprintField = typeof(DatabaseKernel).GetField("_appendReadFootprints", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            var originalFootprints = (DatabaseTransactionFootprint[])footprintField.GetValue(kernel)!;
            try
            {
                footprintField.SetValue(kernel, new[] { originalFootprints[0], originalFootprints[0] });
                foreach (var times in new DurableTimestampState?[][]
                {
                    [equalTime, before], [before, equalTime], [equalTime, null], [null, equalTime]
                })
                {
                    timeField.SetValue(kernel, times);
                    using var mixedCancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
                    await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                        await kernel.CaptureTimestampProjectionAsync(protection, before, null, mixedCancel.Token));
                }
                timeField.SetValue(kernel, new DurableTimestampState?[] { equalTime, equalTime });
                Assert.IsNotNull(await kernel.CaptureTimestampProjectionAsync(protection, before, null, default));
            }
            finally
            {
                footprintField.SetValue(kernel, originalFootprints);
                timeField.SetValue(kernel, appendTimes);
            }
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            var time = new DurableTimestampState(options.Domain, options.Now(), 0, 0, true);
            var unrelated = await kernel.CaptureTimestampProjectionAsync(protection, time, [b.Value.ToString("N")], cancel.Token);
            using (kernel.EnterTimestampProjection(unrelated))
            {
                Assert.IsNotNull(await ((GraphDatabaseEngine)database).GetVertexAsync(b, kernel.CurrentVersion));
            }

            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                await kernel.CaptureTimestampProjectionAsync(protection, time, [a.Value.ToString("N")], cancel.Token));
            Interlocked.Add(ref offset, TimeSpan.FromSeconds(2).Ticks);
            using var rollover = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            var expired = new DurableTimestampState(options.Domain, options.Now(), 0, 0, true);
            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                await kernel.CaptureTimestampProjectionAsync(protection, expired, [b.Value.ToString("N")], rollover.Token));
            Assert.IsFalse(pending.IsCompleted);
        }
        finally
        {
            observer.Release.Set();
        }
        if (fail)
        {
            await Assert.ThrowsAsync<Exception>(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.IsNull(typeof(DatabaseKernel).GetField("_appendReadFootprints", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(kernel));
            Assert.AreEqual(DatabaseKernelState.RecoveryRequired, kernel.State);
            return;
        }
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        var after = new DurableTimestampState(options.Domain, options.Now(), 0, 0, true);
        Assert.IsNotNull(await kernel.CaptureTimestampProjectionAsync(protection, after, [b.Value.ToString("N")], default));
        Assert.AreEqual(DatabaseKernelState.Ready, kernel.State);
    }
}
