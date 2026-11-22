using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("(1).amount")]
    [DataRow("('text').amount")]
    [DataRow("([1,2]).amount")]
    [DataRow("VALUE { RETURN 1 AS item }.amount")]
    public async Task Gql_property_reference_invalid_value_type_reports_condition_and_aborts_update(string expression)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        foreach (bool update in new[] { false, true })
        {
            object request = update
                ? new
                {
                    statement = "INSERT (:PropertyProbe) RETURN " + expression + " AS answer",
                    transactionId = Guid.NewGuid()
                }
                : new
                {
                    query = "RETURN " + expression + " AS answer"
                };
            using var response = await client.PostAsJsonAsync(update ? "/v1/graph/gql/update" : "/v1/graph/gql/query", request);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, body.GetRawText());
            Assert.IsTrue(body.TryGetProperty("gqlStatus", out var status), body.GetRawText());
            Assert.AreEqual("22G03", status.GetString());
        }
        using var check = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (n:PropertyProbe) RETURN n"
        });
        Assert.AreEqual(HttpStatusCode.OK, check.StatusCode);
        Assert.AreEqual(0, (await check.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches").GetArrayLength());
        foreach (var (value, expected) in new[] { ("(NULL).amount", "null"), ("(RECORD {}).amount", "null"), ("(RECORD {amount:1}).amount", "1") })
        {
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = "RETURN " + value + " AS answer"
            });
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, body.GetRawText());
            Assert.AreEqual(expected, body.GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetRawText());
        }
        await host.StopAsync();
    }
}
