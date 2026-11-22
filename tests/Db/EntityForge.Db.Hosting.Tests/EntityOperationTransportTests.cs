using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using EntityForge.Db.Graph;
using EntityForge.Db.Rdf;
using EntityForge.Db.Semantic;
using EntityForge.Db.Transport.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed partial class EntityOperationTransportTests
{
    private const string Administrator = "operation-transport-administrator-token";
    private const string Writer = "operation-transport-writer-token-000001";
    private const string Reader = "operation-transport-reader-token-000001";

    [EntityForge.Db.Mapping.EntityType("operation-transport", "item")]
    public sealed record Item(string Id, string Name);

    [DataTestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph)]
    [DataRow(DatabaseProfileId.Rdf)]
    [DataRow(DatabaseProfileId.Semantic)]
    public async Task Authenticated_lookup_preserves_destination_and_original_outcome(DatabaseProfileId profile)
    {
        string path = Path.Combine(Path.GetTempPath(), "operation-transport-" + Guid.NewGuid().ToString("N"));
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
            Guid operation = Guid.NewGuid();
            Guid incarnation;
            JsonElement original;
            if (profile == DatabaseProfileId.PropertyGraph)
            {
                var client = runtime.GraphDatabase.Operations();
                incarnation = client.DatabaseIncarnation;
                original = JsonSerializer.SerializeToElement(await client.EntityStore<Item>().SaveAsync(new("one", "first"), operation));
                await client.EntityStore<Item>().SaveAsync(new("one", "later"));
            }
            else if (profile == DatabaseProfileId.Rdf)
            {
                var client = runtime.RdfDatabase.Operations();
                incarnation = client.DatabaseIncarnation;
                original = JsonSerializer.SerializeToElement(await client.EntityStore<Item>().SaveAsync(new("one", "first"), operation));
                await client.EntityStore<Item>().SaveAsync(new("one", "later"));
            }
            else
            {
                var client = runtime.Database.Operations();
                incarnation = client.DatabaseIncarnation;
                original = JsonSerializer.SerializeToElement(await client.EntityStore<Item>().SaveAsync(new("one", "first"), new SemanticEntityWriteOperation(operation, DateTimeOffset.UtcNow)));
                await client.EntityStore<Item>().SaveAsync(new("one", "later"));
            }
            var address = host.Application.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()!.Addresses.Single();
            using var http = new HttpClient { BaseAddress = new(address) };
            string route = $"/v1/application/operations/{operation:D}";
            string target = $"?databaseIncarnation={incarnation:D}&expectedProfile={profile}";
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await http.GetAsync(route + target)).StatusCode);
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await http.GetAsync(route + $"?databaseIncarnation={Guid.NewGuid():D}&expectedProfile={profile}")).StatusCode);
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Reader);
            Assert.AreEqual(HttpStatusCode.Conflict, (await http.GetAsync(route + $"?databaseIncarnation={Guid.NewGuid():D}&expectedProfile={profile}")).StatusCode);
            var status = (await http.GetFromJsonAsync<EntityOperationStatusTransport>(route + target))!;
            Assert.AreEqual(EntityOperationStatusTransport.CurrentVersion, status.Version);
            Assert.AreEqual(incarnation, status.DatabaseIncarnation);
            Assert.AreEqual(operation, status.OperationId);
            Assert.AreEqual(profile.ToString(), status.Profile);
            Assert.AreEqual("Committed", status.State);
            Assert.AreEqual(original.GetProperty("Version").GetRawText(), status.Receipt!.Value.GetProperty("Version").GetRawText());
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await http.PostAsync(route + "/abort" + target, null)).StatusCode);
            using (var invalid = new ByteArrayContent([1]))
            {
                Assert.AreEqual(HttpStatusCode.Unauthorized, (await http.PostAsync(route + "/submit" + target, invalid)).StatusCode);
            }

            foreach (string action in new[] { "preparebatch", "apply", "bulk" })
            {
                using var invalid = new ByteArrayContent([1]);
                Assert.AreEqual(HttpStatusCode.Unauthorized, (await http.PostAsync(route + "/" + action + target, invalid)).StatusCode);
            }
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Writer);
            using var abort = await http.PostAsync(route + "/abort" + target, null);
            abort.EnsureSuccessStatusCode();
            Assert.AreEqual("Committed", (await abort.Content.ReadFromJsonAsync<EntityOperationStatusTransport>())!.State);
            var absent = await http.GetFromJsonAsync<EntityOperationStatusTransport>($"/v1/application/operations/{Guid.NewGuid():D}" + target);
            Assert.AreEqual("NotFound", absent!.State);
            Assert.IsNull(absent.Receipt);
            await ExerciseSubmission(runtime, http, profile, incarnation);
            await ExerciseReadScopes(runtime, http, profile, incarnation);
            await ExerciseApplicationClients(runtime, http, profile, incarnation);
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
