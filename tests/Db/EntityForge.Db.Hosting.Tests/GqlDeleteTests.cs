using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("MATCH (n:Victim) DELETE n", false, 2, 1)]
    [DataRow("MATCH (n:Victim) NODETACH DELETE n", false, 2, 1)]
    [DataRow("MATCH (n:Victim) DETACH DELETE n", true, 1, 0)]
    [DataRow("MATCH (n:Survivor) DETACH DELETE n", true, 1, 0)]
    [DataRow("MATCH (n:Victim) FOR i IN [1,2] DETACH DELETE n", true, 1, 0)]
    [DataRow("OPTIONAL MATCH (n:Absent) DETACH DELETE n", true, 2, 1)]
    [DataRow("MATCH (n:Victim)-[r]->(m) FOR i IN [1,2] DELETE r", true, 2, 0)]
    [DataRow("MATCH (n:Victim)-[r]->(m) DELETE n,r", true, 1, 0)]
    [DataRow("MATCH (n:Victim)-[r]->(m) DELETE r,n", true, 1, 0)]
    [DataRow("MATCH (n:Victim)-[r]->(m) FOR i IN [1,2] DELETE n,r", true, 1, 0)]
    [DataRow("MATCH (n:Victim)-[r]->(m) DELETE n,r,m", true, 0, 0)]
    [DataRow("MATCH p=(n:Victim)-[r]->(m) DELETE p", false, 2, 1)]
    [DataRow("MATCH p=(n:Victim)-[r]->(m) DETACH DELETE p", false, 2, 1)]
    [DataRow("MATCH p=(n:Victim)-[r]->(m) FOR item IN ELEMENTS(p) DELETE item", true, 0, 0)]
    public async Task Gql_standard_deletion_requires_explicit_detach_and_orders_unique_targets_atomically(string statement, bool succeeds, int nodes, int edges)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (:Victim)-[:link]->(:Survivor)"
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode);
        var request = new
        {
            statement,
            transactionId = Guid.NewGuid()
        };
        using var first = await client.PostAsJsonAsync("/v1/graph/gql/update", request);
        Assert.AreEqual(succeeds, first.IsSuccessStatusCode, await first.Content.ReadAsStringAsync());
        if (succeeds)
        {
            using var replay = await client.PostAsJsonAsync("/v1/graph/gql/update", request);
            Assert.AreEqual(await first.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync());
        }
        async Task<int> Count(string pattern)
        {
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = pattern + " RETURN COUNT(*) AS answer"
            });
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetInt32();
        }
        Assert.AreEqual(nodes, await Count("MATCH (n)"));
        Assert.AreEqual(edges, await Count("MATCH (n)-[r]->(m)"));
    }
}
