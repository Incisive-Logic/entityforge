using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    public async Task Gql_standard_unbounded_simple_paths_allow_closed_edge_reuse_and_preserve_replay()
    {
        _directory = NewDirectory();
        var seed = new
        {
            statement = "INSERT (:SimplePair {name:'A'})-[:pair]->(:SimplePair {name:'B'})",
            transactionId = Guid.NewGuid()
        };
        const string start = "(origin:SimplePair {name:'A'})";
        const string end = "(destination:SimplePair {name:'A'})";
        const string repeated = "((a)-[outward:pair]->(b)<-[backward:pair]-(c))+";
        var update = new
        {
            statement = "MATCH REPEATABLE ELEMENTS p=SIMPLE " + start + repeated + end + " SET destination.checked=TRUE RETURN PATH_LENGTH(p) AS answer",
            transactionId = Guid.NewGuid()
        };
        string? receipt = null;
        for (int restart = 0; restart < 2; restart++)
        {
            await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
                .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
            await host.StartAsync();
            using var client = Client(host);
            using var seeded = await client.PostAsJsonAsync("/v1/graph/gql/update", seed);
            Assert.AreEqual(HttpStatusCode.OK, seeded.StatusCode, await seeded.Content.ReadAsStringAsync());
            foreach (var (query, expected) in new[]
            {
                ("MATCH REPEATABLE ELEMENTS p=SIMPLE " + start + "-[e:pair]-+" + end + " RETURN SUM(PATH_LENGTH(p)) AS answer", 2),
                ("MATCH REPEATABLE ELEMENTS p=SIMPLE " + start + "-[e:pair]-*" + end + " RETURN COUNT(*) AS answer", 2),
                ("MATCH REPEATABLE ELEMENTS p=SIMPLE " + start + repeated + end + " RETURN CASE WHEN SIZE(outward)=1 AND SIZE(backward)=1 AND PATH_LENGTH(p)=2 THEN 1 ELSE 0 END AS answer", 1),
                ("MATCH REPEATABLE ELEMENTS p=ALL SHORTEST SIMPLE " + start + repeated + end + " RETURN SUM(PATH_LENGTH(p)) AS answer", 2),
                ("MATCH REPEATABLE ELEMENTS p=SIMPLE " + start + "-[e:pair]-{3,}(destination) RETURN COUNT(*) AS answer", 0),
                ("MATCH REPEATABLE ELEMENTS p=TRAIL " + start + "-[e:pair]-+" + end + " RETURN COUNT(*) AS answer", 0),
                ("MATCH REPEATABLE ELEMENTS p=ACYCLIC " + start + "-[e:pair]-+" + end + " RETURN COUNT(*) AS answer", 0),
                ("MATCH p=SIMPLE " + start + "-[e:pair]-+" + end + " RETURN COUNT(*) AS answer", 0),
                ("MATCH REPEATABLE ELEMENTS p=SIMPLE " + start + "-[e:pair]-+(destination) RETURN CASE WHEN CURRENT_DATE=DATE() THEN SUM(PATH_LENGTH(p)) ELSE 0 END AS answer", 3)
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
            using var changed = await client.PostAsJsonAsync("/v1/graph/gql/update", update);
            Assert.AreEqual(HttpStatusCode.OK, changed.StatusCode, await changed.Content.ReadAsStringAsync());
            receipt ??= await changed.Content.ReadAsStringAsync();
            Assert.AreEqual(receipt, await changed.Content.ReadAsStringAsync());
            Assert.AreEqual(2, (await changed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result").GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetInt32());
            await host.StopAsync();
        }
    }
}
