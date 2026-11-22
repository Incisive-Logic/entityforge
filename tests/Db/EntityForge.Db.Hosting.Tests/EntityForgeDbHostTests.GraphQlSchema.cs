using EntityForge.Db.Query.Adapters;
using System.Net.Http.Json;
using System.Text.Json;
using EntityForge.Db.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    public async Task GraphQl_unordered_pages_preserve_every_identity_across_storage_order()
    {
        _directory = NewDirectory();
        await using var host = BuildHost();
        await host.StartAsync();
        using var client = Client(host);
        string[] expected = Enumerable.Range(0, 19).Select(i => "value-" + i).ToArray();
        for (int i = 0; i < expected.Length; i++)
        {
            await ApplyAsync(client, Session("key-" + i, Industry(), new EntityForge.Semantics.StringSemanticValue(expected[i])));
        }
        using var leaseResponse = await client.PostAsJsonAsync("/v1/semantic/snapshot-leases", new SnapshotLeaseRequest(300));
        var lease = (await leaseResponse.Content.ReadFromJsonAsync<SnapshotLeaseResponse>())!;
        string? after = null;
        var actual = new List<string>();
        for (int page = 0; ; page++)
        {
            Assert.IsTrue(page < 3, "Pagination did not terminate.");
            using var response = await client.PostAsJsonAsync("/graphql", new
            {
                query = """
                    query Page($after:String,$lease:UUID!,$property:URI!) {
                      entities(concept:"https://example.test/Company",conceptMatchMode:direct,
                        first:16,after:$after,snapshotLeaseId:$lease) {
                        nodes { industry:field(property:$property) { values { value } } }
                        pageInfo { endCursor hasNextPage snapshotVersion }
                      }
                    }
                    """,
                variables = new
                {
                    after,
                    lease = lease.LeaseId,
                    property = "https://example.test/industry"
                }
            });
            string body = await response.Content.ReadAsStringAsync();
            using var json = JsonDocument.Parse(body);
            Assert.IsFalse(json.RootElement.TryGetProperty("errors", out _), body);
            var entities = json.RootElement.GetProperty("data").GetProperty("entities");
            actual.AddRange(entities.GetProperty("nodes").EnumerateArray().Select(node => node.GetProperty("industry").GetProperty("values")[0].GetProperty("value").GetString()!));
            var info = entities.GetProperty("pageInfo");
            if (!info.GetProperty("hasNextPage").GetBoolean())
            {
                break;
            }
            after = info.GetProperty("endCursor").GetString();
        }
        CollectionAssert.AreEquivalent(expected, actual);
    }

    [TestMethod]
    public async Task GraphQl_schema_keeps_snapshot_pages_and_temporal_aggregate_population()
    {
        _directory = NewDirectory();
        await using var host = BuildHost();
        await host.StartAsync();
        using var client = Client(host);
        await ApplyAsync(client, Session("1", Industry(), new EntityForge.Semantics.StringSemanticValue("Alpha")));
        await ApplyAsync(client, Session("2", Industry(), new EntityForge.Semantics.StringSemanticValue("Beta")));
        using var leaseResponse = await client.PostAsJsonAsync("/v1/semantic/snapshot-leases", new SnapshotLeaseRequest(300));
        var lease = (await leaseResponse.Content.ReadFromJsonAsync<SnapshotLeaseResponse>())!;
        await ApplyAsync(client, Session("1", Industry(), new EntityForge.Semantics.StringSemanticValue("Changed")));
        string? after = null;
        var actual = new List<string>();
        for (int page = 0; page < 2; page++)
        {
            var variables = new Dictionary<string, JsonElement>
            {
                ["lease"] = JsonSerializer.SerializeToElement(lease.LeaseId),
                ["after"] = JsonSerializer.SerializeToElement(after),
                ["property"] = JsonSerializer.SerializeToElement("https://example.test/industry")
            };
            using var response = await client.PostAsJsonAsync("/graphql", new EntityForgeGraphQlRequest("""
                query Page($lease: UUID!, $after: String, $property: URI!) {
                  entities(concept: "https://example.test/Company", conceptMatchMode: direct,
                    snapshotLeaseId: $lease, first: 1, after: $after,
                    orderBy: [{property: "https://example.test/industry", direction: ascending}]) {
                    nodes { industry: field(property: $property) { values { value } } }
                    pageInfo { snapshotVersion endCursor hasNextPage }
                  }
                }
                """, variables));
            string json = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(json);
            Assert.IsFalse(document.RootElement.TryGetProperty("errors", out _), json);
            var result = document.RootElement.GetProperty("data").GetProperty("entities");
            actual.Add(result.GetProperty("nodes")[0].GetProperty("industry").GetProperty("values")[0].GetProperty("value").GetString()!);
            Assert.AreEqual(lease.SnapshotVersion, result.GetProperty("pageInfo").GetProperty("snapshotVersion").GetInt64());
            after = result.GetProperty("pageInfo").GetProperty("endCursor").GetString();
            Assert.AreEqual(page == 0, result.GetProperty("pageInfo").GetProperty("hasNextPage").GetBoolean());
        }
        CollectionAssert.AreEqual(new[] { "Alpha", "Beta" }, actual);
        using var aggregate = await client.PostAsJsonAsync("/graphql", new EntityForgeGraphQlRequest("""
            query Count($time: TimeRange!) {
              aggregateEntities(concept: "https://example.test/Company", observationTime: $time,
                measures: [{name: "count", function: "countEntities"}]) { inputEntityCount }
            }
            """, new Dictionary<string, JsonElement>
        {
            ["time"] = JsonSerializer.SerializeToElement(new { toExclusive = "2000-01-01T00:00:00Z" })
        }));
        string aggregateJson = await aggregate.Content.ReadAsStringAsync();
        using var count = JsonDocument.Parse(aggregateJson);
        Assert.IsFalse(count.RootElement.TryGetProperty("errors", out _), aggregateJson);
        Assert.AreEqual(0, count.RootElement.GetProperty("data").GetProperty("aggregateEntities").GetProperty("inputEntityCount").GetInt32());
    }

    [TestMethod]
    public async Task GraphQl_document_extension_is_opt_in_and_preserves_aliased_selection_and_evidence()
    {
        _directory = NewDirectory();
        await using var host = BuildHost();
        await host.StartAsync();
        await host.Application.Services.GetRequiredService<EntityForgeDbRuntime>().Database.ApplyAsync(Session());
        using var client = Client(host);
        const string query = """
            { entities(concept: "https://example.test/Company", includeEvidence: true) {
              a: nodes { industry: field(property: "https://example.test/industry") { values { value } } }
              b: nodes { identity }
            } }
            """;
        foreach (bool enabled in new[] { false, true })
        {
            using var response = await client.PostAsJsonAsync("/graphql", new EntityForgeGraphQlRequest(query,
                Extensions: enabled ? new Dictionary<string, JsonElement> { ["entityDocument"] = JsonSerializer.SerializeToElement(true) } : null));
            string json = await response.Content.ReadAsStringAsync();
            using var result = JsonDocument.Parse(json);
            Assert.IsFalse(result.RootElement.TryGetProperty("errors", out _), json);
            var data = result.RootElement.GetProperty("data").GetProperty("entities");
            Assert.AreEqual(1, data.GetProperty("a")[0].EnumerateObject().Count());
            Assert.AreEqual(1, data.GetProperty("b")[0].EnumerateObject().Count());
            Assert.AreEqual(enabled, result.RootElement.TryGetProperty("extensions", out var extension));
            if (!enabled)
            {
                continue;
            }

            var transport = extension.GetProperty("entityDocument").Deserialize<EntityForge.Db.Transport.Contracts.EntityProjectionTransportResponse>(
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            var decoded = EntityForge.Db.Client.EntityProjectionTransport.Decode(transport);
            Assert.AreEqual(1, decoded.Document.Roots.Count);
            Assert.IsTrue(decoded.Document.Evidence.Count > 0);
            Assert.IsTrue(decoded.Document.Roots[0].Fields.Any(f => f.Name == "industry"));
        }
    }

    [TestMethod]
    public async Task GraphQl_schema_preserves_competing_values_and_provider_selection()
    {
        _directory = NewDirectory();
        await using var host = BuildHost();
        await host.StartAsync();
        using var client = Client(host);
        var preferred = new Uri("https://source.example/approved");
        await ApplyAsync(client, SourceSnapshot(preferred, "companies", "1",
            new EntityForge.Semantics.SemanticFieldChange(Name(), EntityForge.Semantics.SemanticFieldChangeKind.Set,
                new EntityForge.Semantics.StringSemanticValue("Approved"))));
        await ApplyAsync(client, SourceSnapshot(new Uri("https://source.example/other"), "companies", "1",
            new EntityForge.Semantics.SemanticFieldChange(Name(), EntityForge.Semantics.SemanticFieldChangeKind.Set,
                new EntityForge.Semantics.StringSemanticValue("Other"))));
        var response = await client.PostAsJsonAsync("/graphql", new EntityForgeGraphQlRequest($$"""
            query Read($providers: [URI!]) {
              entities(concept: "{{Company().Identifier.AbsoluteUri}}", includeEvidence: true) {
                nodes {
                  all: field(property: "{{Name().Identifier.AbsoluteUri}}") { values { value evidence } }
                  preferred: field(property: "{{Name().Identifier.AbsoluteUri}}", valueSelection: preferredProvider,
                    preferredProviders: $providers) { values { value evidence } }
                }
                evidence { identifier provider observedAt validFrom validTo }
              }
            }
            """, new Dictionary<string, JsonElement> { ["providers"] = JsonSerializer.SerializeToElement(new[] { preferred.AbsoluteUri }) }));
        string json = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);
        Assert.IsFalse(document.RootElement.TryGetProperty("errors", out _), json);
        var entity = document.RootElement.GetProperty("data").GetProperty("entities").GetProperty("nodes")[0];
        Assert.AreEqual(2, entity.GetProperty("all").GetProperty("values").GetArrayLength());
        Assert.AreEqual("Approved", entity.GetProperty("preferred").GetProperty("values")[0].GetProperty("value").GetString());
    }

    [TestMethod]
    public async Task GraphQl_schema_handles_object_variables_fragments_defaults_and_exact_aliases()
    {
        _directory = NewDirectory();
        await using var host = BuildHost();
        await host.StartAsync();
        await host.Application.Services.GetRequiredService<EntityForgeDbRuntime>().Database.ApplyAsync(Session());
        using var client = Client(host);
        var variables = new Dictionary<string, JsonElement>
        {
            ["where"] = JsonSerializer.SerializeToElement(new { property = Industry().Identifier.AbsoluteUri, eq = "Technology" })
        };
        var response = await client.PostAsJsonAsync("/graphql", new EntityForgeGraphQlRequest("""
            query Read($where: EntityFilter!, $show: Boolean! = true) {
              companies: entities(concept: "https://example.test/Company", where: $where, includeEvidence: true) {
                nodes {
                  ...Company
                  ... on Entity { identity }
                  ignored: field(property: "https://example.test/industry") @skip(if: $show) { values { value } }
                }
                page: pageInfo { version: snapshotVersion }
                proof: evidence { id: identifier }
              }
            }
            fragment Company on Entity {
              identity
              industry: field(property: "https://example.test/industry") {
                candidates: values { chosen: value }
              }
            }
            """, variables, "Read"));
        string json = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);
        Assert.IsFalse(document.RootElement.TryGetProperty("errors", out _), json);
        var companies = document.RootElement.GetProperty("data").GetProperty("companies");
        var node = companies.GetProperty("nodes")[0];
        Assert.AreEqual(2, node.EnumerateObject().Count());
        var value = node.GetProperty("industry").GetProperty("candidates")[0];
        Assert.AreEqual("Technology", value.GetProperty("chosen").GetString());
        Assert.AreEqual(1, value.EnumerateObject().Count());
        Assert.AreEqual(1, companies.GetProperty("page").EnumerateObject().Count());
        Assert.AreEqual(1, companies.GetProperty("proof")[0].EnumerateObject().Count());
    }

    [TestMethod]
    public async Task GraphQl_schema_introspection_and_aggregate_object_variables_work()
    {
        _directory = NewDirectory();
        await using var host = BuildHost();
        await host.StartAsync();
        await host.Application.Services.GetRequiredService<EntityForgeDbRuntime>().Database.ApplyAsync(Session());
        using var client = Client(host);
        var response = await client.PostAsJsonAsync("/graphql", new EntityForgeGraphQlRequest("""
            query Count($measures: [Measure!]!) {
              aggregateEntities(concept: "https://example.test/Company", measures: $measures) {
                rows { values }
                count: inputEntityCount
              }
              __type(name: "EntityFilter") { name kind inputFields { name } }
            }
            """, new Dictionary<string, JsonElement>
        {
            ["measures"] = JsonSerializer.SerializeToElement(new[] { new { function = "countEntities", name = "total" } })
        }));
        string json = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);
        Assert.IsFalse(document.RootElement.TryGetProperty("errors", out _), json);
        var data = document.RootElement.GetProperty("data");
        Assert.AreEqual("INPUT_OBJECT", data.GetProperty("__type").GetProperty("kind").GetString());
        Assert.AreEqual(1, data.GetProperty("aggregateEntities").GetProperty("count").GetInt64());
        Assert.AreEqual(1, data.GetProperty("aggregateEntities").GetProperty("rows")[0].EnumerateObject().Count());
    }

    [TestMethod]
    [DataRow("{ entities(concept: \"https://example.test/Company\", invented: true) { nodes { identity } } }")]
    [DataRow("{ entities(concept: \"relative\") { nodes { identity } } }")]
    [DataRow("{ entities(concept: \"https://example.test/Company\", snapshotLeaseId: \"wrong\") { nodes { identity } } }")]
    [DataRow("{ entities(concept: \"https://example.test/Company\", validTime: {fromInclusive: \"wrong\"}) { nodes { identity } } }")]
    [DataRow("{ entities(concept: \"https://example.test/Company\", where: {property: \"https://example.test/name\", eq: {bad: true}}) { nodes { identity } } }")]
    [DataRow("{ entities(concept: \"https://example.test/Company\", where: {property: \"https://example.test/name\", exists: true, eq: \"x\"}) { nodes { identity } } }")]
    [DataRow("{ entities(concept: \"https://example.test/Company\", conceptMatchMode: invented) { nodes { identity } } }")]
    [DataRow("query Q($concept: URI!) { entities(concept: $concept) { nodes { identity } } }")]
    [DataRow("{ entities(concept: \"https://example.test/Company\") { nodes { x: identity x: types } } }")]
    [DataRow("{ entities(concept: \"https://example.test/Company\") { nodes { ...Loop } } } fragment Loop on Entity { ...Loop }")]
    [DataRow("{ a: entities(concept: \"https://example.test/Company\") { nodes { identity } } b: entities(concept: \"https://example.test/Company\") { nodes { identity } } }")]
    public async Task GraphQl_schema_rejects_invalid_operations(string query)
    {
        _directory = NewDirectory();
        await using var host = BuildHost();
        await host.StartAsync();
        using var client = Client(host);
        var response = await client.PostAsJsonAsync("/graphql", new EntityForgeGraphQlRequest(query));
        string json = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);
        Assert.IsTrue(document.RootElement.TryGetProperty("errors", out var errors), json);
        Assert.IsTrue(errors.GetArrayLength() > 0);
    }
}
