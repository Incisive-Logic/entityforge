using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("REPEATABLE ELEMENTS ", "WALK", 1, false, 9)]
    [DataRow("", "WALK", 0, false, 9)]
    [DataRow("REPEATABLE ELEMENTS ", "TRAIL", 0, false, 9)]
    [DataRow("REPEATABLE ELEMENTS ", "SIMPLE", 0, false, 9)]
    [DataRow("REPEATABLE ELEMENTS ", "WALK", 1, true, 9)]
    [DataRow("", "WALK", 0, true, 9)]
    [DataRow("REPEATABLE ELEMENTS ", "TRAIL", 0, true, 9)]
    [DataRow("REPEATABLE ELEMENTS ", "SIMPLE", 0, true, 9)]
    [DataRow("REPEATABLE ELEMENTS ", "WALK", 1, true, 1)]
    [DataRow("", "WALK", 1, true, 1)]
    [DataRow("REPEATABLE ELEMENTS ", "TRAIL", 1, true, 1)]
    [DataRow("REPEATABLE ELEMENTS ", "SIMPLE", 1, true, 1)]
    [DataRow("", "WALK", 1, true, 0)]
    [DataRow("REPEATABLE ELEMENTS ", "SIMPLE", 1, true, 0)]
    public async Task Gql_standard_wide_finite_repetition_preserves_walk_and_uniqueness_modes(string matchMode, string pathMode, int expected, bool sameBinding, int repetitions)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (a:WideCycle)-[:r]->()-[:s]->(a)"
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode);
        string query = "MATCH " + matchMode + "p=" + pathMode + " (a:WideCycle)((x)-[rs:r]->(y)-[ss:s]->(z)){" + repetitions + "}(" +
            (sameBinding ? "a" : "b:WideCycle") + ") RETURN COUNT(*) AS total,MAX(PATH_LENGTH(p)) AS hops,MAX(SIZE(rs)) AS repetitions";
        using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query
        });
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var row = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings");
        Assert.AreEqual(expected, row.GetProperty("total").GetInt32());
        if (expected == 1)
        {
            Assert.AreEqual(2 * repetitions, row.GetProperty("hops").GetInt32());
            Assert.AreEqual(repetitions, row.GetProperty("repetitions").GetInt32());
        }
        if (sameBinding)
        {
            var variants = new List<string> { "MATCH (a:WideCycle) " + query };
            if (repetitions == 1 && (matchMode.Length == 0 || pathMode != "WALK"))
            {
                string unbounded = query.Replace("{1}", "+", StringComparison.Ordinal);
                variants.Add(unbounded);
                variants.Add("MATCH (a:WideCycle) " + unbounded);
            }
            foreach (string variant in variants)
            {
                using var scoped = await client.PostAsJsonAsync("/v1/graph/gql/query", new
                {
                    query = variant
                });
                Assert.AreEqual(HttpStatusCode.OK, scoped.StatusCode, variant + ": " + await scoped.Content.ReadAsStringAsync());
                var actual = (await scoped.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings");
                foreach (string column in new[] { "total", "hops", "repetitions" })
                {
                    Assert.AreEqual(row.GetProperty(column).GetRawText(), actual.GetProperty(column).GetRawText(), variant);
                }
            }
        }
        await host.StopAsync();
    }

    [TestMethod]
    [DataRow("MATCH p=(:WideStart)((x)-[rs:r]->(y)-[ss:s]->(z)){1,5000}(:WideFinish) RETURN PATH_LENGTH(p) AS answer", 18)]
    [DataRow("MATCH p=(:WideStart)-[rs:r]->{0}(:WideStart) RETURN PATH_LENGTH(p)+SIZE(rs) AS answer", 0)]
    [DataRow("MATCH p=(:WideStart)-[rs:r]->{0,0}(:WideFinish) RETURN COUNT(*) AS answer", 0)]
    [DataRow("MATCH p=(:WideStart)((x)-[rs:r]->(y)-[ss:s]->(z)){9}(:WideFinish) RETURN PATH_LENGTH(p) AS answer", 18)]
    [DataRow("MATCH p=(:WideStart)((x)-[rs:r]->(y)-[ss:s]->(z)){1,20}(:WideFinish) RETURN SIZE(rs) AS answer", 9)]
    [DataRow("MATCH p=ALL SHORTEST (:WideStart)((x)-[rs:r]->(y)-[ss:s]->(z)){1,20}(:WideFinish) RETURN PATH_LENGTH(p) AS answer", 18)]
    [DataRow("MATCH (:WideStart)((x)-[rs:r]->(y)-[ss:s]->(z) WHERE y IS LABELED WideMiddle){1,20}(:WideFinish) RETURN SIZE(y) AS answer", 9)]
    [DataRow("MATCH (:WideStart)((x)-[rs:r]->(y)-[ss:s]->(z) WHERE y IS LABELED Missing){1,20}(:WideFinish) RETURN COUNT(*) AS answer", 0)]
    [DataRow("MATCH p=(:WideStart)((x)-[rs:r]->(y)-[ss:s]->(z)){0}(:WideStart) RETURN PATH_LENGTH(p)+SIZE(x)+SIZE(rs)+SIZE(y)+SIZE(ss)+SIZE(z) AS answer", 0)]
    [DataRow("MATCH (:WideStart)(()-[:r]->()-[:s]->()){1,20}(:WideFinish) RETURN COUNT(*) AS answer", 1)]
    [DataRow("MATCH (a:WideStart) CALL (a) { MATCH p=(a)((x)-[rs:r]->(y)-[ss:s]->(z)){9}(:WideFinish) RETURN PATH_LENGTH(p) AS lengthValue } RETURN lengthValue AS answer", 18)]
    public async Task Gql_standard_wide_finite_repeated_chains_preserve_bounds_and_group_values(string query, int expected)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        var statement = new StringBuilder("INSERT (:WideStart)");
        for (int iteration = 0; iteration < 9; iteration++)
        {
            statement.Append("-[:r]->(:WideMiddle)-[:s]->(").Append(iteration == 8 ? ":WideFinish" : "").Append(')');
        }
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = statement.ToString()
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode, await seed.Content.ReadAsStringAsync());
        using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query
        });
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, query + ": " + await response.Content.ReadAsStringAsync());
        var rows = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
        Assert.AreEqual(1, rows.GetArrayLength());
        Assert.AreEqual(expected, rows[0].GetProperty("bindings").GetProperty("answer").GetInt32());
        await host.StopAsync();
    }
}
