using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    public async Task Gql_standard_updating_CALL_preserves_duplicate_invocations_and_unit_cardinality()
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        var request = new
        {
            statement = "FOR i IN [1,1] CALL (i) { INSERT (:Added {amount:i}) }",
            transactionId = Guid.NewGuid()
        };
        for (int retry = 0; retry < 2; retry++)
        {
            using var update = await client.PostAsJsonAsync("/v1/graph/gql/update", request);
            Assert.AreEqual(HttpStatusCode.OK, update.StatusCode, await update.Content.ReadAsStringAsync());
        }
        using var query = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (n:Added) RETURN count(*) AS total,sum(n.amount) AS answer"
        });
        var result = (await query.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings");
        Assert.AreEqual(2, result.GetProperty("total").GetInt32());
        Assert.AreEqual(2, result.GetProperty("answer").GetInt32());
        await host.StopAsync();
    }

    [TestMethod]
    [DataRow("FOR i IN [1,0] CALL (i) { INSERT (:Added {amount:1/i}) } RETURN COUNT(*) AS total")]
    [DataRow("LET i=1 CALL (i) { INSERT (:Added) RETURN missing AS extra } RETURN i")]
    [DataRow("LET i=1 CALL () { INSERT (:Added {amount:i}) } RETURN i")]
    [DataRow("LET i=1 CALL (i) { LET i=2 INSERT (:Added {amount:i}) } RETURN i")]
    [DataRow("CALL () { INSERT (:Added) RETURN 1/0 AS answer }")]
    public async Task Gql_standard_updating_CALL_failure_and_invalid_scope_commit_nothing(string statement)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var update = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement
        });
        Assert.AreEqual(HttpStatusCode.BadRequest, update.StatusCode, await update.Content.ReadAsStringAsync());
        using var query = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (n:Added) RETURN count(*) AS total"
        });
        var result = (await query.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings");
        Assert.AreEqual(0, result.GetProperty("total").GetInt32());
        await host.StopAsync();
    }
}
