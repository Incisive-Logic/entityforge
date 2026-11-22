using EntityForge.Db.Gql;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("MATCH (n:Absent) INSERT (:New {x:missing})")]
    [DataRow("MATCH (n:Absent) INSERT (:New {x:1,x:2})")]
    [DataRow("MATCH (n:Absent) INSERT (n:Changed)")]
    [DataRow("MATCH (n:Absent) INSERT (n)-[n:link]->(:New)")]
    [DataRow("FOR i IN [] INSERT (n:New) SET missing.x=1")]
    [DataRow("FOR i IN [] INSERT (n:New) SET n.x=missing")]
    public void Gql_standard_read_creation_validates_scope_and_declarations_before_matching(string statement)
    {
        Assert.ThrowsExactly<ArgumentException>(() => GraphGqlMutationCompiler.Compile(statement, transactionId: Guid.NewGuid()));
    }

    [TestMethod]
    [DataRow("FOR i IN [1,2,2] INSERT (n:New {x:i})", 3, 5)]
    [DataRow("FOR i IN [1,2] INSERT (n:New {x:i}) SET n.y=n.x SET n.x=n.y*2", 2, 6)]
    [DataRow("MATCH (p:Parent) INSERT (p)-[r:child]->(n:New {x:p.x}) SET r.weight=n.x,n.captured=CURRENT_TIMESTAMP REMOVE n.x SET n.x=p.x+1", 2, 7)]
    [DataRow("FOR i IN [1,2] INSERT (n:New {x:i}) SET n:Tagged REMOVE n:Tagged", 2, 3)]
    [DataRow("MATCH (p:Parent) FOR i IN [1,2] INSERT (n:New {x:p.x}) SET p.x=p.x+1", 4, 10)]
    [DataRow("LET x=2 INSERT (n:New {x:x*2})", 1, 4)]
    [DataRow("CALL () { RETURN 3 AS x } INSERT (n:New {x:x})", 1, 3)]
    [DataRow("MATCH (p:Parent) INSERT (p)-[:child {source:p.x}]->(n:New {x:p.x,captured:CURRENT_TIMESTAMP})", 2, 5)]
    [DataRow("MATCH (p:Absent) INSERT (n:New {x:1})", 0, 0)]
    public async Task Gql_standard_read_stages_insert_per_row_entities_and_capture_atomic_retries(string statement, int count, int sum)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seeded = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (:Parent {x:2}),(:Parent {x:3})"
        });
        Assert.AreEqual(HttpStatusCode.OK, seeded.StatusCode);
        var request = new
        {
            statement,
            transactionId = Guid.NewGuid()
        };
        using var first = await client.PostAsJsonAsync("/v1/graph/gql/update", request);
        Assert.AreEqual(HttpStatusCode.OK, first.StatusCode, await first.Content.ReadAsStringAsync());
        async Task<string> Read()
        {
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = "MATCH (n:New) RETURN n.x AS x,n.captured AS captured ORDER BY x,captured"
            });
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches").GetRawText();
        }
        string original = await Read();
        using var retry = await client.PostAsJsonAsync("/v1/graph/gql/update", request);
        Assert.AreEqual(await first.Content.ReadAsStringAsync(), await retry.Content.ReadAsStringAsync());
        Assert.AreEqual(original, await Read());
        using var totals = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (n:New) RETURN COUNT(*) AS entityCount,COALESCE(SUM(n.x),0) AS total"
        });
        Assert.AreEqual(HttpStatusCode.OK, totals.StatusCode, await totals.Content.ReadAsStringAsync());
        var values = (await totals.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings");
        Assert.AreEqual(count, values.GetProperty("entityCount").GetInt32());
        Assert.AreEqual(sum, values.GetProperty("total").GetInt32());
        using var failed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "FOR i IN [1,0] INSERT (:FailedCreation {x:1/i})"
        });
        Assert.IsFalse(failed.IsSuccessStatusCode);
        using var failedAction = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "FOR i IN [1,0] INSERT (n:FailedCreation) SET n.x=1/i"
        });
        Assert.IsFalse(failedAction.IsSuccessStatusCode);
        using var missing = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "OPTIONAL MATCH (p:Absent) INSERT (:FailedCreation)-[:link]->(p)"
        });
        Assert.IsFalse(missing.IsSuccessStatusCode);
        using var unchanged = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (n:FailedCreation) RETURN n"
        });
        Assert.AreEqual(0, (await unchanged.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches").GetArrayLength());
    }

    [TestMethod]
    [DataRow("INSERT (n:New {x:1}) SET n.x=2,n.y=n.x", "n.x=2 AND n.y=1")]
    [DataRow("INSERT (n:New {x:1}) SET n.x=2 SET n.y=n.x", "n.x=2 AND n.y=2")]
    [DataRow("INSERT (n:New {x:1}) REMOVE n.x SET n.y=coalesce(n.x,9)", "n.x IS NULL AND n.y=9")]
    [DataRow("INSERT (n:New) SET n:Tagged SET n.y=CASE WHEN n IS LABELED New&Tagged THEN 2 ELSE 0 END", "n IS LABELED Tagged AND n.y=2")]
    [DataRow("INSERT (n:New)-[r:link]->(m:Other) SET n.name='A',m.name='B',r.weight=3 SET n.y=r.weight", "n.name='A' AND n.y=3")]
    [DataRow("INSERT (n:New) SET n.captured=CURRENT_TIMESTAMP", "n.captured IS TYPED ZONED DATETIME")]
    public async Task Gql_standard_creation_actions_are_atomic_and_replay_captured_values(string statement, string condition)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        var request = new
        {
            statement,
            transactionId = Guid.NewGuid()
        };
        using var first = await client.PostAsJsonAsync("/v1/graph/gql/update", request);
        Assert.AreEqual(HttpStatusCode.OK, first.StatusCode, await first.Content.ReadAsStringAsync());
        async Task<string> Read()
        {
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = "MATCH (n:New) RETURN n AS answer"
            });
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches").GetRawText();
        }
        string original = await Read();
        using var retry = await client.PostAsJsonAsync("/v1/graph/gql/update", request);
        Assert.AreEqual(await first.Content.ReadAsStringAsync(), await retry.Content.ReadAsStringAsync());
        Assert.AreEqual(original, await Read());
        using var query = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (n:New) RETURN " + condition + " AS answer"
        });
        var rows = (await query.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
        Assert.AreEqual(1, rows.GetArrayLength());
        Assert.IsTrue(rows[0].GetProperty("bindings").GetProperty("answer").GetBoolean());
        using var rejected = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (n:FailedCreation) SET n.x=1 SET n.y=1/0"
        });
        Assert.IsFalse(rejected.IsSuccessStatusCode);
        using var unchanged = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (n:FailedCreation) RETURN n"
        });
        Assert.AreEqual(0, (await unchanged.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches").GetArrayLength());
    }
}
