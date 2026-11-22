using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("CARDINALITY(TRIM([1,2],-1))", "2202E")]
    [DataRow("CARDINALITY(TRIM([1,2],3))", "2202E")]
    [DataRow("CARDINALITY(CAST([1,2] AS LIST<INTEGER>[1]))", "2202F")]
    [DataRow("(1).amount", "22G03")]
    [DataRow("ASIN(2)", "2202M")]
    [DataRow("LOG(1,8)", "2201Z")]
    public async Task Gql_corrected_conditions_survive_inline_procedures_and_abort_updates(string expression, string status)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        foreach (bool update in new[] { false, true })
        {
            string statement = (update ? "INSERT (:ConditionProbe) " : "") +
                "CALL { RETURN " + expression + " AS answer } RETURN answer";
            object request = update ? new
            {
                statement,
                transactionId = Guid.NewGuid()
            }
            : new
            {
                query = statement
            };
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/" + (update ? "update" : "query"), request);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, body.GetRawText());
            Assert.IsTrue(body.TryGetProperty("gqlStatus", out var condition), body.GetRawText());
            Assert.AreEqual(status, condition.GetString(), body.GetRawText());
        }
        using var stored = await client.PostAsJsonAsync("/v1/graph/gql/query",
            new
            {
                query = "MATCH (:ConditionProbe) RETURN COUNT(*) AS total"
            });
        Assert.AreEqual(HttpStatusCode.OK, stored.StatusCode, await stored.Content.ReadAsStringAsync());
        Assert.AreEqual(0, (await stored.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("matches")[0].GetProperty("bindings").GetProperty("total").GetInt32());
        using var safe = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "CALL { RETURN CASE WHEN TRUE THEN 1 ELSE " + expression + " END AS answer } RETURN answer"
        });
        Assert.AreEqual(HttpStatusCode.OK, safe.StatusCode, await safe.Content.ReadAsStringAsync());
        Assert.AreEqual(1, (await safe.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetInt32());
        await host.StopAsync();
    }
}
