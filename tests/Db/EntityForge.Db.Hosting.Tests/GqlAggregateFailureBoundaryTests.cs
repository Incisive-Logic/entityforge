using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("FOR x IN [1,0,2] RETURN SUM(1/x) AS answer")]
    [DataRow("FOR x IN [1,2] RETURN SUM(x)/(SUM(x)-3) AS answer")]
    [DataRow("FOR x IN [1,2] RETURN SUM(x) AS total NEXT RETURN total/(total-3) AS answer")]
    [DataRow("FOR x IN [1,2] RETURN x AS key, SUM(x) AS total GROUP BY key NEXT RETURN SUM(total)/(SUM(total)-3) AS answer")]
    public async Task Gql_aggregate_expression_failure_preserves_diagnostic_and_rollback_after_restart(string query)
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
                foreach (bool update in new[] { false, true })
                {
                    object request = update
                        ? new
                        {
                            statement = "INSERT (:AggregateFailureProbe) " + query,
                            transactionId = Guid.NewGuid()
                        }
                        : new
                        {
                            query
                        };
                    using var response = await client.PostAsJsonAsync(update ? "/v1/graph/gql/update" : "/v1/graph/gql/query", request);
                    var body = await response.Content.ReadFromJsonAsync<JsonElement>();
                    Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, body.GetRawText());
                    Assert.IsTrue(body.TryGetProperty("gqlStatus", out var status), body.GetRawText());
                    Assert.AreEqual("22012", status.GetString());
                }
            }
            using var check = await client.PostAsJsonAsync("/v1/graph/gql/query",
                new
                {
                    query = "MATCH (n:AggregateFailureProbe) RETURN COUNT(*) AS totalNodes"
                });
            Assert.AreEqual(HttpStatusCode.OK, check.StatusCode, await check.Content.ReadAsStringAsync());
            Assert.AreEqual(0, (await check.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0]
                .GetProperty("bindings").GetProperty("totalNodes").GetInt32());
            using var control = await client.PostAsJsonAsync("/v1/graph/gql/query",
                new
                {
                    query = "FOR x IN [1,2] RETURN x AS key, SUM(x) AS total GROUP BY key NEXT RETURN SUM(total) AS answer"
                });
            Assert.AreEqual(HttpStatusCode.OK, control.StatusCode, await control.Content.ReadAsStringAsync());
            Assert.AreEqual(3, (await control.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0]
                .GetProperty("bindings").GetProperty("answer").GetInt32());
            await host.StopAsync();
        }
    }
}
