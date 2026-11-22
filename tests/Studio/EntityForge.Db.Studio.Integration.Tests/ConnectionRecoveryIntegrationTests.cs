using System.Net.Http.Json;
using EntityForge.Db.AppModel.Connections;
using EntityForge.Db.AppModel.Queries;
using EntityForge.Db.Hosting;
using EntityForge.Semantics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EntityForge.Db.Studio.Integration.Tests;

[TestClass]
public sealed class ConnectionRecoveryIntegrationTests
{
    [TestMethod]
    public async Task Live_catalogue_publication_and_server_restart_preserve_drafts_and_restore_execution()
    {
        string root = Path.Combine(Path.GetTempPath(), "studio-recovery-" + Guid.NewGuid().ToString("N"));
        const string token = "studio-recovery-administrator-token-0001";
        using var connection = new EntityForgeConnectionSession();
        using var builder = new EntityQuerySession();
        using var native = new NativeQuerySession();
        try
        {
            Uri address;
            var draft = new SemanticQueryDocument(new("urn:Company", "Company"), [], []);
            const string text = "query { entities(concept: \"urn:Company\") { nodes { identity } } }";
            await using (var host = CreateHost("http://127.0.0.1:0"))
            {
                await host.StartAsync();
                address = new(host.Application.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
                await connection.ConnectAsync(new("demo", "Demo", address), token);
                Assert.AreEqual(0, connection.SemanticCatalog!.Concepts.Count);
                builder.Bind(connection.QueryExecutor);
                native.Bind(connection.NativeQueryExecutor);
                builder.EditDraft(draft);
                native.Draft = text;
                using var seed = new HttpClient { BaseAddress = address };
                seed.DefaultRequestHeaders.Authorization = new("Bearer", token);
                using var published = await seed.PostAsJsonAsync("v1/semantic/vocabularies",
                    new SemanticVocabularyPublicationRequest("urn:studio:vocabulary", "1",
                        [new("urn:Company", SemanticTermRole.Concept, "Company")]));
                Assert.IsTrue(published.IsSuccessStatusCode, await published.Content.ReadAsStringAsync());
                var changed = await connection.CheckForUpdatesAsync();
                Assert.IsTrue(changed.CatalogChanged);
                Assert.IsFalse(changed.SessionInvalidated);
                Assert.AreEqual("Company", connection.SemanticQueryMetadata!.Concepts.Single().DisplayName);
                builder.RefreshBinding(connection.QueryExecutor, changed.SessionInvalidated);
                await builder.ExecuteAsync(draft);
                Assert.AreEqual(EntityQueryExecutionState.Empty, builder.State, builder.Error);
                await host.StopAsync();
            }
            var lost = await connection.CheckForUpdatesAsync();
            Assert.IsFalse(connection.Availability!.Available);
            builder.RefreshBinding(connection.QueryExecutor, lost.SessionInvalidated);
            native.RefreshBinding(connection.NativeQueryExecutor, lost.SessionInvalidated);
            Assert.IsNull(builder.Result);
            await using (var restarted = CreateHost(address.AbsoluteUri))
            {
                await restarted.StartAsync();
                var recovered = await connection.CheckForUpdatesAsync();
                Assert.IsTrue(recovered.SessionInvalidated);
                Assert.IsTrue(connection.Availability!.Available);
                builder.RefreshBinding(connection.QueryExecutor, recovered.SessionInvalidated);
                native.RefreshBinding(connection.NativeQueryExecutor, recovered.SessionInvalidated);
                Assert.AreSame(draft, builder.Draft);
                Assert.AreEqual(text, native.Draft);
                await builder.ExecuteAsync(builder.Draft!);
                await native.ExecuteAsync();
                Assert.AreEqual(EntityQueryExecutionState.Empty, builder.State, builder.Error);
                Assert.AreEqual(EntityQueryExecutionState.Succeeded, native.State, native.Error);
                await restarted.StopAsync();
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }

        EntityForgeDbHost CreateHost(string url) => EntityForgeDbServer.CreateBuilder().UseSemanticProfile().UseLocalStorage(root)
            .UseBearerTokenSecurity(new(token, requireHttps: false)).ConfigureWebHost(web => web.UseUrls(url)).Build();
    }
}
