using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("MERGE (n:Created {name:'A'}) SET n.hits=coalesce(n.hits,0)+1")]
    [DataRow("MERGE (n:Created {name:'A'}) ON CREATE SET n.hits=0 ON MATCH SET n.hits=n.hits+1 SET n.hits=n.hits+1,n.temp=true REMOVE n.temp")]
    [DataRow("MERGE (n:Created {name:'A'})-[r:created]-(m:Other {name:'B'}) ON CREATE SET r.weight=1 SET r.weight=r.weight+1")]
    [DataRow("MERGE (n:Created {name:'A'}) ON CREATE SET n.hits=1,n:Fresh ON MATCH SET n.hits=n.hits+1")]
    [DataRow("MERGE (n:Created {name:'A'}) ON MATCH SET n.hits=n.hits+1 ON CREATE SET n.hits=1")]
    [DataRow("MERGE (n:Created {name:'A'})-[r:created]->(m:Other {name:'B'}) ON CREATE SET r.weight=size(n.name)+size(m.name) ON MATCH SET r.weight=r.weight+1")]
    [DataRow("MERGE (n:Created {name:'A'}) ON CREATE SET n += {value:7} ON MATCH SET n.value=n.value+1")]
    [DataRow("MERGE (:Merged {name:'A'})")]
    [DataRow("MERGE (:Merged {name:'A'})-[:link {weight:2}]->(:Merged {name:'B'})")]
    [DataRow("MERGE (:Merged {name:'A'})<-[:link]-(:Merged {name:'B'})")]
    [DataRow("MERGE (n:Merged {name:'A'})-[:link]->(n)")]
    [DataRow("MERGE (:Merged {name:null})")]
    [DataRow("MERGE (:Merged {id:'00000000-0000-0000-0000-000000000001'})")]
    [DataRow("MERGE (:Merged),(:Merged)")]
    [DataRow("MERGE (:Merged) ON CREATE SET n.x=1")]
    [DataRow("FOR x IN [1,2] MERGE (n:RejectedMerge {amount:x})")]
    [DataRow("INSERT (:RejectedMerge) MERGE (:RejectedMerge)")]
    [DataRow("MATCH (n:MergeGuard) MERGE (n)-[:link]->(:RejectedMerge)")]
    public async Task Gql_rejects_merge_extension_without_writes(string statement)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (:MergeGuard {hits:7})"
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode, await seed.Content.ReadAsStringAsync());
        var request = new
        {
            statement,
            transactionId = Guid.NewGuid()
        };
        for (int attempt = 0; attempt < 2; attempt++)
        {
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/update", request);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, await response.Content.ReadAsStringAsync());
        }
        using var query = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (n) RETURN COUNT(*) AS total,SUM(n.hits) AS hits"
        });
        Assert.AreEqual(HttpStatusCode.OK, query.StatusCode, await query.Content.ReadAsStringAsync());
        var row = (await query.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings");
        Assert.AreEqual(1, row.GetProperty("total").GetInt32());
        Assert.AreEqual(7, row.GetProperty("hits").GetInt32());
        using var edges = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH ()-[e]->() RETURN COUNT(*) AS total"
        });
        Assert.AreEqual(HttpStatusCode.OK, edges.StatusCode, await edges.Content.ReadAsStringAsync());
        Assert.AreEqual(0, (await edges.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("total").GetInt32());
        await host.StopAsync();
    }
}
