using EntityForge.Db.Gql;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("MATCH (n {age:1, age:2}) RETURN n")]
    [DataRow("MATCH (n {age:n.other}) RETURN n")]
    [DataRow("MATCH (n) RETURN n /* unterminated")]
    [DataRow("MATCH (n) RETURN n LIMIT 1 LIMIT 2")]
    [DataRow("MATCH (n) RETURN n OFFSET 1 SKIP 2")]
    [DataRow("MATCH (n) RETURN n LIMIT -1")]
    [DataRow("MATCH (n) RETURN n.age IS TYPED Unknown AS answer")]
    [DataRow("RETURN [] IS :: LIST<> AS answer")]
    [DataRow("RETURN [] IS :: LIST<INTEGER AS answer")]
    [DataRow("RETURN [] IS :: LIST<Unknown> AS answer")]
    [DataRow("RETURN missing IS :: NODE AS answer")]
    [DataRow("RETURN null IS :: (INTEGER | STRING) NOT NULL AS answer")]
    public void Gql_new_pattern_and_scalar_forms_reject_invalid_syntax(string query)
    {
        Assert.ThrowsExactly<ArgumentException>(() => new GraphGqlQueryCompiler().Compile(new(query)));
    }

    [TestMethod]
    public void Gql_zero_limit_preserves_capability_and_constraint_validation()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new GraphGqlQueryCompiler().Compile(new(
            "MATCH (n)-[r]->{10001}(m) RETURN n LIMIT 0")));
        Assert.IsTrue(new GraphGqlQueryCompiler().Compile(new(
            "MATCH (n) OPTIONAL MATCH (n {age:20})-[r]->(m) RETURN n")).RequiresCompleteGraph);
        Assert.IsTrue(new GraphGqlQueryCompiler().Compile(new(
            "MATCH (n) OPTIONAL MATCH (n)-[r]->(m)-[s]->(p) RETURN n")).RequiresCompleteGraph);
        string properties = String.Join(",", Enumerable.Range(0, 33).Select(index => $"p{index}:1"));
        Assert.ThrowsExactly<ArgumentException>(() => new GraphGqlQueryCompiler().Compile(new(
            $"MATCH (n {{{properties}}}) RETURN n LIMIT 0")));
    }

    [TestMethod]
    [DataRow("MATCH (n)-[r]->{1,2}(m) WHERE r.weight=1 RETURN n", "ERROR")]
    [DataRow("MATCH (n:Person)-[r:knows]->(m) WHERE r.weight > 1 RETURN r.weight AS answer", "2")]
    [DataRow("MATCH (n:Person)-[r:knows]->(m) WHERE r.weight=n.age RETURN r.weight AS answer", "2")]
    [DataRow("MATCH (n:Person)-[r:knows]->(m) RETURN r.weight AS answer ORDER BY answer DESC LIMIT 1", "2")]
    [DataRow("MATCH (n:Person)-[r:knows]->(m) RETURN r.weight+1 AS answer ORDER BY answer ASC LIMIT 1", "2")]
    [DataRow("MATCH (n:Person {age:2}) RETURN n.age AS answer", "2")]
    [DataRow("MATCH (n:Person {score:-1}) RETURN n.score AS answer", "-1")]
    [DataRow("MATCH (n:Person {name:'} , OPTIONAL MATCH RETURN WHERE'}) RETURN n.age AS answer", "2")]
    public async Task Gql_standard_inline_constraints_and_edge_expressions_return_expected_values(string query, string expected)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = $"/* comment with 'ignored */ INSERT (a:Person {{id:'{Guid.NewGuid()}', age:2, score:-1, name:'}} , OPTIONAL MATCH RETURN WHERE'}}), " +
                $"(b:Person {{id:'{Guid.NewGuid()}', age:3}}), " +
                $"(a)-[r:knows {{id:'{Guid.NewGuid()}', weight:2}}]->(b), " +
                $"(a)-[s:knows {{id:'{Guid.NewGuid()}', weight:1}}]->(b) // trailing comment"
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode, await seed.Content.ReadAsStringAsync());
        using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query
        });
        if (expected == "ERROR")
        {
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, await response.Content.ReadAsStringAsync());
            return;
        }
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        var rows = result.GetProperty("matches");
        Assert.AreEqual(1, rows.GetArrayLength());
        Assert.AreEqual(expected, rows[0].GetProperty("bindings").GetProperty("answer").ToString());
    }
}
