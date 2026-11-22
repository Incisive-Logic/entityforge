using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("FOR item IN [DURATION('P1M'),DURATION('P1D')] RETURN item ORDER BY item")]
    [DataRow("FOR item IN [[DURATION('P1M')],[DURATION('P1D')]] RETURN item ORDER BY item")]
    public async Task Gql_pipeline_sort_failure_is_a_request_error_and_rolls_back_after_restart(string query)
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
                            statement = "INSERT (:SortFailureProbe) " + query,
                            transactionId = Guid.NewGuid()
                        }
                        : new
                        {
                            query
                        };
                    using var response = await client.PostAsJsonAsync(update ? "/v1/graph/gql/update" : "/v1/graph/gql/query", request);
                    string text = await response.Content.ReadAsStringAsync();
                    Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, text);
                    using var body = JsonDocument.Parse(text);
                    Assert.AreEqual("request_invalid", body.RootElement.GetProperty("code").GetString());
                    StringAssert.Contains(body.RootElement.GetProperty("detail").GetString()!, "duration families");
                }
            }
            using var check = await client.PostAsJsonAsync("/v1/graph/gql/query",
                new
                {
                    query = "MATCH (n:SortFailureProbe) RETURN COUNT(*) AS totalNodes"
                });
            Assert.AreEqual(HttpStatusCode.OK, check.StatusCode, await check.Content.ReadAsStringAsync());
            Assert.AreEqual(0, (await check.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0]
                .GetProperty("bindings").GetProperty("totalNodes").GetInt32());
            using var control = await client.PostAsJsonAsync("/v1/graph/gql/query",
                new
                {
                    query = "FOR item IN [3,1,2] RETURN item ORDER BY item"
                });
            Assert.AreEqual(HttpStatusCode.OK, control.StatusCode, await control.Content.ReadAsStringAsync());
            CollectionAssert.AreEqual(new[] { 1, 2, 3 }, (await control.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("matches").EnumerateArray().Select(row => row.GetProperty("bindings").GetProperty("item").GetInt32()).ToArray());
            await host.StopAsync();
        }
    }
}
