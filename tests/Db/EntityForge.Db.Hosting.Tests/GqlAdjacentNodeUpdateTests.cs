using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("(a:BoundaryPerson)(a {name:'A'})")]
    [DataRow("((a:BoundaryPerson))(a {name:'A'})")]
    [DataRow("(:BoundaryPerson {name:'A'})(:BoundaryPerson {name:'A'})")]
    [DataRow("(:BoundaryPerson|Missing {name:'A'})")]
    [DataRow("(a:BoundaryPerson)(b {name:'A'})")]
    [DataRow("(a:BoundaryPerson)((b {name:'A'}))")]
    [DataRow("((a:BoundaryPerson)(b {name:'A'})-[:hop]->(c)){1}")]
    [DataRow("((a:BoundaryPerson)(b {name:'A'})-[:hop]->(c)){1} FOR nodeValue IN b")]
    [DataRow("(a:BoundaryPerson)(b WHERE EXISTS { (b)-[:hop]->() })")]
    [DataRow("((a:BoundaryPerson)(b)-[:hop]->(c) WHERE VALUE { RETURN b.name AS n }='A'){1}")]
    public async Task Gql_adjacent_node_updates_replay_after_restart_and_roll_back_failed_results(string pattern)
    {
        _directory = NewDirectory();
        var request = new
        {
            statement = "MATCH " + pattern + " INSERT (:BoundaryOutput {amount:1})",
            transactionId = Guid.NewGuid()
        };
        var invalid = new
        {
            statement = "MATCH " + pattern + " INSERT (:BoundaryOutput {amount:99}) RETURN CAST(1.5M AS INTEGER) AS bad",
            transactionId = Guid.NewGuid()
        };
        string? receipt = null;
        for (int restart = 0; restart < 2; restart++)
        {
            await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
                .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
            await host.StartAsync();
            using var client = Client(host);
            if (restart == 0)
            {
                using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
                {
                    statement = "INSERT (:BoundaryPerson {name:'A'})-[:hop]->(:BoundaryPerson {name:'B'})"
                });
                Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode, await seed.Content.ReadAsStringAsync());
            }
            using var updated = await client.PostAsJsonAsync("/v1/graph/gql/update", request);
            string body = await updated.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, updated.StatusCode, body);
            receipt ??= body;
            Assert.AreEqual(receipt, body);
            using var rejected = await client.PostAsJsonAsync("/v1/graph/gql/update", invalid);
            Assert.AreEqual(HttpStatusCode.BadRequest, rejected.StatusCode, await rejected.Content.ReadAsStringAsync());
            Assert.AreEqual("22003", (await rejected.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("gqlStatus").GetString());
            using var verified = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = "MATCH (n:BoundaryOutput) RETURN COUNT(*)=1 AND SUM(n.amount)=1 AS answer"
            });
            Assert.AreEqual(HttpStatusCode.OK, verified.StatusCode, await verified.Content.ReadAsStringAsync());
            var rows = (await verified.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
            Assert.AreEqual(1, rows.GetArrayLength());
            Assert.IsTrue(rows[0].GetProperty("bindings").GetProperty("answer").GetBoolean());
            await host.StopAsync();
        }
    }
}
