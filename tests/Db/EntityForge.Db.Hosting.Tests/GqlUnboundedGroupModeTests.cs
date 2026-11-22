using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("", 1)]
    [DataRow("WALK", 1)]
    [DataRow("TRAIL", 1)]
    [DataRow("SIMPLE", 1)]
    [DataRow("ACYCLIC", 0)]
    public async Task Gql_standard_unbounded_group_modes_preserve_cycles_and_zero_length_selection(string mode, int expected)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seeded = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (a:GroupCycle {name:'A'})-[:cycleStep {allowed:TRUE}]->(b:GroupCycle {name:'B'})-[:cycleStep {allowed:TRUE}]->(a)," +
                "(a)-[:cycleStep {allowed:FALSE}]->(b)"
        });
        Assert.AreEqual(HttpStatusCode.OK, seeded.StatusCode, await seeded.Content.ReadAsStringAsync());
        const string start = "(origin:GroupCycle {name:'A'})";
        const string end = "(destination:GroupCycle {name:'A'})";
        const string group = "((starts)-[links:cycleStep WHERE links.allowed IS TRUE]->(ends))";
        using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH p=" + mode + " " + start + group + "+" + end +
                " RETURN COUNT(*) AS total,COALESCE(SUM(PATH_LENGTH(p)),0) AS hops"
        });
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var row = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings");
        Assert.AreEqual(expected, row.GetProperty("total").GetInt32());
        Assert.AreEqual(expected * 2, row.GetProperty("hops").GetInt32());
        using var multiEdge = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH p=" + mode + " " + start +
                "((a)-[e:cycleStep WHERE e.allowed IS TRUE]->(b)-[f:cycleStep WHERE f.allowed IS TRUE]->(c))+" + end +
                " RETURN COUNT(*) AS total,COALESCE(SUM(PATH_LENGTH(p)),0) AS hops"
        });
        Assert.AreEqual(HttpStatusCode.OK, multiEdge.StatusCode, await multiEdge.Content.ReadAsStringAsync());
        var multiRow = (await multiEdge.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings");
        Assert.AreEqual(expected, multiRow.GetProperty("total").GetInt32());
        Assert.AreEqual(expected * 2, multiRow.GetProperty("hops").GetInt32());
        if (mode is "TRAIL" or "ACYCLIC")
        {
            foreach (string repeated in new[]
            {
                group + "+",
                "((a)-[e:cycleStep WHERE e.allowed IS TRUE]->(b)-[f:cycleStep WHERE f.allowed IS TRUE]->(c))+",
                "-[links:cycleStep {allowed:TRUE}]->+"
            })
            {
                using var repeatable = await client.PostAsJsonAsync("/v1/graph/gql/query", new
                {
                    query = "MATCH REPEATABLE ELEMENTS p=" + mode.ToLowerInvariant() + " " + start + repeated + end +
                        " RETURN COUNT(*) AS total,COALESCE(SUM(PATH_LENGTH(p)),0) AS hops"
                });
                Assert.AreEqual(HttpStatusCode.OK, repeatable.StatusCode, await repeatable.Content.ReadAsStringAsync());
                var repeatRow = (await repeatable.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings");
                Assert.AreEqual(expected, repeatRow.GetProperty("total").GetInt32());
                Assert.AreEqual(expected * 2, repeatRow.GetProperty("hops").GetInt32());
            }
        }
        if (mode.Length == 0)
        {
            using var shortest = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = "MATCH p=SHORTEST 2 GROUPS " + start + group + "*" + end +
                    " RETURN COUNT(*) AS total,SUM(PATH_LENGTH(p)) AS hops"
            });
            Assert.AreEqual(HttpStatusCode.OK, shortest.StatusCode, await shortest.Content.ReadAsStringAsync());
            var selected = (await shortest.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings");
            Assert.AreEqual(2, selected.GetProperty("total").GetInt32());
            Assert.AreEqual(2, selected.GetProperty("hops").GetInt32());
            using var unboundedWalk = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = "MATCH REPEATABLE ELEMENTS p=" + start + group + "+" + end + " RETURN p"
            });
            Assert.AreEqual(HttpStatusCode.BadRequest, unboundedWalk.StatusCode, await unboundedWalk.Content.ReadAsStringAsync());
        }
        if (mode == "TRAIL")
        {
            using var sharing = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = "MATCH REPEATABLE ELEMENTS p=TRAIL (a:GroupCycle {name:'A'})-[e:cycleStep {allowed:TRUE}]->(b:GroupCycle {name:'B'})," +
                    "q=TRAIL (a)-[f:cycleStep {allowed:TRUE}]->(b) RETURN COUNT(*) AS total"
            });
            Assert.AreEqual(HttpStatusCode.OK, sharing.StatusCode, await sharing.Content.ReadAsStringAsync());
            Assert.AreEqual(1, (await sharing.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("total").GetInt32());
        }
        await host.StopAsync();
    }
}
