using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("DELETE", false)]
    [DataRow("NODETACH DELETE", false)]
    [DataRow("DETACH DELETE", true)]
    public async Task Gql_DELETE_scalar_subqueries_preserve_relationship_constraints(string deletion, bool detach)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        async Task<JsonElement> Update(string statement, HttpStatusCode expected = HttpStatusCode.OK)
        {
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/update", new
            {
                statement,
                transactionId = Guid.NewGuid()
            });
            Assert.AreEqual(expected, response.StatusCode, await response.Content.ReadAsStringAsync());
            return await response.Content.ReadFromJsonAsync<JsonElement>();
        }
        async Task<int> Count(string query)
        {
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query
            });
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetInt32();
        }
        await Update("INSERT (a:DeleteScalarEndpoint {rank:1})-[e:probeLink]->(b:DeleteScalarEndpoint {rank:2})");
        await Update("MATCH (n:NoDeleteScalarEndpoint) " + deletion + " VALUE { RETURN 1/0 AS failure }");
        var visible = await Update("MATCH (n:DeleteScalarEndpoint) " + deletion + " VALUE { RETURN NULL AS target } RETURN *");
        foreach (var row in visible.GetProperty("result").GetProperty("matches").EnumerateArray())
        {
            CollectionAssert.AreEquivalent(new[] { "n" }, row.GetProperty("bindings").EnumerateObject().Select(item => item.Name).ToArray());
        }
        await Update("MATCH (a:DeleteScalarEndpoint {rank:1}) SET a.mark=TRUE " + deletion + " VALUE { RETURN a }",
            detach ? HttpStatusCode.OK : HttpStatusCode.BadRequest);
        Assert.AreEqual(detach ? 1 : 2, await Count("MATCH (n:DeleteScalarEndpoint) WHERE n.mark IS NULL RETURN COUNT(*) AS answer"));
        Assert.AreEqual(detach ? 0 : 1, await Count("MATCH ()-[e:probeLink]->() RETURN COUNT(*) AS answer"));
        if (!detach)
        {
            await Update("MATCH ()-[e:probeLink]->() DELETE VALUE { RETURN e }");
            await Update("MATCH (a:DeleteScalarEndpoint {rank:1}) " + deletion + " VALUE { RETURN a }");
        }
        Assert.AreEqual(1, await Count("MATCH (n:DeleteScalarEndpoint) RETURN COUNT(*) AS answer"));
        await host.StopAsync();
    }

    [TestMethod]
    public async Task Gql_DELETE_scalar_subqueries_preserve_atomicity_and_input_view()
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        async Task<JsonElement> Update(string statement, HttpStatusCode expected = HttpStatusCode.OK)
        {
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/update", new
            {
                statement,
                transactionId = Guid.NewGuid()
            });
            Assert.AreEqual(expected, response.StatusCode, await response.Content.ReadAsStringAsync());
            return await response.Content.ReadFromJsonAsync<JsonElement>();
        }
        await Update("FOR rank IN [1,2,3] INSERT (:DeleteScalarProbe {rank:rank})");
        await Update("MATCH (n:DeleteScalarProbe) SET n.mark=TRUE DELETE VALUE { RETURN 42 AS answer }", HttpStatusCode.BadRequest);
        await Update("DELETE VALUE { MATCH (n:DeleteScalarProbe) RETURN n }", HttpStatusCode.BadRequest);
        await Update("MATCH (n:DeleteScalarProbe) DELETE CASE WHEN TRUE THEN NULL ELSE VALUE { RETURN 1/0 AS failure } END");
        using var unchanged = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (n:DeleteScalarProbe) WHERE n.mark IS NULL RETURN COUNT(*) AS answer"
        });
        Assert.AreEqual(3, (await unchanged.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetInt32());
        var removed = await Update("MATCH (n:DeleteScalarProbe) DELETE VALUE { RETURN CASE WHEN n.rank=1 THEN n ELSE NULL END AS target } RETURN COUNT(*) AS answer");
        Assert.AreEqual(3, removed.GetProperty("result").GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetInt32());
        await Update("DELETE VALUE { MATCH (n:DeleteScalarProbe) WHERE n.rank=2 RETURN n }");
        await Update("MATCH (n:DeleteScalarProbe) DELETE VALUE { RETURN n } DELETE VALUE { RETURN 1/0 AS failure }", HttpStatusCode.BadRequest);
        await Update("MATCH (n:DeleteScalarProbe) DELETE VALUE { RETURN n }");
        using var remaining = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (n:DeleteScalarProbe) RETURN COUNT(*) AS answer"
        });
        Assert.AreEqual(0, (await remaining.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetInt32());
        await host.StopAsync();
    }

    [TestMethod]
    [DataRow("n.amount=VALUE {\n RETURN 7 AS answer\n }")]
    [DataRow("n={amount:VALUE {\n RETURN 7 AS answer\n }}")]
    [DataRow("n.amount=3 +\n 4")]
    [DataRow("n={amount:3 +\n 4}")]
    public async Task Gql_multiline_SET_expressions_preserve_values(string assignment)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (:MultilineProbe {amount:1})"
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode, await seed.Content.ReadAsStringAsync());
        using var update = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "MATCH (n:MultilineProbe) SET " + assignment + " RETURN n.amount AS answer",
            transactionId = Guid.NewGuid(),
        });
        Assert.AreEqual(HttpStatusCode.OK, update.StatusCode, await update.Content.ReadAsStringAsync());
        Assert.AreEqual(7, (await update.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result")
            .GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetInt32());
        await host.StopAsync();
    }

    [TestMethod]
    public async Task Gql_INSERT_scalar_subqueries_use_input_view_and_preserve_atomicity()
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        async Task<JsonElement> Update(string statement, HttpStatusCode expected = HttpStatusCode.OK)
        {
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/update", new
            {
                statement,
                transactionId = Guid.NewGuid()
            });
            Assert.AreEqual(expected, response.StatusCode, await response.Content.ReadAsStringAsync());
            return await response.Content.ReadFromJsonAsync<JsonElement>();
        }
        var seed = await Update("INSERT (seed:CreatedProbe {amount:VALUE { RETURN 7 AS answer }}) RETURN *");
        CollectionAssert.AreEquivalent(new[] { "seed" }, seed.GetProperty("result").GetProperty("matches")[0]
            .GetProperty("bindings").EnumerateObject().Select(item => item.Name).ToArray());
        var correlated = await Update("MATCH (seed:CreatedProbe) INSERT (n:CreatedProbe {amount:VALUE { RETURN seed.amount+1 AS answer }}) RETURN n.amount AS answer");
        Assert.AreEqual(8, correlated.GetProperty("result").GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetInt32());
        var batch = await Update("FOR x IN [1,2] INSERT (n:CreatedBatch {amount:VALUE { MATCH (m:CreatedBatch) RETURN COUNT(*) AS answer }}) RETURN n.amount AS answer");
        CollectionAssert.AreEquivalent(new[] { 0, 0 }, batch.GetProperty("result").GetProperty("matches").EnumerateArray()
            .Select(row => row.GetProperty("bindings").GetProperty("answer").GetInt32()).ToArray());
        var edge = await Update("INSERT (a:EdgeProbe),(b:EdgeProbe),(a)-[r:link {amount:VALUE { RETURN 3 AS answer }}]->(b) RETURN r.amount AS answer");
        Assert.AreEqual(3, edge.GetProperty("result").GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetInt32());
        var quoted = await Update("INSERT (n:LiteralProbe {text:'VALUE { not a query }',`odd:key`:VALUE { RETURN 5 AS answer }}),(emptyNode:LiteralProbe) RETURN n.`odd:key` AS answer");
        Assert.AreEqual(5, quoted.GetProperty("result").GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetInt32());
        var prior = await Update("INSERT (:EarlierProbe) INSERT (n:LaterProbe {amount:VALUE { MATCH (m:EarlierProbe) RETURN COUNT(*) AS answer }}) RETURN n.amount AS answer");
        Assert.AreEqual(1, prior.GetProperty("result").GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetInt32());
        await Update("MATCH (absent:NoSuchProbe) INSERT (:RollbackProbe {amount:VALUE { RETURN 1/0 AS failure }})");
        await Update("INSERT (:RollbackProbe {amount:VALUE { FOR x IN [1,2] RETURN x AS answer }})", HttpStatusCode.BadRequest);
        await Update("INSERT (:RollbackProbe) INSERT (:RollbackProbe {amount:VALUE { RETURN 1/0 AS failure }})", HttpStatusCode.BadRequest);
        using var read = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (n:RollbackProbe) RETURN COUNT(*) AS answer"
        });
        Assert.AreEqual(HttpStatusCode.OK, read.StatusCode);
        Assert.AreEqual(0, (await read.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetInt32());
        await host.StopAsync();
    }

    [TestMethod]
    public async Task Gql_SET_scalar_subqueries_observe_statement_input_and_roll_back()
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        async Task<JsonElement> Update(string statement, HttpStatusCode expected = HttpStatusCode.OK)
        {
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/update", new
            {
                statement,
                transactionId = Guid.NewGuid()
            });
            Assert.AreEqual(expected, response.StatusCode, await response.Content.ReadAsStringAsync());
            return await response.Content.ReadFromJsonAsync<JsonElement>();
        }
        await Update("INSERT (:ScalarProbe {amount:1}), (:ScalarProbe {amount:2})");
        await Update("MATCH (n:ScalarProbe) SET n.amount=VALUE { MATCH (m:ScalarProbe) RETURN SUM(m.amount) AS total }");
        using var read = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (n:ScalarProbe) RETURN n.amount AS amount"
        });
        Assert.AreEqual(HttpStatusCode.OK, read.StatusCode, await read.Content.ReadAsStringAsync());
        var amounts = (await read.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")
            .EnumerateArray().Select(row => row.GetProperty("bindings").GetProperty("amount").GetInt64()).ToArray();
        CollectionAssert.AreEquivalent(new long[] { 3, 3 }, amounts);
        await Update("OPTIONAL MATCH (n:MissingScalarProbe) SET n.amount=VALUE { RETURN 1/0 AS failure }");
        await Update("MATCH (n:ScalarProbe) SET n.amount=8 SET n.amount=VALUE { RETURN 1/0 AS failure }", HttpStatusCode.BadRequest);
        using var unchanged = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (n:ScalarProbe) RETURN n.amount AS amount"
        });
        Assert.AreEqual(HttpStatusCode.OK, unchanged.StatusCode);
        CollectionAssert.AreEquivalent(new long[] { 3, 3 }, (await unchanged.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("matches").EnumerateArray().Select(row => row.GetProperty("bindings").GetProperty("amount").GetInt64()).ToArray());
        var returned = await Update("MATCH (n:ScalarProbe) SET n.amount=4 SET n.amount=VALUE { MATCH (m:ScalarProbe) RETURN SUM(m.amount) AS total } RETURN *");
        var returnedRows = returned.GetProperty("result").GetProperty("matches");
        Assert.AreEqual(2, returnedRows.GetArrayLength());
        foreach (var row in returnedRows.EnumerateArray())
        {
            CollectionAssert.AreEquivalent(new[] { "n" }, row.GetProperty("bindings").EnumerateObject().Select(item => item.Name).ToArray());
        }
        var correlated = await Update("MATCH (n:ScalarProbe) SET n.amount=VALUE { RETURN n.amount+1 AS amount } RETURN n.amount AS amount");
        CollectionAssert.AreEquivalent(new long[] { 9, 9 }, correlated.GetProperty("result").GetProperty("matches")
            .EnumerateArray().Select(row => row.GetProperty("bindings").GetProperty("amount").GetInt64()).ToArray());
        await Update("MATCH (n:ScalarProbe) SET n.amount=CASE WHEN TRUE THEN 9 ELSE VALUE { RETURN 1/0 AS failure } END");
        var replaced = await Update("MATCH (n:ScalarProbe) SET n={total:VALUE { MATCH (m:ScalarProbe) RETURN SUM(m.amount) AS total }} RETURN n.amount IS NULL AS removed,n.total AS total");
        var replacedRows = replaced.GetProperty("result").GetProperty("matches");
        Assert.AreEqual(2, replacedRows.GetArrayLength());
        foreach (var row in replacedRows.EnumerateArray())
        {
            Assert.IsTrue(row.GetProperty("bindings").GetProperty("removed").GetBoolean());
            Assert.AreEqual(18L, row.GetProperty("bindings").GetProperty("total").GetInt64());
        }
        await Update("OPTIONAL MATCH (n:MissingScalarProbe) SET n={total:VALUE { RETURN 1/0 AS failure }}");
        await Update("MATCH (n:ScalarProbe) SET n.total=0 SET n={total:VALUE { RETURN 1/0 AS failure }}", HttpStatusCode.BadRequest);
        using var final = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (n:ScalarProbe) RETURN n.total AS total"
        });
        Assert.AreEqual(HttpStatusCode.OK, final.StatusCode);
        CollectionAssert.AreEquivalent(new long[] { 18, 18 }, (await final.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("matches").EnumerateArray().Select(row => row.GetProperty("bindings").GetProperty("total").GetInt64()).ToArray());
        await host.StopAsync();
    }
}
