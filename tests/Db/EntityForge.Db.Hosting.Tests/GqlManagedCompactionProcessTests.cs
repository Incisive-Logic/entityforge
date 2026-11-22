using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EntityForge.Db.Recovery.Worker;
using Microsoft.Extensions.DependencyInjection;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public async Task Managed_gql_compaction_process_kill_recovers_generation_and_preserves_store_identities(int boundary)
    {
        _directory = NewDirectory();
        string managed = NewDirectory(), control = NewDirectory();
        Guid request = Guid.NewGuid(), nextRequest = Guid.NewGuid();
        GqlManagedCatalog.Receipt retired;
        await using (var host = ConfiguredManagedHost(managed, secured: false))
        {
            await host.StartAsync();
            var runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
            await runtime.ExecuteManagedGqlCommandAsync(Guid.NewGuid(), "CREATE GRAPH survivor ANY");
            retired = await runtime.ExecuteManagedGqlCommandAsync(request, "CREATE GRAPH retiredGraph ANY");
            await runtime.ExecuteManagedGqlCommandAsync(Guid.NewGuid(), "DROP GRAPH retiredGraph");
            using var client = Client(host);
            using var write = await client.PostAsJsonAsync("/v1/graph/gql/update", new
            {
                statement = "USE survivor INSERT (:Witness {amount:87})"
            });
            Assert.AreEqual(HttpStatusCode.OK, write.StatusCode);
            await host.StopAsync();
        }
        string owner = Path.Combine(managed, "catalog.owner");
        byte[] ownership = await File.ReadAllBytesAsync(owner);
        string oracle = Path.Combine(control, "compaction.json");
        Directory.CreateDirectory(control);
        await File.WriteAllBytesAsync(oracle, JsonSerializer.SerializeToUtf8Bytes(new ManagedCompactionScenario(_directory, managed, 1, 3, boundary)));
        using (var process = StartReclamationWorker("--compaction-crash-worker", oracle))
        {
            Task<string> errors = process.StandardError.ReadToEndAsync();
            Task<string>? output = null;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            try
            {
                await WaitReclamationBoundaryAsync(process, ManagedCatalogCompactionProbe.Stages[boundary], errors, deadline.Token);
                output = process.StandardOutput.ReadToEndAsync();
                Assert.IsFalse(process.HasExited);
                await StopHistoryServer(process);
                Assert.AreNotEqual(0, process.ExitCode);
                Assert.AreEqual("", await errors);
            }
            finally
            {
                await StopHistoryServer(process);
                output ??= process.StandardOutput.ReadToEndAsync();
                await Task.WhenAll(output, errors);
            }
        }
        CollectionAssert.AreEqual(ownership, await File.ReadAllBytesAsync(owner));
        for (int round = 0; round < 2; round++)
        {
            await using var host = ConfiguredManagedHost(managed, secured: false);
            await host.StartAsync();
            var runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
            var before = await runtime.InspectManagedGqlRetentionAsync();
            Assert.AreEqual(round == 0 && boundary == 0 ? 1L : 2L, before.Generation);
            Assert.AreEqual(round == 0 ? 3L : 4L, before.CatalogVersion);
            Assert.AreEqual(1, before.RetiredIdentityCount);
            if (round == 0 && boundary == 0)
            {
                Assert.AreEqual(retired, await runtime.ExecuteManagedGqlCommandAsync(request, "CREATE GRAPH retiredGraph ANY"));
            }
            var compacted = await runtime.CompactManagedGqlCatalogAsync(1, 3);
            Assert.AreEqual(new GqlManagedCatalog.Compaction(1, 2, 3, 1, 1), compacted);
            await Assert.ThrowsExactlyAsync<GqlCatalogGenerationException>(() =>
                runtime.ExecuteManagedGqlCommandAsync(request, "CREATE GRAPH retiredGraph ANY"));
            var fresh = await runtime.ExecuteManagedGqlCommandAsync(nextRequest, "CREATE GRAPH freshGraph ANY", generation: 2);
            Assert.AreEqual(4L, fresh.Version);
            var after = await runtime.InspectManagedGqlRetentionAsync();
            Assert.AreEqual(1, after.ReceiptCount);
            Assert.AreEqual(3, after.KnownIdentityCount);
            Assert.IsTrue(after.Stores.Any(item => item.StoreId == retired.StoreId && item.State == "retired-verified"));
            using var client = Client(host);
            using var read = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = "USE survivor MATCH (n:Witness) RETURN n.amount AS amount"
            });
            Assert.AreEqual(HttpStatusCode.OK, read.StatusCode);
            var rows = (await read.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
            Assert.AreEqual(1, rows.GetArrayLength());
            Assert.AreEqual(87, rows[0].GetProperty("bindings").GetProperty("amount").GetInt32());
            await host.StopAsync();
        }
        CollectionAssert.AreEqual(ownership, await File.ReadAllBytesAsync(owner));
    }
}
