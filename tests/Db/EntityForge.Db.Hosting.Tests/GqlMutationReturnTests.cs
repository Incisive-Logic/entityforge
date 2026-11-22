using EntityForge.Db.Gql;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("FOREACH (i IN COLLECT { UNWIND [1,2] AS x RETURN x } | CREATE (:Added {age:i})) MATCH (n:Added) RETURN sum(n.age) AS answer")]
    [DataRow("MATCH (n:Person) FOREACH (i IN [x IN [1,2] | x+COUNT { MATCH (m:Person) }] | SET n.age=n.age+i) RETURN sum(n.age) AS answer")]
    [DataRow("CREATE (n:Added {age:5}) FOREACH (i IN COLLECT { MATCH (m:Added) RETURN m.age } | SET n.age=n.age+i) RETURN n.age AS answer")]
    [DataRow("MATCH (n:Person) FOREACH (i IN [1,2] | SET n.age=n.age+i) RETURN sum(n.age) AS answer")]
    [DataRow("MATCH (n:Person) FOREACH (i IN [] | SET n.age=0) RETURN sum(n.age) AS answer")]
    [DataRow("MATCH (n:Person) FOREACH (i IN null | SET n.age=0) RETURN sum(n.age) AS answer")]
    [DataRow("MATCH (n:Person) FOREACH (i IN 2 | SET n.age=n.age+i) RETURN sum(n.age) AS answer")]
    [DataRow("MATCH (n:Person) FOREACH (i IN [n,n] | SET i.age=i.age+1) RETURN sum(n.age) AS answer")]
    [DataRow("MATCH (n:Person) FOREACH (i IN [1,2] | FOREACH (j IN [3,4] | SET n.age=n.age+i*j)) RETURN sum(n.age) AS answer")]
    [DataRow("MATCH (n:Person) FOREACH (n IN [1,2] | CREATE (:Added {age:n})) RETURN sum(n.age) AS answer")]
    [DataRow("FOREACH (i IN [1,2] | CREATE (:Added {age:i})) MATCH (n:Added) RETURN sum(n.age) AS answer")]
    [DataRow("FOREACH (i IN [1,1] | MERGE (n:Added {age:i}) ON CREATE SET n.hits=0 SET n.hits=n.hits+1) MATCH (n:Added) RETURN sum(n.hits) AS answer")]
    [DataRow("CREATE (n:Added {age:0}) FOREACH (i IN [1,2] | SET n.age=n.age+i) RETURN n.age AS answer")]
    [DataRow("CALL () { FOREACH (i IN [1,2] | CREATE (:Added {age:i})) } MATCH (n:Added) RETURN sum(n.age) AS answer")]
    [DataRow("UNWIND [1,1] AS i CALL (i) { MERGE (n:Added {age:i}) ON CREATE SET n.hits=0 SET n.hits=n.hits+1 RETURN n.hits AS hits } RETURN sum(hits) AS answer")]
    [DataRow("UNWIND [1,1,2] AS x MERGE (n:Added {age:x}) RETURN sum(n.age) AS answer")]
    public async Task Gql_standard_update_rejects_vendor_iteration_and_merge_without_writes(string statement)
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
        Assert.AreEqual(HttpStatusCode.BadRequest, update.StatusCode, await update.Content.ReadAsStringAsync());
        using var query = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (n) RETURN COUNT(*) AS total,SUM(n.age) AS ages"
        });
        Assert.AreEqual(HttpStatusCode.OK, query.StatusCode, await query.Content.ReadAsStringAsync());
        var bindings = (await query.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings");
        Assert.AreEqual(3, bindings.GetProperty("total").GetInt32());
        Assert.AreEqual(60, bindings.GetProperty("ages").GetInt32());
        await host.StopAsync();
    }

    [TestMethod]
    [DataRow("INSERT (n:Added {age:7}) RETURN n.age AS answer", 7)]
    [DataRow("FOR n IN [1,2,2] RETURN n NEXT RETURN SUM(n) AS amount UNION ALL RETURN COUNT(*) AS amount NEXT INSERT (a:Added {age:amount}) RETURN SUM(a.age) AS answer", 8)]
    [DataRow("MATCH (n:Person) SET n.checked=TRUE RETURN n.age AS age NEXT RETURN SUM(age) AS total UNION ALL RETURN COUNT(*) AS total NEXT RETURN SUM(total) AS answer", 63)]
    [DataRow("RETURN 1 AS n UNION ALL RETURN 2 AS n NEXT INSERT (a:Added {age:n}) RETURN SUM(a.age) AS answer", 3)]
    [DataRow("RETURN 1+2 AS \"1+2\" UNION ALL RETURN 1+2 AS \"1+2\" NEXT YIELD `1+2` AS n INSERT (a:Added {age:n}) RETURN SUM(a.age) AS answer", 6)]
    [DataRow("MATCH (n:Person) RETURN n UNION ALL MATCH (n:Absent) RETURN n NEXT SET n.age=n.age+1 RETURN SUM(n.age) AS answer", 63)]
    [DataRow("FOR i IN [1,2] CALL (i) { INSERT (n:Added {age:i}) } RETURN sum(i) AS answer", 3)]
    [DataRow("FOR i IN [1,1] CALL (i) { INSERT (n:Added {age:i}) RETURN n.age AS age } RETURN sum(age) AS answer", 2)]
    [DataRow("MATCH (n:Person) CALL (n) { SET n.age=n.age+1 } RETURN sum(n.age) AS answer", 63)]
    [DataRow("MATCH (n:Person) CALL (n) { LET localAmount=1 SET n.age=n.age+localAmount } RETURN sum(n.age) AS answer", 63)]
    [DataRow("MATCH (n:Person) CALL { SET n.age=n.age+1 RETURN n.age AS age } RETURN sum(age) AS answer", 63)]
    [DataRow("FOR i IN [1,2] CALL (i) { FILTER false INSERT (n:Added) } RETURN sum(i) AS answer", 3)]
    [DataRow("FOR i IN [1,2] CALL (i) { FILTER WHERE FALSE INSERT (n:Added) } RETURN sum(i) AS answer", 3)]
    [DataRow("FOR i IN [1,2] CALL (i) { FILTER i=1 INSERT (n:Added) RETURN i AS selected } RETURN count(*) AS answer", 1)]
    [DataRow("FOR i IN [1,2] OPTIONAL CALL (i) { FILTER i=1 INSERT (n:Added) RETURN i AS selected } RETURN count(*) AS answer", 2)]
    [DataRow("CALL () { CALL () { INSERT (n:Added) } } RETURN 1 AS answer", 1)]
    [DataRow("MATCH (n:Person) DELETE n RETURN count(n) AS answer", 3)]
    [DataRow("MATCH (n:Person) LET age=n.age DELETE n RETURN sum(age) AS answer", 60)]
    [DataRow("INSERT (a)-[r:link]->(b) DETACH DELETE a,b RETURN count(r) AS answer", 1)]
    [DataRow("MATCH (n:Person) LET age=n.age DELETE n RETURN sum(age) AS total NEXT INSERT (a:Added {age:total}) RETURN a.age AS answer", 60)]
    [DataRow("MATCH (n:Person) DELETE n RETURN count(n) AS deleted NEXT MATCH (m:Person) RETURN count(m) AS answer", 0)]
    [DataRow("MATCH (n:Person) LET age=n.age DELETE n RETURN age NEXT INSERT (m:Added {age:age+1}) RETURN sum(m.age) AS answer", 63)]
    [DataRow("MATCH (n:Person) SET n.age=n.age+1 RETURN sum(n.age) AS answer", 63)]
    [DataRow("MATCH (n:Person) SET n.age=n.age+1 RETURN n.age AS answer ORDER BY answer DESC LIMIT 1", 31)]
    [DataRow("MATCH (n:Absent) SET n.age=1 RETURN count(*) AS answer", 0)]
    [DataRow("MATCH (n:Person) SET n.flag=true FILTER n.age>10 SET n.age=n.age+1 RETURN sum(n.age) AS answer", 52)]
    public async Task Gql_standard_update_RETURN_projects_the_final_row_stream(string statement, int expected)
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
            statement,
            transactionId = Guid.NewGuid()
        });
        Assert.AreEqual(HttpStatusCode.OK, update.StatusCode, await update.Content.ReadAsStringAsync());
        var body = await update.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual(expected, body.GetProperty("result").GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetInt32());
        await host.StopAsync();
    }

    [TestMethod]
    [DataRow("RETURN PATH[n] AS answer")]
    [DataRow("RETURN n.age AS answer")]
    [DataRow("RETURN n IS LABELED Person AS answer")]
    [DataRow("RETURN PROPERTY_EXISTS(n,age) AS answer")]
    [DataRow("SET n.age=1 RETURN 1 AS answer")]
    [DataRow("REMOVE n.age RETURN 1 AS answer")]
    [DataRow("SET n={age:1} RETURN 1 AS answer")]
    public async Task Gql_standard_update_rejects_deleted_entity_state_and_rolls_back(string following)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (:Person {age:7})"
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode);
        string statement = "MATCH (n:Person) DELETE n " + following;
        GraphGqlStandardSyntax.Validate(statement);
        using var update = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement
        });
        Assert.AreEqual(HttpStatusCode.BadRequest, update.StatusCode, await update.Content.ReadAsStringAsync());
        Assert.AreEqual("22G11", (await update.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("gqlStatus").GetString());
        using var query = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (n:Person) RETURN n.age AS answer"
        });
        var result = await query.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual(7, result.GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetInt32());
        await host.StopAsync();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Gql_standard_update_RETURN_replays_original_volatile_result_after_changes_and_restart(bool call)
    {
        _directory = NewDirectory();
        var transactionId = Guid.NewGuid();
        string statement = call
            ? "CALL () { INSERT (n:Added {amount:7}) RETURN n AS created } RETURN created.amount AS answer,CURRENT_TIMESTAMP AS stamp,created AS node"
            : "INSERT (n:Added {amount:7}) RETURN n.amount AS answer,CURRENT_TIMESTAMP AS stamp,n AS node";
        string? original = null;
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
                original ??= body;
                Assert.AreEqual(original, body);
                using var change = await client.PostAsJsonAsync("/v1/graph/gql/update", new
                {
                    statement = "MATCH (n:Added) SET n.amount=99"
                });
                Assert.AreEqual(HttpStatusCode.OK, change.StatusCode);
            }
            await host.StopAsync();
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Gql_standard_update_RETURN_evaluation_failure_does_not_commit(bool oversized)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        string statement = oversized
            ? "INSERT (n:Added) FOR i IN [1,2,3,4,5,6,7,8,9,10] FOR j IN [1,2,3,4,5,6,7,8,9,10] FOR k IN [1,2,3,4,5,6,7,8,9,10] FOR l IN [1,2,3,4,5] RETURN '" + new string('x', 300) + "' AS answer"
            : "INSERT (n:Added) RETURN 1/0 AS answer";
        using var update = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement
        });
        Assert.AreEqual(HttpStatusCode.BadRequest, update.StatusCode, await update.Content.ReadAsStringAsync());
        using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (n:Added) RETURN count(*) AS answer"
        });
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual(0, result.GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetInt32());
        await host.StopAsync();
    }
}
