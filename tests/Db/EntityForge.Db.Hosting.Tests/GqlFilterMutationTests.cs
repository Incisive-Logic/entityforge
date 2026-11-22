using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("FILTER true INSERT (:Filtered {amount:1})", 1)]
    [DataRow("FILTER false INSERT (:Filtered {amount:1})", 0)]
    [DataRow("FOR x IN [1,2,3] FILTER x>1 INSERT (:Filtered {amount:x})", 2)]
    [DataRow("FILTER true INSERT (__filterInput:Filtered {amount:1})", 1)]
    [DataRow("FILTER __filterInput=0 INSERT (:Filtered {amount:1})", -1)]
    [DataRow("FILTER true MERGE (:Filtered {amount:1})", -1)]
    [DataRow("FILTER false MERGE (:Filtered {amount:1})", -1)]
    public async Task Gql_standard_filter_selects_rows_before_atomic_insertion_and_rejects_merge(string statement, int expected)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var response = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement
        });
        if (expected < 0)
        {
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            return;
        }
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        using var query = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (n:Filtered) RETURN n.amount AS answer"
        });
        Assert.AreEqual(HttpStatusCode.OK, query.StatusCode);
        var result = await query.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual(expected, result.GetProperty("matches").GetArrayLength());
    }
}
