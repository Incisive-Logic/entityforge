using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using EntityForge.Db.Storage.Local;
using EntityForge.Db.Storage.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("replace")]
    [DataRow("stop")]
    [DataRow("cancel")]
    public async Task Managed_gql_backup_serializes_lifecycle_and_releases_cancelled_capture(string scenario)
    {
        _directory = NewDirectory();
        string root = NewDirectory(), parent = NewDirectory(), output = Path.Combine(parent, "backup");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        await using var host = ManagedRuntimeHost();
        await host.StartAsync(deadline.Token);
        var runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
        await runtime.EnableManagedGqlCatalogAsync(root, deadline.Token);
        var original = await runtime.ExecuteManagedGqlCommandAsync(Guid.NewGuid(), "CREATE GRAPH sales ANY", deadline.Token);
        var selected = runtime.SelectGqlGraph("USE sales RETURN 1 AS answer");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async ValueTask<int> Hold(CancellationToken token)
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return 0;
        }
        Task blocker = selected.Database.PartitionKernel.CaptureCheckpointImageAsync(Hold, deadline.Token).AsTask();
        Task? backup = null, operation = null;
        try
        {
            await entered.Task.WaitAsync(deadline.Token);
            backup = runtime.CreateManagedGqlBackupAsync(output, cancellation.Token);
            // Staging is allocated after catalog mutation admission, before the
            // blocked checkpoint capture. Observe that state rather than a delay.
            while (!Directory.Exists(parent) || Directory.GetDirectories(parent, ".gql-backup-*").Length == 0)
            {
                if (backup.IsCompleted)
                {
                    await backup;
                    Assert.Fail("Capture ended before the controlled checkpoint was released.");
                }
                await Task.Delay(10, deadline.Token);
            }
            Assert.IsFalse(backup.IsCompleted);
            if (scenario == "cancel")
            {
                cancellation.Cancel();
                await Assert.ThrowsAsync<OperationCanceledException>(() => backup);
                Assert.IsFalse(Directory.Exists(output));
                Assert.AreEqual(0, Directory.GetDirectories(parent, ".gql-backup-*").Length);
            }
            else
            {
                operation = scenario == "stop" ? runtime.StopAsync(deadline.Token) :
                    runtime.ExecuteManagedGqlCommandAsync(Guid.NewGuid(), "CREATE OR REPLACE GRAPH sales ANY", deadline.Token);
                Assert.IsFalse(operation.IsCompleted, "Lifecycle mutation must wait for the active backup.");
            }
            release.TrySetResult();
            await blocker;
            selected.Lifetime.Dispose();
            if (scenario != "cancel")
            {
                await backup;
                await operation!;
                var manifest = JsonSerializer.Deserialize<EntityForgeDbRuntime.ManagedBackupManifest>(await File.ReadAllBytesAsync(Path.Combine(output, "backup.json"), deadline.Token))!;
                Assert.AreEqual(original.Incarnation, manifest.Stores.Single().Incarnation);
                var entries = GqlManagedCatalog.InspectSnapshot(await File.ReadAllBytesAsync(Path.Combine(output, "catalog.json"), deadline.Token));
                Assert.AreEqual(original.After, entries["sales"]);
            }
            else
            {
                var replacement = await runtime.ExecuteManagedGqlCommandAsync(Guid.NewGuid(), "CREATE OR REPLACE GRAPH sales ANY", deadline.Token);
                Assert.AreNotEqual(original.Incarnation, replacement.Incarnation);
                await runtime.CreateManagedGqlBackupAsync(output, deadline.Token);
            }
        }
        finally
        {
            release.TrySetResult();
            try
            {
                await blocker;
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
            }
            selected.Lifetime.Dispose();
            if (backup is not null)
            {
                try
                {
                    await backup;
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested || deadline.IsCancellationRequested)
                {
                }
            }
            if (operation is not null)
            {
                await operation;
            }
        }
        await host.StopAsync();
    }

    [TestMethod]
    public async Task Managed_gql_backup_preserves_active_stores_and_receipts_for_recovery()
    {
        _directory = NewDirectory();
        string root = NewDirectory(), output = Path.Combine(NewDirectory(), "capture"), restored = NewDirectory();
        Guid request = Guid.NewGuid();
        GqlManagedCatalog.Receipt original;
        await using (var host = ManagedRuntimeHost())
        {
            await host.StartAsync();
            var runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
            await runtime.EnableManagedGqlCatalogAsync(root);
            original = await runtime.ExecuteManagedGqlCommandAsync(request, "CREATE GRAPH sales.orders ANY");
            await runtime.ExecuteManagedGqlCommandAsync(Guid.NewGuid(), "CREATE GRAPH other ANY");
            await runtime.ExecuteManagedGqlCommandAsync(Guid.NewGuid(), "CREATE GRAPH retired ANY");
            await runtime.ExecuteManagedGqlCommandAsync(Guid.NewGuid(), "DROP GRAPH retired");
            using var client = Client(host);
            foreach (string name in new[] { "sales.orders", "other" })
            {
                using var response = await client.PostAsJsonAsync("/v1/graph/gql/update", new
                {
                    statement = "USE " + name + " INSERT (:Person {name:'captured'})"
                });
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            }
            await runtime.CreateManagedGqlBackupAsync(output);
            await runtime.ExecuteManagedGqlCommandAsync(Guid.NewGuid(), "CREATE OR REPLACE GRAPH sales.orders ANY");
            await host.StopAsync();
        }
        var manifest = JsonSerializer.Deserialize<EntityForgeDbRuntime.ManagedBackupManifest>(await File.ReadAllBytesAsync(Path.Combine(output, "backup.json")))!;
        Assert.AreEqual(1, manifest.Format);
        Assert.AreEqual(2, manifest.Stores.Length, "Retired physical stores must not enter the active recovery set.");
        byte[] metadata = await File.ReadAllBytesAsync(Path.Combine(output, "catalog.json"));
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(metadata)), manifest.CatalogSha256);
        var manager = new LocalDatabaseBackupManager();
        foreach (var store in manifest.Stores)
        {
            string artifact = Path.Combine(output, store.StoreId.ToString("N") + ".efbackup");
            var verified = await manager.VerifyAsync(artifact);
            Assert.AreEqual(store.Incarnation, verified.DatabaseIncarnation);
            Assert.AreEqual(store.Sha256, verified.Sha256);
        }
        await EntityForgeDbRuntime.RestoreManagedGqlBackupAsync(output, restored, new EntityForgeDbHostOptions());
        _directory = NewDirectory();
        await using var recovered = ManagedRuntimeHost();
        await recovered.StartAsync();
        var restoredRuntime = recovered.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
        await restoredRuntime.EnableManagedGqlCatalogAsync(restored);
        Assert.AreEqual(original, await restoredRuntime.ExecuteManagedGqlCommandAsync(request, "CREATE GRAPH sales.orders ANY"));
        using var reader = Client(recovered);
        foreach (string name in new[] { "sales.orders", "other" })
        {
            using var response = await reader.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = "USE " + name + " MATCH (p:Person) RETURN p.name AS name"
            });
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            var rows = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
            Assert.AreEqual(1, rows.GetArrayLength());
            Assert.AreEqual("captured", rows[0].GetProperty("bindings").GetProperty("name").GetString());
        }
        await recovered.StopAsync();
    }

    [TestMethod]
    [DataRow("cancelled")]
    [DataRow("existing")]
    [DataRow("managed")]
    [DataRow("default")]
    public async Task Managed_gql_backup_rejects_invalid_destinations_or_cancellation_without_losing_catalog(string scenario)
    {
        _directory = NewDirectory();
        string root = NewDirectory(), parent = NewDirectory();
        Directory.CreateDirectory(parent);
        string target = scenario switch
        {
            "existing" => parent,
            "managed" => Path.Combine(root, "capture"),
            "default" => Path.Combine(_directory, "capture"),
            _ => Path.Combine(parent, "capture")
        };
        await using var host = ManagedRuntimeHost();
        await host.StartAsync();
        var runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
        await runtime.EnableManagedGqlCatalogAsync(root);
        var first = await runtime.ExecuteManagedGqlCommandAsync(Guid.NewGuid(), "CREATE GRAPH sales ANY");
        using var cancellation = new CancellationTokenSource();
        if (scenario == "cancelled")
        {
            cancellation.Cancel();
        }
        var error = await Assert.ThrowsAsync<Exception>(() => runtime.CreateManagedGqlBackupAsync(target, cancellation.Token));
        Assert.IsTrue(error is ArgumentException or IOException or OperationCanceledException, error.ToString());
        Assert.AreEqual(0, Directory.GetDirectories(parent, ".gql-backup-*").Length);
        Assert.IsTrue(runtime.IsReady);
        var next = await runtime.ExecuteManagedGqlCommandAsync(Guid.NewGuid(), "CREATE GRAPH more ANY");
        Assert.AreEqual(first.Version + 1, next.Version);
        await host.StopAsync();
    }

    [TestMethod]
    [DataRow("catalog")]
    [DataRow("manifest")]
    [DataRow("missing")]
    [DataRow("swapped")]
    [DataRow("cancelled")]
    [DataRow("existing")]
    public async Task Managed_gql_backup_restore_rejects_incomplete_or_mismatched_sets_without_publication(string scenario)
    {
        _directory = NewDirectory();
        string root = NewDirectory(), parent = NewDirectory();
        string output = Path.Combine(parent, "backup"), target = Path.Combine(parent, "recovered");
        await using var host = ManagedRuntimeHost();
        await host.StartAsync();
        var runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
        await runtime.EnableManagedGqlCatalogAsync(root);
        await runtime.ExecuteManagedGqlCommandAsync(Guid.NewGuid(), "CREATE GRAPH firstGraph ANY");
        await runtime.ExecuteManagedGqlCommandAsync(Guid.NewGuid(), "CREATE GRAPH secondGraph ANY");
        await runtime.CreateManagedGqlBackupAsync(output);
        var manifest = JsonSerializer.Deserialize<EntityForgeDbRuntime.ManagedBackupManifest>(await File.ReadAllBytesAsync(Path.Combine(output, "backup.json")))!;
        string first = Path.Combine(output, manifest.Stores[0].StoreId.ToString("N") + ".efbackup");
        using var cancellation = new CancellationTokenSource();
        switch (scenario)
        {
            case "catalog":
                await File.AppendAllTextAsync(Path.Combine(output, "catalog.json"), " ");
                break;
            case "manifest":
                manifest.Stores[0] = manifest.Stores[0] with
                {
                    Sha256 = new string('0', 64)
                };
                await File.WriteAllBytesAsync(Path.Combine(output, "backup.json"), JsonSerializer.SerializeToUtf8Bytes(manifest));
                break;
            case "missing":
                File.Move(first, first + ".missing");
                break;
            case "swapped":
                File.Copy(Path.Combine(output, manifest.Stores[1].StoreId.ToString("N") + ".efbackup"), first, overwrite: true);
                break;
            case "cancelled":
                cancellation.Cancel();
                break;
            case "existing":
                Directory.CreateDirectory(target);
                await File.WriteAllTextAsync(Path.Combine(target, "sentinel"), "preserved");
                break;
        }
        await Assert.ThrowsAsync<Exception>(() => EntityForgeDbRuntime.RestoreManagedGqlBackupAsync(output, target, new EntityForgeDbHostOptions(), cancellation.Token));
        if (scenario == "existing")
        {
            Assert.AreEqual("preserved", await File.ReadAllTextAsync(Path.Combine(target, "sentinel")));
        }
        else
        {
            Assert.IsFalse(Directory.Exists(target));
        }
        Assert.AreEqual(0, Directory.GetDirectories(parent, ".gql-restore-*").Length);
        Assert.IsTrue(runtime.IsReady);
        await host.StopAsync();
    }
}
