using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("USE CURRENT_GRAPH MATCH (n:ScopeSample) RETURN COUNT(*) AS answer", 1)]
    [DataRow("USE CURRENT_GRAPH { MATCH (n:ScopeSample) RETURN n.amount AS amount NEXT RETURN amount AS answer }", 7)]
    [DataRow("USE CURRENT_PROPERTY_GRAPH MATCH (n:ScopeSample) RETURN SUM(n.amount) AS answer", 7)]
    [DataRow("CALL { USE CURRENT_GRAPH MATCH (n:ScopeSample) RETURN n.amount AS amount } RETURN SUM(amount) AS answer", 7)]
    [DataRow("USE CURRENT_GRAPH MATCH (n:ScopeSample) RETURN n NEXT USE CURRENT_GRAPH RETURN n.amount AS answer", 7)]
    [DataRow("USE CURRENT_GRAPH RETURN 1 AS answer UNION ALL USE CURRENT_PROPERTY_GRAPH RETURN 2 AS answer NEXT RETURN SUM(answer) AS answer", 3)]
    public async Task Gql_standard_current_graph_selection_preserves_request_scope(string query, int expected)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (:ScopeSample {amount:7})"
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode, await seed.Content.ReadAsStringAsync());
        using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query
        });
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var rows = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
        Assert.AreEqual(1, rows.GetArrayLength());
        Assert.AreEqual(expected, rows[0].GetProperty("bindings").GetProperty("answer").GetInt32());
        await host.StopAsync();
    }

    [TestMethod]
    public async Task Gql_standard_current_graph_updates_replay_and_other_graphs_are_rejected()
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        var write = new
        {
            statement = "USE CURRENT_GRAPH INSERT (n:ScopeSample {amount:7}) RETURN n NEXT USE CURRENT_PROPERTY_GRAPH SET n.amount=n.amount+1 RETURN n.amount AS answer",
            transactionId = Guid.NewGuid()
        };
        using var first = await client.PostAsJsonAsync("/v1/graph/gql/update", write);
        Assert.AreEqual(HttpStatusCode.OK, first.StatusCode, await first.Content.ReadAsStringAsync());
        using var replay = await client.PostAsJsonAsync("/v1/graph/gql/update", write);
        Assert.AreEqual(HttpStatusCode.OK, replay.StatusCode, await replay.Content.ReadAsStringAsync());
        Assert.AreEqual(await first.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync());
        foreach (string graph in new[] { "otherGraph", "HOME_GRAPH", "HOME_PROPERTY_GRAPH" })
        {
            using var rejected = await client.PostAsJsonAsync("/v1/graph/gql/update", new
            {
                statement = "USE " + graph + " INSERT (:ScopeSample {amount:99})"
            });
            Assert.AreEqual(HttpStatusCode.BadRequest, rejected.StatusCode, await rejected.Content.ReadAsStringAsync());
        }
        using var verified = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (n:ScopeSample) RETURN COUNT(*)=1 AND SUM(n.amount)=8 AS answer"
        });
        Assert.AreEqual(HttpStatusCode.OK, verified.StatusCode, await verified.Content.ReadAsStringAsync());
        Assert.IsTrue((await verified.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetBoolean());
        await host.StopAsync();
    }
}
