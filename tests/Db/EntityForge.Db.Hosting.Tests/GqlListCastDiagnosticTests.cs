using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("CAST([1,2] AS LIST<INTEGER>[1])")]
    [DataRow("CAST([1,2] AS INTEGER ARRAY[1] NOT NULL)")]
    [DataRow("CAST([[1,2]] AS LIST<LIST<INTEGER>[1]>[2])")]
    [DataRow("CAST(RECORD {items:[1,2]} AS RECORD {items LIST<INTEGER>[1]})")]
    [DataRow("CAST([RECORD {items:[1,2]}] AS LIST<RECORD {items LIST<INTEGER>[1]}>)")]
    [DataRow("CAST(RECORD {child:RECORD {items:[1,2]}} AS RECORD {child RECORD {items INTEGER ARRAY[1] NOT NULL}})")]
    [DataRow("CAST(RECORD {items:[[1,2]]} AS RECORD {items LIST<LIST<INTEGER>[1]>[2]})")]
    public async Task Gql_list_cast_truncation_reports_standard_condition_and_rolls_back(string expression)
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
                    statement = "INSERT (:CastProbe) RETURN " + expression + " AS answer",
                    transactionId = Guid.NewGuid()
                }
                : new
                {
                    query = "RETURN " + expression + " AS answer"
                };
            using var response = await client.PostAsJsonAsync(update ? "/v1/graph/gql/update" : "/v1/graph/gql/query", request);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, await response.Content.ReadAsStringAsync());
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.IsTrue(problem.TryGetProperty("gqlStatus", out var status), problem.GetRawText());
            Assert.AreEqual("2202F", status.GetString());
        }
        using var query = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (n:CastProbe) RETURN n"
        });
        Assert.AreEqual(HttpStatusCode.OK, query.StatusCode, await query.Content.ReadAsStringAsync());
        Assert.AreEqual(0, (await query.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches").GetArrayLength());
        await host.StopAsync();
    }
}
