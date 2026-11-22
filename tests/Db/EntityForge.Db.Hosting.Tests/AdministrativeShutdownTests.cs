using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Administrative_shutdown_drains_admitted_jobs_before_disposing_profiles(bool rdf)
    {
        _directory = NewDirectory();
        var builder = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory)
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0"));
        await using var host = (rdf ? builder.UseRdfProfile() : builder.UsePropertyGraphProfile()).Build();
        await host.StartAsync();
        var runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
        var coordinator = host.Application.Services.GetRequiredService<EntityForgeDbAdministrativeJobCoordinator>();
        var graph = rdf ? null : runtime.GraphDatabase;
        var rdfDatabase = rdf ? runtime.RdfDatabase : null;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task job = coordinator.ExecuteAsync(async token =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            if (rdf)
            {
                await rdfDatabase!.CreateCheckpointAsync(token);
            }
            else
            {
                await graph!.CreateCheckpointAsync(token);
            }
            return true;
        }, deadline.Token).AsTask();
        Task? stop = null;
        try
        {
            await entered.Task.WaitAsync(deadline.Token);
            stop = runtime.StopAsync(deadline.Token);
            await Task.WhenAny(stop, Task.Delay(200, deadline.Token));
            Assert.IsFalse(stop.IsCompleted, "Runtime shutdown must retain the profile while an admitted administrative operation is running.");
            release.TrySetResult();
            await job;
            await stop;
        }
        finally
        {
            release.TrySetResult();
            await job;
            if (stop is not null)
            {
                await stop;
            }
        }
        await host.StopAsync();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Administrative_shutdown_retains_drain_after_cancelled_wait_and_job_failure(bool fail)
    {
        using var coordinator = new EntityForgeDbAdministrativeJobCoordinator(new());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var cancellation = new CancellationTokenSource();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task job = coordinator.ExecuteAsync(async token =>
        {
            await release.Task.WaitAsync(token);
            if (fail)
            {
                throw new InvalidOperationException("Injected job failure.");
            }
            return true;
        }, deadline.Token).AsTask();
        Task stop = coordinator.StopAsync(cancellation.Token);
        try
        {
            Assert.IsFalse(stop.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => stop);
            Task retained = coordinator.StopAsync(deadline.Token);
            Assert.IsFalse(retained.IsCompleted);
            await Assert.ThrowsAsync<EntityForgeDbAdministrativeJobRejectedException>(() =>
                coordinator.ExecuteAsync(_ => ValueTask.FromResult(true), deadline.Token).AsTask());
            release.TrySetResult();
            if (fail)
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => job);
            }
            else
            {
                await job;
            }
            await retained;
        }
        finally
        {
            release.TrySetResult();
            try
            {
                await job;
            }
            catch (InvalidOperationException) when (fail)
            {
            }
            await coordinator.StopAsync(CancellationToken.None);
        }
    }
}
