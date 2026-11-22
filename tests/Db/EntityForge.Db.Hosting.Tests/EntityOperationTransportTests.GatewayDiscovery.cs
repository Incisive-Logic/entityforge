using System.Net;
using System.Net.Http.Json;
using EntityForge.Db.Graph;
using EntityForge.Db.Rdf;
using EntityForge.Db.Semantic;
using EntityForge.Db.Transport.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityOperationTransportTests
{
    [TestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph, false)]
    [DataRow(DatabaseProfileId.Rdf, false)]
    [DataRow(DatabaseProfileId.Semantic, false)]
    [DataRow(DatabaseProfileId.Semantic, true)]
    public async Task Gateway_discovery_advertises_only_its_actual_surfaces(DatabaseProfileId profile, bool supplyReadiness)
    {
        string root = Path.Combine(Path.GetTempPath(), "gateway-discovery-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var graph = await GraphDatabase.OpenLocalAsync(new(Path.Combine(root, "graph")));
            await using var rdf = await RdfDatabase.OpenLocalAsync(new(Path.Combine(root, "rdf")));
            await using var semantic = await SemanticDatabase.OpenLocalAsync(new(Path.Combine(root, "semantic")));
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            bool ready = true;
            var discovery = supplyReadiness ? new EntityForgeDbGatewayDiscovery(_ => ValueTask.FromResult(new EntityForgeDbGatewayReadiness(ready, ready ? "Ready" : "Recovering"))) : null;
            var security = new EntityForgeDbBearerSecurityOptions(Administrator, Writer, Reader, requireHttps: false);
            await using var app = profile switch
            {
                DatabaseProfileId.PropertyGraph => EntityForgeDbApplicationGateway.Build(builder, graph.Operations(), security, discovery),
                DatabaseProfileId.Rdf => EntityForgeDbApplicationGateway.Build(builder, rdf.Operations(), security, discovery),
                _ => EntityForgeDbApplicationGateway.Build(builder, semantic.Operations(), security, discovery)
            };
            await app.StartAsync();
            using var http = new HttpClient { BaseAddress = new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
            Assert.AreEqual(HttpStatusCode.OK, (await http.GetAsync("health/live")).StatusCode);
            Assert.AreEqual(supplyReadiness ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable, (await http.GetAsync("health/ready")).StatusCode);
            ready = false;
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, (await http.GetAsync("health/ready")).StatusCode);
            var capabilities = (await http.GetFromJsonAsync<EntityForgeHostCapabilitiesTransport>("v1/capabilities"))!;
            Assert.AreEqual("application-gateway", capabilities.HostKind);
            Assert.AreEqual(profile switch
            {
                DatabaseProfileId.PropertyGraph => graph.Operations().DatabaseIncarnation,
                DatabaseProfileId.Rdf => rdf.Operations().DatabaseIncarnation,
                _ => semantic.Operations().DatabaseIncarnation
            }, capabilities.DatabaseIncarnation);
            Assert.AreEqual(3, capabilities.Surfaces.Count);
            Assert.IsTrue(capabilities.Surfaces.Any(s => s.Identifier == EntityForgeSurfaceIds.AccessDiscovery));
            Assert.IsTrue(capabilities.Surfaces.Any(s => s.Identifier == EntityForgeSurfaceIds.ApplicationOperations));
            Assert.IsFalse(capabilities.Surfaces.Any(s => s.Identifier is EntityForgeSurfaceIds.SemanticCatalog or EntityForgeSurfaceIds.Administration or EntityForgeSurfaceIds.EntityProjection));
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await http.GetAsync($"v1/application/operations/{Guid.NewGuid()}?databaseIncarnation={capabilities.DatabaseIncarnation}&expectedProfile={profile}")).StatusCode);
            http.DefaultRequestHeaders.Authorization = new("Bearer", Reader);
            var access = await http.GetFromJsonAsync<EntityForgeAccessReport>("v1/security/access");
            Assert.AreEqual(true, access!.CanRead);
            Assert.AreEqual(false, access.CanWrite);
            Assert.AreEqual(capabilities.DatabaseIncarnation, access.DatabaseIncarnation);
            await app.StopAsync();
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }
}
