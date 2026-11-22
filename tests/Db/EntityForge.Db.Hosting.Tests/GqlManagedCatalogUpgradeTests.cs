using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using EntityForge.Db.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task Managed_gql_host_backup_cli_restores_catalog_formats_with_explicit_receipt_retention(bool legacy, bool expire)
    {
        _directory = NewDirectory();
        string root = NewDirectory(), backup = Path.Combine(NewDirectory(), "snapshot"), target = Path.Combine(NewDirectory(), "restored");
        Guid request = Guid.NewGuid();
        GqlManagedCatalog.Receipt receipt;
        await using (var host = ConfiguredManagedHost(root, secured: false))
        {
            await host.StartAsync();
            var runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
            receipt = await runtime.ExecuteManagedGqlCommandAsync(request, "CREATE GRAPH sales ANY");
            using var client = Client(host);
            using var write = await client.PostAsJsonAsync("/v1/graph/gql/update", new
            {
                statement = "USE sales INSERT (:Witness {amount:91})"
            });
            Assert.AreEqual(HttpStatusCode.OK, write.StatusCode);
            await runtime.CreateGqlHostBackupAsync(backup);
            await host.StopAsync();
        }
        if (legacy)
        {
            string catalogPath = Path.Combine(backup, "catalog.json"), manifestPath = Path.Combine(backup, "backup.json");
            byte[] catalog = ConvertCatalogFixtureToFormat3(await File.ReadAllBytesAsync(catalogPath));
            var manifest = JsonSerializer.Deserialize<EntityForgeDbRuntime.ManagedBackupManifest>(await File.ReadAllBytesAsync(manifestPath))!;
            await File.WriteAllBytesAsync(catalogPath, catalog);
            await File.WriteAllBytesAsync(manifestPath, JsonSerializer.SerializeToUtf8Bytes(manifest with
            {
                CatalogSha256 = Convert.ToHexString(SHA256.HashData(catalog))
            }));
        }
        var archiveHashes = Directory.GetFiles(backup, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, path => SHA256.HashData(File.ReadAllBytes(path)));
        var start = HistoryServerStart(_directory);
        start.ArgumentList.Add("restore-gql-host");
        start.ArgumentList.Add(backup);
        start.ArgumentList.Add(target);
        if (expire)
        {
            start.ArgumentList.Add("--expire-catalog-receipts");
        }
        using (var process = Process.Start(start)!)
        {
            Task<string> output = process.StandardOutput.ReadToEndAsync(), errors = process.StandardError.ReadToEndAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try
            {
                await process.WaitForExitAsync(deadline.Token);
                Assert.AreEqual(0, process.ExitCode, await output + await errors);
            }
            finally
            {
                await StopHistoryServer(process);
                await Task.WhenAll(output, errors);
            }
        }
        foreach (var (path, hash) in archiveHashes)
        {
            CollectionAssert.AreEqual(hash, SHA256.HashData(await File.ReadAllBytesAsync(path)));
        }
        var recovery = await EntityForgeDbRuntime.ReadRecoveredGqlHostAsync(target, new());
        using (var catalog = new GqlManagedCatalog(recovery.ManagedDirectory))
        {
            Assert.AreEqual(receipt.After, catalog.Entries["sales"]);
            Assert.AreEqual(expire ? 2L : 1L, catalog.InspectRetention().Generation);
            Assert.AreEqual(expire ? 0 : 1, catalog.InspectRetention().ReceiptCount);
            if (expire)
            {
                Assert.ThrowsExactly<GqlCatalogGenerationException>(() => catalog.FindReceipt(request, "create", "sales"));
            }
            else
            {
                Assert.AreEqual(receipt, catalog.FindReceipt(request, "create", "sales"));
            }
        }
        await using var restored = EntityForgeDbServer.CreateBuilder().UseLocalStorage(recovery.DataDirectory).UsePropertyGraphProfile()
            .UseGqlGraphs(recovery.Catalog).ConfigureStorage(options => options.ManagedGqlDirectory = recovery.ManagedDirectory)
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await restored.StartAsync();
        using var reader = Client(restored);
        using var read = await reader.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "USE sales MATCH (n:Witness) RETURN n.amount AS amount"
        });
        Assert.AreEqual(HttpStatusCode.OK, read.StatusCode);
        var rows = (await read.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
        Assert.AreEqual(1, rows.GetArrayLength());
        Assert.AreEqual(91, rows[0].GetProperty("bindings").GetProperty("amount").GetInt32());
        await restored.StopAsync();
    }

    [TestMethod]
    public async Task Managed_gql_catalog_upgrade_requires_explicit_receipt_expiry_when_new_metadata_would_exceed_capacity()
    {
        string root = NewDirectory();
        GqlManagedCatalog.Receipt original;
        using (var catalog = new GqlManagedCatalog(root))
        {
            original = catalog.Create(Guid.NewGuid(), "sales", Guid.NewGuid(), Guid.NewGuid());
        }
        var document = JsonNode.Parse(ConvertCatalogFixtureToFormat3(SaturatedManagedCatalog(original, metadataBytes: true)))!;
        int currentLength = JsonSerializer.SerializeToUtf8Bytes(document).Length;
        int replaced = (4 * 1024 * 1024 - 32 - currentLength) / 5;
        Assert.IsTrue(replaced is > 0 and < 1000);
        document["State"]!["Receipts"]![1]!["Name"] = new string('\u3042', replaced) + new string('x', 1000 - replaced);
        document["Sha256"] = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(document["State"])));
        byte[] source = JsonSerializer.SerializeToUtf8Bytes(document);
        Assert.IsTrue(source.Length < 4 * 1024 * 1024);
        string path = Path.Combine(root, "catalog.json");
        await File.WriteAllBytesAsync(path, source);
        await Assert.ThrowsAsync<InvalidOperationException>(() => EntityForgeDbRuntime.UpgradeManagedGqlCatalogAsync(root));
        CollectionAssert.AreEqual(source, await File.ReadAllBytesAsync(path));
        Assert.IsFalse(File.Exists(Path.Combine(root, "catalog.format3.json")));
        Assert.IsTrue(await EntityForgeDbRuntime.UpgradeManagedGqlCatalogAsync(root, expireReceipts: true));
        CollectionAssert.AreEqual(source, await File.ReadAllBytesAsync(Path.Combine(root, "catalog.format3.json")));
        using var upgraded = new GqlManagedCatalog(root);
        Assert.AreEqual(2L, upgraded.InspectRetention().Generation);
        Assert.AreEqual(0, upgraded.InspectRetention().ReceiptCount);
        Assert.AreEqual(original.After, upgraded.Entries["sales"]);
        Assert.ThrowsExactly<GqlCatalogGenerationException>(() => upgraded.FindReceipt(original.RequestId, "create", "sales"));
    }

    [TestMethod]
    public async Task Managed_gql_catalog_offline_upgrade_preserves_data_replay_and_original_metadata_through_cli()
    {
        _directory = NewDirectory();
        string root = NewDirectory();
        Guid request = Guid.NewGuid();
        GqlManagedCatalog.Receipt original;
        await using (var host = ConfiguredManagedHost(root, secured: false))
        {
            await host.StartAsync();
            var runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
            original = await runtime.ExecuteManagedGqlCommandAsync(request, "CREATE GRAPH sales ANY");
            using var client = Client(host);
            using var write = await client.PostAsJsonAsync("/v1/graph/gql/update", new
            {
                statement = "USE sales INSERT (:Witness {amount:73})"
            });
            Assert.AreEqual(HttpStatusCode.OK, write.StatusCode, await write.Content.ReadAsStringAsync());
            await host.StopAsync();
        }
        string catalog = Path.Combine(root, "catalog.json");
        byte[] source = ConvertCatalogFixtureToFormat3(await File.ReadAllBytesAsync(catalog));
        await File.WriteAllBytesAsync(catalog, source);
        var before = Directory.GetFiles(Path.Combine(root, original.StoreId.ToString("N")), "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, path => SHA256.HashData(File.ReadAllBytes(path)));
        for (int round = 0; round < 2; round++)
        {
            var start = HistoryServerStart(_directory);
            start.ArgumentList.Add("upgrade-gql-catalog");
            start.ArgumentList.Add(root);
            using var process = Process.Start(start)!;
            Task<string> output = process.StandardOutput.ReadToEndAsync(), errors = process.StandardError.ReadToEndAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            try
            {
                await process.WaitForExitAsync(deadline.Token);
                Assert.AreEqual(0, process.ExitCode, await errors);
                StringAssert.Contains(await output, round == 0 ? "Upgraded managed GQL catalog" : "already format 4");
            }
            finally
            {
                await StopHistoryServer(process);
                await Task.WhenAll(output, errors);
            }
        }
        CollectionAssert.AreEqual(source, await File.ReadAllBytesAsync(Path.Combine(root, "catalog.format3.json")));
        foreach (var (path, hash) in before)
        {
            CollectionAssert.AreEqual(hash, SHA256.HashData(await File.ReadAllBytesAsync(path)));
        }
        await using var reopened = ConfiguredManagedHost(root, secured: false);
        await reopened.StartAsync();
        var recovered = reopened.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
        Assert.AreEqual(original, await recovered.ExecuteManagedGqlCommandAsync(request, "CREATE GRAPH sales ANY"));
        using var readClient = Client(reopened);
        using var read = await readClient.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "USE sales MATCH (n:Witness) RETURN n.amount AS amount"
        });
        Assert.AreEqual(HttpStatusCode.OK, read.StatusCode, await read.Content.ReadAsStringAsync());
        var rows = (await read.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
        Assert.AreEqual(1, rows.GetArrayLength());
        Assert.AreEqual(73, rows[0].GetProperty("bindings").GetProperty("amount").GetInt32());
        await reopened.StopAsync();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Managed_gql_catalog_upgrade_reconciles_interrupted_backup_or_catalog_publication(bool afterReplacement)
    {
        string root = NewDirectory();
        using (var catalog = new GqlManagedCatalog(root))
        {
            catalog.Drop(Guid.NewGuid(), "absent", ifExists: true);
        }
        string path = Path.Combine(root, "catalog.json");
        byte[] source = ConvertCatalogFixtureToFormat3(await File.ReadAllBytesAsync(path));
        await File.WriteAllBytesAsync(path, source);
        int synchronized = 0;
        DurableDirectory.BeforeSync.Value = directory =>
        {
            if (directory == root && ++synchronized == (afterReplacement ? 2 : 1))
            {
                throw new IOException("Injected upgrade directory sync failure.");
            }
        };
        try
        {
            await Assert.ThrowsAsync<IOException>(() => EntityForgeDbRuntime.UpgradeManagedGqlCatalogAsync(root));
        }
        finally
        {
            DurableDirectory.BeforeSync.Value = null;
        }
        CollectionAssert.AreEqual(source, await File.ReadAllBytesAsync(Path.Combine(root, "catalog.format3.json")));
        if (!afterReplacement)
        {
            CollectionAssert.AreEqual(source, await File.ReadAllBytesAsync(path));
        }
        int retrySyncs = 0;
        DurableDirectory.BeforeSync.Value = _ => retrySyncs++;
        try
        {
            Assert.AreEqual(!afterReplacement, await EntityForgeDbRuntime.UpgradeManagedGqlCatalogAsync(root));
        }
        finally
        {
            DurableDirectory.BeforeSync.Value = null;
        }
        Assert.IsTrue(retrySyncs > 0, "Resolving an ambiguous upgrade must synchronize the observed namespace.");
        using var upgraded = new GqlManagedCatalog(root);
        Assert.AreEqual(1, upgraded.InspectRetention().ReceiptCount);
    }

    [TestMethod]
    [DataRow("owner")]
    [DataRow("conflicting-backup")]
    [DataRow("corrupt-source")]
    public async Task Managed_gql_catalog_upgrade_refuses_conflicts_without_changing_source(string scenario)
    {
        string root = NewDirectory();
        using (var catalog = new GqlManagedCatalog(root))
        {
            catalog.Drop(Guid.NewGuid(), "absent", ifExists: true);
        }
        string path = Path.Combine(root, "catalog.json");
        byte[] source = ConvertCatalogFixtureToFormat3(await File.ReadAllBytesAsync(path));
        if (scenario == "corrupt-source")
        {
            var document = JsonNode.Parse(source)!;
            document["Sha256"] = new string('0', 64);
            source = JsonSerializer.SerializeToUtf8Bytes(document);
        }
        await File.WriteAllBytesAsync(path, source);
        if (scenario == "conflicting-backup")
        {
            await File.WriteAllTextAsync(Path.Combine(root, "catalog.format3.json"), "preserve previous evidence");
        }
        using var owner = scenario == "owner" ? new FileStream(Path.Combine(root, "catalog.owner"), FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null;
        if (scenario == "owner")
        {
            await Assert.ThrowsAsync<IOException>(() => EntityForgeDbRuntime.UpgradeManagedGqlCatalogAsync(root));
        }
        else
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => EntityForgeDbRuntime.UpgradeManagedGqlCatalogAsync(root));
        }
        CollectionAssert.AreEqual(source, await File.ReadAllBytesAsync(path));
        if (scenario == "conflicting-backup")
        {
            Assert.AreEqual("preserve previous evidence", await File.ReadAllTextAsync(Path.Combine(root, "catalog.format3.json")));
        }
    }

    private static byte[] ConvertCatalogFixtureToFormat3(byte[] current)
    {
        var document = JsonNode.Parse(current)!;
        var state = document["State"]!.AsObject();
        Assert.AreEqual(1L, state["Generation"]!.GetValue<long>());
        Assert.AreEqual(0L, state["BaseVersion"]!.GetValue<long>());
        state.Remove("Generation");
        state.Remove("BaseVersion");
        state.Remove("SeedEntries");
        state.Remove("SeedRetired");
        state["Format"] = 3;
        document["Sha256"] = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(state)));
        return JsonSerializer.SerializeToUtf8Bytes(document);
    }
}
