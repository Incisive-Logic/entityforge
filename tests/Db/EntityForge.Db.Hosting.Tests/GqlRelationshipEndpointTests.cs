using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("CALL () { INSERT (a:Endpoint {name:'A'})-[r:LINK]->(b:Endpoint {name:'B'}) RETURN a,b } RETURN a.name||'-'||b.name AS answer", "A-B")]
    [DataRow("INSERT (a:Endpoint {name:'A'})-[r:LINK]->(b:Endpoint {name:'B'}) SET a.name=a.name||'x' SET a.name=a.name||'x' RETURN a.name AS answer", "Axx")]
    [DataRow("INSERT (a:Endpoint {name:'A'})-[r:LINK]->(b:Endpoint {name:'B'}) CALL (a) { SET a.name=a.name||'x' } CALL (a) { SET a.name=a.name||'x' } RETURN a.name AS answer", "Axx")]
    public async Task Gql_standard_endpoint_bindings_remain_current_through_calls_and_updates(string statement, string expected)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var response = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement
        });
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var result = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result");
        Assert.IsTrue(result.GetProperty("matches").GetArrayLength() > 0);
        foreach (var row in result.GetProperty("matches").EnumerateArray())
        {
            Assert.AreEqual(expected, row.GetProperty("bindings").GetProperty("answer").GetString());
        }
    }

    [TestMethod]
    [DataRow("MATCH (a:Endpoint)-[r:LINK]->(b) RETURN a IS SOURCE OF r AND b IS DESTINATION OF r AS answer", "true")]
    [DataRow("MATCH (a:Endpoint)-[r:LINK]->(b) RETURN a,b NEXT RETURN a.name||'-'||b.name AS answer", "\"A-B\"")]
    [DataRow("MATCH ()<-[r:LINK]-(a:Endpoint) RETURN a IS SOURCE OF r AS answer", "true")]
    [DataRow("MATCH (a:Endpoint)-[r:LINK]->(b) WHERE a.name='A' RETURN b.name AS answer", "\"B\"")]
    [DataRow("MATCH (a:Endpoint)-[r:LINK]->() RETURN CASE WHEN a IS LABELED Endpoint THEN ['Endpoint'] ELSE [] END AS answer", "[\"Endpoint\"]")]
    [DataRow("MATCH (:Endpoint)-[r:LINK]->(b) RETURN VALUE { FOR x IN [b] RETURN COLLECT_LIST(x.name) AS names } AS answer", "[\"B\"]")]
    [DataRow("MATCH (a:Endpoint)-[r:LINK]->() RETURN VALUE { RETURN COLLECT_LIST(a.name) AS names } AS answer", "[\"A\"]")]
    [DataRow("MATCH (a:Endpoint {name:'A'}) OPTIONAL MATCH (a)-[r:MISSING]->() RETURN CASE WHEN r IS NULL THEN NULL ELSE a END AS answer", "null")]
    [DataRow("MATCH (a:Endpoint {name:'A'}) OPTIONAL MATCH (a)-[r:LINK]->(b) WHERE b.name='B' RETURN a IS SOURCE OF r AS answer", "true")]
    public async Task Gql_standard_endpoint_bindings_return_captured_nodes(string query, string expected)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var created = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (:Endpoint {name:'A'})-[:LINK]->(:Endpoint {name:'B'})"
        });
        Assert.AreEqual(HttpStatusCode.OK, created.StatusCode, await created.Content.ReadAsStringAsync());
        using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query
        });
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var rows = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
        Assert.AreEqual(1, rows.GetArrayLength());
        Assert.AreEqual(expected, rows[0].GetProperty("bindings").GetProperty("answer").GetRawText());
    }

    [TestMethod]
    public async Task Gql_standard_endpoint_assignments_observe_prior_writes_and_replay()
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var created = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (:Endpoint {name:'A'})-[:LINK]->(:Endpoint {name:'B'})"
        });
        Assert.AreEqual(HttpStatusCode.OK, created.StatusCode, await created.Content.ReadAsStringAsync());
        Guid transactionId = Guid.NewGuid();
        string? original = null;
        for (int retry = 0; retry < 2; retry++)
        {
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/update", new
            {
                transactionId,
                statement = "MATCH (a:Endpoint)-[r:LINK]->(b) SET a.name='Changed' SET b.copiedName=a.name " +
                    "INSERT (c:EndpointCopy {name:b.name}) INSERT (d:EndpointCopy {name:a.name}) " +
                    "RETURN b.copiedName AS answer,c.name AS created,d.name AS inserted"
            });
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            var result = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result");
            var bindings = result.GetProperty("matches")[0].GetProperty("bindings");
            Assert.AreEqual("Changed", bindings.GetProperty("answer").GetString());
            Assert.AreEqual("B", bindings.GetProperty("created").GetString());
            Assert.AreEqual("Changed", bindings.GetProperty("inserted").GetString());
            if (retry == 0)
            {
                original = result.GetRawText();
            }
            else
            {
                Assert.AreEqual(original, result.GetRawText());
            }
        }
    }
}
