using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EntityForge.Db.Hosting;
using EntityForge.Db.Storage.Local;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    public async Task Optimization_jobs_record_exact_receipts_and_authorize_each_operation()
    {
        _directory = NewDirectory();
        var policy = new OptimizationJobPolicy();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory)
            .UseBearerTokenSecurity(new(AdministratorToken, readerToken: ReaderToken, requireHttps: false))
            .ConfigureServices(services => services.AddSingleton<IEntityForgeDbAuthorizationPolicy>(policy))
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host, AdministratorToken);
        var capabilities = await client.GetFromJsonAsync<JsonElement>("/v1/capabilities");
        StringAssert.Contains(capabilities.GetRawText(), "semantic-optimization-jobs-v1");
        var target = await client.GetFromJsonAsync<JsonElement>("/v1/administration/target");
        Guid database = target.GetProperty("databaseIncarnation").GetGuid();
        foreach (string operation in new[] { "optimize", "reset-observations" })
        {
            Guid id = Guid.NewGuid();
            var body = new
            {
                databaseIncarnation = database,
                operation,
                expectedTarget = target.GetProperty("dataDirectory").GetString()
            };
            using var reader = Client(host, ReaderToken);
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await reader.PostAsJsonAsync($"/v1/administration/jobs/{id}", body)).StatusCode);
            policy.DenyChanges = true;
            Assert.AreEqual(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/v1/administration/jobs/{id}", body)).StatusCode);
            policy.DenyChanges = false;
            using var response = await client.PostAsJsonAsync($"/v1/administration/jobs/{id}", body);
            response.EnsureSuccessStatusCode();
            var receipt = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.AreEqual("Succeeded", receipt.GetProperty("state").GetString());
            Assert.AreEqual(operation, receipt.GetProperty("operation").GetString());
            if (operation == "optimize")
            {
                Assert.AreEqual(0, receipt.GetProperty("result").GetProperty("appliedPlacements").GetArrayLength());
            }
            else
            {
                Assert.IsTrue(receipt.GetProperty("result").GetProperty("observationsCleared").GetBoolean());
            }

            using var repeat = await client.PostAsJsonAsync($"/v1/administration/jobs/{id}", body);
            Assert.AreEqual(receipt.GetProperty("updatedAt").GetString(), (await repeat.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("updatedAt").GetString());
            var lookup = await client.GetFromJsonAsync<JsonElement>($"/v1/administration/jobs/{id}?databaseIncarnation={database}");
            Assert.AreEqual(receipt.GetProperty("result").GetRawText(), lookup.GetProperty("result").GetRawText());
        }
        CollectionAssert.Contains(policy.Seen, EntityForgeDbAdministrativeOperation.ApplyPropertyStorageOptimization);
        CollectionAssert.Contains(policy.Seen, EntityForgeDbAdministrativeOperation.ResetPropertyStorageObservations);
    }

    private sealed class OptimizationJobPolicy : IEntityForgeDbAuthorizationPolicy
    {
        public bool DenyChanges;

        public List<EntityForgeDbAdministrativeOperation> Seen { get; } = [];

        public ValueTask<bool> AuthorizeAsync(Microsoft.AspNetCore.Http.HttpContext context, EntityForgeDbAdministrativeOperation operation, CancellationToken cancellationToken = default)
        {
            Seen.Add(operation);
            return ValueTask.FromResult(!DenyChanges || operation is not (EntityForgeDbAdministrativeOperation.ApplyPropertyStorageOptimization or EntityForgeDbAdministrativeOperation.ResetPropertyStorageObservations));
        }
    }

    [TestMethod]
    public async Task Administration_jobs_honor_custom_policy_even_for_administrator_token()
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .UseBearerTokenSecurity(new(AdministratorToken, requireHttps: false))
            .ConfigureServices(services => services.AddSingleton<IEntityForgeDbAuthorizationPolicy, DenyAdministrationPolicy>())
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host, AdministratorToken);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await client.GetAsync("/v1/administration/target")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/v1/administration/jobs/{Guid.NewGuid()}", new
        {
            databaseIncarnation = Guid.NewGuid(),
            operation = "checkpoint",
            expectedTarget = _directory
        })).StatusCode);
        Assert.IsFalse(Directory.Exists(Path.Combine(_directory, ".administration-jobs")));
    }

    [TestMethod]
    [DataRow("graph")]
    [DataRow("rdf")]
    [DataRow("semantic")]
    public async Task Administration_jobs_verify_backups_and_preserve_receipts_across_restart(string profile)
    {
        _directory = NewDirectory();
        EntityForgeDbHost Build()
        {
            var builder = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory)
                .UseAdministration(new(), Path.Combine(_directory, "backups"))
                .UseBearerTokenSecurity(new(AdministratorToken, WriterToken, ReaderToken, requireHttps: false))
                .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0"));
            if (profile == "graph")
            {
                builder.UsePropertyGraphProfile();
            }

            if (profile == "rdf")
            {
                builder.UseRdfProfile();
            }

            return builder.Build();
        }
        Guid job = Guid.NewGuid(), checkpoint = Guid.NewGuid(), interrupted = Guid.NewGuid();
        Guid database;
        string path;
        await using (var host = Build())
        {
            await host.StartAsync();
            using var client = Client(host, AdministratorToken);
            var target = await client.GetFromJsonAsync<JsonElement>("/v1/administration/target");
            database = target.GetProperty("databaseIncarnation").GetGuid();
            path = Path.Combine(target.GetProperty("backupDirectory").GetString()!, $"reviewed-{job:N}.efbackup");
            var body = new
            {
                databaseIncarnation = database,
                operation = "backup",
                backupName = "reviewed",
                expectedTarget = path
            };
            using var created = await client.PostAsJsonAsync($"/v1/administration/jobs/{job}", body);
            created.EnsureSuccessStatusCode();
            var receipt = await created.Content.ReadFromJsonAsync<JsonElement>();
            Assert.AreEqual("Succeeded", receipt.GetProperty("state").GetString());
            var verified = await new LocalDatabaseBackupManager().VerifyAsync(path);
            Assert.AreEqual(database, verified.DatabaseIncarnation);
            var restored = await new LocalDatabaseBackupManager().RestoreAsync(path, Path.Combine(_directory, "restore-check"),
                EntityForgeDbHostOptions.DefaultCheckpointFileName, EntityForgeDbHostOptions.DefaultCommitLogFileName,
                EntityForgeDbHostOptions.DefaultManifestFileName, EntityForgeDbHostOptions.DefaultChangeJournalFileName,
                EntityForgeDbHostOptions.DefaultPageFileName, new EntityForge.Db.Storage.Pages.GroupedRedoRestoreLogInitializer());
            Assert.AreEqual(database, restored.DatabaseIncarnation);
            Assert.AreEqual(verified.Sha256, restored.Sha256);
            string checksum = verified.Sha256;
            using var repeated = await client.PostAsJsonAsync($"/v1/administration/jobs/{job}", body);
            repeated.EnsureSuccessStatusCode();
            Assert.AreEqual(checksum, (await new LocalDatabaseBackupManager().VerifyAsync(path)).Sha256);
            Assert.AreEqual(receipt.GetProperty("updatedAt").GetString(),
                (await repeated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("updatedAt").GetString());
            using var changed = await client.PostAsJsonAsync($"/v1/administration/jobs/{job}", body with
            {
                backupName = "different"
            });
            Assert.AreEqual(HttpStatusCode.BadRequest, changed.StatusCode);
            using var checkpointResponse = await client.PostAsJsonAsync($"/v1/administration/jobs/{checkpoint}", new
            {
                databaseIncarnation = database,
                operation = "checkpoint",
                expectedTarget = target.GetProperty("dataDirectory").GetString()
            });
            checkpointResponse.EnsureSuccessStatusCode();
            Assert.AreEqual("Succeeded", (await checkpointResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("state").GetString());
            // Simulate a persisted intent whose process ended before storing a terminal receipt.
            string running = JsonSerializer.Serialize(new
            {
                version = 1,
                id = interrupted,
                databaseIncarnation = database,
                operation = "checkpoint",
                target = target.GetProperty("dataDirectory").GetString(),
                state = "Running",
                updatedAt = DateTimeOffset.UtcNow
            });
            File.WriteAllText(Path.Combine(_directory, ".administration-jobs", interrupted.ToString("N") + ".json"), running);
        }
        await using (var host = Build())
        {
            await host.StartAsync();
            using var client = Client(host, AdministratorToken);
            var retained = await client.GetFromJsonAsync<JsonElement>($"/v1/administration/jobs/{job}?databaseIncarnation={database}");
            Assert.AreEqual("Succeeded", retained.GetProperty("state").GetString());
            var unknown = await client.GetFromJsonAsync<JsonElement>($"/v1/administration/jobs/{interrupted}?databaseIncarnation={database}");
            Assert.AreEqual("Unknown", unknown.GetProperty("state").GetString());
            Assert.IsTrue(File.Exists(path));
        }
    }

    [TestMethod]
    public async Task Administration_jobs_enforce_roles_identity_targets_and_no_overwrite()
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .UseAdministration(new(), Path.Combine(_directory, "backups"))
            .UseBearerTokenSecurity(new(AdministratorToken, WriterToken, ReaderToken, requireHttps: false))
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var admin = Client(host, AdministratorToken);
        var target = await admin.GetFromJsonAsync<JsonElement>("/v1/administration/target");
        var database = target.GetProperty("databaseIncarnation").GetGuid();
        Guid id = Guid.NewGuid();
        string path = Path.Combine(target.GetProperty("backupDirectory").GetString()!, $"safe-{id:N}.efbackup");
        var body = new
        {
            databaseIncarnation = database,
            operation = "backup",
            backupName = "safe",
            expectedTarget = path
        };
        var capabilities = await admin.GetFromJsonAsync<JsonElement>("/v1/capabilities");
        Assert.IsFalse(capabilities.GetRawText().Contains("semantic-optimization-jobs-v1"));
        Assert.AreEqual(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/v1/administration/jobs/{Guid.NewGuid()}",
            new
            {
                databaseIncarnation = database,
                operation = "optimize",
                expectedTarget = target.GetProperty("dataDirectory").GetString()
            })).StatusCode);
        foreach (string? token in new[] { null, ReaderToken, WriterToken })
        {
            using var client = Client(host, token);
            Assert.AreEqual(HttpStatusCode.Unauthorized,
                (await client.PostAsJsonAsync($"/v1/administration/jobs/{id}", body)).StatusCode);
            Assert.AreEqual(HttpStatusCode.Unauthorized,
                (await client.GetAsync("/v1/administration/target")).StatusCode);
        }
        Assert.AreEqual(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/v1/administration/jobs/{id}", body with
        {
            expectedTarget = "changed"
        })).StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/v1/administration/jobs/{id}", body with
        {
            backupName = "../escape"
        })).StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/v1/administration/jobs/{id}", body with
        {
            databaseIncarnation = Guid.NewGuid()
        })).StatusCode);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "preserve me");
        using var response = await admin.PostAsJsonAsync($"/v1/administration/jobs/{id}", body);
        response.EnsureSuccessStatusCode();
        Assert.AreEqual("Failed", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("state").GetString());
        Assert.AreEqual("preserve me", File.ReadAllText(path));
        Assert.AreEqual(HttpStatusCode.Conflict, (await admin.GetAsync($"/v1/administration/jobs/{id}?databaseIncarnation={Guid.NewGuid()}")).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await admin.GetAsync($"/v1/administration/jobs/{Guid.NewGuid()}?databaseIncarnation={database}")).StatusCode);
    }
}
