using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task Sparql_native_json_response_budget_covers_bindings_and_graphs(bool graph, bool unicode)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UseRdfProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        foreach (int rows in new[] { 8, unicode ? 200 : 70 })
        {
            string query = (graph ? "CONSTRUCT { ?s <urn:p> ?payload } WHERE" : "SELECT ?payload") +
                " { VALUES ?i { " + String.Join(" ", Enumerable.Range(0, rows)) +
                " } BIND(IRI(CONCAT('urn:s', STR(?i))) AS ?s) BIND('" +
                new string(unicode ? '雪' : 'x', unicode ? 40_000 : 250_000) + "' AS ?payload) }";
            using var response = await client.PostAsJsonAsync("/v1/rdf/sparql", new
            {
                query
            });
            if (rows == 8)
            {
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, response.IsSuccessStatusCode ? null : await response.Content.ReadAsStringAsync());
                using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.AreEqual(8, result.RootElement.GetProperty(graph ? "quads" : "bindings").GetArrayLength());
                continue;
            }
            Assert.AreEqual(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            string body = await response.Content.ReadAsStringAsync();
            Assert.IsTrue(body.Length < 4096);
            using var problem = JsonDocument.Parse(body);
            Assert.AreEqual("query_response_size_exceeded", problem.RootElement.GetProperty("code").GetString());
        }
        using var recovered = await client.PostAsJsonAsync("/v1/rdf/sparql", new
        {
            query = "ASK {}"
        });
        Assert.AreEqual(HttpStatusCode.OK, recovered.StatusCode);
        using var answer = JsonDocument.Parse(await recovered.Content.ReadAsStringAsync());
        Assert.IsTrue(answer.RootElement.GetProperty("value").GetBoolean());
    }
}
