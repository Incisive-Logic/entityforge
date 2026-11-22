using System.Net;
using System.Net.Http.Headers;
using EntityForge.Db.Semantic;
using EntityForge.Semantics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityOperationTransportTests
{
    [TestMethod]
    public async Task Revisions_require_write_authority_and_the_exact_destination_for_every_action()
    {
        string path = Path.Combine(Path.GetTempPath(), "revision-authority-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(path)
                .UseBearerTokenSecurity(new(Administrator, Writer, Reader, requireHttps: false))
                .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
            await host.StartAsync();
            var database = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>().Database;
            var session = SemanticEntityDeliveryCodec.Session(database.EntityStore<Item>()
                .PrepareSave(new("revision", "value"), SemanticEntityWriteOperation.Create()).Export());
            using var http = new HttpClient
            {
                BaseAddress = new(host.Application.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.Single())
            };
            ISemanticEntityClient client = new HttpSemanticEntityClient(http, database.Operations().DatabaseIncarnation);
            var source = new SemanticContributionScope(session.Start.Source.SourceSystem, session.Start.Source.SourcePartition);
            Func<ISemanticContributionRevisionWriter, Task>[] mutations =
            [
                async writer =>
{
 await writer.BeginRevisionAsync(session.Start, null);
},
                async writer =>
{
 await writer.AcceptRevisionPartAsync(session.Pages.Single());
},
                async writer =>
{
 await writer.SealRevisionAsync(session.Close);
},
                async writer =>
{
 await writer.PublishRevisionAsync(session.Start.SessionId);
},
                async writer =>
{
 await writer.AbandonRevisionAsync(session.Start.SessionId);
}
];
            var before = database.CurrentVersion;
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Reader);
            foreach (var mutate in mutations)
            {
                var failure = await Assert.ThrowsExceptionAsync<HttpRequestException>(() => mutate(client.Revisions));
                Assert.AreEqual(HttpStatusCode.Unauthorized, failure.StatusCode);
            }
            Assert.IsNull(await client.Revisions.GetRevisionAsync(session.Start.SessionId));
            Assert.IsNull(await client.Revisions.GetPublishedRevisionAsync(source));
            Assert.AreEqual(before, database.CurrentVersion);
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Writer);
            ISemanticEntityClient wrong = new HttpSemanticEntityClient(http, Guid.NewGuid());
            foreach (var mutate in mutations)
            {
                var failure = await Assert.ThrowsExceptionAsync<HttpRequestException>(() => mutate(wrong.Revisions));
                Assert.AreEqual(HttpStatusCode.Conflict, failure.StatusCode);
            }
            await Assert.ThrowsExceptionAsync<HttpRequestException>(async () => await wrong.Revisions.GetRevisionAsync(session.Start.SessionId));
            await Assert.ThrowsExceptionAsync<HttpRequestException>(async () => await wrong.Revisions.GetPublishedRevisionAsync(source));
            Assert.AreEqual(before, database.CurrentVersion);
            Assert.IsNull(await database.Operations().Revisions.GetRevisionAsync(session.Start.SessionId));

            await client.Revisions.BeginRevisionAsync(session.Start, null);
            await client.Revisions.AcceptRevisionPartAsync(session.Pages.Single());
            await client.Revisions.SealRevisionAsync(session.Close);
            await client.Revisions.PublishRevisionAsync(session.Start.SessionId);
            Assert.AreEqual(session.Start.SessionId, (await client.Revisions.GetPublishedRevisionAsync(source))!.GenerationId);
            Assert.AreEqual(SemanticContributionRevisionState.Published, (await client.Revisions.GetRevisionAsync(session.Start.SessionId))!.State);
            Assert.AreEqual(session.Start.SessionId, (await database.Operations().Revisions.GetPublishedRevisionAsync(source))!.GenerationId);
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
