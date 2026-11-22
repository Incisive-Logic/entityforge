using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("FOREACH (i IN [1,0] | CREATE (:ForeachCheck {v:1/i}))")]
    [DataRow("FOREACH (i IN [1] | CREATE (n:ForeachCheck)) RETURN n")]
    [DataRow("FOREACH (i IN [1] | MATCH (n) SET n.v=i)")]
    [DataRow("FOREACH (i IN [1] | RETURN i)")]
    [DataRow("FOREACH (i IN [] | SET absent.v=1)")]
    [DataRow("MATCH (n) FOREACH (i IN collect(n) | SET i.v=1)")]
    public async Task Gql_rejects_foreach_extension_without_writes(string statement)
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
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, await response.Content.ReadAsStringAsync());
        using var queried = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (n:ForeachCheck) RETURN count(*) AS answer"
        });
        Assert.AreEqual(0, (await queried.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetInt32());
    }
}
