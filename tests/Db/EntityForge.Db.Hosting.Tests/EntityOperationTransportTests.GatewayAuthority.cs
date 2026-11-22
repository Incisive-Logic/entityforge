using System.Net.Http.Headers;
using EntityForge.Db.Graph;
using EntityForge.Db.Rdf;
using EntityForge.Db.Semantic;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityOperationTransportTests
{
    [TestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph)]
    [DataRow(DatabaseProfileId.Rdf)]
    [DataRow(DatabaseProfileId.Semantic)]
    public async Task Gateway_reader_cannot_register_a_mapping_through_an_embedded_client(DatabaseProfileId profile)
    {
        string root = Path.Combine(Path.GetTempPath(), "gateway-authority-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var graph = await GraphDatabase.OpenLocalAsync(new(Path.Combine(root, "graph")));
            await using var rdf = await RdfDatabase.OpenLocalAsync(new(Path.Combine(root, "rdf")));
            await using var semantic = await SemanticDatabase.OpenLocalAsync(new(Path.Combine(root, "semantic")));
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var security = new EntityForgeDbBearerSecurityOptions(Administrator, Writer, Reader, requireHttps: false);
            await using var gateway = profile switch
            {
                DatabaseProfileId.PropertyGraph => EntityForgeDbApplicationGateway.Build(builder, graph.Operations(), security),
                DatabaseProfileId.Rdf => EntityForgeDbApplicationGateway.Build(builder, rdf.Operations(), security),
                _ => EntityForgeDbApplicationGateway.Build(builder, semantic.Operations(), security)
            };
            await gateway.StartAsync();
            using var http = new HttpClient
            {
                BaseAddress = new(gateway.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.Single())
            };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Reader);
            Func<Task<Item?>> read;
            Func<Task> save;
            Func<DatabaseVersion> version;
            if (profile == DatabaseProfileId.PropertyGraph)
            {
                var store = new HttpGraphEntityClient(http, graph.Operations().DatabaseIncarnation).EntityStore<Item>();
                read = () => store.FindAsync("one").AsTask();
                save = async () => await store.SaveAsync(new("one", "writer"));
                version = () => graph.CurrentVersion;
            }
            else if (profile == DatabaseProfileId.Rdf)
            {
                var store = new HttpRdfEntityClient(http, rdf.Operations().DatabaseIncarnation).EntityStore<Item>();
                read = () => store.FindAsync("one").AsTask();
                save = async () => await store.SaveAsync(new("one", "writer"));
                version = () => rdf.CurrentVersion;
            }
            else
            {
                var store = new HttpSemanticEntityClient(http, semantic.Operations().DatabaseIncarnation).EntityStore<Item>();
                read = () => store.FindAsync("one").AsTask();
                save = async () => await store.SaveAsync(new("one", "writer"));
                version = () => semantic.CurrentVersion;
            }
            var before = version();
            await Assert.ThrowsExactlyAsync<HttpRequestException>(async () => await read());
            Assert.AreEqual(before, version(), "A reader must not persist a manifest.");
            http.DefaultRequestHeaders.Authorization = new("Bearer", Writer);
            await save();
            http.DefaultRequestHeaders.Authorization = new("Bearer", Reader);
            Assert.AreEqual(new Item("one", "writer"), await read());
            await gateway.StopAsync();
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
