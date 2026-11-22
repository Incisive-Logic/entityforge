using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("RAND()")]
    [DataRow("RAND(1)")]
    [DataRow("randomUUID()")]
    public async Task Gql_rejects_vendor_random_functions_before_writes(string expression)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "FOR i IN [1,2] RETURN " + expression + " AS answer"
        });
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, await response.Content.ReadAsStringAsync());
        using var update = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (n:Randomized) SET n.sample=" + expression,
            transactionId = Guid.NewGuid()
        });
        Assert.AreEqual(HttpStatusCode.BadRequest, update.StatusCode, await update.Content.ReadAsStringAsync());
        using var queried = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (n:Randomized) RETURN COUNT(*) AS total"
        });
        Assert.AreEqual(HttpStatusCode.OK, queried.StatusCode, await queried.Content.ReadAsStringAsync());
        Assert.AreEqual(0, (await queried.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("total").GetInt32());
        await host.StopAsync();
    }
}
