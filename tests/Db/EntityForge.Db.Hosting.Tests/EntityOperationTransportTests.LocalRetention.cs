using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using EntityForge.Db.Graph;
using EntityForge.Db.Rdf;
using EntityForge.Db.Semantic;
using EntityForge.Db.Transport.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityOperationTransportTests
{
    [DataTestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph)]
    [DataRow(DatabaseProfileId.Rdf)]
    [DataRow(DatabaseProfileId.Semantic)]
    public async Task Local_expiry_is_authenticated_destination_bound_and_preserved_by_http_clients(DatabaseProfileId profile)
    {
        string path = Path.Combine(Path.GetTempPath(), "local-expiry-http-" + Guid.NewGuid().ToString("N"));
        try
        {
            var builder = EntityForgeDbServer.CreateBuilder().UseLocalStorage(path)
                .UseBearerTokenSecurity(new(Administrator, Writer, Reader, requireHttps: false))
                .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0"));
            if (profile == DatabaseProfileId.PropertyGraph)
            {
                builder.UsePropertyGraphProfile();
            }

            if (profile == DatabaseProfileId.Rdf)
            {
                builder.UseRdfProfile();
            }

            await using var host = builder.Build();
            await host.StartAsync();
            var runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
            IDatabaseOperationRetention maintenance = profile switch
            {
                DatabaseProfileId.PropertyGraph => (IDatabaseOperationRetention)runtime.GraphDatabase,
                DatabaseProfileId.Rdf => (IDatabaseOperationRetention)runtime.RdfDatabase,
                _ => (IDatabaseOperationRetention)runtime.Database
            };
            Guid incarnation = profile switch
            {
                DatabaseProfileId.PropertyGraph => runtime.GraphDatabase.Operations().DatabaseIncarnation,
                DatabaseProfileId.Rdf => runtime.RdfDatabase.Operations().DatabaseIncarnation,
                _ => runtime.Database.Operations().DatabaseIncarnation
            };
            var cutoff = DateTimeOffset.UtcNow.AddDays(-1);
            Guid operation = EntityOperationIds.New(cutoff.AddDays(-1));
            using var http = new HttpClient
            {
                BaseAddress = new(host.Application.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.Single())
            };
            http.DefaultRequestHeaders.Authorization = new("Bearer", Writer);
            Func<Task> submit = profile switch
            {
                DatabaseProfileId.PropertyGraph => async () => await new HttpGraphEntityClient(http, incarnation)
                    .EntityStore<Item>().SaveAsync(new("one", "value"), operation),
                DatabaseProfileId.Rdf => async () => await new HttpRdfEntityClient(http, incarnation)
                    .EntityStore<Item>().SaveAsync(new("one", "value"), operation),
                _ => async () => await new HttpSemanticEntityClient(http, incarnation)
                    .EntityStore<Item>().SaveAsync(new("one", "value"), new SemanticEntityWriteOperation(operation, DateTimeOffset.UtcNow))
            };
            await submit();
            await maintenance.RetainOperationsAsync(cutoff);
            await Assert.ThrowsExactlyAsync<EntityOperationExpiredException>(submit);
            string route = $"/v1/application/operations/{operation:D}?databaseIncarnation={incarnation:D}&expectedProfile={profile}";
            http.DefaultRequestHeaders.Authorization = null;
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await http.GetAsync(route)).StatusCode);
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Reader);
            Assert.AreEqual(HttpStatusCode.Conflict, (await http.GetAsync(route.Replace(incarnation.ToString("D"), Guid.NewGuid().ToString("D")))).StatusCode);
            using var response = await http.GetAsync(route);
            Assert.AreEqual(HttpStatusCode.Gone, response.StatusCode);
            var status = (await response.Content.ReadFromJsonAsync<EntityOperationStatusTransport>())!;
            Assert.AreEqual("Expired", status.State);
            Assert.AreEqual(operation, status.OperationId);
            Assert.AreEqual(incarnation, status.DatabaseIncarnation);
            Assert.AreEqual(profile.ToString(), status.Profile);
            Assert.IsNull(status.Receipt);
        }
        finally
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
    }
}
