using EntityForge.Db.Kernel;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class KernelLifecycleTests
{
    [TestMethod]
    public async Task Commit_coordinator_closes_admission_and_drains_active_publication_once()
    {
        var coordinator = new DatabaseCommitCoordinator();
        await coordinator.WaitAsync();

        Task firstDisposal = coordinator.DisposeAsync().AsTask();
        Task secondDisposal = coordinator.DisposeAsync().AsTask();
        Assert.IsFalse(firstDisposal.IsCompleted);
        await Assert.ThrowsExceptionAsync<ObjectDisposedException>(async () =>
            await coordinator.WaitAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));

        coordinator.Release();
        await Task.WhenAll(firstDisposal, secondDisposal).WaitAsync(TimeSpan.FromSeconds(2));
    }

    [TestMethod]
    public async Task Maintenance_disposal_waits_for_active_job_and_rejects_queued_work()
    {
        var coordinator = new DatabaseMaintenanceCoordinator(() => true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task active = coordinator.ExecuteAsync("reindex", async (_, _, _) =>
        {
            entered.TrySetResult();
            await release.Task;
            return (new DatabaseVersion(1), 1);
        }).AsTask();
        await entered.Task;

        Task disposal = coordinator.DisposeAsync().AsTask();
        Assert.IsFalse(disposal.IsCompleted);
        await Assert.ThrowsExceptionAsync<ObjectDisposedException>(async () =>
            await coordinator.ExecuteAsync("queued", (_, _, _) =>
                ValueTask.FromResult((new DatabaseVersion(2), 1))).AsTask().WaitAsync(TimeSpan.FromSeconds(2)));

        release.TrySetResult();
        await active;
        await disposal.WaitAsync(TimeSpan.FromSeconds(2));
    }
}
