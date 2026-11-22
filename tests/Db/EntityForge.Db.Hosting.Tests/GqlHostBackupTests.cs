using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    public async Task Gql_host_backup_recovers_default_configured_and_managed_graphs_with_distinct_routing()
    {
        _directory = NewDirectory();
        string managed = NewDirectory(), output = Path.Combine(NewDirectory(), "backup"), restored = NewDirectory();
        var catalog = new GqlLocalGraphCatalog("home", new Dictionary<string, string> { ["sales.orders"] = NewDirectory() },
            new Dictionary<string, string> { ["sales.orders"] = NewDirectory() });
        Guid request = Guid.NewGuid();
        GqlManagedCatalog.Receipt original;
        string[] references = ["HOME_GRAPH", "`sales.orders`", "sales.orders", "live.orders"];
        await using (var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .UseGqlGraphs(catalog).ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build())
        {
            await host.StartAsync();
            var runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
            await runtime.EnableManagedGqlCatalogAsync(managed);
            original = await runtime.ExecuteManagedGqlCommandAsync(request, "CREATE GRAPH live.orders ANY");
            using var client = Client(host);
            for (int index = 0; index < references.Length; index++)
            {
                using var write = await client.PostAsJsonAsync("/v1/graph/gql/update", new
                {
                    statement = "USE " + references[index] + " INSERT (:Person {amount:" + index + "})"
                });
                Assert.AreEqual(HttpStatusCode.OK, write.StatusCode, await write.Content.ReadAsStringAsync());
            }
            await runtime.CreateGqlHostBackupAsync(output);
            await runtime.ExecuteManagedGqlCommandAsync(Guid.NewGuid(), "CREATE OR REPLACE GRAPH live.orders ANY");
            await host.StopAsync();
        }
        var manifest = JsonSerializer.Deserialize<EntityForgeDbRuntime.ManagedBackupManifest>(await File.ReadAllBytesAsync(Path.Combine(output, "backup.json")))!;
        Assert.AreEqual(2, manifest.Format);
        Assert.AreEqual(4, manifest.Stores.Length);
        Assert.AreEqual(3, manifest.HostGraphs!.Length);
        Assert.IsFalse((await File.ReadAllTextAsync(Path.Combine(output, "backup.json"))).Contains(_directory, StringComparison.Ordinal));
        var recovery = await EntityForgeDbRuntime.RestoreGqlHostBackupAsync(output, restored, new EntityForgeDbHostOptions());
        Assert.IsTrue(File.Exists(Path.Combine(restored, "host.json")));
        Assert.IsTrue(recovery.DataDirectory.StartsWith(restored + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        Assert.IsTrue(recovery.Catalog.StoreDirectories.Values.All(path => path.StartsWith(restored + Path.DirectorySeparatorChar, StringComparison.Ordinal)));
        for (int round = 0; round < 2; round++)
        {
            recovery = await EntityForgeDbRuntime.ReadRecoveredGqlHostAsync(restored, new EntityForgeDbHostOptions());
            await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(recovery.DataDirectory).UsePropertyGraphProfile()
                .UseGqlGraphs(recovery.Catalog).ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
            await host.StartAsync();
            var runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
            await runtime.EnableManagedGqlCatalogAsync(recovery.ManagedDirectory);
            Assert.AreEqual(original, await runtime.ExecuteManagedGqlCommandAsync(request, "CREATE GRAPH live.orders ANY"));
            if (round == 0)
            {
                await runtime.ExecuteManagedGqlCommandAsync(Guid.NewGuid(), "CREATE GRAPH laterGraph ANY");
            }
            using var client = Client(host);
            for (int index = 0; index < references.Length; index++)
            {
                using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
                {
                    query = "USE " + references[index] + " MATCH (r:Person) RETURN r.amount AS amount"
                });
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
                var rows = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
                Assert.AreEqual(1, rows.GetArrayLength());
                Assert.AreEqual(index, rows[0].GetProperty("bindings").GetProperty("amount").GetInt32());
            }
            await host.StopAsync();
        }
        string hostMap = Path.Combine(restored, "host.json");
        byte[] savedMap = await File.ReadAllBytesAsync(hostMap);
        var damagedMap = JsonSerializer.Deserialize<EntityForgeDbRuntime.ManagedBackupManifest>(savedMap)!;
        damagedMap.HostGraphs![0] = damagedMap.HostGraphs[0] with
        {
            Name = "changedName"
        };
        await File.WriteAllBytesAsync(hostMap, JsonSerializer.SerializeToUtf8Bytes(damagedMap));
        await Assert.ThrowsAsync<InvalidDataException>(() => EntityForgeDbRuntime.ReadRecoveredGqlHostAsync(restored, new EntityForgeDbHostOptions()));
        await File.WriteAllBytesAsync(hostMap, savedMap);
        string checkpoint = Path.Combine(recovery.DataDirectory, EntityForgeDbHostOptions.DefaultCheckpointFileName);
        File.Move(checkpoint, checkpoint + ".missing");
        await Assert.ThrowsAsync<InvalidDataException>(() => EntityForgeDbRuntime.ReadRecoveredGqlHostAsync(restored, new EntityForgeDbHostOptions()));
        Assert.IsFalse(File.Exists(checkpoint));
    }

    [TestMethod]
    [DataRow("missing-default")]
    [DataRow("duplicate-default")]
    [DataRow("managed-name")]
    [DataRow("managed-id")]
    [DataRow("missing-store")]
    [DataRow("wrong-scope")]
    [DataRow("routing-checksum")]
    public async Task Gql_host_backup_rejects_inconsistent_routing_before_recovery_publication(string scenario)
    {
        _directory = NewDirectory();
        string root = NewDirectory(), output = Path.Combine(NewDirectory(), "backup"), target = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .UseGqlGraphs(new("home", new Dictionary<string, string> { ["configured"] = NewDirectory() }))
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        var runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
        await runtime.EnableManagedGqlCatalogAsync(root);
        var created = await runtime.ExecuteManagedGqlCommandAsync(Guid.NewGuid(), "CREATE GRAPH live ANY");
        await runtime.CreateGqlHostBackupAsync(output);
        string path = Path.Combine(output, "backup.json");
        var manifest = JsonSerializer.Deserialize<EntityForgeDbRuntime.ManagedBackupManifest>(await File.ReadAllBytesAsync(path))!;
        var graphs = manifest.HostGraphs!;
        int home = Array.FindIndex(graphs, graph => graph.IsDefault), other = Array.FindIndex(graphs, graph => !graph.IsDefault);
        switch (scenario)
        {
            case "missing-default":
                graphs[home] = graphs[home] with
                {
                    IsDefault = false
                };
                break;
            case "duplicate-default":
                graphs[other] = graphs[other] with
                {
                    IsDefault = true
                };
                break;
            case "managed-name":
                graphs[other] = graphs[other] with
                {
                    Name = "live"
                };
                break;
            case "managed-id":
                graphs[other] = graphs[other] with
                {
                    StoreId = created.StoreId
                };
                break;
            case "missing-store":
                manifest = manifest with
                {
                    Stores = manifest.Stores.Where(store => store.StoreId != graphs[other].StoreId).ToArray()
                };
                break;
            case "routing-checksum":
                graphs[other] = graphs[other] with
                {
                    Name = "changedName"
                };
                break;
        }
        if (scenario != "routing-checksum")
        {
            // Exercise structural validation even when the bytes have a valid checksum.
            manifest = manifest with
            {
                HostGraphsSha256 = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(graphs)))
            };
        }
        await File.WriteAllBytesAsync(path, JsonSerializer.SerializeToUtf8Bytes(manifest));
        if (scenario == "wrong-scope")
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => EntityForgeDbRuntime.RestoreManagedGqlBackupAsync(output, target, new EntityForgeDbHostOptions()));
        }
        else
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => EntityForgeDbRuntime.RestoreGqlHostBackupAsync(output, target, new EntityForgeDbHostOptions()));
        }
        Assert.IsFalse(Directory.Exists(target));
        await host.StopAsync();
    }
}
