using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("MATCH (n:Counter) SET n.x=2,n.y=n.x", "n.x=2 AND n.y=0")]
    [DataRow("MATCH (n:Counter) SET n.x=2 SET n.y=n.x", "n.x=2 AND n.y=2")]
    [DataRow("MATCH (n:Counter) SET n.x=2 REMOVE n.x SET n.y=coalesce(n.x,9)", "n.x IS NULL AND n.y=9")]
    [DataRow("MATCH (n:Counter) SET n:Seen SET n.y=CASE WHEN n IS LABELED Counter&Seen THEN 2 ELSE 0 END", "n.y=2 AND n IS LABELED Seen")]
    [DataRow("MATCH (n:Counter) FOR i IN [1,2,3] SET n.x=n.x+1,n.y=n.x", "n.x=1 AND n.y=0")]
    [DataRow("MATCH (n:Counter) RETURN n,n AS alias NEXT FOR i IN [1,2] SET n.x=alias.x+1", "n.x=1")]
    [DataRow("MATCH (n:Counter) SET n={x:4} SET n.y=n.x", "n.x=4 AND n.y=4")]
    [DataRow("MATCH (n:Counter) SET n.x=1 SET n.x=n.x+1 SET n.x=n.x+1", "n.x=3")]
    public async Task Gql_standard_write_sequences_and_repeated_rows_preserve_visibility_and_atomic_retries(string statement, string condition)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (:Counter {x:0})"
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode, await seed.Content.ReadAsStringAsync());
        var request = new
        {
            statement,
            transactionId = Guid.NewGuid()
        };
        using var first = await client.PostAsJsonAsync("/v1/graph/gql/update", request);
        Assert.AreEqual(HttpStatusCode.OK, first.StatusCode, await first.Content.ReadAsStringAsync());
        using var retry = await client.PostAsJsonAsync("/v1/graph/gql/update", request);
        Assert.AreEqual(await first.Content.ReadAsStringAsync(), await retry.Content.ReadAsStringAsync());
        using var query = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (n:Counter) RETURN " + condition + " AS answer"
        });
        Assert.IsTrue((await query.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetBoolean());
        using var failed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "MATCH (n:Counter) SET n.bad=1 SET n.fail=1/0"
        });
        Assert.IsFalse(failed.IsSuccessStatusCode);
        using var unchanged = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (n:Counter) WHERE n.bad IS NOT NULL RETURN n"
        });
        Assert.AreEqual(0, (await unchanged.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches").GetArrayLength());
    }
}
