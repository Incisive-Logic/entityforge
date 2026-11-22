using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("CAST(RECORD {items:[1,2]} AS RECORD {items LIST<INTEGER>[1]})", "2202F")]
    [DataRow("CAST([RECORD {items:[1,2]}] AS LIST<RECORD {items LIST<INTEGER>[1]}>)", "2202F")]
    [DataRow("CAST(RECORD {child:RECORD {items:[1,2]}} AS RECORD {child RECORD {items LIST<DEC>[1]}})", "2202F")]
    [DataRow("CAST(RECORD {amount:1.5} AS RECORD {amount INTEGER})", "22003")]
    [DataRow("CAST([RECORD {amount:9223372036854775808.0}] AS LIST<RECORD {amount INTEGER}>)", "22003")]
    [DataRow("CAST(RECORD {child:RECORD {amount:0.1e0}} AS RECORD {child RECORD {amount DEC}})", "22003")]
    public async Task Gql_nested_record_cast_failure_preserves_diagnostic_and_atomicity_after_restart(string expression, string expectedStatus)
    {
        _directory = NewDirectory();
        Guid rejectedId = Guid.NewGuid();
        for (int restart = 0; restart < 2; restart++)
        {
            await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
                .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
            await host.StartAsync();
            using var client = Client(host);
            foreach (bool update in new[] { false, true })
            {
                object request = update
                    ? new
                    {
                        statement = "INSERT (:RecordFailureProbe) RETURN " + expression + " AS answer",
                        transactionId = rejectedId
                    }
                    : new
                    {
                        query = "RETURN " + expression + " AS answer"
                    };
                using var response = await client.PostAsJsonAsync(update ? "/v1/graph/gql/update" : "/v1/graph/gql/query", request);
                Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, await response.Content.ReadAsStringAsync());
                var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
                Assert.IsTrue(problem.TryGetProperty("gqlStatus", out var status), problem.GetRawText());
                Assert.AreEqual(expectedStatus, status.GetString());
            }
            using var check = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = "MATCH (n:RecordFailureProbe) RETURN n"
            });
            Assert.AreEqual(HttpStatusCode.OK, check.StatusCode, await check.Content.ReadAsStringAsync());
            Assert.AreEqual(0, (await check.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches").GetArrayLength());
            await host.StopAsync();
        }
    }
}
