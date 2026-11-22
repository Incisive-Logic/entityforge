using EntityForge.Db.Hosting;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class MaintenanceShutdownAdmissionTests
{
    [TestMethod]
    public async Task Shutdown_cancels_new_admission_but_does_not_cancel_admitted_durability_work()
    {
        using var coordinator = new EntityForgeDbAdministrativeJobCoordinator(new());
        using var shutdown = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var job = coordinator.ExecuteAsync(async token =>
        {
            entered.SetResult();
            await finish.Task;
            token.ThrowIfCancellationRequested();
            return 1;
        }, shutdown.Token, drainOnCancellation: true).AsTask();
        await entered.Task;
        shutdown.Cancel();
        await Assert.ThrowsExceptionAsync<TaskCanceledException>(() => coordinator.ExecuteAsync(
            token => ValueTask.FromResult(2), shutdown.Token, drainOnCancellation: true).AsTask());
        finish.SetResult();
        Assert.AreEqual(1, await job);
    }

    [TestMethod]
    public async Task Draining_still_enforces_the_original_administrative_timeout()
    {
        using var coordinator = new EntityForgeDbAdministrativeJobCoordinator(
            new(jobTimeout: TimeSpan.FromMilliseconds(50)));
        using var shutdown = new CancellationTokenSource();
        await Assert.ThrowsExceptionAsync<EntityForgeDbAdministrativeJobTimeoutException>(() =>
            coordinator.ExecuteAsync<int>(async token =>
            {
                shutdown.Cancel();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return 0;
            }, shutdown.Token, drainOnCancellation: true).AsTask());
    }
}
