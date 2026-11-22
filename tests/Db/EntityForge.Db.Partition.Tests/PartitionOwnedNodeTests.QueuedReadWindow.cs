using System.Reflection;
using EntityForge.Db.Kernel;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [TestMethod]
    public async Task Timestamp_queued_read_window_races_never_leak_coordinator_ownership()
    {
        using var visibility = TimestampVisibility();
        using var observer = new PausedReadAdmissionWal();
        var opened = await OpenCohortStore(DatabaseProfileId.PropertyGraph, Path.Combine(_root, "queued-window-races"), observer);
        await using var database = opened.Database;
        var kernel = opened.Kernel;
        var coordinator = (DatabaseCommitCoordinator)typeof(DatabaseKernel).GetField("_commitCoordinator", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(kernel)!;
        var method = typeof(DatabaseKernel).GetMethod("WaitForTimestampCoordinatorAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        for (int i = 0; i < 120; i++)
        {
            await coordinator.WaitAsync();
            var window = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancellation = new CancellationTokenSource();
            var waiting = ((ValueTask<bool>)method.Invoke(kernel, [window.Task, cancellation.Token])!).AsTask();
            Assert.IsFalse(waiting.IsCompleted);
            if (i % 3 == 0)
            {
                window.SetResult();
                coordinator.Release();
            }
            else if (i % 3 == 1)
            {
                coordinator.Release();
                window.SetResult();
            }
            else
            {
                cancellation.Cancel();
                window.SetResult();
                coordinator.Release();
            }
            try
            {
                if (await waiting.WaitAsync(TimeSpan.FromSeconds(2)))
                {
                    coordinator.Release();
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {

            }
            await coordinator.WaitAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.AreEqual(1, coordinator.Snapshot().ActivePublications);
            Assert.AreEqual(0, coordinator.Snapshot().WaitingPublications);
            coordinator.Release();
            Assert.AreEqual(0, coordinator.Snapshot().ActivePublications);
        }
    }

    [TestMethod]
    public async Task Timestamp_read_window_uncontended_admission_cost_probe()
    {
        using var visibility = TimestampVisibility();
        using var observer = new PausedReadAdmissionWal();
        var opened = await OpenCohortStore(DatabaseProfileId.PropertyGraph, Path.Combine(_root, "queued-window-cost"), observer);
        await using var database = opened.Database;
        var kernel = opened.Kernel;
        var coordinator = (DatabaseCommitCoordinator)typeof(DatabaseKernel).GetField("_commitCoordinator", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(kernel)!;
        var method = typeof(DatabaseKernel).GetMethod("WaitForTimestampCoordinatorAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var wait = method.CreateDelegate<Func<Task, CancellationToken, ValueTask<bool>>>(kernel);
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Baseline()
        {
            coordinator.WaitAsync().GetAwaiter().GetResult();
            coordinator.Release();
        }
        void Window()
        {
            Assert.IsTrue(wait(signal.Task, default).GetAwaiter().GetResult());
            coordinator.Release();
        }
        for (int i = 0; i < 100; i++)
        {
            Baseline();
            Window();
        }
        const int count = 10000;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < count; i++)
        {
            Baseline();
        }

        long baseline = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < count; i++)
        {
            Window();
        }

        long window = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.AreEqual(0, coordinator.Snapshot().ActivePublications);
        Assert.AreEqual(0, coordinator.Snapshot().WaitingPublications);
        Console.WriteLine($"Admissions={count}; BaselineBytes={baseline}; WindowBytes={window}; ExtraBytesPerAdmission={(window - baseline) / count}");
    }
}
