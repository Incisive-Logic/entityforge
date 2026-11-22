using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Gql_bounded_response_preserves_configured_http_json_contract(bool supportingGraph)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureServices(services => services.ConfigureHttpJsonOptions(options =>
            {
                options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
                options.SerializerOptions.NumberHandling = JsonNumberHandling.WriteAsString;
            }))
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new Dictionary<string, object>
        {
            ["query"] = "RETURN 7 AS answer",
            ["include_supporting_graph"] = supportingGraph
        });
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.IsTrue(result.RootElement.TryGetProperty("query_id", out _));
        Assert.IsFalse(result.RootElement.TryGetProperty("queryId", out _));
        Assert.AreEqual(JsonValueKind.String, result.RootElement.GetProperty("snapshot_version").ValueKind);
        Assert.AreEqual("7", result.RootElement.GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetString());
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task Gql_query_response_size_is_bounded_without_partial_success(bool supportingGraph, bool unicode)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        foreach (int width in new[] { 5, 50 })
        {
            string items = String.Join(",", Enumerable.Range(1, width));
            string query = "FOR i IN [" + items + "] FOR j IN [" + items + "] RETURN '" +
                new string(unicode ? '雪' : 'x', unicode ? 2500 : 10_000) + "' AS payload";
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query,
                includeSupportingGraph = supportingGraph
            });
            if (width == 5)
            {
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, response.IsSuccessStatusCode ? null : await response.Content.ReadAsStringAsync());
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.AreEqual(25, document.RootElement.GetProperty("matches").GetArrayLength());
                continue;
            }
            Assert.AreEqual(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            string body = await response.Content.ReadAsStringAsync();
            Assert.IsTrue(body.Length < 4096);
            using var problem = JsonDocument.Parse(body);
            Assert.AreEqual("query_response_size_exceeded", problem.RootElement.GetProperty("code").GetString());
            Assert.IsFalse(problem.RootElement.TryGetProperty("gqlStatus", out _), "An HTTP representation budget must not masquerade as a language syntax error.");
        }
        using var recovered = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "RETURN 1 AS answer"
        });
        Assert.AreEqual(HttpStatusCode.OK, recovered.StatusCode);
    }
}
