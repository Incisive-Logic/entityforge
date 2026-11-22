using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("UNWIND [1,1,2] AS x MERGE (a:MergeLeft {k:x})-[r:mergeLink {k:x}]->(b:Middle)<-[s:tail]-(c:MergeRight {k:x}) ON CREATE SET r.visits=1,s.visits=1 ON MATCH SET r.visits=r.visits+1,s.visits=s.visits+1")]
    [DataRow("UNWIND [1,1,2] AS x MERGE (a:MergeLeft {k:x})-[r:mergeLink {k:x}]->(b:MergeRight {k:x})-[s:tail]->(a) ON CREATE SET r.visits=1 ON MATCH SET r.visits=r.visits+1")]
    [DataRow("UNWIND [1,1,2] AS x MERGE (a:MergeLeft {k:x})-[r:mergeLink {k:x}]-(b:MergeRight {k:x}) ON CREATE SET r.visits=1 ON MATCH SET r.visits=r.visits+1")]
    [DataRow("UNWIND [1,1,2] AS x MERGE (a:MergeLeft {k:x})-[r:mergeLink {k:x}]->(b:MergeRight {k:x}) ON CREATE SET r.visits=1 ON MATCH SET r.visits=r.visits+1")]
    [DataRow("MATCH (a:MergeLeft) UNWIND [1,1,2] AS x MERGE (a)-[r:mergeLink {k:x}]->(b:MergeRight {k:x}) ON CREATE SET r.visits=1 ON MATCH SET r.visits=r.visits+1")]
    [DataRow("UNWIND [1,1,2] AS x MERGE (a:MergeLeft {k:x})-[r:mergeLink {k:x}]->(a) ON CREATE SET r.visits=1 ON MATCH SET r.visits=r.visits+1")]
    [DataRow("UNWIND [1,1] AS x MERGE (a:MergeLeft {k:x})-[r:mergeLink {k:x}]->(b:MergeRight {k:x}) ON CREATE SET a.k=2,r.visits=1")]
    [DataRow("UNWIND [1,NULL] AS x MERGE (a:MergeLeft {k:x})-[r:mergeLink {k:1}]->(b:MergeRight)")]
    [DataRow("UNWIND [1,1] AS x MERGE (a:MergeLeft {k:x})-[r:mergeLink {k:x}]->(b:MergeRight) ON MATCH SET b.k=1/0")]
    [DataRow("MATCH (a:MergeLeft),(b:MergeRight) UNWIND [1,2] AS x MERGE (a)-[r:mergeLink {k:coalesce(a.visits,0)}]->(b) ON CREATE SET a.visits=coalesce(a.visits,0)+1,r.visits=1")]
    [DataRow("MATCH (a:MergeLeft),(b:MergeRight) UNWIND [1,1] AS x MERGE (a)-[r:mergeLink {k:x}]->(b) ON CREATE SET r.visits=0 SET r.visits=r.visits+1,r.temp=true REMOVE r.temp")]
    [DataRow("MATCH (a:MergeLeft),(b:MergeRight) UNWIND [1,1,2] AS x MERGE (a)-[r:mergeLink {k:x}]-(b) ON CREATE SET r.visits=1 ON MATCH SET r.visits=r.visits+1")]
    [DataRow("MATCH (a:MergeLeft) UNWIND [1,1] AS x MERGE (a)-[r:mergeLink {k:x}]-(a) ON CREATE SET r.visits=1 ON MATCH SET r.visits=r.visits+1")]
    [DataRow("MATCH (a:MergeLeft),(b:MergeRight) UNWIND [1,1,2] AS x MERGE (a)-[r:mergeLink {k:x}]->(b) ON CREATE SET r.visits=1 ON MATCH SET r.visits=r.visits+1")]
    [DataRow("MATCH (a:MergeLeft),(b:MergeRight) UNWIND [1,1] AS x MERGE (a)<-[r:mergeLink {k:x}]-(b) ON CREATE SET r.visits=1 ON MATCH SET r.visits=r.visits+1")]
    [DataRow("MATCH (a:MergeLeft) UNWIND [1,1] AS x MERGE (a)-[r:mergeLink {k:x}]->(a) ON CREATE SET r.visits=1 ON MATCH SET r.visits=r.visits+1")]
    [DataRow("MATCH (a:MergeLeft),(b:MergeRight) UNWIND [1,NULL] AS x MERGE (a)-[r:mergeLink {k:x}]->(b)")]
    [DataRow("MATCH (a:MergeLeft),(b:MergeRight) UNWIND [1,1] AS x MERGE (a)-[r:mergeLink {k:x}]->(b) ON MATCH SET r.visits=1/0")]
    [DataRow("MATCH (a:MergeLeft) OPTIONAL MATCH (b:Absent) MERGE (a)-[r:mergeLink]->(b)")]
    [DataRow("UNWIND [1] AS merge MERGE (n:MergeRow {k:merge}) ON CREATE SET n.visits=1")]
    [DataRow("UNWIND [1,1] AS x MERGE (n:MergeRow {k:x}) SET n.visits=coalesce(n.visits,0)+1")]
    [DataRow("UNWIND [1,1] AS x MERGE (n:MergeRow {k:x}) ON CREATE SET n.visits=0 ON MATCH SET n.visits=n.visits+1 SET n.visits=n.visits+1,n.temp=true REMOVE n.temp")]
    [DataRow("UNWIND [1,2] AS x MERGE (n:MergeRow {k:x}) ON CREATE SET n.visits=1 SET n.visits=1/(2-x)")]
    [DataRow("UNWIND [] AS x MERGE (n:MergeRow {k:x}) SET missing.visits=1")]
    [DataRow("UNWIND [1] AS x MERGE (n:MergeRow {k:x}) SET n.visits=1 ON CREATE SET n.visits=2")]
    [DataRow("UNWIND [1] AS x MERGE (n:MergeRow {k:x}) ON CREATE SET n.visits=1 ON MATCH SET n.visits=1/0")]
    [DataRow("UNWIND [1,1] AS x MERGE (n:MergeRow {k:x}) ON CREATE SET n.visits=1 ON MATCH SET n.visits=1/0")]
    [DataRow("UNWIND range(1,10001) AS x MERGE (n:MergeRow {k:x})")]
    [DataRow("UNWIND [1,1,2] AS x MERGE (n:MergeRow {k:x}) ON CREATE SET n.visits=1 ON MATCH SET n.visits=n.visits+1")]
    [DataRow("UNWIND [1,1.0] AS x MERGE (n:MergeRow {k:x}) ON CREATE SET n.visits=1 ON MATCH SET n.visits=n.visits+1")]
    [DataRow("UNWIND [1,1,1] AS x MERGE (n:MergeRow {k:x}) ON CREATE SET n.visits=1 ON MATCH SET n.k=2")]
    [DataRow("UNWIND [1,1,1] AS x MERGE (n:MergeRow {k:x}) ON CREATE SET n.k=2,n.visits=1")]
    [DataRow("UNWIND [] AS x MERGE (n:MergeRow {k:1/0}) ON CREATE SET n.visits=1")]
    [DataRow("UNWIND [1,NULL] AS x MERGE (n:MergeRow {k:x})")]
    [DataRow("UNWIND [1,2] AS x MERGE (n:MergeRow {k:x}) ON CREATE SET n.visits=1/(2-x)")]
    [DataRow("UNWIND [] AS x MERGE (n:MergeRow {k:x}) ON MATCH SET missing.visits=1")]
    [DataRow("UNWIND [1] AS x MERGE (n:MergeRow {k:x})-[r:edge]->(m:MergeRow),(z:MergeRow)")]
    [DataRow("FOR x IN [1,2] MERGE (n:RejectedMerge {amount:x})")]
    [DataRow("INSERT (:RejectedMerge) MERGE (:RejectedMerge)")]
    [DataRow("MATCH (n:MergeGuard) MERGE (n)-[:link]->(:RejectedMerge)")]
    public async Task Gql_rejects_row_merge_extension_without_writes(string statement)
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
