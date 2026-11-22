using System.Diagnostics;
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
    public async Task Gql_host_backup_jobs_authorize_backup_and_retain_outcomes_across_restart()
    {
        _directory = NewDirectory();
        string root = NewDirectory(), backups = NewDirectory();
        Guid id = Guid.NewGuid(), graphRequest = Guid.NewGuid();
        string? original = null;
        var policy = new GqlBackupJobPolicy();
        for (int round = 0; round < 2; round++)
        {
            await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
                .UseGqlGraphs(new("main", new Dictionary<string, string>()))
                .ConfigureStorage(options => options.ManagedGqlDirectory = root).UseAdministration(new(), backups)
                .UseBearerTokenSecurity(new(AdministratorToken, WriterToken, ReaderToken, requireHttps: false))
                .ConfigureServices(services => services.AddSingleton<IEntityForgeDbAuthorizationPolicy>(policy))
                .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
            await host.StartAsync();
            using var admin = Client(host, AdministratorToken);
            using var graph = await admin.PostAsJsonAsync("/v1/administration/gql/graphs/" + graphRequest, new
            {
                statement = "CREATE GRAPH sales ANY",
                generation = 1
            });
            Assert.AreEqual(HttpStatusCode.OK, graph.StatusCode);
            var target = await admin.GetFromJsonAsync<JsonElement>("/v1/administration/target");
            string path = Path.Combine(backups, $"snapshot-{id:N}.efgqlbackup");
            var body = new
            {
                databaseIncarnation = target.GetProperty("databaseIncarnation").GetGuid(),
                operation = "gql-host-backup",
                backupName = "snapshot",
                expectedTarget = path
            };
            string route = "/v1/administration/jobs/" + id;
            using var writer = Client(host, WriterToken);
            using var denied = await writer.PostAsJsonAsync(route, body);
            Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
            policy.DenyBackup = true;
            using var policyDenied = await admin.PostAsJsonAsync(route, body);
            Assert.AreEqual(HttpStatusCode.Forbidden, policyDenied.StatusCode);
            policy.DenyBackup = false;
            using var response = await admin.PostAsJsonAsync(route, body);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            var job = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.AreEqual("Succeeded", job.GetProperty("state").GetString(), job.GetRawText());
            original ??= job.GetRawText();
            Assert.AreEqual(original, job.GetRawText());
            Assert.AreEqual("gql-host", job.GetProperty("result").GetProperty("kind").GetString());
            var manifest = JsonSerializer.Deserialize<EntityForgeDbRuntime.ManagedBackupManifest>(await File.ReadAllBytesAsync(Path.Combine(path, "backup.json")))!;
            Assert.AreEqual(2, manifest.Stores.Length);
            using var conflict = await admin.PostAsJsonAsync(route, new
            {
                body.databaseIncarnation,
                operation = "gql-host-backup",
                backupName = "different",
                expectedTarget = Path.Combine(backups, $"different-{id:N}.efgqlbackup")
            });
            Assert.AreEqual(HttpStatusCode.BadRequest, conflict.StatusCode);
            await host.StopAsync();
        }
        CollectionAssert.Contains(policy.Seen, EntityForgeDbAdministrativeOperation.CreateBackup);
    }

    [TestMethod]
    public async Task Gql_host_backup_cli_restores_and_rebacks_up_recovered_configuration()
    {
        _directory = NewDirectory();
        string root = NewDirectory(), backup = NewDirectory(), restored = NewDirectory(), secondBackup = NewDirectory();
        await using (var host = ConfiguredManagedHost(root, secured: false))
        {
            await host.StartAsync();
            var runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
            await runtime.ExecuteManagedGqlCommandAsync(Guid.NewGuid(), "CREATE GRAPH sales ANY");
            using var client = Client(host);
            using var write = await client.PostAsJsonAsync("/v1/graph/gql/update", new
            {
                statement = "USE sales INSERT (:Person {name:'Ada'})"
            });
            Assert.AreEqual(HttpStatusCode.OK, write.StatusCode);
            await host.StopAsync();
        }
        async Task Run(bool recovered, params string[] arguments)
        {
            var start = HistoryServerStart(_directory);
            start.Environment["ENTITYFORGE_DB_PROFILE"] = "property-graph";
            start.Environment[recovered ? "ENTITYFORGE_DB_GQL_RECOVERY_DIRECTORY" : "ENTITYFORGE_DB_GQL_MANAGED_DIRECTORY"] = recovered ? restored : root;
            foreach (string argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }
            using var process = Process.Start(start)!;
            Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try
            {
                await process.WaitForExitAsync(deadline.Token);
                Assert.AreEqual(0, process.ExitCode, await output + await error);
            }
            finally
            {
                await StopHistoryServer(process);
                await Task.WhenAll(output, error);
            }
        }
        await Run(false, "backup-gql-host", backup);
        await Run(false, "restore-gql-host", backup, restored);
        await Run(true, "backup-gql-host", secondBackup);
        string secondRestored = NewDirectory();
        await Run(false, "restore-gql-host", secondBackup, secondRestored);
        var recovery = await EntityForgeDbRuntime.ReadRecoveredGqlHostAsync(secondRestored, new EntityForgeDbHostOptions());
        await using var recoveredHost = EntityForgeDbServer.CreateBuilder().UseLocalStorage(recovery.DataDirectory).UsePropertyGraphProfile()
            .UseGqlGraphs(recovery.Catalog).ConfigureStorage(options => options.ManagedGqlDirectory = recovery.ManagedDirectory)
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await recoveredHost.StartAsync();
        using var reader = Client(recoveredHost);
        using var read = await reader.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "USE sales MATCH (p:Person) RETURN p.name AS name"
        });
        Assert.AreEqual(HttpStatusCode.OK, read.StatusCode);
        var rows = (await read.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
        Assert.AreEqual(1, rows.GetArrayLength());
        Assert.AreEqual("Ada", rows[0].GetProperty("bindings").GetProperty("name").GetString());
        await recoveredHost.StopAsync();
    }

    private sealed class GqlBackupJobPolicy : IEntityForgeDbAuthorizationPolicy
    {
        internal bool DenyBackup;

        internal List<EntityForgeDbAdministrativeOperation> Seen { get; } = [];

        public ValueTask<bool> AuthorizeAsync(HttpContext context, EntityForgeDbAdministrativeOperation operation,
            CancellationToken cancellationToken = default)
        {
            Seen.Add(operation);
            return ValueTask.FromResult(!DenyBackup || operation != EntityForgeDbAdministrativeOperation.CreateBackup);
        }
    }
}
