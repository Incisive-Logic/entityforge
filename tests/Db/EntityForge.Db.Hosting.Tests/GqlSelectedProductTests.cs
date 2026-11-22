using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("MATCH (mid:Required) RETURN mid NEXT YIELD mid AS chosen MATCH REPEATABLE ELEMENTS p=ALL SHORTEST (:SelectionStart)(()-[:step]->()){1,2}(chosen)-[:finishLink]->(:SelectionEnd),(:SelectionStart) RETURN PATH_LENGTH(p) AS answer", 3)]
    [DataRow("MATCH (mid:Required) RETURN mid AS \"chosen node\" NEXT YIELD \"chosen node\" AS chosen MATCH REPEATABLE ELEMENTS p=ALL SHORTEST (:SelectionStart)(()-[:step]->()){1,2}(chosen)-[:finishLink]->(:SelectionEnd),(:SelectionStart) RETURN PATH_LENGTH(p) AS answer", 3)]
    [DataRow("MATCH (`mid`:Required) MATCH REPEATABLE ELEMENTS p=ALL SHORTEST (:SelectionStart)(()-[:step]->()){1,2}(mid)-[:finishLink]->(:SelectionEnd),(:SelectionStart) RETURN PATH_LENGTH(p) AS answer", -1)]
    [DataRow("MATCH (mid:Required) MATCH REPEATABLE ELEMENTS p=ALL SHORTEST (:SelectionStart)(()-[:step]->()){1,2}(`mid`)-[:finishLink]->(:SelectionEnd),(:SelectionStart) RETURN PATH_LENGTH(p) AS answer", -1)]
    [DataRow("LET retained=7 OPTIONAL MATCH (mid:Missing) OPTIONAL MATCH REPEATABLE ELEMENTS p=ALL SHORTEST (:SelectionStart)(()-[:step]->()){1,2}(mid)-[:finishLink]->(:SelectionEnd),(:SelectionStart) RETURN CASE WHEN mid IS NULL AND p IS NULL THEN retained ELSE 0 END AS answer", 7)]
    [DataRow("OPTIONAL MATCH (mid:Missing) RETURN mid NEXT YIELD mid AS chosen MATCH REPEATABLE ELEMENTS p=ALL SHORTEST (:SelectionStart)(()-[:step]->()){1,2}(chosen)-[:finishLink]->(:SelectionEnd),(:SelectionStart) RETURN COUNT(*) AS answer", 0)]
    [DataRow("MATCH REPEATABLE ELEMENTS p=ANY (a:SelectionStart)-[:step]->(b WHERE VALUE { RETURN b NEXT YIELD \"b\" AS selectedNode RETURN selectedNode IS LABELED Short AS \"selectedNode IS LABELED Short\" }),(:SelectionEnd) RETURN COUNT(*) AS answer", 1)]
    [DataRow("MATCH p=ANY (a:SelectionStart)-[:step]->(b WHERE VALUE { RETURN b NEXT YIELD `b` AS selectedNode RETURN selectedNode IS LABELED Short AS \"selectedNode IS LABELED Short\" }) RETURN COUNT(*) AS answer", 1)]
    [DataRow("MATCH REPEATABLE ELEMENTS p=ANY (a:SelectionStart)-[:step]->(b WHERE VALUE { RETURN b NEXT RETURN * NEXT YIELD b AS selectedNode RETURN selectedNode IS LABELED Short AS \"selectedNode IS LABELED Short\" }),(:SelectionEnd) RETURN COUNT(*) AS answer", 1)]
    [DataRow("MATCH REPEATABLE ELEMENTS p=ANY (a:SelectionStart)-[:step]->(b WHERE EXISTS { FILTER b IS LABELED Short RETURN * }),(:SelectionEnd) RETURN COUNT(*) AS answer", 1)]
    [DataRow("MATCH REPEATABLE ELEMENTS p=ANY (a:SelectionStart)-[:step]->(b WHERE VALUE { RETURN b NEXT YIELD b AS selectedNode RETURN selectedNode IS LABELED Short AS \"selectedNode IS LABELED Short\" }),(:SelectionEnd) RETURN COUNT(*) AS answer", 1)]
    [DataRow("MATCH REPEATABLE ELEMENTS p=ANY (a:SelectionStart)-[:step]->(b WHERE VALUE { RETURN b NEXT YIELD b RETURN b IS LABELED Short AS \"b IS LABELED Short\" }),(:SelectionEnd) RETURN COUNT(*) AS answer", 1)]
    [DataRow("MATCH REPEATABLE ELEMENTS p=ANY (a:SelectionStart)-[:step]->(b WHERE VALUE { RETURN b IS LABELED Short AS accepted NEXT RETURN accepted }),(:SelectionEnd) RETURN COUNT(*) AS answer", 1)]
    [DataRow("MATCH REPEATABLE ELEMENTS p=ANY (a:SelectionStart)-[:step]->(b WHERE VALUE { RETURN b IS LABELED Short AS accepted UNION RETURN b IS LABELED Short AS accepted }),(:SelectionEnd) RETURN COUNT(*) AS answer", 1)]
    [DataRow("MATCH REPEATABLE ELEMENTS p=ANY (a:SelectionStart)-[:step]->(b WHERE VALUE { CALL (b) { RETURN b IS LABELED Short AS accepted } RETURN accepted }),(:SelectionEnd) RETURN COUNT(*) AS answer", 1)]
    [DataRow("MATCH REPEATABLE ELEMENTS p=ANY (a:SelectionStart)-[:step]->(b WHERE VALUE { CALL () { RETURN b IS LABELED Short AS accepted } RETURN accepted }),(:SelectionEnd) RETURN COUNT(*) AS answer", -1)]
    [DataRow("MATCH REPEATABLE ELEMENTS p=ANY (a:SelectionStart)-[:step]->(b WHERE VALUE { FOR accepted IN [FALSE,b IS LABELED Short] RETURN accepted AS b ORDER BY b DESC LIMIT 1 }),(:SelectionEnd) RETURN COUNT(*) AS answer", 1)]
    [DataRow("MATCH REPEATABLE ELEMENTS p=ANY (a:SelectionStart)-[:step]->(b WHERE VALUE { FOR accepted IN [FALSE,b IS LABELED Short] RETURN accepted ORDER BY accepted DESC LIMIT 1 }),(:SelectionEnd) RETURN COUNT(*) AS answer", 1)]
    [DataRow("MATCH REPEATABLE ELEMENTS p=ANY (a:SelectionStart)-[:step]->(b WHERE VALUE { RETURN b IS LABELED Short AS \"b IS LABELED Short\" }),(endNode:SelectionEnd) RETURN COUNT(*) AS answer", 1)]
    [DataRow("MATCH REPEATABLE ELEMENTS p=ANY (a:SelectionStart)-[:step]->(b WHERE VALUE { LET accepted=b IS LABELED Short FILTER accepted RETURN accepted }),(:SelectionEnd) RETURN COUNT(*) AS answer", 1)]
    [DataRow("MATCH REPEATABLE ELEMENTS p=ANY (a:SelectionStart)-[:step]->(b WHERE EXISTS { FILTER b IS LABELED Short RETURN b }),(:SelectionEnd) RETURN COUNT(*) AS answer", 1)]
    [DataRow("LET retained=7 OPTIONAL MATCH REPEATABLE ELEMENTS p=ANY (a:SelectionStart)-[:step]->(b WHERE VALUE { LET accepted=b IS LABELED Required RETURN accepted }),(:SelectionEnd) RETURN CASE WHEN p IS NULL THEN retained ELSE 0 END AS answer", 7)]
    [DataRow("MATCH REPEATABLE ELEMENTS p=ANY (a:SelectionStart WHERE VALUE { RETURN b IS NOT NULL AS \"b IS NOT NULL\" })-[:step]->(),(b:Short) RETURN COUNT(*) AS answer", -1)]
    [DataRow("MATCH REPEATABLE ELEMENTS p=ANY (a:SelectionStart)-[:step]->(b WHERE VALUE { LET b=1 RETURN b }=1),(:SelectionEnd) RETURN COUNT(*) AS answer", -1)]
    [DataRow("LET retained=7 OPTIONAL MATCH REPEATABLE ELEMENTS p=ANY (a:SelectionStart)-[:step]->(b WHERE EXISTS { MATCH (b:Required) }),(:SelectionEnd) RETURN CASE WHEN p IS NULL THEN retained ELSE 0 END AS answer", 7)]
    [DataRow("MATCH (origin:SelectionStart) MATCH REPEATABLE ELEMENTS p=ANY (origin)-[:step]->(b WHERE EXISTS { MATCH (b:Short) }),(:SelectionEnd) RETURN COUNT(*) AS answer", 1)]
    [DataRow("MATCH REPEATABLE ELEMENTS p=ANY (a:SelectionStart)-[:step]->(b WHERE EXISTS { MATCH (b:Short) MATCH (b)-[:finishLink]->(:SelectionEnd) }),(:SelectionEnd) RETURN COUNT(*) AS answer", 1)]
    [DataRow("MATCH REPEATABLE ELEMENTS p=ANY (a:SelectionStart WHERE EXISTS { MATCH (b:Short) })-[:step]->(),(b:Short) RETURN COUNT(*) AS answer", -1)]
    [DataRow("MATCH REPEATABLE ELEMENTS p=ANY (a:SelectionStart WHERE EXISTS { MATCH (a)-[:step]->(:Short) })-[:step]->(b:Short),(:SelectionEnd) RETURN PATH_LENGTH(p) AS answer", 1)]
    [DataRow("MATCH REPEATABLE ELEMENTS p=ANY (a:SelectionStart)-[:step]->(b WHERE EXISTS { MATCH (b:Required) }),(:SelectionEnd) RETURN COUNT(*) AS answer", 0)]
    [DataRow("MATCH REPEATABLE ELEMENTS p=ANY (a:SelectionStart)-[:step]->(b WHERE EXISTS { MATCH (b:Short) }),(:SelectionEnd) RETURN COUNT(*) AS answer", 1)]
    [DataRow("MATCH REPEATABLE ELEMENTS p=ANY (a:SelectionStart)-[:step]->(b WHERE NOT EXISTS { MATCH (b:Short) }),(:SelectionEnd) RETURN COUNT(*) AS answer", 1)]
    [DataRow("MATCH REPEATABLE ELEMENTS (mid:Required),p=ALL SHORTEST (:SelectionStart)(()-[:step]->()){1,2}(mid)-[:finishLink]->(:SelectionEnd) RETURN COUNT(*) AS answer", 0)]
    [DataRow("MATCH REPEATABLE ELEMENTS p=ALL SHORTEST (:SelectionStart)(()-[:step]->()){1,2}(mid)-[:finishLink]->(:SelectionEnd),(mid:Required) RETURN COUNT(*) AS answer", 0)]
    [DataRow("MATCH REPEATABLE ELEMENTS (mid:Short),p=ALL SHORTEST (:SelectionStart)(()-[:step]->()){1,2}(mid)-[:finishLink]->(:SelectionEnd) RETURN PATH_LENGTH(p) AS answer", 2)]
    [DataRow("MATCH (mid:Required) MATCH REPEATABLE ELEMENTS p=ALL SHORTEST (:SelectionStart)(()-[:step]->()){1,2}(mid)-[:finishLink]->(:SelectionEnd),(:SelectionStart) RETURN PATH_LENGTH(p) AS answer", 3)]
    [DataRow("MATCH (mid:Required) CALL (mid) { MATCH REPEATABLE ELEMENTS p=ALL SHORTEST (:SelectionStart)(()-[:step]->()){1,2}(mid)-[:finishLink]->(:SelectionEnd),(:SelectionStart) RETURN PATH_LENGTH(p) AS lengthValue } RETURN lengthValue AS answer", 3)]
    [DataRow("MATCH REPEATABLE ELEMENTS p=ALL SHORTEST TRAIL (:SelectionStart)(()-[:step]->())+()-[:finishLink]->(:SelectionEnd),q=ANY SHORTEST TRAIL (:SelectionStart)(()-[:step]->())+()-[:finishLink]->(:SelectionEnd) RETURN PATH_LENGTH(p)+PATH_LENGTH(q) AS answer", 4)]
    [DataRow("MATCH REPEATABLE ELEMENTS p=ALL SHORTEST (:SelectionStart)(()-[:step]->()){1,2}()-[:finishLink]->(:SelectionEnd),q=ANY SHORTEST (:SelectionStart)(()-[:step]->()){1,2}()-[:finishLink]->(:SelectionEnd) RETURN PATH_LENGTH(p)+PATH_LENGTH(q) AS answer", 4)]
    [DataRow("MATCH REPEATABLE ELEMENTS p=ALL SHORTEST (:SelectionStart)(()-[:step]->()){1,2}(mid)-[:finishLink]->(:SelectionEnd),(mid:Short) WHERE PATH_LENGTH(p)>2 RETURN COUNT(*) AS answer", 0)]
    [DataRow("LET retained=7 OPTIONAL MATCH REPEATABLE ELEMENTS (mid:Required),p=ALL SHORTEST (:SelectionStart)(()-[:step]->()){1,2}(mid)-[:finishLink]->(:SelectionEnd) RETURN CASE WHEN p IS NULL AND mid IS NULL THEN retained ELSE 0 END AS answer", 7)]
    [DataRow("MATCH REPEATABLE ELEMENTS ALL SHORTEST (:SelectionStart)(()-[:step]->()){1,2}()-[:finishLink]->(:SelectionEnd),ANY SHORTEST (:SelectionStart)(()-[:step]->()){1,2}()-[:finishLink]->(:SelectionEnd) RETURN COUNT(*) AS answer", 1)]
    [DataRow("MATCH REPEATABLE ELEMENTS (mid:Required),p=SHORTEST 2 (:SelectionStart)(()-[:step]->()){1,2}(mid)-[:finishLink]->(:SelectionEnd) RETURN PATH_LENGTH(p) AS answer", 3)]
    [DataRow("MATCH REPEATABLE ELEMENTS p=ANY 0 (:SelectionStart)(()-[:step]->()){1,2}()-[:finishLink]->(:SelectionEnd),(:SelectionStart) RETURN COUNT(*) AS answer", 0)]
    [DataRow("MATCH REPEATABLE ELEMENTS (mid:Short),p=ALL SHORTEST (a:SelectionStart)(()-[edges:step WHERE a IS NOT NULL]->()){1,2}(mid)-[:finishLink]->(:SelectionEnd) RETURN SIZE(edges)+PATH_LENGTH(p) AS answer", 3)]
    [DataRow("LET answer=7 MATCH REPEATABLE ELEMENTS ALL SHORTEST (:SelectionStart)(()-[:step]->()){1,2}()-[:finishLink]->(:SelectionEnd),ANY SHORTEST (:SelectionStart)(()-[:step]->()){1,2}()-[:finishLink]->(:SelectionEnd) RETURN *", 7)]
    [DataRow("MATCH REPEATABLE ELEMENTS p=ANY (a:SelectionStart WHERE b IS NOT NULL)(()-[:step]->()){1,2}()-[:finishLink]->(:SelectionEnd),(b:Short) RETURN p", -1)]
    [DataRow("MATCH REPEATABLE ELEMENTS p=ANY (:SelectionStart)-[shared:step]->()(()-[:step]->()){0,1}()-[:finishLink]->(:SelectionEnd),(shared:Short) RETURN p", -1)]
    public async Task Gql_standard_selected_products_select_components_before_joining(string query, int expected)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (a:SelectionStart)-[:step]->(:Short)-[:finishLink]->(z:SelectionEnd)," +
                "(a)-[:step]->()-[:step]->(:Required)-[:finishLink]->(z)"
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode, await seed.Content.ReadAsStringAsync());
        using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query
        });
        if (expected < 0)
        {
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            return;
        }
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var rows = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
        Assert.AreEqual(1, rows.GetArrayLength());
        var bindings = rows[0].GetProperty("bindings");
        Assert.AreEqual(1, bindings.EnumerateObject().Count());
        Assert.AreEqual(expected, bindings.GetProperty("answer").GetInt32());
        await host.StopAsync();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Gql_standard_selected_products_update_joined_bindings_and_replay(bool existsPrefilter)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (:SelectionStart)-[:step]->(:Short)-[:finishLink]->(:SelectionEnd)"
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode, await seed.Content.ReadAsStringAsync());
        var update = new
        {
            statement = "MATCH REPEATABLE ELEMENTS (mid:Short),p=ALL SHORTEST (:SelectionStart)(()-[:step]->()){1,2}" +
                (existsPrefilter ? "(mid WHERE EXISTS { MATCH (mid:Short) } AND VALUE { LET accepted=mid IS LABELED Short RETURN accepted })" : "(mid)") +
                "-[:finishLink]->(:SelectionEnd) SET mid.visited=TRUE RETURN mid.visited AS answer",
            transactionId = Guid.NewGuid()
        };
        using var response = await client.PostAsJsonAsync("/v1/graph/gql/update", update);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var rows = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result").GetProperty("matches");
        Assert.AreEqual(1, rows.GetArrayLength());
        Assert.IsTrue(rows[0].GetProperty("bindings").GetProperty("answer").GetBoolean());
        using var replay = await client.PostAsJsonAsync("/v1/graph/gql/update", update);
        Assert.AreEqual(await response.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync());
        using var persisted = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (n:Short WHERE n.visited IS TRUE) RETURN COUNT(*) AS answer"
        });
        Assert.AreEqual(HttpStatusCode.OK, persisted.StatusCode, await persisted.Content.ReadAsStringAsync());
        Assert.AreEqual(1, (await persisted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetInt32());
        await host.StopAsync();
    }
}
