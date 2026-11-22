using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("MATCH (n:Person) SET n['mark']=true")]
    [DataRow("MATCH (n:Person) SET n[['other','mark'][1]]=true")]
    [DataRow("MATCH (n:Person) WITH n,'mark' AS key SET n[key]=true")]
    [DataRow("MATCH (n:Person)-[:uses]->(k:Key) SET n[k.name]=true")]
    [DataRow("MATCH (n:Person) SET n[$key]=true")]
    [DataRow("MATCH (n:Person) SET n[$keys[1]]=true")]
    [DataRow("MATCH (n:Person) SET n[$options.key]=true")]
    [DataRow("UNWIND $keys AS key MATCH (n:Person) SET n[key]=true")]
    public async Task Gql_rejects_dynamic_property_mutations_without_writes(string statement)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (:Person {name:'Ada'})-[:uses]->(:Key {name:'mark'})"
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode, await seed.Content.ReadAsStringAsync());
        var request = new
        {
            statement,
            transactionId = Guid.NewGuid(),
            parameters = new
            {
                key = "mark",
                keys = new[] { "other", "mark" },
                options = new
                {
                    key = "mark"
                }
            }
        };
        using var first = await client.PostAsJsonAsync("/v1/graph/gql/update", request);
        Assert.AreEqual(HttpStatusCode.BadRequest, first.StatusCode, await first.Content.ReadAsStringAsync());
        using var retry = await client.PostAsJsonAsync("/v1/graph/gql/update", request);
        Assert.AreEqual(HttpStatusCode.BadRequest, retry.StatusCode, await retry.Content.ReadAsStringAsync());
        Assert.AreEqual(await first.Content.ReadAsStringAsync(), await retry.Content.ReadAsStringAsync());
        using var query = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (n:Person) RETURN n.mark IS NULL AS answer"
        });
        Assert.AreEqual(HttpStatusCode.OK, query.StatusCode, await query.Content.ReadAsStringAsync());
        Assert.IsTrue((await query.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetBoolean());
        using var remove = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "MATCH (n:Person) REMOVE n[['other','mark'][1]]"
        });
        Assert.AreEqual(HttpStatusCode.BadRequest, remove.StatusCode, await remove.Content.ReadAsStringAsync());
        using var absent = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (n:Person) RETURN n.mark IS NULL AS answer"
        });
        Assert.IsTrue((await absent.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetBoolean());
    }

    [TestMethod]
    [DataRow("SET n.mark=true, n[42]=1")]
    [DataRow("SET n.mark=true, n[null]=1")]
    [DataRow("SET n.mark=true, n[['key']]=1")]
    [DataRow("REMOVE n.name, n[42]")]
    [DataRow("REMOVE n.name, n[null]")]
    public async Task Gql_rejects_dynamic_property_keys_atomically(string action)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (:Person {name:'Ada'})"
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode, await seed.Content.ReadAsStringAsync());
        using var failed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "MATCH (n:Person) " + action
        });
        Assert.AreEqual(HttpStatusCode.BadRequest, failed.StatusCode, await failed.Content.ReadAsStringAsync());
        using var query = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (n:Person) RETURN n.name='Ada' AND n.mark IS NULL AS answer"
        });
        Assert.IsTrue((await query.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetBoolean());
    }
}
