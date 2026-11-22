using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    public async Task Gql_maintenance_snapshot_retains_every_store_until_released()
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .UseGqlGraphs(new("main", new Dictionary<string, string> { ["other"] = NewDirectory() }))
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        var runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
        var profiles = runtime.AcquireMaintenanceProfiles();
        Task stopping = runtime.StopAsync(CancellationToken.None);
        try
        {
            Assert.AreEqual(2, profiles.Length);
            Assert.AreEqual(0, runtime.AcquireMaintenanceProfiles().Length);
            Assert.IsFalse(stopping.IsCompleted);
            foreach (var item in profiles)
            {
                Assert.IsNotNull(item.Lifetime);
                await item.Profile.GetCurrentVersionAsync(CancellationToken.None);
            }
            profiles[0].Lifetime!.Dispose();
            profiles[0].Lifetime!.Dispose();
            Assert.IsFalse(stopping.IsCompleted, "Releasing one store must not release another store's ownership.");
        }
        finally
        {
            foreach (var item in profiles)
            {
                item.Lifetime?.Dispose();
            }
            await stopping.WaitAsync(TimeSpan.FromSeconds(10));
            await host.StopAsync();
        }
    }

    [TestMethod]
    public async Task Gql_store_lifetime_rejects_new_acquisitions_and_drains_each_lease_once()
    {
        var lifetime = new GqlStoreLifetime();
        var first = lifetime.Acquire();
        var second = lifetime.Acquire();
        Task drained = lifetime.CloseAsync();
        Assert.IsFalse(drained.IsCompleted);
        Assert.ThrowsExactly<ObjectDisposedException>(() => lifetime.Acquire());
        first.Dispose();
        first.Dispose();
        Assert.IsFalse(drained.IsCompleted);
        second.Dispose();
        await drained.WaitAsync(TimeSpan.FromSeconds(5));
        await lifetime.CloseAsync();
    }

    [TestMethod]
    public async Task Gql_store_lifetime_drains_concurrent_releases()
    {
        var lifetime = new GqlStoreLifetime();
        var leases = Enumerable.Range(0, 128).Select(_ => lifetime.Acquire()).ToArray();
        Task drained = lifetime.CloseAsync();
        await Task.WhenAll(leases.Select(lease => Task.Run(() =>
        {
            lease.Dispose();
            lease.Dispose();
        })));
        await drained.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Gql_host_shutdown_waits_for_selected_store_lifetime(bool named)
    {
        _directory = NewDirectory();
        var builder = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile();
        if (named)
        {
            builder.UseGqlGraphs(new("main", new Dictionary<string, string> { ["other"] = NewDirectory() }));
        }
        await using var host = builder.ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        var runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
        var selected = runtime.SelectGqlGraph(named ? "USE other RETURN 1 AS answer" : "RETURN 1 AS answer");
        Task stopping = host.StopAsync();
        try
        {
            Assert.AreNotSame(stopping, await Task.WhenAny(stopping, Task.Delay(50)));
            using var snapshot = await selected.Database.AcquireSnapshotAsync(null, default);
            Assert.ThrowsExactly<InvalidOperationException>(() => runtime.SelectGqlGraph("RETURN 1 AS answer"));
        }
        finally
        {
            selected.Lifetime.Dispose();
            await stopping.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
