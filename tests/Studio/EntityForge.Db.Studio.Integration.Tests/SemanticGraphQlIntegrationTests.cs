using EntityForge.Db.AppModel.Connections;
using EntityForge.Db.AppModel.Queries;
using EntityForge.Db.Hosting;
using EntityForge.Db.Semantic;
using EntityForge.Semantics.Protobuf;
using EntityForge.Semantics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Net.Http.Json;

namespace EntityForge.Db.Studio.Integration.Tests;

[TestClass]
public sealed class SemanticGraphQlIntegrationTests
{
    [TestMethod]
    public async Task Studio_GraphQl_returns_typed_entities_evidence_schema_and_validation_errors()
    {
        string root = Path.Combine(Path.GetTempPath(), "studio-graphql-" + Guid.NewGuid().ToString("N"));
        const string token = "studio-graphql-reader-token-000000001";
        try
        {
            await using var host = EntityForgeDbServer.CreateBuilder().UseSemanticProfile().UseLocalStorage(root)
                .UseBearerTokenSecurity(new("studio-graphql-administrator-token-0001", readerToken: token, requireHttps: false))
                .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
            await host.StartAsync();
            Guid sessionId = Guid.NewGuid();
            var provider = new Uri("urn:provider:registry");
            var identity = new SemanticEntityIdentity(new(new Uri("urn:Company"), SemanticTermRole.Concept),
                new(new Uri("urn:number"), "Number", SemanticValueKind.String), new StringSemanticValue("1"));
            var change = new SemanticEntityChange(Guid.NewGuid(), SemanticEntityChangeKind.ReplaceSourceState, identity,
                new SemanticProvenance(provider, "1", DateTimeOffset.UtcNow,
                    new(new Uri("urn:vocabulary"), "1", SemanticModelKind.Vocabulary)), fields:
                [new(new(new Uri("urn:name"), SemanticTermRole.Property), SemanticFieldChangeKind.Set, new StringSemanticValue("Acme"))]);
            SemanticChangeSessionPage[] pages = [new(sessionId, 0, [change])];
            var codec = new SemanticChangeSessionTransportCodec();
            var envelope = new SemanticSessionEnvelope(new(sessionId, SemanticChangeSessionKind.Incremental,
                new(provider, new Uri("urn:partition"), "1", Guid.NewGuid(), "1"), Guid.NewGuid(), DateTimeOffset.UtcNow),
                pages, new(sessionId, 1, 1, codec.ComputeContentHash(pages), DateTimeOffset.UtcNow));
            var address = new Uri(host.Application.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            using var seed = new HttpClient { BaseAddress = address };
            seed.DefaultRequestHeaders.Authorization = new("Bearer", "studio-graphql-administrator-token-0001");
            using var applied = await seed.PostAsJsonAsync("v1/semantic/sessions", new
            {
                start = Convert.ToBase64String(codec.Serialize(envelope.Start)),
                pages = envelope.Pages.Select(p => Convert.ToBase64String(codec.Serialize(p))).ToArray(),
                close = Convert.ToBase64String(codec.Serialize(envelope.Close))
            });
            Assert.IsTrue(applied.IsSuccessStatusCode, await applied.Content.ReadAsStringAsync());
            using var connection = new EntityForgeConnectionSession();
            await connection.ConnectAsync(new("graphql", "GraphQL", address), token);
            Assert.IsNotNull(connection.NativeQueryExecutor);
            Assert.AreEqual(NativeQueryLanguage.GraphQl, connection.NativeQueryExecutor.Language);
            using var session = new NativeQuerySession();
            session.Bind(connection.NativeQueryExecutor);
            session.Draft = """
                query Read($concept: URI!) { entities(concept: $concept, includeEvidence: true) {
                  nodes { identity name: field(property: "urn:name") { values { value evidence } } }
                } }
                """;
            session.Variables = "{\"concept\":\"urn:Company\"}";
            await session.ExecuteAsync();
            Assert.AreEqual(EntityQueryExecutionState.Succeeded, session.State, session.Error);
            var document = session.Result!.Entities!.Document;
            Assert.AreEqual(1, document.Roots.Count);
            Assert.AreEqual("name", document.Roots[0].Fields.Single().Name);
            Assert.IsTrue(document.Evidence.Count > 0);
            Assert.AreEqual("Acme", session.Result.Document.GetProperty("data").GetProperty("entities").GetProperty("nodes")[0]
                .GetProperty("name").GetProperty("values")[0].GetProperty("value").GetString());
            var schema = await connection.NativeQueryExecutor.ExecuteAsync(GraphQlEditorLanguage.IntrospectionQuery);
            var language = GraphQlEditorLanguage.FromIntrospection(schema.Document);
            const string draft = "{ entities(concept: \"urn:Company\") { nodes { fi";
            Assert.IsTrue(language.Complete(draft, draft.Length).Items.Any(s => s.Name == "field"));
            Assert.AreEqual(0, language.Validate("{ entities(concept: \"urn:Company\") { nodes { identity } } }").Count);
            Assert.AreEqual(0, language.Validate("{ __schema { queryType { name fields { name description } } } }").Count);
            session.Draft = "{ unknownField }";
            await session.ExecuteAsync();
            Assert.AreEqual(EntityQueryExecutionState.Failed, session.State);
            Assert.IsNull(session.Result);
            await host.StopAsync();
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
