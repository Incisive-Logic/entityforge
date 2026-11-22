using System.Net;
using System.Text;
using EntityForge.Db.AppModel.Queries;
using EntityForge.Db.Client;
using EntityForge.Semantics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EntityForge.Db.AppModel.Tests;

[TestClass]
public sealed class HttpSemanticQueryMetadataProviderTests
{
    [TestMethod]
    public async Task Semantic_transport_preserves_catalogue_identifiers_and_friendly_names()
    {
        const string json = """
            {"vocabularyVersion":1,"vocabularies":[],"identitySchemes":[],"concepts":[{"identifier":"https://schema.org/Organization","displayName":"Organisation","properties":[{"identifier":"https://schema.org/name","displayName":"Name","valueKind":"string","minimum":0,"maximum":1}],"relationships":[]}]}
            """;
        using var client = new HttpClient(new Handler(json)) { BaseAddress = new Uri("http://localhost/") };
        var provider = new HttpSemanticQueryMetadataProvider(client);

        SemanticQueryMetadata metadata = await provider.GetAsync();

        Assert.AreEqual("Organisation", metadata.Concepts.Single().DisplayName);
        Assert.AreEqual("https://schema.org/name",
            metadata.PropertiesByConcept.Values.Single().Single().Identifier);
    }

    [TestMethod]
    public async Task Semantic_transport_scopes_properties_and_relationships_to_their_concepts()
    {
        const string json = """
            {"vocabularyVersion":1,"vocabularies":[],"identitySchemes":[],"concepts":[{"identifier":"https://example.test/company","displayName":"Company","properties":[{"identifier":"https://example.test/industry","displayName":"Industry","valueKind":"string","minimum":0,"maximum":1}],"relationships":[{"identifier":"https://example.test/employs","displayName":"Employs","targetConcepts":["https://example.test/employee"],"minimum":0}]},{"identifier":"https://example.test/employee","displayName":"Employee","properties":[{"identifier":"https://example.test/jobTitle","displayName":"Job title","valueKind":"string","minimum":0,"maximum":1}],"relationships":[]}]}
            """;
        using var client = new HttpClient(new Handler(json)) { BaseAddress = new Uri("http://localhost/") };

        SemanticQueryMetadata metadata = await new HttpSemanticQueryMetadataProvider(client).GetAsync();

        const string company = "https://example.test/company";
        const string employee = "https://example.test/employee";
        Assert.AreEqual("https://example.test/industry", metadata.PropertiesByConcept[company].Single().Identifier);
        Assert.AreEqual("https://example.test/jobTitle", metadata.PropertiesByConcept[employee].Single().Identifier);
        Assert.AreEqual(employee, metadata.RelationshipsByConcept[company].Single().TargetTypes!.Single());
        Assert.AreEqual(0, metadata.RelationshipsByConcept[employee].Count);
    }

    [TestMethod]
    public void Query_metadata_uses_precise_value_kinds_to_select_valid_operators()
    {
        var number = new SemanticCatalogProperty(new Uri("https://example.test/headcount"), "Headcount",
            SemanticValueKind.Integer, 0, 1);
        var concept = new SemanticCatalogConcept(new Uri("https://example.test/company"), "Company", null,
            [number], [], [], [], []);

        SemanticQueryProperty property = SemanticQueryMetadataMapper.FromCatalog(
            new SemanticCatalog(1, [], [], [concept])).PropertiesByConcept[concept.Identifier.AbsoluteUri].Single();

        Assert.AreEqual(QueryValueEditor.Number, property.Editor);
        Assert.IsTrue(property.Operators.Contains(QueryComparisonOperator.GreaterThan));
        Assert.IsFalse(property.Operators.Contains(QueryComparisonOperator.Contains));
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
