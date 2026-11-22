using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    public async Task Gql_standard_mixed_edge_directions_match_the_directed_store_orientation()
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (:DirectionNode {name:'A'})-[:directionEdge]->(:DirectionNode {name:'B'})-[:directionEdge]->(:DirectionNode {name:'C'})",
            transactionId = Guid.NewGuid()
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode, await seed.Content.ReadAsStringAsync());
        foreach (var (query, expected) in new[]
        {
            ("MATCH (a:DirectionNode {name:'A'})~[e:directionEdge]~>(b) RETURN COUNT(*) AS answer", 1),
            ("MATCH (a:DirectionNode {name:'A'})<~[e:directionEdge]~(b) RETURN COUNT(*) AS answer", 0),
            ("MATCH (a:DirectionNode {name:'C'})<~[e:directionEdge]~(b) RETURN COUNT(*) AS answer", 1),
            ("MATCH (a:DirectionNode {name:'A'})~>(b) RETURN COUNT(*) AS answer", 1),
            ("MATCH (a:DirectionNode {name:'C'})<~(b) RETURN COUNT(*) AS answer", 1),
            ("MATCH p=(a:DirectionNode {name:'A'})~[e:directionEdge]~>+(b:DirectionNode {name:'C'}) RETURN PATH_LENGTH(p) AS answer", 2),
            ("MATCH p=(a:DirectionNode {name:'C'}) ((x)<~[e:directionEdge]~(y))+(b:DirectionNode {name:'A'}) RETURN PATH_LENGTH(p) AS answer", 2),
            ("MATCH p=ALL SHORTEST (a:DirectionNode {name:'C'}) ((x)<~[e:directionEdge]~(y)){1,3}(b:DirectionNode {name:'A'}) RETURN PATH_LENGTH(p) AS answer", 2),
            ("MATCH (a:DirectionNode {name:'A'}) OPTIONAL MATCH (a)<~[e:directionEdge]~(b) RETURN CASE WHEN COUNT(*)=1 AND COUNT(e)=0 THEN 1 ELSE 0 END AS answer", 1)
        })
        {
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query
            });
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, query + ": " + await response.Content.ReadAsStringAsync());
            var rows = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
            Assert.AreEqual(1, rows.GetArrayLength(), query);
            Assert.AreEqual(expected, rows[0].GetProperty("bindings").GetProperty("answer").GetInt32(), query);
        }
        var update = new
        {
            statement = "MATCH (a:DirectionNode {name:'C'})<~[e:directionEdge]~(b) SET b.checked=TRUE RETURN b.name AS answer",
            transactionId = Guid.NewGuid()
        };
        using var changed = await client.PostAsJsonAsync("/v1/graph/gql/update", update);
        Assert.AreEqual(HttpStatusCode.OK, changed.StatusCode, await changed.Content.ReadAsStringAsync());
        using var replay = await client.PostAsJsonAsync("/v1/graph/gql/update", update);
        Assert.AreEqual(HttpStatusCode.OK, replay.StatusCode, await replay.Content.ReadAsStringAsync());
        Assert.AreEqual(await changed.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync());
        Assert.AreEqual("B", (await changed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result").GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetString());
        await host.StopAsync();
    }
}
