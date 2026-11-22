using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    public async Task Gql_NaN_grouping_is_value_based_across_evaluation_and_restart()
    {
        _directory = NewDirectory();
        for (int attempt = 0; attempt < 2; attempt++)
        {
            await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
                .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
            await host.StartAsync();
            using var client = Client(host);
            if (attempt == 0)
            {
                using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
                {
                    statement = "INSERT (:NaNProbe {sampleValue:0e0/0e0}),(:NaNProbe {sampleValue:0e0/0e0})"
                });
                Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode, await seed.Content.ReadAsStringAsync());
            }
            foreach (var (query, expected) in new[]
            {
                ("FOR x IN [0e0/0e0,0e0/0e0] RETURN COUNT(DISTINCT x) AS answer", 1),
                ("FOR x IN [0e0/0e0,0e0/0e0] RETURN DISTINCT x NEXT RETURN COUNT(*) AS answer", 1),
                ("FOR x IN [0e0/0e0,0e0/0e0] RETURN x AS n,COUNT(*) AS qty GROUP BY n NEXT RETURN COUNT(*) AS answer", 1),
                ("RETURN 0e0/0e0 AS n UNION DISTINCT RETURN 0e0/0e0 AS n NEXT RETURN COUNT(*) AS answer", 1),
                ("LET n=0e0/0e0 RETURN CASE WHEN n=n THEN 1 ELSE 0 END AS answer", 0),
                ("FOR x IN [-0e0,0e0] RETURN COUNT(DISTINCT x) AS answer", 1),
                ("MATCH (n:NaNProbe) RETURN COUNT(DISTINCT n.sampleValue) AS answer", 1),
                ("MATCH (n:NaNProbe) RETURN n.sampleValue AS sampleValue,COUNT(*) AS qty GROUP BY sampleValue NEXT RETURN COUNT(*) AS answer", 1)
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
            await host.StopAsync();
        }
    }
}
