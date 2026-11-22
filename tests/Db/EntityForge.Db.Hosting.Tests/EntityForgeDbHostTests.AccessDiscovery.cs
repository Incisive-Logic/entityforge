using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EntityForge.Db.Hosting;
using EntityForge.Db.Transport.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    public async Task Access_discovery_reports_roles_without_secrets_and_advertises_operation_identity()
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .UseBearerTokenSecurity(new(AdministratorToken, WriterToken, ReaderToken, requireHttps: false))
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var anonymous = Client(host);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/v1/security/access")).StatusCode);
        var capabilities = await anonymous.GetFromJsonAsync<EntityForgeHostCapabilitiesTransport>("/v1/capabilities");
        Assert.IsTrue(capabilities!.Surfaces.Any(s => s.Identifier == EntityForgeSurfaceIds.AccessDiscovery));
        Assert.IsTrue(capabilities.Surfaces.Any(s => s.Identifier == EntityForgeSurfaceIds.ApplicationOperations));
        Assert.AreNotEqual(Guid.Empty, capabilities.DatabaseIncarnation);
        foreach (var role in new[] { (ReaderToken, "Read", false, false), (WriterToken, "Write", true, false), (AdministratorToken, "Administration", true, true) })
        {
            using var client = Client(host, role.Item1);
            using var response = await client.GetAsync("/v1/security/access");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.IsTrue(response.Headers.CacheControl!.NoStore);
            string json = await response.Content.ReadAsStringAsync();
            Assert.IsFalse(json.Contains(role.Item1));
            var report = JsonSerializer.Deserialize<EntityForgeAccessReport>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            Assert.AreEqual(role.Item2, report.Role);
            Assert.AreEqual(true, report.CanRead);
            Assert.AreEqual(role.Item3, report.CanWrite);
            Assert.AreEqual(role.Item4, report.CanAdminister);
            Assert.AreEqual(capabilities.DatabaseIncarnation, report.DatabaseIncarnation);
            var missing = await client.GetFromJsonAsync<EntityOperationStatusTransport>($"/v1/application/operations/{Guid.NewGuid()}?databaseIncarnation={report.DatabaseIncarnation}&expectedProfile=PropertyGraph");
            Assert.AreEqual("NotFound", missing!.State);
        }
    }

    [TestMethod]
    public async Task Access_discovery_does_not_guess_custom_administration_policy()
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .UseBearerTokenSecurity(new(AdministratorToken, requireHttps: false))
            .ConfigureServices(services => services.AddSingleton<IEntityForgeDbAuthorizationPolicy, DiscoveryTestPolicy>())
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host, AdministratorToken);
        var report = await client.GetFromJsonAsync<EntityForgeAccessReport>("/v1/security/access");
        Assert.IsNull(report!.CanAdminister);
        Assert.AreEqual(true, report.CanRead);
    }

    private sealed class DiscoveryTestPolicy : IEntityForgeDbAuthorizationPolicy
    {
        public ValueTask<bool> AuthorizeAsync(HttpContext context, EntityForgeDbAdministrativeOperation operation, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Discovery must not execute a resource-specific policy against the wrong route.");
    }
}
