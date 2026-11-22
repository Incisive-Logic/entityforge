using EntityForge.Db.Rdf;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    public async Task Sparql_retained_readers_defer_checkpoints_without_losing_readiness()
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UseRdfProfile()
            .ConfigureStorage(options => options.CheckpointInterval = MaintenanceTestInterval)
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        var runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
        using (var lease = await runtime.RdfDatabase.AcquireSnapshotAsync())
        {
            await runtime.RdfDatabase.CommitSparqlUpdateAsync(Guid.NewGuid(), "INSERT DATA { <urn:s> <urn:p> 1 }");
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await runtime.RdfDatabase.CreateCheckpointAsync());
            await Task.Delay(MaintenanceTestInterval * 10);
            Assert.IsTrue(runtime.IsReady, runtime.LastMaintenanceFailure?.ToString());
        }
        string checkpoint = Path.Combine(_directory, EntityForgeDbHostOptions.DefaultCheckpointFileName);
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(MaintenanceTestTimeout);
        while ((!File.Exists(checkpoint) || !runtime.IsReady) && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(MaintenanceTestInterval);
        }
        Assert.IsTrue(File.Exists(checkpoint));
        Assert.IsTrue(runtime.IsReady, runtime.LastMaintenanceFailure?.ToString());
        Assert.AreEqual(1, runtime.RdfDatabase.Find(new()).Count);
        await host.StopAsync();
    }
}
