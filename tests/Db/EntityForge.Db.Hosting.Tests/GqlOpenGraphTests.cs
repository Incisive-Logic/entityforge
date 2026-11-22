using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    public async Task Gql_standard_open_graph_accepts_new_labels_properties_and_value_types_after_restart()
    {
        _directory = NewDirectory();
        await using (var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build())
        {
            await host.StartAsync();
            using var client = Client(host);
            foreach (string statement in new[]
            {
                "INSERT (a:OpenItem {numberId:1,payload:42})-[:NewLink {enabled:TRUE}]->(:OpenItem {numberId:2,payload:'text'})",
                "MATCH (a:OpenItem WHERE a.numberId=1) SET a:Reviewed, a.payload='changed', a.extra=TRUE",
                "MATCH (a:OpenItem WHERE a.numberId=2) REMOVE a.payload"
            })
            {
                using var response = await client.PostAsJsonAsync("/v1/graph/gql/update", new
                {
                    statement
                });
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            }
            await host.StopAsync();
        }
        await using var reopened = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await reopened.StartAsync();
        using var reader = Client(reopened);
        using var result = await reader.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (a:OpenItem&Reviewed)-[e:NewLink]->(b:OpenItem) RETURN a.payload AS payload, a.extra AS extra, e.enabled AS enabled, PROPERTY_EXISTS(b,payload) AS present"
        });
        Assert.AreEqual(HttpStatusCode.OK, result.StatusCode, await result.Content.ReadAsStringAsync());
        var rows = (await result.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
        Assert.AreEqual(1, rows.GetArrayLength());
        var bindings = rows[0].GetProperty("bindings");
        Assert.AreEqual("changed", bindings.GetProperty("payload").GetString());
        Assert.IsTrue(bindings.GetProperty("extra").GetBoolean());
        Assert.IsTrue(bindings.GetProperty("enabled").GetBoolean());
        Assert.IsFalse(bindings.GetProperty("present").GetBoolean());
        using var extension = await reader.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (:AfterRestart {amount:1.5})"
        });
        Assert.AreEqual(HttpStatusCode.OK, extension.StatusCode, await extension.Content.ReadAsStringAsync());
        await reopened.StopAsync();
    }
}
