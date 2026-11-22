using System.Net;
using System.Text;
using EntityForge.Db.Client;
using EntityForge.Semantics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EntityForge.Db.AppModel.Tests;

[TestClass]
public sealed class HttpSemanticCatalogProviderTests
{
    [TestMethod]
    public async Task Semantic_catalogue_transport_preserves_resolved_logical_shape()
    {
        const string json = """
            {"vocabularyVersion":4,"vocabularies":[{"identifier":"https://example.test/hr","version":"2","conceptCount":1,"propertyCount":1,"relationshipCount":0}],"identitySchemes":[],"concepts":[{"identifier":"https://example.test/Employee","displayName":"Employee","description":"A member of staff.","properties":[{"identifier":"https://example.test/name","displayName":"Name","valueKind":"string","minimum":1,"maximum":1}],"relationships":[]}]}
            """;
        using var client = new HttpClient(new Handler(json)) { BaseAddress = new Uri("http://localhost/") };

        SemanticCatalog catalog = await new HttpSemanticCatalogProvider(client).GetAsync();

        Assert.AreEqual(4, catalog.VocabularyVersion);
        Assert.AreEqual("Employee", catalog.Concepts.Single().DisplayName);
        Assert.AreEqual(1, catalog.Concepts.Single().Properties.Single().Minimum);
        Assert.AreEqual(SemanticValueKind.String, catalog.Concepts.Single().Properties.Single().ValueKind);
    }

    private sealed class Handler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
    }
}
