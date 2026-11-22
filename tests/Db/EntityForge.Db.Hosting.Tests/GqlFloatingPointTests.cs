using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    public async Task Gql_standard_floating_properties_preserve_types_comparisons_and_restart_persistence()
    {
        _directory = NewDirectory();
        for (int restart = 0; restart < 2; restart++)
        {
            await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
                .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
            await host.StartAsync();
            using var client = Client(host);
            if (restart == 0)
            {
                using var seeded = await client.PostAsJsonAsync("/v1/graph/gql/update", new
                {
                    statement = "INSERT (n:NumericSample {whole:1e0,huge:1e300,negativeZero:-0e0})," +
                        "(a:NumericKey {key:9007199254740993}),(b:NumericKey {key:9007199254740992e0})"
                });
                Assert.AreEqual(HttpStatusCode.OK, seeded.StatusCode, await seeded.Content.ReadAsStringAsync());
                using var merged = await client.PostAsJsonAsync("/v1/graph/gql/update", new
                {
                    statement = "MATCH (n:NumericKey {key:9007199254740992e0}) SET n.matched=true"
                });
                Assert.AreEqual(HttpStatusCode.OK, merged.StatusCode, await merged.Content.ReadAsStringAsync());
            }
            foreach ((string query, string expected) in new[]
            {
                ("MATCH (n:NumericSample) RETURN n.whole IS TYPED FLOAT AS answer", "true"),
                ("MATCH (n:NumericSample {whole:1}) RETURN n.huge AS answer", "1e300"),
                ("MATCH (n:NumericSample) WHERE n.whole=1 RETURN n.negativeZero AS answer", "0"),
                ("MATCH (n:NumericSample) RETURN n.negativeZero IS TYPED FLOAT AS answer", "true"),
                ("MATCH (n:NumericSample) RETURN sum(n.huge) AS answer", "1e300"),
                ("MATCH (n:NumericKey) WHERE n.matched=true RETURN n.key AS answer", "9007199254740992"),
                ("MATCH (n:NumericKey) RETURN count(DISTINCT n.key) AS answer", "2")
            })
            {
                using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
                {
                    query
                });
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, query + ": " + await response.Content.ReadAsStringAsync());
                var rows = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
                Assert.AreEqual(1, rows.GetArrayLength(), query);
                using var wanted = JsonDocument.Parse(expected);
                Assert.AreEqual(ScalarKey(wanted.RootElement), ScalarKey(rows[0].GetProperty("bindings").GetProperty("answer")), query);
            }
            using var graph = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = "MATCH (n:NumericSample) RETURN n"
            });
            Assert.AreEqual(HttpStatusCode.OK, graph.StatusCode, await graph.Content.ReadAsStringAsync());
            await host.StopAsync();
        }
    }
}
