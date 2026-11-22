using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("SET n={fresh:3}", "n.old IS NULL AND n.keep IS NULL AND n.fresh=3")]
    [DataRow("SET n += {old:null,fresh:3}", "ERROR")]
    [DataRow("SET n={}", "n.old IS NULL AND n.keep IS NULL")]
    [DataRow("SET n += {}", "ERROR")]
    [DataRow("SET n=$props", "ERROR")]
    [DataRow("SET n=properties(m)", "ERROR")]
    [DataRow("SET r={fresh:3}", "r.old IS NULL AND r.fresh=3")]
    [DataRow("SET r += {old:null,fresh:3}", "ERROR")]
    [DataRow("SET n={a:1},n={a:2},n={a:1}", "(n.a=1 OR n.a=2) AND n.old IS NULL AND n.keep IS NULL")]
    [DataRow("SET n.temporary=1,n={fresh:3}", "n.temporary IS NULL AND n.fresh=3 AND n.old IS NULL AND n.keep IS NULL")]
    [DataRow("SET n.old=1,n.old=2,n.old=1", "n.old=1 OR n.old=2")]
    [DataRow("WITH n,$props AS props SET n=props", "ERROR")]
    public async Task Gql_standard_set_all_properties_and_rejected_map_extensions(string action, string assertion)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (:Person {old:1,keep:2})-[:rel {old:1}]->(:Source {fresh:3})"
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode, await seed.Content.ReadAsStringAsync());
        var request = new
        {
            statement = "MATCH (n:Person)-[r:rel]->(m:Source) " + action,
            transactionId = Guid.NewGuid(),
            parameters = new
            {
                props = new
                {
                    fresh = 3
                }
            }
        };
        using var first = await client.PostAsJsonAsync("/v1/graph/gql/update", request);
        if (assertion == "ERROR")
        {
            Assert.AreEqual(HttpStatusCode.BadRequest, first.StatusCode, await first.Content.ReadAsStringAsync());
            using var unchanged = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = "MATCH (n:Person)-[r:rel]->(m:Source) RETURN n.old=1 AND n.keep=2 AND r.old=1 AND n.fresh IS NULL AS answer"
            });
            Assert.AreEqual(HttpStatusCode.OK, unchanged.StatusCode, await unchanged.Content.ReadAsStringAsync());
            Assert.IsTrue((await unchanged.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetBoolean());
            return;
        }
        Assert.AreEqual(HttpStatusCode.OK, first.StatusCode, await first.Content.ReadAsStringAsync());
        using var retry = await client.PostAsJsonAsync("/v1/graph/gql/update", request);
        Assert.AreEqual(HttpStatusCode.OK, retry.StatusCode, await retry.Content.ReadAsStringAsync());
        Assert.AreEqual(await first.Content.ReadAsStringAsync(), await retry.Content.ReadAsStringAsync());
        using var query = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (n:Person)-[r:rel]->(m:Source) RETURN " + assertion + " AS answer"
        });
        Assert.AreEqual(HttpStatusCode.OK, query.StatusCode, await query.Content.ReadAsStringAsync());
        Assert.IsTrue((await query.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetBoolean());
    }

    [TestMethod]
    [DataRow("null")]
    [DataRow("42")]
    [DataRow("[]")]
    [DataRow("{old:3,bad:{nested:1}}")]
    [DataRow("{old:3,bad:[1,null]}")]
    public async Task Gql_standard_invalid_property_maps_fail_atomically(string expression)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (:Person {old:1})"
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode, await seed.Content.ReadAsStringAsync());
        using var failed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "MATCH (n:Person) SET n=" + expression
        });
        Assert.AreEqual(HttpStatusCode.BadRequest, failed.StatusCode, await failed.Content.ReadAsStringAsync());
        using var query = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (n:Person) RETURN n.old=1 AND n.bad IS NULL AS answer"
        });
        Assert.IsTrue((await query.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetBoolean());
    }
}
