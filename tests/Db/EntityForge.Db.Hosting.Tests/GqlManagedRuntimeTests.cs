using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EntityForge.Db.Graph;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Managed_gql_failed_conditional_publication_preserves_original_store(bool replace)
    {
        _directory = NewDirectory();
        string root = NewDirectory();
        Guid request = Guid.NewGuid();
        var mode = replace ? GqlManagedCatalog.CreateMode.Replace : GqlManagedCatalog.CreateMode.IfAbsent;
        GqlManagedCatalog.Receipt original;
        await using (var host = ManagedRuntimeHost())
        {
            await host.StartAsync();
            var runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
            await runtime.EnableManagedGqlCatalogAsync(root);
            original = await runtime.CreateManagedGqlGraphAsync(Guid.NewGuid(), "sales");
            var selected = runtime.SelectGqlGraph("USE sales RETURN 1 AS answer");
            using (selected.Lifetime)
            {
                Directory.CreateDirectory(Path.Combine(root, "catalog.json.new"));
                await Assert.ThrowsAsync<IOException>(() => runtime.CreateManagedGqlGraphAsync(request, "sales", mode: mode));
                Assert.IsFalse(runtime.IsReady);
                using var snapshot = await selected.Database.AcquireSnapshotAsync();
                Assert.AreEqual(original.Incarnation, selected.Database.Operations().DatabaseIncarnation);
            }
            await host.StopAsync();
        }
        Directory.Delete(Path.Combine(root, "catalog.json.new"));
        var priorDirectories = Directory.GetDirectories(root).Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal);
        Assert.AreEqual(replace ? 2 : 1, priorDirectories.Count);
        await using var reopened = ManagedRuntimeHost();
        await reopened.StartAsync();
        var recovered = reopened.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
        await recovered.EnableManagedGqlCatalogAsync(root);
        var restored = recovered.SelectGqlGraph("USE sales RETURN 1 AS answer");
        using (restored.Lifetime)
        {
            Assert.AreEqual(original.Incarnation, restored.Database.Operations().DatabaseIncarnation);
        }
        var outcome = await recovered.CreateManagedGqlGraphAsync(request, "sales", mode: mode);
        Assert.AreEqual(replace, outcome.Changed);
        Assert.AreEqual(original.After, outcome.Before);
        Assert.AreEqual(!replace, priorDirectories.Contains(outcome.StoreId.ToString("N")));
        Assert.AreEqual(outcome, await recovered.CreateManagedGqlGraphAsync(request, "sales", mode: mode));
        Assert.AreEqual(replace ? 3 : 1, Directory.GetDirectories(root).Length);
        await reopened.StopAsync();
    }

    [TestMethod]
    public async Task Managed_gql_conditional_receipts_do_not_rebind_after_restart_or_later_changes()
    {
        _directory = NewDirectory();
        string root = NewDirectory();
        Guid createNoop = Guid.NewGuid(), dropNoop = Guid.NewGuid(), replace = Guid.NewGuid();
        GqlManagedCatalog.Receipt? created = null, dropped = null, replaced = null;
        for (int round = 0; round < 2; round++)
        {
            await using var host = ManagedRuntimeHost();
            await host.StartAsync();
            var runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
            await runtime.EnableManagedGqlCatalogAsync(root);
            if (round == 0)
            {
                await runtime.CreateManagedGqlGraphAsync(Guid.NewGuid(), "sales");
                created = await runtime.CreateManagedGqlGraphAsync(createNoop, "sales", mode: GqlManagedCatalog.CreateMode.IfAbsent);
                dropped = await runtime.DropManagedGqlGraphAsync(dropNoop, "absent", ifExists: true);
                Assert.IsFalse(created.Changed);
                Assert.IsFalse(dropped.Changed);
                Assert.AreEqual(1, Directory.GetDirectories(root).Length);
                await runtime.CreateManagedGqlGraphAsync(Guid.NewGuid(), "absent", mode: GqlManagedCatalog.CreateMode.IfAbsent);
                await runtime.DropManagedGqlGraphAsync(Guid.NewGuid(), "sales");
                replaced = await runtime.CreateManagedGqlGraphAsync(replace, "sales", mode: GqlManagedCatalog.CreateMode.Replace);
                Assert.IsNull(replaced.Before);
            }
            Assert.AreEqual(created, await runtime.CreateManagedGqlGraphAsync(createNoop, "sales", mode: GqlManagedCatalog.CreateMode.IfAbsent));
            Assert.AreEqual(dropped, await runtime.DropManagedGqlGraphAsync(dropNoop, "absent", ifExists: true));
            Assert.AreEqual(replaced, await runtime.CreateManagedGqlGraphAsync(replace, "sales", mode: GqlManagedCatalog.CreateMode.Replace));
            var graph = runtime.SelectGqlGraph("USE sales RETURN 1 AS answer");
            using (graph.Lifetime)
            {
                Assert.AreEqual(replaced!.Incarnation, graph.Database.Operations().DatabaseIncarnation);
            }
            runtime.SelectGqlGraph("USE absent RETURN 1 AS answer").Lifetime.Dispose();
            Assert.AreEqual(3, Directory.GetDirectories(root).Length);
            await host.StopAsync();
        }
    }

    [TestMethod]
    public async Task Managed_gql_replacement_publishes_new_route_while_old_readers_drain()
    {
        _directory = NewDirectory();
        string root = NewDirectory();
        await using var host = ManagedRuntimeHost();
        await host.StartAsync();
        var runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
        await runtime.EnableManagedGqlCatalogAsync(root);
        var first = await runtime.CreateManagedGqlGraphAsync(Guid.NewGuid(), "sales");
        var old = runtime.SelectGqlGraph("USE sales RETURN 1 AS answer");
        Guid request = Guid.NewGuid();
        using var caller = new CancellationTokenSource();
        Task<GqlManagedCatalog.Receipt>? replacing = null;
        try
        {
            replacing = runtime.CreateManagedGqlGraphAsync(request, "sales", caller.Token, GqlManagedCatalog.CreateMode.Replace);
            Guid current = first.Incarnation;
            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while (current == first.Incarnation && DateTime.UtcNow < deadline)
            {
                var selected = runtime.SelectGqlGraph("USE sales RETURN 1 AS answer");
                using (selected.Lifetime)
                {
                    current = selected.Database.Operations().DatabaseIncarnation;
                }
                if (current == first.Incarnation)
                {
                    await Task.Delay(10);
                }
            }
            Assert.AreNotEqual(first.Incarnation, current);
            Assert.IsFalse(replacing.IsCompleted, "Old readers must retain the replaced store.");
            caller.Cancel();
            using var snapshot = await old.Database.AcquireSnapshotAsync();
            Assert.AreEqual(first.Incarnation, old.Database.Operations().DatabaseIncarnation);
        }
        finally
        {
            old.Lifetime.Dispose();
            if (replacing is not null)
            {
                var receipt = await replacing.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.AreEqual(first.After, receipt.Before);
                Assert.AreEqual(receipt, await runtime.CreateManagedGqlGraphAsync(request, "sales", mode: GqlManagedCatalog.CreateMode.Replace));
            }
            await host.StopAsync();
        }
        Assert.AreEqual(2, Directory.GetDirectories(root).Length);
    }

    [TestMethod]
    public async Task Managed_gql_reopen_rejects_swapped_database_identities_and_releases_locks()
    {
        _directory = NewDirectory();
        string root = NewDirectory();
        GqlManagedCatalog.Receipt first, second;
        await using (var host = ManagedRuntimeHost())
        {
            await host.StartAsync();
            var runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
            await runtime.EnableManagedGqlCatalogAsync(root);
            first = await runtime.CreateManagedGqlGraphAsync(Guid.NewGuid(), "first");
            second = await runtime.CreateManagedGqlGraphAsync(Guid.NewGuid(), "second");
            await host.StopAsync();
        }
        string firstPath = Path.Combine(root, first.StoreId.ToString("N"));
        string secondPath = Path.Combine(root, second.StoreId.ToString("N"));
        string staging = Path.Combine(root, "swap-test");
        Directory.Move(firstPath, staging);
        Directory.Move(secondPath, firstPath);
        Directory.Move(staging, secondPath);
        await using var reopened = ManagedRuntimeHost();
        await reopened.StartAsync();
        var recovered = reopened.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
        await Assert.ThrowsAsync<InvalidDataException>(() => recovered.EnableManagedGqlCatalogAsync(root));
        Assert.ThrowsExactly<ArgumentException>(() => recovered.SelectGqlGraph("USE first RETURN 1 AS answer"));
        await using (var inspected = await GraphDatabase.OpenLocalAsync(new(firstPath)))
        {
            Assert.AreEqual(second.Incarnation, inspected.Operations().DatabaseIncarnation);
        }
        await using (var inspected = await GraphDatabase.OpenLocalAsync(new(secondPath)))
        {
            Assert.AreEqual(first.Incarnation, inspected.Operations().DatabaseIncarnation);
        }
        using (var catalog = new GqlManagedCatalog(root))
        {
            Assert.AreEqual(first.Incarnation, catalog.Entries["first"].Incarnation);
        }
        await reopened.StopAsync();
    }

    [TestMethod]
    public async Task Managed_gql_concurrent_retries_prepare_one_store_and_reject_conflicting_identities()
    {
        _directory = NewDirectory();
        string root = NewDirectory();
        await using var host = ManagedRuntimeHost();
        await host.StartAsync();
        var runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
        await runtime.EnableManagedGqlCatalogAsync(root);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => runtime.CreateManagedGqlGraphAsync(Guid.NewGuid(), "cancelled", cancelled.Token));
        Assert.AreEqual(0, Directory.GetDirectories(root).Length);
        Guid create = Guid.NewGuid(), drop = Guid.NewGuid();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            return await runtime.CreateManagedGqlGraphAsync(create, "sales");
        })).ToArray();
        start.SetResult();
        var receipts = await Task.WhenAll(requests);
        Assert.IsTrue(receipts.All(receipt => receipt == receipts[0]));
        Assert.AreEqual(1, Directory.GetDirectories(root).Length);
        await Assert.ThrowsAsync<ArgumentException>(() => runtime.CreateManagedGqlGraphAsync(create, "other"));
        await Assert.ThrowsAsync<ArgumentException>(() => runtime.DropManagedGqlGraphAsync(create, "sales"));
        var drops = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => runtime.DropManagedGqlGraphAsync(drop, "sales")));
        Assert.IsTrue(drops.All(receipt => receipt == drops[0]));
        Assert.AreEqual(1, Directory.GetDirectories(root).Length);
        Assert.ThrowsExactly<ArgumentException>(() => runtime.SelectGqlGraph("USE sales RETURN 1 AS answer"));
        await host.StopAsync();
    }

    [TestMethod]
    public async Task Managed_gql_retention_budget_blocks_new_preparation_but_keeps_receipt_replay()
    {
        _directory = NewDirectory();
        string root = NewDirectory();
        await using var host = ManagedRuntimeHost();
        await host.StartAsync();
        var runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
        await runtime.EnableManagedGqlCatalogAsync(root);
        Guid create = Guid.NewGuid();
        var receipt = await runtime.CreateManagedGqlGraphAsync(create, "sales");
        for (int index = 0; index < 63; index++)
        {
            Directory.CreateDirectory(Path.Combine(root, "retained-test-" + index));
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.CreateManagedGqlGraphAsync(Guid.NewGuid(), "overflow"));
        Assert.AreEqual(receipt, await runtime.CreateManagedGqlGraphAsync(create, "sales"));
        Assert.AreEqual(64, Directory.GetDirectories(root).Length);
        Assert.IsTrue(runtime.IsReady);
        await host.StopAsync();
    }

    [TestMethod]
    public async Task Managed_gql_reopen_refuses_empty_replacement_of_cataloged_store()
    {
        _directory = NewDirectory();
        string root = NewDirectory();
        Guid identity;
        await using (var host = ManagedRuntimeHost())
        {
            await host.StartAsync();
            var runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
            await runtime.EnableManagedGqlCatalogAsync(root);
            identity = (await runtime.CreateManagedGqlGraphAsync(Guid.NewGuid(), "sales")).StoreId;
            await host.StopAsync();
        }
        string path = Path.Combine(root, identity.ToString("N"));
        Directory.Move(path, Path.Combine(root, "retained-for-test"));
        Directory.CreateDirectory(path);
        await using var reopened = ManagedRuntimeHost();
        await reopened.StartAsync();
        var recovered = reopened.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
        await Assert.ThrowsAsync<InvalidDataException>(() => recovered.EnableManagedGqlCatalogAsync(root));
        Assert.AreEqual(0, Directory.GetFileSystemEntries(path).Length, "Failed open must not initialize an empty replacement store.");
        await reopened.StopAsync();
    }

    [TestMethod]
    public async Task Managed_gql_runtime_persists_routes_and_replays_original_operations_after_replacement()
    {
        _directory = NewDirectory();
        string catalogRoot = NewDirectory();
        Guid create = Guid.NewGuid(), drop = Guid.NewGuid(), replace = Guid.NewGuid();
        GqlManagedCatalog.Receipt? first = null, removed = null, replacement = null;
        for (int round = 0; round < 3; round++)
        {
            await using var host = ManagedRuntimeHost();
            await host.StartAsync();
            var runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
            await runtime.EnableManagedGqlCatalogAsync(catalogRoot);
            var replay = await runtime.CreateManagedGqlGraphAsync(create, "sales.data");
            first ??= replay;
            Assert.AreEqual(first, replay);
            using var client = Client(host);
            if (round == 0)
            {
                using var write = await client.PostAsJsonAsync("/v1/graph/gql/update", new
                {
                    statement = "USE `sales.data` INSERT (:Person {name:'Ada'})"
                });
                Assert.AreEqual(HttpStatusCode.OK, write.StatusCode, await write.Content.ReadAsStringAsync());
            }
            if (round == 1)
            {
                removed = await runtime.DropManagedGqlGraphAsync(drop, "sales.data");
                replacement = await runtime.CreateManagedGqlGraphAsync(replace, "sales.data");
                Assert.AreNotEqual(first.StoreId, replacement.StoreId);
                using var write = await client.PostAsJsonAsync("/v1/graph/gql/update", new
                {
                    statement = "USE `sales.data` INSERT (:Person {name:'Bob'})"
                });
                Assert.AreEqual(HttpStatusCode.OK, write.StatusCode, await write.Content.ReadAsStringAsync());
            }
            if (round > 0)
            {
                Assert.AreEqual(first, await runtime.CreateManagedGqlGraphAsync(create, "sales.data"));
                Assert.AreEqual(removed, await runtime.DropManagedGqlGraphAsync(drop, "sales.data"));
                Assert.AreEqual(replacement, await runtime.CreateManagedGqlGraphAsync(replace, "sales.data"));
            }
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = "USE `sales.data` MATCH (p:Person) RETURN p.name AS name"
            });
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            var rows = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
            Assert.AreEqual(1, rows.GetArrayLength());
            Assert.AreEqual(round == 0 ? "Ada" : "Bob", rows[0].GetProperty("bindings").GetProperty("name").GetString());
            Assert.AreEqual(round == 0 ? 1 : 2, Directory.GetDirectories(catalogRoot).Length);
            await host.StopAsync();
        }
    }

    [TestMethod]
    public async Task Managed_gql_drop_removes_routing_but_drains_readers_before_shutdown()
    {
        _directory = NewDirectory();
        await using var host = ManagedRuntimeHost();
        await host.StartAsync();
        var runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
        await runtime.EnableManagedGqlCatalogAsync(NewDirectory());
        await runtime.CreateManagedGqlGraphAsync(Guid.NewGuid(), "sales");
        var selected = runtime.SelectGqlGraph("USE sales RETURN 1 AS answer");
        using var caller = new CancellationTokenSource();
        Task? stopping = null;
        Task<GqlManagedCatalog.Receipt>? dropping = null;
        try
        {
            dropping = runtime.DropManagedGqlGraphAsync(Guid.NewGuid(), "sales", caller.Token);
            Assert.IsFalse(dropping.IsCompleted);
            Assert.ThrowsExactly<ArgumentException>(() => runtime.SelectGqlGraph("USE sales RETURN 1 AS answer"));
            caller.Cancel();
            using (var snapshot = await selected.Database.AcquireSnapshotAsync())
            {
                Assert.IsFalse(dropping.IsCompleted);
            }
            stopping = runtime.StopAsync(CancellationToken.None);
            Assert.IsFalse(stopping.IsCompleted);
        }
        finally
        {
            selected.Lifetime.Dispose();
            if (dropping is not null)
            {
                await dropping.WaitAsync(TimeSpan.FromSeconds(10));
            }
            if (stopping is not null)
            {
                await stopping.WaitAsync(TimeSpan.FromSeconds(10));
            }
            await host.StopAsync();
        }
    }

    [TestMethod]
    public async Task Managed_gql_failed_publication_fails_closed_and_does_not_adopt_orphan_store()
    {
        _directory = NewDirectory();
        string root = NewDirectory();
        await using (var host = ManagedRuntimeHost())
        {
            await host.StartAsync();
            var runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
            await runtime.EnableManagedGqlCatalogAsync(root);
            Directory.CreateDirectory(Path.Combine(root, "catalog.json.new"));
            await Assert.ThrowsAsync<IOException>(() => runtime.CreateManagedGqlGraphAsync(Guid.NewGuid(), "unpublished"));
            Assert.IsFalse(runtime.IsReady);
            Assert.ThrowsExactly<InvalidOperationException>(() => runtime.SelectGqlGraph("RETURN 1 AS answer"));
            await host.StopAsync();
        }
        Directory.Delete(Path.Combine(root, "catalog.json.new"));
        await using var reopened = ManagedRuntimeHost();
        await reopened.StartAsync();
        var recovered = reopened.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
        await recovered.EnableManagedGqlCatalogAsync(root);
        Assert.ThrowsExactly<ArgumentException>(() => recovered.SelectGqlGraph("USE unpublished RETURN 1 AS answer"));
        await recovered.CreateManagedGqlGraphAsync(Guid.NewGuid(), "published");
        Assert.AreEqual(2, Directory.GetDirectories(root).Length, "The failed preparation is retained, never adopted as the new store.");
        Assert.IsTrue(recovered.IsReady);
        await reopened.StopAsync();
    }

    [TestMethod]
    public async Task Managed_gql_runtime_rejects_overlapping_storage_and_protects_default_graph()
    {
        _directory = NewDirectory();
        await using var host = ManagedRuntimeHost();
        await host.StartAsync();
        var runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
        await Assert.ThrowsAsync<ArgumentException>(() => runtime.EnableManagedGqlCatalogAsync(Path.Combine(_directory, "nested")));
        await Assert.ThrowsAsync<ArgumentException>(() => runtime.EnableManagedGqlCatalogAsync(Path.GetPathRoot(_directory)!));
        await runtime.EnableManagedGqlCatalogAsync(NewDirectory());
        await Assert.ThrowsAsync<ArgumentException>(() => runtime.CreateManagedGqlGraphAsync(Guid.NewGuid(), "main"));
        await Assert.ThrowsAsync<ArgumentException>(() => runtime.DropManagedGqlGraphAsync(Guid.NewGuid(), "main"));
        await Assert.ThrowsAsync<ArgumentException>(() => runtime.CreateManagedGqlGraphAsync(Guid.Empty, "invalid"));
        Assert.IsTrue(runtime.IsReady);
        await host.StopAsync();
    }

    private EntityForgeDbHost ManagedRuntimeHost() => EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory!)
        .UsePropertyGraphProfile().UseGqlGraphs(new("main", new Dictionary<string, string>()))
        .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
}
