using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    public async Task Managed_gql_retention_distinguishes_active_retired_unknown_and_locked_stores_without_mutation()
    {
        _directory = NewDirectory();
        string root = NewDirectory();
        await using var host = ConfiguredManagedHost(root, secured: true);
        await host.StartAsync();
        var runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
        var original = await runtime.ExecuteManagedGqlCommandAsync(Guid.NewGuid(), "CREATE GRAPH sales ANY");
        var current = await runtime.ExecuteManagedGqlCommandAsync(Guid.NewGuid(), "CREATE OR REPLACE GRAPH sales ANY");
        await runtime.ExecuteManagedGqlCommandAsync(Guid.NewGuid(), "CREATE GRAPH IF NOT EXISTS sales ANY");
        await runtime.ExecuteManagedGqlCommandAsync(Guid.NewGuid(), "DROP GRAPH IF EXISTS absentGraph");
        string unknown = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(unknown);
        string sentinel = Path.Combine(unknown, "sentinel");
        await File.WriteAllTextAsync(sentinel, "preserve");
        byte[] catalog = await File.ReadAllBytesAsync(Path.Combine(root, "catalog.json"));
        using var reader = Client(host, ReaderToken);
        using var denied = await reader.GetAsync("/v1/administration/gql/retention/");
        Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
        using var admin = Client(host, AdministratorToken);
        using var response = await admin.GetAsync("/v1/administration/gql/retention");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var report = await response.Content.ReadFromJsonAsync<EntityForgeDbRuntime.ManagedRetentionReport>();
        Assert.IsNotNull(report);
        Assert.AreEqual(1, report.ActiveGraphCount);
        Assert.AreEqual(1, report.RetiredIdentityCount);
        Assert.AreEqual(4, report.ReceiptCount);
        Assert.IsFalse(report.Truncated);
        Assert.AreEqual("active", report.Stores.Single(store => store.StoreId == current.StoreId).State);
        Assert.AreEqual("retired-verified", report.Stores.Single(store => store.StoreId == original.StoreId).State);
        Assert.AreEqual("unknown", report.Stores.Single(store => store.DirectoryName == Path.GetFileName(unknown)).State);
        string retired = Path.Combine(root, original.StoreId.ToString("N"));
        using (var owner = new FileStream(Path.Combine(retired, EntityForgeDbHostOptions.DefaultCommitLogFileName + ".owner"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var locked = await runtime.InspectManagedGqlRetentionAsync();
            Assert.AreEqual("unverified", locked.Stores.Single(store => store.StoreId == original.StoreId).State);
        }
        Assert.AreEqual("retired-verified", (await runtime.InspectManagedGqlRetentionAsync()).Stores.Single(store => store.StoreId == original.StoreId).State);
        CollectionAssert.AreEqual(catalog, await File.ReadAllBytesAsync(Path.Combine(root, "catalog.json")));
        Assert.AreEqual("preserve", await File.ReadAllTextAsync(sentinel));
        Assert.IsTrue(Directory.Exists(retired));
        await host.StopAsync();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Managed_gql_retention_refuses_unverified_or_replaced_retired_checkpoint_identity(bool swapped)
    {
        _directory = NewDirectory();
        string root = NewDirectory();
        await using var host = ConfiguredManagedHost(root, secured: false);
        await host.StartAsync();
        var runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
        var first = await runtime.ExecuteManagedGqlCommandAsync(Guid.NewGuid(), "CREATE GRAPH firstGraph ANY");
        var second = await runtime.ExecuteManagedGqlCommandAsync(Guid.NewGuid(), "CREATE GRAPH secondGraph ANY");
        await runtime.ExecuteManagedGqlCommandAsync(Guid.NewGuid(), "DROP GRAPH firstGraph");
        await runtime.ExecuteManagedGqlCommandAsync(Guid.NewGuid(), "DROP GRAPH secondGraph");
        string checkpoint = Path.Combine(root, first.StoreId.ToString("N"), EntityForgeDbHostOptions.DefaultCheckpointFileName);
        if (swapped)
        {
            File.Copy(Path.Combine(root, second.StoreId.ToString("N"), EntityForgeDbHostOptions.DefaultCheckpointFileName), checkpoint, overwrite: true);
        }
        else
        {
            File.Move(checkpoint, checkpoint + ".missing");
        }
        var report = await runtime.InspectManagedGqlRetentionAsync();
        Assert.AreEqual(swapped ? "identity-mismatch" : "unverified", report.Stores.Single(store => store.StoreId == first.StoreId).State);
        Assert.AreEqual("retired-verified", report.Stores.Single(store => store.StoreId == second.StoreId).State);
        Assert.AreEqual(2, report.RetiredIdentityCount);
        Assert.IsTrue(runtime.IsReady);
        await host.StopAsync();
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("corrupt")]
    [DataRow("swapped")]
    public async Task Managed_gql_retention_requires_matching_valid_manifest_without_repair(string scenario)
    {
        _directory = NewDirectory();
        string root = NewDirectory();
        await using var host = ConfiguredManagedHost(root, secured: false);
        await host.StartAsync();
        var runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
        var first = await runtime.ExecuteManagedGqlCommandAsync(Guid.NewGuid(), "CREATE GRAPH firstGraph ANY");
        var second = await runtime.ExecuteManagedGqlCommandAsync(Guid.NewGuid(), "CREATE GRAPH secondGraph ANY");
        await runtime.ExecuteManagedGqlCommandAsync(Guid.NewGuid(), "DROP GRAPH firstGraph");
        await runtime.ExecuteManagedGqlCommandAsync(Guid.NewGuid(), "DROP GRAPH secondGraph");
        string manifest = Path.Combine(root, first.StoreId.ToString("N"), EntityForgeDbHostOptions.DefaultManifestFileName);
        if (scenario == "missing")
        {
            File.Move(manifest, manifest + ".missing");
        }
        else if (scenario == "swapped")
        {
            File.Copy(Path.Combine(root, second.StoreId.ToString("N"), EntityForgeDbHostOptions.DefaultManifestFileName), manifest, overwrite: true);
        }
        else
        {
            byte[] bytes = await File.ReadAllBytesAsync(manifest);
            bytes[^1] ^= 1;
            await File.WriteAllBytesAsync(manifest, bytes);
        }
        byte[]? before = File.Exists(manifest) ? await File.ReadAllBytesAsync(manifest) : null;
        byte[] catalog = await File.ReadAllBytesAsync(Path.Combine(root, "catalog.json"));
        var report = await runtime.InspectManagedGqlRetentionAsync();
        Assert.AreEqual(scenario == "swapped" ? "identity-mismatch" : "unverified",
            report.Stores.Single(store => store.StoreId == first.StoreId).State);
        Assert.AreEqual("retired-verified", report.Stores.Single(store => store.StoreId == second.StoreId).State);
        if (before is null)
        {
            Assert.IsFalse(File.Exists(manifest));
        }
        else
        {
            CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(manifest));
        }
        CollectionAssert.AreEqual(catalog, await File.ReadAllBytesAsync(Path.Combine(root, "catalog.json")));
        Assert.IsTrue(runtime.IsReady);
        await host.StopAsync();
    }

    [TestMethod]
    public async Task Managed_gql_retention_bounds_operator_created_directory_inventory()
    {
        _directory = NewDirectory();
        string root = NewDirectory();
        await using var host = ConfiguredManagedHost(root, secured: false);
        await host.StartAsync();
        for (int index = 0; index < 260; index++)
        {
            Directory.CreateDirectory(Path.Combine(root, "unknown-" + index));
        }
        var runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
        var report = await runtime.InspectManagedGqlRetentionAsync();
        Assert.IsTrue(report.Truncated);
        Assert.AreEqual(256, report.Stores.Length);
        Assert.IsTrue(report.Stores.All(store => store.State == "unknown"));
        Assert.AreEqual(260, Directory.GetDirectories(root).Length);
        await host.StopAsync();
    }
}
