using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("SET n:$('Dynamic')")]
    [DataRow("SET n:$(['Dynamic','Reviewed'])")]
    [DataRow("WITH n,$labels AS labels SET n:$(labels)")]
    [DataRow("SET n:$($labels)")]
    [DataRow("SET n:$([])")]
    public async Task Gql_standard_rejects_dynamic_labels_without_mutation(string action)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (:Person)"
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode, await seed.Content.ReadAsStringAsync());
        var request = new
        {
            statement = "MATCH (n:Person) " + action,
            transactionId = Guid.NewGuid(),
            parameters = new
            {
                labels = new[] { "Dynamic", "Reviewed" }
            }
        };
        using var first = await client.PostAsJsonAsync("/v1/graph/gql/update", request);
        Assert.AreEqual(HttpStatusCode.BadRequest, first.StatusCode, await first.Content.ReadAsStringAsync());
        using var retry = await client.PostAsJsonAsync("/v1/graph/gql/update", request);
        Assert.AreEqual(await first.Content.ReadAsStringAsync(), await retry.Content.ReadAsStringAsync());
        using var query = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (n:Person) RETURN n:Dynamic AS answer"
        });
        Assert.AreEqual(false, (await query.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetBoolean());
        using var remove = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "MATCH (n:Person) REMOVE n:$(['Dynamic','Reviewed'])"
        });
        Assert.AreEqual(HttpStatusCode.BadRequest, remove.StatusCode, await remove.Content.ReadAsStringAsync());
        using var absent = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (n:Person) RETURN n:!Dynamic&!Reviewed AS answer"
        });
        Assert.IsTrue((await absent.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetBoolean());
    }

    [TestMethod]
    [DataRow("SET n IS Unexpected, n:$(null)")]
    [DataRow("SET n IS Unexpected, n:$(['Dynamic',null])")]
    [DataRow("SET n IS Unexpected, n:$(42)")]
    [DataRow("REMOVE n IS Person, n:$(['Dynamic',42])")]
    public async Task Gql_standard_invalid_dynamic_labels_fail_atomically(string action)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (:Person)"
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode, await seed.Content.ReadAsStringAsync());
        using var failed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "MATCH (n:Person) " + action
        });
        Assert.AreEqual(HttpStatusCode.BadRequest, failed.StatusCode, await failed.Content.ReadAsStringAsync());
        using var unchanged = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (n) RETURN n:Person&!Unexpected&!Dynamic AS answer"
        });
        Assert.IsTrue((await unchanged.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetBoolean());
    }

    [TestMethod]
    public async Task Gql_standard_label_mutations_support_stages_multiple_labels_and_retries()
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (:Person&Employee {age:1}),(:Person&Employee {age:2})"
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode, await seed.Content.ReadAsStringAsync());
        foreach (string statement in new[]
        {
            "MATCH (n:Person) RETURN n ORDER BY n.age LIMIT 1 NEXT SET n IS Selected, n IS Reviewed, n.mark=true",
            "MATCH (n:Person) RETURN n ORDER BY n.age LIMIT 1 NEXT REMOVE n IS Employee, n IS Absent, n.mark"
        })
        {
            Guid transactionId = Guid.NewGuid();
            using var first = await client.PostAsJsonAsync("/v1/graph/gql/update", new
            {
                statement,
                transactionId
            });
            Assert.AreEqual(HttpStatusCode.OK, first.StatusCode, await first.Content.ReadAsStringAsync());
            using var retry = await client.PostAsJsonAsync("/v1/graph/gql/update", new
            {
                statement,
                transactionId
            });
            Assert.AreEqual(HttpStatusCode.OK, retry.StatusCode, await retry.Content.ReadAsStringAsync());
            Assert.AreEqual(await first.Content.ReadAsStringAsync(), await retry.Content.ReadAsStringAsync());
        }
        using var selected = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (n:Person) RETURN n ORDER BY n.age NEXT RETURN n IS LABELED Selected&Reviewed&!Employee AND n.mark IS NULL AS answer"
        });
        Assert.AreEqual(HttpStatusCode.OK, selected.StatusCode, await selected.Content.ReadAsStringAsync());
        var result = await selected.Content.ReadFromJsonAsync<JsonElement>();
        CollectionAssert.AreEqual(new[] { true, false }, result.GetProperty("matches").EnumerateArray()
            .Select(row => row.GetProperty("bindings").GetProperty("answer").GetBoolean()).ToArray());
        using var failed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "MATCH (n:Person) SET n IS Unexpected, n.age=1/0"
        });
        Assert.AreEqual(HttpStatusCode.BadRequest, failed.StatusCode, await failed.Content.ReadAsStringAsync());
        using var unchanged = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (n:Unexpected) RETURN n"
        });
        Assert.AreEqual(HttpStatusCode.OK, unchanged.StatusCode, await unchanged.Content.ReadAsStringAsync());
        Assert.AreEqual(0, (await unchanged.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches").GetArrayLength());
    }
}
