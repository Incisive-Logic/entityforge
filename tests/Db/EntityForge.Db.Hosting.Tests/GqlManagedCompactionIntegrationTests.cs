using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    public async Task Managed_gql_compaction_authorizes_review_fences_stale_requests_and_survives_backup_restore()
    {
        _directory = NewDirectory();
        string root = NewDirectory(), backup = Path.Combine(NewDirectory(), "snapshot"), target = NewDirectory();
        Guid oldRequest = Guid.NewGuid(), currentRequest = Guid.NewGuid();
        GqlManagedCatalog.Receipt original;
        await using (var host = ConfiguredManagedHost(root, secured: true))
        {
            await host.StartAsync();
            var runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
            original = await runtime.ExecuteManagedGqlCommandAsync(oldRequest, "CREATE GRAPH sales ANY");
            await runtime.ExecuteManagedGqlCommandAsync(Guid.NewGuid(), "CREATE OR REPLACE GRAPH sales ANY");
            using var admin = Client(host, AdministratorToken);
            using var write = await admin.PostAsJsonAsync("/v1/graph/gql/update", new
            {
                statement = "USE sales INSERT (:Witness {amount:22})"
            });
            Assert.AreEqual(HttpStatusCode.OK, write.StatusCode);
            const string compact = "/v1/administration/gql/catalog/compact";
            foreach (string? token in new[] { null, ReaderToken, WriterToken })
            {
                using var denied = Client(host, token);
                using var response = await denied.PostAsJsonAsync(compact + "/", new
                {
                    generation = 1,
                    version = 2
                });
                Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
            }
            using var staleReview = await admin.PostAsJsonAsync(compact, new
            {
                generation = 1,
                version = 1
            });
            Assert.AreEqual(HttpStatusCode.Conflict, staleReview.StatusCode);
            using var completed = await admin.PostAsJsonAsync(compact, new
            {
                generation = 1,
                version = 2
            });
            Assert.AreEqual(HttpStatusCode.OK, completed.StatusCode, await completed.Content.ReadAsStringAsync());
            string completedBody = await completed.Content.ReadAsStringAsync();
            var inventory = await admin.GetFromJsonAsync<EntityForgeDbRuntime.ManagedRetentionReport>("/v1/administration/gql/retention");
            Assert.AreEqual(2L, inventory!.Generation);
            Assert.AreEqual(2L, inventory.BaseVersion);
            Assert.AreEqual(0, inventory.ReceiptCount);
            Assert.AreEqual(2, inventory.KnownIdentityCount);
            Assert.AreEqual(4096, inventory.IdentityCapacity);
            using var old = await admin.PostAsJsonAsync("/v1/administration/gql/graphs/" + oldRequest,
                new
                {
                    statement = "CREATE GRAPH sales ANY",
                    generation = 1
                });
            Assert.AreEqual(HttpStatusCode.Conflict, old.StatusCode);
            using var missingGeneration = await admin.PostAsJsonAsync("/v1/administration/gql/graphs/" + Guid.NewGuid(),
                new
                {
                    statement = "CREATE GRAPH freshGraph ANY"
                });
            Assert.AreEqual(HttpStatusCode.BadRequest, missingGeneration.StatusCode);
            Assert.AreEqual(2, Directory.GetDirectories(root).Length);
            using var created = await admin.PostAsJsonAsync("/v1/administration/gql/graphs/" + currentRequest,
                new
                {
                    statement = "CREATE GRAPH freshGraph ANY",
                    generation = 2
                });
            Assert.AreEqual(HttpStatusCode.OK, created.StatusCode, await created.Content.ReadAsStringAsync());
            using var replay = await admin.PostAsJsonAsync(compact, new
            {
                generation = 1,
                version = 2
            });
            Assert.AreEqual(HttpStatusCode.OK, replay.StatusCode);
            Assert.AreEqual(completedBody, await replay.Content.ReadAsStringAsync());
            Assert.AreEqual(1, (await runtime.InspectManagedGqlRetentionAsync()).ReceiptCount);
            await runtime.ReclaimManagedGqlStoreAsync(original.StoreId, original.Incarnation);
            await runtime.CreateGqlHostBackupAsync(backup);
            await host.StopAsync();
        }
        var recovery = await EntityForgeDbRuntime.RestoreGqlHostBackupAsync(backup, target, new());
        for (int round = 0; round < 2; round++)
        {
            await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(recovery.DataDirectory).UsePropertyGraphProfile()
                .UseGqlGraphs(recovery.Catalog).ConfigureStorage(options => options.ManagedGqlDirectory = recovery.ManagedDirectory)
                .UseBearerTokenSecurity(new(AdministratorToken, requireHttps: false))
                .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
            await host.StartAsync();
            using var admin = Client(host, AdministratorToken);
            using var replay = await admin.PostAsJsonAsync("/v1/administration/gql/graphs/" + currentRequest,
                new
                {
                    statement = "CREATE GRAPH freshGraph ANY",
                    generation = 2
                });
            Assert.AreEqual(HttpStatusCode.OK, replay.StatusCode, await replay.Content.ReadAsStringAsync());
            using var stale = await admin.PostAsJsonAsync("/v1/administration/gql/graphs/" + oldRequest,
                new
                {
                    statement = "CREATE GRAPH sales ANY",
                    generation = 1
                });
            Assert.AreEqual(HttpStatusCode.Conflict, stale.StatusCode);
            using var read = await admin.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = "USE sales MATCH (n:Witness) RETURN n.amount AS amount"
            });
            Assert.AreEqual(HttpStatusCode.OK, read.StatusCode);
            var rows = (await read.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
            Assert.AreEqual(1, rows.GetArrayLength());
            Assert.AreEqual(22, rows[0].GetProperty("bindings").GetProperty("amount").GetInt32());
            var runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
            Assert.AreEqual(1, (await runtime.InspectManagedGqlRetentionAsync()).RetiredIdentityCount);
            await host.StopAsync();
        }
    }

    [TestMethod]
    public async Task Managed_gql_compaction_respects_custom_manage_policy()
    {
        _directory = NewDirectory();
        string root = NewDirectory();
        var policy = new CompactionPolicy();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .UseGqlGraphs(new("main", new Dictionary<string, string>())).ConfigureStorage(options => options.ManagedGqlDirectory = root)
            .UseBearerTokenSecurity(new(AdministratorToken, requireHttps: false))
            .ConfigureServices(services => services.AddSingleton<IEntityForgeDbAuthorizationPolicy>(policy))
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var admin = Client(host, AdministratorToken);
        using var denied = await admin.PostAsJsonAsync("/v1/administration/gql/catalog/compact", new
        {
            generation = 1,
            version = 0
        });
        Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.IsTrue(policy.SeenManage);
        var runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
        Assert.AreEqual(1L, (await runtime.InspectManagedGqlRetentionAsync()).Generation);
        await host.StopAsync();
    }

    [TestMethod]
    public async Task Managed_gql_compaction_and_graph_creation_share_one_review_and_generation_boundary()
    {
        _directory = NewDirectory();
        string root = NewDirectory();
        await using var host = ConfiguredManagedHost(root, secured: false);
        await host.StartAsync();
        var runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
        for (int index = 0; index < 8; index++)
        {
            var before = await runtime.InspectManagedGqlRetentionAsync();
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<bool> command = Task.Run(async () =>
            {
                await start.Task;
                try
                {
                    await runtime.ExecuteManagedGqlCommandAsync(Guid.NewGuid(), "CREATE GRAPH graph" + index + " ANY", generation: before.Generation);
                    return true;
                }
                catch (GqlCatalogGenerationException)
                {
                    return false;
                }
            });
            Task<bool> compact = Task.Run(async () =>
            {
                await start.Task;
                try
                {
                    await runtime.CompactManagedGqlCatalogAsync(before.Generation, before.CatalogVersion);
                    return true;
                }
                catch (GqlCatalogVersionException)
                {
                    return false;
                }
            });
            start.SetResult();
            bool[] outcomes = await Task.WhenAll(command, compact);
            Assert.AreEqual(1, outcomes.Count(success => success));
            var after = await runtime.InspectManagedGqlRetentionAsync();
            Assert.AreEqual(before.Generation + (outcomes[1] ? 1 : 0), after.Generation);
            Assert.AreEqual(before.CatalogVersion + (outcomes[0] ? 1 : 0), after.CatalogVersion);
            Assert.AreEqual(after.ActiveGraphCount, Directory.GetDirectories(root).Length, "The losing creation must not allocate an orphan.");
        }
        await host.StopAsync();
    }

    private sealed class CompactionPolicy : IEntityForgeDbAuthorizationPolicy
    {
        internal bool SeenManage;

        public ValueTask<bool> AuthorizeAsync(HttpContext context, EntityForgeDbAdministrativeOperation operation, CancellationToken cancellationToken = default)
        {
            SeenManage |= operation == EntityForgeDbAdministrativeOperation.ManageGqlGraphs;
            return ValueTask.FromResult(operation != EntityForgeDbAdministrativeOperation.ManageGqlGraphs);
        }
    }
}
