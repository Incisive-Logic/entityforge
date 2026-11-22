using System.Reflection;
using EntityForge.Db.Graph;
using EntityForge.Db.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    public async Task Cancelled_runtime_stop_does_not_dispose_store_before_maintenance_finishes()
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        var runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
        var field = typeof(EntityForgeDbRuntime).GetField("_maintenance", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var original = (HostedMaintenanceTask)field.GetValue(runtime)!;
        await original.StopAsync(CancellationToken.None);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        field.SetValue(runtime, new HostedMaintenanceTask(_ => release.Task));
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        try
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() => runtime.StopAsync(caller.Token));
            Task disposing = runtime.DisposeAsync().AsTask();
            Assert.IsFalse(disposing.IsCompleted);
            using (var snapshot = await runtime.GraphDatabase.AcquireSnapshotAsync())
            {
                Assert.IsFalse(disposing.IsCompleted, "A subsequent dispose must retain the still-running worker.");
            }
            release.TrySetResult();
            await disposing.WaitAsync(TimeSpan.FromSeconds(5));
            await using var reopened = await GraphDatabase.OpenLocalAsync(new(_directory));
        }
        finally
        {
            release.TrySetResult();
            await runtime.DisposeAsync();
            field.SetValue(runtime, original);
            await host.StopAsync();
        }
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task Completed_maintenance_failure_does_not_retain_hosted_store_locks(bool dispose, bool disposalFails)
    {
        _directory = NewDirectory();
        string other = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .UseGqlGraphs(new("main", new Dictionary<string, string> { ["other"] = other }))
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        var runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
        // Fault injection replaces only a drained worker; no live task is abandoned.
        var field = typeof(EntityForgeDbRuntime).GetField("_maintenance", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var original = (HostedMaintenanceTask)field.GetValue(runtime)!;
        await original.StopAsync(CancellationToken.None);
        var failure = new IOException("Injected completed maintenance failure.");
        var disposalFailure = new IOException("Injected store disposal failure.");
        field.SetValue(runtime, new HostedMaintenanceTask(_ => Task.FromException(failure)));
        if (disposalFails)
        {
            var profileField = typeof(EntityForgeDbRuntime).GetField("_hostedProfile", BindingFlags.Instance | BindingFlags.NonPublic)!;
            profileField.SetValue(runtime, new MaintenanceFaultProfile((IHostedDatabaseProfile)profileField.GetValue(runtime)!, disposalFailure));
        }
        try
        {
            var observed = await Assert.ThrowsAsync<AggregateException>(() => dispose
                ? runtime.DisposeAsync().AsTask() : runtime.StopAsync(CancellationToken.None));
            Assert.IsTrue(observed.Flatten().InnerExceptions.Contains(failure));
            Assert.AreEqual(disposalFails, observed.Flatten().InnerExceptions.Contains(disposalFailure));
            await using var primary = await GraphDatabase.OpenLocalAsync(new(_directory));
            await using var secondary = await GraphDatabase.OpenLocalAsync(new(other));
        }
        finally
        {
            field.SetValue(runtime, original);
            await runtime.DisposeAsync();
            await host.StopAsync();
        }
    }

    private sealed class MaintenanceFaultProfile(IHostedDatabaseProfile inner, Exception failure) : IHostedDatabaseProfile
    {
        public DatabaseProfileId Profile => inner.Profile;

        public ValueTask<long> GetCurrentVersionAsync(CancellationToken cancellationToken) => inner.GetCurrentVersionAsync(cancellationToken);

        public ValueTask<long> CreateCheckpointAsync(CancellationToken cancellationToken) => inner.CreateCheckpointAsync(cancellationToken);

        public ValueTask<DatabaseStorageStatus> GetStorageStatusAsync(CancellationToken cancellationToken) => inner.GetStorageStatusAsync(cancellationToken);

        public async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            throw failure;
        }
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task Maintenance_shutdown_retains_worker_after_cancelled_wait(bool workerFails, bool callbackFails)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int cancellationCount = 0;
        var maintenance = new HostedMaintenanceTask(async token =>
        {
            using var registration = token.Register(() =>
            {
                Interlocked.Increment(ref cancellationCount);
                cancelled.TrySetResult();
                if (callbackFails)
                {
                    throw new InvalidOperationException("Cancellation callback failure.");
                }
            });
            await release.Task;
            if (workerFails)
            {
                throw new IOException("Maintenance worker failure.");
            }
        });
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        try
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() => maintenance.StopAsync(caller.Token));
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task second = maintenance.StopAsync(CancellationToken.None);
            Assert.IsFalse(second.IsCompleted, "A cancelled waiter must not lose the still-running maintenance task.");
            release.TrySetResult();
            if (workerFails || callbackFails)
            {
                await Assert.ThrowsAsync<Exception>(() => second.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.IsTrue(second.IsFaulted, "The worker must finish with a fault, rather than timing out.");
                var errors = second.Exception!.Flatten().InnerExceptions;
                Assert.AreEqual(workerFails, errors.Any(error => error is IOException));
                Assert.AreEqual(callbackFails, errors.Any(error => error is InvalidOperationException));
                await Assert.ThrowsAsync<Exception>(() => maintenance.StopAsync(CancellationToken.None));
            }
            else
            {
                await second.WaitAsync(TimeSpan.FromSeconds(5));
                await maintenance.StopAsync(CancellationToken.None);
            }
            Assert.AreEqual(1, cancellationCount);
        }
        finally
        {
            release.TrySetResult();
            try
            {
                await maintenance.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception) when (workerFails || callbackFails)
            {
            }
        }
    }
}
