using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("MATCH (n:Person) SET n.age=n.age+1 RETURN n NEXT FILTER n.age>20 SET n.high=true", "MATCH (n:Person) WHERE n.high=true RETURN count(*) AS answer", 2)]
    [DataRow("MATCH (n:Person) SET n.flag=true RETURN count(*) AS total NEXT MATCH (m:Person) SET m.total=total", "MATCH (n:Person) RETURN sum(n.total) AS answer", 9)]
    [DataRow("MATCH (n:Person) SET n.flag=true RETURN n LIMIT 1 NEXT SET n.chosen=true", "MATCH (n:Person) WHERE n.flag=true RETURN count(*) AS answer", 3)]
    [DataRow("MATCH (n:Person) SET n.flag=true RETURN n LIMIT 1 NEXT SET n.chosen=true", "MATCH (n:Person) WHERE n.chosen=true RETURN count(*) AS answer", 1)]
    [DataRow("MATCH (n:Person) SET n.age=n.age+1 RETURN n NEXT FILTER n.age>20 DELETE n", "MATCH (n:Person) RETURN sum(n.age) AS answer", 11)]
    [DataRow("INSERT (a:Added {amount:7}) RETURN a NEXT INSERT (b:Added {amount:a.amount+1})", "MATCH (n:Added) RETURN sum(n.amount) AS answer", 15)]
    [DataRow("MATCH (n:Person) INSERT (a:Added {amount:n.age}) RETURN a NEXT SET a.amount=a.amount+1", "MATCH (n:Added) RETURN sum(n.amount) AS answer", 63)]
    [DataRow("INSERT (a:Added {amount:7}) RETURN a NEXT MATCH (n:Added) SET n.amount=n.amount+1", "MATCH (n:Added) RETURN sum(n.amount) AS answer", 8)]
    [DataRow("MATCH (n:Person) SET n.flag=true RETURN n NEXT INSERT (a:Added {amount:n.age}) RETURN a NEXT SET a.amount=a.amount+1", "MATCH (n:Added) RETURN sum(n.amount) AS answer", 63)]
    [DataRow("FOR x IN [1,1,2] MERGE (a:Added {amount:x}) ON INSERT SET a.hits=1 ON MATCH SET a.hits=a.hits+1 RETURN a NEXT SET a.marked=true", "MATCH (n:Added) RETURN sum(n.hits) AS answer", -1)]
    [DataRow("MERGE (a:Added {amount:7}) RETURN a NEXT MERGE (a)-[r:link]->(b:Added {amount:8}) RETURN b NEXT SET b.amount=9", "MATCH (n:Added) RETURN sum(n.amount) AS answer", -1)]
    [DataRow("MATCH (n:Person) SET n.flag=true RETURN n NEXT MERGE (a:Added {amount:n.age}) RETURN a NEXT SET a.amount=a.amount+1", "MATCH (n:Added) RETURN sum(n.amount) AS answer", -1)]
    [DataRow("INSERT (a:Added {amount:7}) INSERT (b:Added {amount:8})", "MATCH (n:Added) RETURN sum(n.amount) AS answer", 15)]
    public async Task Gql_standard_interleaved_updates_observe_changes_and_whole_row_stream(string statement, string query, int expected)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (:Person {age:10}),(:Person {age:20}),(:Person {age:30})"
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode, await seed.Content.ReadAsStringAsync());
        using var update = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement
        });
        if (expected < 0)
        {
            Assert.AreEqual(HttpStatusCode.BadRequest, update.StatusCode, await update.Content.ReadAsStringAsync());
            using var unchanged = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = "MATCH (n:Person) RETURN COUNT(*)=3 AND SUM(n.age)=60 AND COUNT(n.flag)=0 AS answer"
            });
            Assert.AreEqual(HttpStatusCode.OK, unchanged.StatusCode, await unchanged.Content.ReadAsStringAsync());
            Assert.IsTrue((await unchanged.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetBoolean());
            return;
        }
        Assert.AreEqual(HttpStatusCode.OK, update.StatusCode, await update.Content.ReadAsStringAsync());
        using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query
        });
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual(expected, result.GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetInt32());
        await host.StopAsync();
    }

    [TestMethod]
    public async Task Gql_standard_interleaved_update_failure_discards_prior_changes()
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (:Person {age:10})"
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode);
        using var update = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "MATCH (n:Person) SET n.age=99 RETURN n NEXT SET n.age=1/0"
        });
        Assert.AreEqual(HttpStatusCode.BadRequest, update.StatusCode, await update.Content.ReadAsStringAsync());
        using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (n:Person) RETURN n.age AS answer"
        });
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual(10, result.GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetInt32());
        await host.StopAsync();
    }

    [TestMethod]
    public async Task Gql_standard_interleaved_creation_replay_survives_restart()
    {
        _directory = NewDirectory();
        Guid transactionId = Guid.NewGuid();
        string statement = "INSERT (a:Added {amount:7}) RETURN a NEXT INSERT (b:Added {amount:a.amount+1}) RETURN a,b NEXT SET a.amount=b.amount";
        string? receipt = null;
        for (int restart = 0; restart < 2; restart++)
        {
            await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
                .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
            await host.StartAsync();
            using var client = Client(host);
            for (int retry = 0; retry < 2; retry++)
            {
                using var update = await client.PostAsJsonAsync("/v1/graph/gql/update", new
                {
                    statement,
                    transactionId
                });
                string body = await update.Content.ReadAsStringAsync();
                Assert.AreEqual(HttpStatusCode.OK, update.StatusCode, body);
                receipt ??= body;
                Assert.AreEqual(receipt, body);
            }
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = "MATCH (n:Added) RETURN sum(n.amount) AS answer,count(*) AS total"
            });
            var result = await response.Content.ReadFromJsonAsync<JsonElement>();
            var values = result.GetProperty("matches")[0].GetProperty("bindings");
            Assert.AreEqual(16, values.GetProperty("answer").GetInt32());
            Assert.AreEqual(2, values.GetProperty("total").GetInt32());
            await host.StopAsync();
        }
    }
}
