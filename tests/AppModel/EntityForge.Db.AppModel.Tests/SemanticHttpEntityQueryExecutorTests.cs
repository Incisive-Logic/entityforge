using System.Net;
using System.Text;
using System.Text.Json;
using EntityForge.Db.AppModel.Queries;
using EntityForge.Db.Client;
using EntityForge.Db.Transport.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EntityForge.Db.AppModel.Tests;

[TestClass]
public sealed class SemanticHttpEntityQueryExecutorTests
{
    [TestMethod]
    public async Task Canonical_blocks_compile_to_the_semantic_transport_shape()
    {
        string? requestJson = null;
        var response = new EntityProjectionTransportResponse(Guid.NewGuid(), 17, [], []);
        using var client = new HttpClient(new StubHandler(async request =>
        {
            requestJson = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(response), Encoding.UTF8, "application/json"),
            };
        }))
        {
            BaseAddress = new Uri("https://example.test/")
        };
        var property = new SemanticQueryProperty("https://example.test/name", "Name", QueryValueEditor.Text,
            new HashSet<QueryComparisonOperator> { QueryComparisonOperator.Contains });
        var query = new SemanticQueryDocument(
            new SemanticQueryConcept("https://example.test/Company", "Company"),
            [new SemanticPropertyConditionBlock(Guid.NewGuid().ToString("N"), property, QueryComparisonOperator.Contains,
                JsonSerializer.SerializeToElement("Forge"))],
            [new SemanticQueryProjectionField(property, "name")],
            ConceptMatchMode: QueryConceptMatchMode.ExactConceptOnly);

        EntityProjectionQueryResult result = await new SemanticEntityQueryExecutor(
            new HttpPortableEntityQueryClient(client)).ExecuteAsync(query);

        using JsonDocument json = JsonDocument.Parse(requestJson!);
        Assert.AreEqual("https://example.test/Company", json.RootElement.GetProperty("rootConcept").GetString());
        Assert.AreEqual("propertyComparison", json.RootElement.GetProperty("filter").GetProperty("kind").GetString());
        Assert.AreEqual("contains", json.RootElement.GetProperty("filter").GetProperty("operator").GetString());
        Assert.AreEqual("direct", json.RootElement.GetProperty("conceptMatchMode").GetString());
        Assert.AreEqual(new DatabaseVersion(17), result.SnapshotVersion);
    }

    [TestMethod]
    public async Task Multi_hop_blocks_remain_recursive_on_the_wire()
    {
        string? requestJson = null;
        var response = new EntityProjectionTransportResponse(Guid.NewGuid(), 19, [], []);
        using var client = new HttpClient(new StubHandler(async request =>
        {
            requestJson = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(response), Encoding.UTF8, "application/json")
            };
        }))
        {
            BaseAddress = new Uri("https://example.test/")
        };
        var company = new SemanticQueryConcept("https://example.test/Company", "Company");
        var employee = new SemanticQueryConcept("https://example.test/Employee", "Employee");
        var department = new SemanticQueryConcept("https://example.test/Department", "Department");
        var employs = new SemanticQueryRelationship("https://example.test/employs", "Employs", [employee.Identifier]);
        var memberOf = new SemanticQueryRelationship("https://example.test/memberOf", "Member of", [department.Identifier]);
        var name = new SemanticQueryProperty("https://example.test/name", "Name", QueryValueEditor.Text,
            new HashSet<QueryComparisonOperator> { QueryComparisonOperator.Equals });
        var nested = new SemanticRelationshipConditionBlock("member", memberOf, department, QueryLogicalOperator.All,
            [new SemanticPropertyConditionBlock("name", name, QueryComparisonOperator.Equals,
                JsonSerializer.SerializeToElement("Research"))]);
        var query = new SemanticQueryDocument(company,
            [new SemanticRelationshipConditionBlock("employee", employs, employee, QueryLogicalOperator.All, [nested])], []);

        await new SemanticEntityQueryExecutor(new HttpPortableEntityQueryClient(client)).ExecuteAsync(query);

        using JsonDocument json = JsonDocument.Parse(requestJson!);
        JsonElement secondHop = json.RootElement.GetProperty("filter").GetProperty("targetFilter");
        Assert.AreEqual("https://example.test/memberOf", secondHop.GetProperty("relationship").GetString());
        Assert.AreEqual("Research", secondHop.GetProperty("targetFilter").GetProperty("value")
            .GetProperty("value").GetString());
    }

    [TestMethod]
    public async Task Relationship_blocks_compile_to_nested_semantic_filters()
    {
        string? requestJson = null;
        var response = new EntityProjectionTransportResponse(Guid.NewGuid(), 18, [], []);
        using var client = new HttpClient(new StubHandler(async request =>
        {
            requestJson = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(response), Encoding.UTF8, "application/json")
            };
        }))
        {
            BaseAddress = new Uri("https://example.test/")
        };
        var employee = new SemanticQueryConcept("https://example.test/Employee", "Employee");
        var name = new SemanticQueryProperty("https://example.test/name", "Name", QueryValueEditor.Text,
            new HashSet<QueryComparisonOperator> { QueryComparisonOperator.Equals });
        var employs = new SemanticQueryRelationship("https://example.test/employs", "Employs",
            [employee.Identifier]);
        var query = new SemanticQueryDocument(new("https://example.test/Company", "Company"),
        [new SemanticRelationshipConditionBlock("employed-person", employs, employee,
            QueryLogicalOperator.All,
            [new SemanticPropertyConditionBlock("employee-name", name, QueryComparisonOperator.Equals,
                JsonSerializer.SerializeToElement("Priya Shah"))])], []);

        await new SemanticEntityQueryExecutor(new HttpPortableEntityQueryClient(client)).ExecuteAsync(query);

        using JsonDocument json = JsonDocument.Parse(requestJson!);
        JsonElement filter = json.RootElement.GetProperty("filter");
        Assert.AreEqual("relationshipExists", filter.GetProperty("kind").GetString());
        Assert.AreEqual("https://example.test/employs", filter.GetProperty("relationship").GetString());
        Assert.AreEqual("Priya Shah", filter.GetProperty("targetFilter").GetProperty("value")
            .GetProperty("value").GetString());
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => respond(request);
    }
}
