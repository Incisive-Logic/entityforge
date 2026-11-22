using System.Net.Http.Headers;
using EntityForge.Db.Graph;
using EntityForge.Db.Rdf;
using EntityForge.Db.Semantic;
using EntityForge.Db.Mapping;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityOperationTransportTests
{
    [TestMethod]
    public async Task Semantic_initial_load_keeps_its_specialised_native_replay_contract()
    {
        string path = Path.Combine(Path.GetTempPath(), "http-bulk-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(path)
                .UseBearerTokenSecurity(new(Administrator, Writer, Reader, requireHttps: false))
                .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
            await host.StartAsync();
            var runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
            var prepared = runtime.Database.EntityStore<Item>().PrepareSave(new("bulk", "initial"), SemanticEntityWriteOperation.Create());
            var session = SemanticEntityDeliveryCodec.Session(prepared.Export());
            using var http = new HttpClient
            {
                BaseAddress = new(host.Application.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.Single())
            };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Writer);
            var client = new HttpSemanticEntityClient(http, runtime.Database.Operations().DatabaseIncarnation);
            var receipt = await client.BulkLoadAsync(session);
            Assert.AreEqual(receipt.Version, (await client.BulkLoadAsync(session)).Version);
            // Native initial loading does not register an entity mapping. Hosted reader
            // admission rejects it until the mapping has been provisioned by the owner.
            await Assert.ThrowsExceptionAsync<HttpRequestException>(async () => await client.EntityStore<Item>().FindAsync("bulk"));
            foreach (var manifest in runtime.Database.EntityStore<Item>().Manifests)
            {
                await runtime.Database.Mappings().RegisterAsync(manifest);
            }

            Assert.AreEqual("initial", (await runtime.Database.EntityStore<Item>().FindAsync("bulk"))!.Name);
            Assert.AreEqual("initial", (await client.EntityStore<Item>().FindAsync("bulk"))!.Name);
            // Initial loading has native session replay, not an application binding record.
            Assert.AreEqual(EntityOperationState.NotFound, (await client.ResolveAsync(session.Start.SessionId)).State);
        }
        finally
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
    }

    private sealed class LostReply : DelegatingHandler
    {
        internal bool LoseNext;

        internal LostReply() : base(new HttpClientHandler())
        {
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (LoseNext && request.RequestUri!.AbsolutePath.EndsWith("/submit"))
            {
                LoseNext = false;
                response.EnsureSuccessStatusCode();
                response.Dispose();
                throw new HttpRequestException("Simulated lost successful reply.");
            }
            return response;
        }
    }

    [DataTestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph)]
    [DataRow(DatabaseProfileId.Rdf)]
    [DataRow(DatabaseProfileId.Semantic)]
    public async Task Public_clients_recover_bound_operations_lost_replies_and_checkpoint_replays(DatabaseProfileId profile)
    {
        string path = Path.Combine(Path.GetTempPath(), "http-client-recovery-" + Guid.NewGuid().ToString("N"));
        Guid operation = Guid.NewGuid(), incarnation = Guid.Empty;
        var observed = DateTimeOffset.UtcNow;
        DatabaseVersion committed = default;
        try
        {
            for (int phase = 0; phase < 3; phase++)
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
                var database = profile switch
                {
                    DatabaseProfileId.PropertyGraph => (IEntityOperationHost)runtime.GraphDatabase,
                    DatabaseProfileId.Rdf => (IEntityOperationHost)runtime.RdfDatabase,
                    _ => (IEntityOperationHost)runtime.Database
                };
                Guid actual = profile switch
                {
                    DatabaseProfileId.PropertyGraph => runtime.GraphDatabase.Operations().DatabaseIncarnation,
                    DatabaseProfileId.Rdf => runtime.RdfDatabase.Operations().DatabaseIncarnation,
                    _ => runtime.Database.Operations().DatabaseIncarnation
                };
                if (phase == 0)
                {
                    incarnation = actual;
                }

                Assert.AreEqual(incarnation, actual);
                using var http = new HttpClient(new LostReply())
                {
                    BaseAddress = new(host.Application.Services.GetRequiredService<IServer>()
                    .Features.Get<IServerAddressesFeature>()!.Addresses.Single())
                };
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Writer);
                var graph = new HttpGraphEntityClient(http, incarnation);
                var rdf = new HttpRdfEntityClient(http, incarnation);
                var semantic = new HttpSemanticEntityClient(http, incarnation);
                async Task<DatabaseVersion> Save(string name, Guid id) => profile switch
                {
                    DatabaseProfileId.PropertyGraph => (await graph.EntityStore<Item>().SaveAsync(new("recovery", name), id)).Version,
                    DatabaseProfileId.Rdf => (await rdf.EntityStore<Item>().SaveAsync(new("recovery", name), id)).Version,
                    _ => (await semantic.EntityStore<Item>().SaveAsync(new("recovery", name), new SemanticEntityWriteOperation(id, observed))).Version
                };
                async Task<Item?> Read() => profile switch
                {
                    DatabaseProfileId.PropertyGraph => await graph.EntityStore<Item>().FindAsync("recovery"),
                    DatabaseProfileId.Rdf => await rdf.EntityStore<Item>().FindAsync("recovery"),
                    _ => await semantic.EntityStore<Item>().FindAsync("recovery")
                };
                if (phase == 0)
                {
                    database.Operations.AfterBindingPersisted = () => throw new IOException("Stopped after durable binding.");
                    await Assert.ThrowsExceptionAsync<HttpRequestException>(() => Save("original", operation));
                    continue;
                }
                if (phase == 1)
                {
                    // The server commits; the HTTP response is discarded before reaching the client.
                    using var lostHandler = new LostReply { LoseNext = true };
                    using var lostHttp = new HttpClient(lostHandler) { BaseAddress = http.BaseAddress };
                    lostHttp.DefaultRequestHeaders.Authorization = http.DefaultRequestHeaders.Authorization;
                    await Assert.ThrowsExceptionAsync<HttpRequestException>(async () =>
                    {
                        if (profile == DatabaseProfileId.PropertyGraph)
                        {
                            await new HttpGraphEntityClient(lostHttp, incarnation).EntityStore<Item>().SaveAsync(new("recovery", "original"), operation);
                        }
                        else if (profile == DatabaseProfileId.Rdf)
                        {
                            await new HttpRdfEntityClient(lostHttp, incarnation).EntityStore<Item>().SaveAsync(new("recovery", "original"), operation);
                        }
                        else
                        {
                            await new HttpSemanticEntityClient(lostHttp, incarnation).EntityStore<Item>().SaveAsync(new("recovery", "original"), new SemanticEntityWriteOperation(operation, observed));
                        }
                    });
                    committed = await Save("original", operation);
                    await Save("later", Guid.NewGuid());
                }
                Assert.AreEqual(committed, await Save("original", operation));
                Assert.AreEqual("later", (await Read())!.Name);
                var resolved = profile switch
                {
                    DatabaseProfileId.PropertyGraph => (await graph.ResolveAsync(operation)).Receipt!.Version,
                    DatabaseProfileId.Rdf => (await rdf.ResolveAsync(operation)).Receipt!.Version,
                    _ => (await semantic.ResolveAsync(operation)).Receipt!.Version
                };
                Assert.AreEqual(committed, resolved);
                if (phase == 1)
                {
                    if (profile == DatabaseProfileId.PropertyGraph)
                    {
                        await runtime.GraphDatabase.CreateCheckpointAsync();
                    }
                    else if (profile == DatabaseProfileId.Rdf)
                    {
                        await runtime.RdfDatabase.CreateCheckpointAsync();
                    }
                    else
                    {
                        await runtime.Database.CreateCheckpointAsync();
                    }
                }
            }
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
