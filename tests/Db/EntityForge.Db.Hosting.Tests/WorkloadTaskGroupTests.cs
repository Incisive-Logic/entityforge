namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class WorkloadTaskGroupTests
{
    [TestMethod]
    public async Task Synchronous_participants_can_overlap()
    {
        using var stop = new CancellationTokenSource();
        using var entered = new CountdownEvent(2);
        Func<Task> participant = () =>
        {
            entered.Signal();
            Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(10)), "Workers must be dispatched independently.");
            return Task.CompletedTask;
        };
        await WorkloadTaskGroup.RunAsync([participant, participant], stop).WaitAsync(TimeSpan.FromSeconds(15));
    }

    [TestMethod]
    public async Task Failure_cancels_and_drains_siblings_without_hiding_the_failure()
    {
        using var stop = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var draining = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new IOException("Injected workload failure");
        bool drained = false;
        Task run = WorkloadTaskGroup.RunAsync([
            async () =>
            {
                started.SetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, stop.Token);
                }
                finally
                {
                    draining.SetResult();
                    await release.Task;
                    drained = true;
                }
            },
            async () =>
            {
                await started.Task;
                throw failure;
            }
        ], stop);
        try
        {
            await draining.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsTrue(stop.IsCancellationRequested);
            Assert.IsFalse(run.IsCompleted, "The failed group must await sibling cleanup.");
        }
        finally
        {
            release.TrySetResult();
        }
        var actual = await Assert.ThrowsExactlyAsync<IOException>(async () =>
            await run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreSame(failure, actual);
        Assert.IsTrue(drained);
    }

    [TestMethod]
    public async Task Success_does_not_cancel_the_next_measurement_window()
    {
        using var stop = new CancellationTokenSource();
        int completed = 0;
        await WorkloadTaskGroup.RunAsync([
            () =>
            {
                Interlocked.Increment(ref completed);
                return Task.CompletedTask;
            },
            async () =>
            {
                await Task.Yield();
                Interlocked.Increment(ref completed);
            }
        ], stop);
        Assert.AreEqual(2, completed);
        Assert.IsFalse(stop.IsCancellationRequested);
    }

    [TestMethod]
    public async Task Synchronous_failure_is_reported_and_other_participants_are_drained()
    {
        using var stop = new CancellationTokenSource();
        bool called = false;
        var failure = new IOException("Injected synchronous failure");
        var actual = await Assert.ThrowsExactlyAsync<IOException>(async () =>
            await WorkloadTaskGroup.RunAsync([
                () => throw failure,
                async () =>
                {
                    called = true;
                    await Task.Delay(Timeout.InfiniteTimeSpan, stop.Token);
                }
            ], stop));
        Assert.AreSame(failure, actual);
        Assert.IsTrue(called);
    }

    [TestMethod]
    public async Task Cancellation_callback_failure_does_not_erase_the_original_fault()
    {
        using var stop = new CancellationTokenSource();
        var failure = new IOException("Injected workload failure");
        var cancellationFailure = new InvalidOperationException("Injected callback failure");
        using var registration = stop.Token.Register(() => throw cancellationFailure);
        var actual = await Assert.ThrowsExactlyAsync<AggregateException>(async () =>
            await WorkloadTaskGroup.RunAsync([() => throw failure], stop));
        var errors = actual.Flatten().InnerExceptions;
        CollectionAssert.Contains(errors, failure);
        CollectionAssert.Contains(errors, cancellationFailure);
    }
}
