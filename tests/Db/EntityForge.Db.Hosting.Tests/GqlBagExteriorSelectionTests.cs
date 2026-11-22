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
    [DataRow("SHORTEST 1")]
    [DataRow("SHORTEST 1 GROUPS")]
    public Task Gql_bag_exterior_prefilter_selects_longer_eligible_path(string selector) =>
        CheckBagExteriorSelection(selector, fixedPrefix: false);

    [TestMethod]
    [DataRow("ANY SHORTEST")]
    [DataRow("ALL SHORTEST")]
    [DataRow("SHORTEST 1")]
    [DataRow("SHORTEST 1 GROUPS")]
    public Task Gql_bag_fixed_subpath_prefilter_selects_longer_eligible_path(string selector) =>
        CheckBagExteriorSelection(selector, fixedPrefix: true);

    private async Task CheckBagExteriorSelection(string selector, bool fixedPrefix)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (s:SelectionStart),(m:SelectionMiddle),(b:SelectionBad),(g:SelectionGood),(t:SelectionEnd)," +
                "(s)-[:prefixLink {allowed:FALSE}]->(b),(s)-[:prefixLink {allowed:TRUE}]->(m)," +
                "(m)-[:prefixLink {allowed:TRUE}]->(g),(b)-[:tailLink]->(t),(g)-[:tailLink]->(t)"
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode, await seed.Content.ReadAsStringAsync());
        foreach (bool prefilter in new[] { true, false })
        {
            string query = "MATCH p=" + selector + " (s:SelectionStart)-[e:prefixLink" +
                (prefilter ? " WHERE e.allowed IS TRUE" : "") + "]->{1,2}()" +
                "(segment=()-[:tailLink]->() |+| ()-[:tailLink]->()-[:tailLink]->())+ (t:SelectionEnd)" +
                (prefilter ? "" : " WHERE VALUE { FOR edge IN e FILTER edge.allowed IS TRUE RETURN COUNT(*) AS accepted }=CARDINALITY(e)") +
                " RETURN PATH_LENGTH(p) AS length,CARDINALITY(e) AS prefixLength,CARDINALITY(segment) AS iterations";
            if (fixedPrefix)
            {
                query = "MATCH p=" + selector + " (q=(s:SelectionStart)-[e:prefixLink]->(x)" +
                    (prefilter ? " WHERE e.allowed IS TRUE AND PATH_LENGTH(q)=1" : "") + ")" +
                    "(segment=() -> () |+| () -> () -> ())+ (t:SelectionEnd)" +
                    (prefilter ? "" : " WHERE e.allowed IS TRUE AND PATH_LENGTH(q)=1") +
                    " RETURN PATH_LENGTH(p) AS length,PATH_LENGTH(q) AS prefixLength,CARDINALITY(segment) AS iterations";
            }
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query
            });
            string body = await response.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, body);
            using var result = JsonDocument.Parse(body);
            var rows = result.RootElement.GetProperty("matches");
            int eligibleRows = fixedPrefix && selector is "ALL SHORTEST" or "SHORTEST 1 GROUPS" ? 2 : 1;
            Assert.AreEqual(prefilter ? eligibleRows : 0, rows.GetArrayLength(), body);
            if (prefilter)
            {
                foreach (var row in rows.EnumerateArray())
                {
                    var bindings = row.GetProperty("bindings");
                    Assert.AreEqual(3, bindings.GetProperty("length").GetInt32());
                    Assert.AreEqual(fixedPrefix ? 1 : 2, bindings.GetProperty("prefixLength").GetInt32());
                    int iterations = bindings.GetProperty("iterations").GetInt32();
                    Assert.IsTrue(fixedPrefix ? iterations is 1 or 2 : iterations == 1);
                }
                if (eligibleRows == 2)
                {
                    CollectionAssert.AreEquivalent(new[] { 1, 2 }, rows.EnumerateArray()
                        .Select(row => row.GetProperty("bindings").GetProperty("iterations").GetInt32()).ToArray());
                }
            }
        }
        await host.StopAsync();
    }
}
