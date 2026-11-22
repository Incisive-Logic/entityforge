using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("ANY SHORTEST")]
    [DataRow("ALL SHORTEST")]
    [DataRow("SHORTEST 2")]
    [DataRow("SHORTEST 2 GROUPS")]
    public Task Gql_selected_subpath_prefilters_preserve_quantified_edges(string selector) =>
        Gql_standard_selective_predicates_filter_candidates_before_selection(
            "MATCH p=" + selector +
            " ((a:SelectStart)-[edges:choose]->{1,2}(m) WHERE m.score>1)-[:tailLink]->(d:SelectEnd)" +
            " RETURN PATH_LENGTH(p) AS answer", "[2]");

    [TestMethod]
    [DataRow("")]
    [DataRow("WALK")]
    [DataRow("TRAIL")]
    [DataRow("SIMPLE")]
    [DataRow("ACYCLIC")]
    [DataRow("", true)]
    [DataRow("TRAIL", true)]
    [DataRow("SIMPLE", true)]
    [DataRow("ACYCLIC", true)]
    public async Task Gql_selected_subpath_edge_prefilter_selects_longer_eligible_path_and_replays_update(string mode, bool named = false)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (a:RepetitionStart),(b:RepetitionMiddle {idx:1}),(c:RepetitionMiddle {idx:2}),(z:RepetitionFinish)," +
                "(a)-[:step]->(b),(b)-[:step]->(c),(b)-[:finishLink]->(z),(c)-[:finishLink]->(z)"
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode, await seed.Content.ReadAsStringAsync());
        string pattern = "MATCH p=ALL SHORTEST (" + (named ? "s=" : "") + mode +
            " (a:RepetitionStart)-[edges:step]->{1,2}(m) WHERE m.idx=2" +
            (named ? " AND PATH_LENGTH(s)=2" : "") + ")-[:finishLink]->(z:RepetitionFinish)";
        using var query = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = pattern + " RETURN PATH_LENGTH(p) AS length,SIZE(edges) AS edgeCount,m.idx AS idx" +
                (named ? ",PATH_LENGTH(s) AS subLength,SIZE(ELEMENTS(s)) AS subElements," +
                    "VALUE { FOR node IN ELEMENTS(s) FILTER node IS TYPED NODE RETURN SUM(node.idx) AS total } AS nodeTotal" : "")
        });
        Assert.AreEqual(HttpStatusCode.OK, query.StatusCode, await query.Content.ReadAsStringAsync());
        var rows = (await query.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
        Assert.AreEqual(1, rows.GetArrayLength());
        var bindings = rows[0].GetProperty("bindings");
        Assert.AreEqual(3, bindings.GetProperty("length").GetInt32());
        Assert.AreEqual(2, bindings.GetProperty("edgeCount").GetInt32());
        Assert.AreEqual(2, bindings.GetProperty("idx").GetInt32());
        if (named)
        {
            Assert.AreEqual(2, bindings.GetProperty("subLength").GetInt32());
            Assert.AreEqual(5, bindings.GetProperty("subElements").GetInt32());
            Assert.AreEqual(3, bindings.GetProperty("nodeTotal").GetInt32());
        }
        var request = new
        {
            statement = pattern + " SET m.visited=TRUE" + (named ? ",m.idx=12" : "") + " RETURN m.idx AS answer" +
                (named ? ",VALUE { FOR node IN ELEMENTS(s) FILTER node IS TYPED NODE RETURN SUM(node.idx) AS total } AS nodeTotal" : ""),
            transactionId = Guid.NewGuid()
        };
        using var update = await client.PostAsJsonAsync("/v1/graph/gql/update", request);
        Assert.AreEqual(HttpStatusCode.OK, update.StatusCode, await update.Content.ReadAsStringAsync());
        if (named)
        {
            StringAssert.Contains(await update.Content.ReadAsStringAsync(), "\"nodeTotal\":13");
        }
        using var replay = await client.PostAsJsonAsync("/v1/graph/gql/update", request);
        Assert.AreEqual(HttpStatusCode.OK, replay.StatusCode, await replay.Content.ReadAsStringAsync());
        Assert.AreEqual(await update.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync());
        using var verified = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (m:RepetitionMiddle WHERE m.visited IS TRUE) RETURN m.idx AS idx"
        });
        Assert.AreEqual(HttpStatusCode.OK, verified.StatusCode, await verified.Content.ReadAsStringAsync());
        var persisted = (await verified.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
        Assert.AreEqual(1, persisted.GetArrayLength());
        Assert.AreEqual(named ? 12 : 2, persisted[0].GetProperty("bindings").GetProperty("idx").GetInt32());
        await host.StopAsync();
    }
}
