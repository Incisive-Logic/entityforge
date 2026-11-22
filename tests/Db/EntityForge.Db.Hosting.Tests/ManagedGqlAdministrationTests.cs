using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    public async Task Managed_gql_administration_requires_admin_and_replays_commands_after_automatic_startup()
    {
        _directory = NewDirectory();
        string root = NewDirectory();
        Guid request = Guid.NewGuid();
        JsonElement original = default;
        for (int round = 0; round < 2; round++)
        {
            await using var host = ConfiguredManagedHost(root, secured: true);
            await host.StartAsync();
            var runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
            Assert.IsTrue(runtime.IsReady);
            string path = "/v1/administration/gql/graphs/" + request;
            foreach (string? token in new[] { null, ReaderToken, WriterToken })
            {
                using var denied = Client(host, token);
                using var response = await denied.PostAsJsonAsync(path + "/", new
                {
                    statement = "CREATE GRAPH sales ANY",
                    generation = 1
                });
                Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
            }
            using var admin = Client(host, AdministratorToken);
            using var created = await admin.PostAsJsonAsync(path, new
            {
                statement = "CREATE GRAPH sales ANY",
                generation = 1
            });
            Assert.AreEqual(HttpStatusCode.OK, created.StatusCode, await created.Content.ReadAsStringAsync());
            var body = await created.Content.ReadFromJsonAsync<JsonElement>();
            Assert.AreEqual(1L, body.GetProperty("catalogVersion").GetInt64());
            if (round == 0)
            {
                original = body;
            }
            else
            {
                Assert.AreEqual(original.GetRawText(), body.GetRawText());
            }
            using var writer = Client(host, WriterToken);
            using var query = await writer.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = "USE sales RETURN 1 AS answer"
            });
            Assert.AreEqual(HttpStatusCode.OK, query.StatusCode);
            using var mixed = await admin.PostAsJsonAsync("/v1/administration/gql/graphs/" + Guid.NewGuid(), new
            {
                statement = "DROP GRAPH sales CREATE GRAPH other ANY",
                generation = 1
            });
            Assert.IsFalse(mixed.IsSuccessStatusCode);
            using var conflict = await admin.PostAsJsonAsync(path, new
            {
                statement = "DROP GRAPH sales",
                generation = 1
            });
            Assert.AreEqual(HttpStatusCode.BadRequest, conflict.StatusCode);
            using var ordinary = await writer.PostAsJsonAsync("/v1/graph/gql/update", new
            {
                statement = "DROP GRAPH sales"
            });
            Assert.IsFalse(ordinary.IsSuccessStatusCode);
            Assert.AreEqual(1, Directory.GetDirectories(root).Length);
            await host.StopAsync();
        }
        byte[] metadata = await File.ReadAllBytesAsync(Path.Combine(root, "catalog.json"));
        await File.WriteAllTextAsync(Path.Combine(root, "catalog.json"), "{}");
        await using (var invalid = ConfiguredManagedHost(root, secured: true))
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => invalid.StartAsync());
            Assert.IsFalse(invalid.Application.Services.GetRequiredService<EntityForgeDbRuntime>().IsReady);
        }
        await File.WriteAllBytesAsync(Path.Combine(root, "catalog.json"), metadata);
        await using var repaired = ConfiguredManagedHost(root, secured: true);
        await repaired.StartAsync();
        Assert.IsTrue(repaired.Application.Services.GetRequiredService<EntityForgeDbRuntime>().IsReady);
        await repaired.StopAsync();
    }

    [TestMethod]
    public async Task Managed_gql_administration_denies_unsecured_host_without_explicit_policy()
    {
        _directory = NewDirectory();
        string root = NewDirectory();
        await using var host = ConfiguredManagedHost(root, secured: false);
        await host.StartAsync();
        using var client = Client(host);
        using var response = await client.PostAsJsonAsync("/v1/administration/gql/graphs/" + Guid.NewGuid(), new
        {
            statement = "CREATE GRAPH sales ANY",
            generation = 1
        });
        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.AreEqual(0, Directory.GetDirectories(root).Length);
        await host.StopAsync();
    }

    [TestMethod]
    [DataRow("missing-catalog")]
    [DataRow("overlap")]
    [DataRow("rdf")]
    public void Managed_gql_startup_configuration_rejects_invalid_ownership(string scenario)
    {
        var options = new EntityForgeDbHostOptions
        {
            DataDirectory = NewDirectory(),
            Profile = scenario == "rdf" ? DatabaseProfileId.Rdf : DatabaseProfileId.PropertyGraph,
            GqlGraphs = scenario == "missing-catalog" ? null : new("main", new Dictionary<string, string>()),
            ManagedGqlDirectory = NewDirectory()
        };
        if (scenario == "overlap")
        {
            options.ManagedGqlDirectory = Path.Combine(options.DataDirectory, "managed");
        }
        Assert.ThrowsException<ArgumentException>(() => options.Snapshot());
    }

    private EntityForgeDbHost ConfiguredManagedHost(string root, bool secured)
    {
        var builder = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory!).UsePropertyGraphProfile()
            .UseGqlGraphs(new("main", new Dictionary<string, string>()))
            .ConfigureStorage(options => options.ManagedGqlDirectory = root)
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0"));
        if (secured)
        {
            builder.UseBearerTokenSecurity(new(AdministratorToken, WriterToken, ReaderToken, requireHttps: false));
        }
        return builder.Build();
    }
}
