using EntityForge.Db.Gql;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("MATCH (n:Person) RETURN SUM(1) AS answer", "2")]
    [DataRow("MATCH (n:Person) RETURN COUNT(NULL) AS answer", "0")]
    [DataRow("MATCH (n:Person) RETURN COUNT(true) AS answer", "2")]
    [DataRow("MATCH (n:Person {name:'A'})-[r:peer]->(m) RETURN SUM(r.weight*2) AS answer", "12")]
    [DataRow("MATCH (n:Person {name:'A'})-[r:peer]->(m) RETURN n, SUM(r.weight*2) AS answer", "12")]
    [DataRow("MATCH (n:Person {name:'A'})-[r:peer]->(m) RETURN COUNT(DISTINCT MOD(r.weight,2)) AS answer", "2")]
    [DataRow("MATCH (n:Person {name:'A'})-[r:peer]->(m) RETURN SUM(DISTINCT MOD(r.weight,2)) AS answer", "1")]
    [DataRow("MATCH (n:Person {name:'A'})-[r:peer]->(m) RETURN COUNT(DISTINCT CASE WHEN r.weight=2 THEN NULL ELSE 1 END) AS answer", "1")]
    [DataRow("MATCH (n:Person {name:'B'})<-[r:peer]-(m) RETURN AVG(r.weight+1) AS answer", "2.5")]
    [DataRow("MATCH (n:Person {name:'B'})<-[r:peer]-(m) RETURN MIN(r.weight+1) AS answer", "2")]
    [DataRow("MATCH (n:Person {name:'B'})<-[r:peer]-(m) RETURN MAX(r.weight+1) AS answer", "3")]
    [DataRow("MATCH (n:Absent) RETURN SUM(1) AS answer", "")]
    [DataRow("MATCH (n:Absent) RETURN AVG(1) AS answer", "")]
    [DataRow("MATCH (n:Person {name:'B'})-[e:peer {weight:2}]-(m) RETURN e.weight AS answer", "2")]
    [DataRow("MATCH (n:Person {name:'B'})<-[:peer {weight:2}]-(m) RETURN m.name AS answer", "A")]
    [DataRow("MATCH (n:Person {name:'B'}) OPTIONAL MATCH (n)-[e:peer {weight:9}]-(m) RETURN COALESCE(m.name,'none') AS answer", "none")]
    [DataRow("MATCH (n:Person {name:'B'}) OPTIONAL MATCH (n)-[e:peer {weight:2}]-(m) WHERE e.weight>1 RETURN e.weight AS answer", "2")]
    [DataRow("MATCH (n:Person {name:'B'})-[e:peer {weight:NULL}]-(m) RETURN COUNT(*) AS answer", "0")]
    [DataRow("MATCH DIFFERENT RELATIONSHIPS (n:Person {name:'B'})-[e:peer]-(m)-[f:peer]-(n) RETURN COUNT(*) AS answer", "2")]
    [DataRow("MATCH (n:Person {name:'B'})<-[e:peer]-(m)-[f:peer]->(n) RETURN COUNT(*) AS answer", "2")]
    [DataRow("MATCH (n:Person {name:'B'})-[e:peer]-(m) RETURN n, COUNT(*) AS answer", "2")]
    [DataRow("MATCH (n:Person)-[e:peer]-(m:Person) RETURN COUNT(*) AS answer", "5")]
    [DataRow("MATCH (n:Person {name:'B'})-[e:peer]-(m) RETURN COUNT(*) AS answer", "2")]
    [DataRow("MATCH (n:Person {name:'A'})-[e:peer]-(n) RETURN COUNT(*) AS answer", "1")]
    [DataRow("MATCH (n:Person {name:'B'})-(m) RETURN m.name AS answer LIMIT 1", "A")]
    [DataRow("MATCH (n:Person {name:'B'})-[e:peer]-(m) WHERE e.weight=2 RETURN e.weight AS answer", "2")]
    [DataRow("MATCH (n:Person {name:'B'})-[e:peer]-(m)-[f:peer]-(n) RETURN COUNT(*) AS answer", "2")]
    [DataRow("MATCH (n:Person {name:'B'})-[e:peer]-{2}(n) RETURN COUNT(*) AS answer", "2")]
    [DataRow("MATCH (n:Person {name:'B'}) OPTIONAL MATCH (n)-[e:missing]-(m) RETURN COALESCE(m.name,'none') AS answer", "none")]
    public async Task Gql_standard_direction_neutral_matches_preserve_bags_loops_and_trails(string query, string expected)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = $"INSERT (a:Person {{id:'{Guid.NewGuid()}',name:'A'}}), (b:Person {{id:'{Guid.NewGuid()}',name:'B'}}), " +
                $"(a)-[r:peer {{id:'{Guid.NewGuid()}',weight:1}}]->(b), (a)-[s:peer {{id:'{Guid.NewGuid()}',weight:2}}]->(b), " +
                $"(a)-[t:peer {{id:'{Guid.NewGuid()}',weight:3}}]->(a)"
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode, await seed.Content.ReadAsStringAsync());
        using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query
        });
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var rows = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
        Assert.AreEqual(1, rows.GetArrayLength());
        Assert.AreEqual(expected, rows[0].GetProperty("bindings").GetProperty("answer").ToString());
        using var invalid = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (n:Person) RETURN SUM(n.name) AS answer"
        });
        Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [TestMethod]
    public void Gql_inline_relationship_maps_validate_values_and_work_limits()
    {
        var compiler = new GraphGqlQueryCompiler();
        Assert.ThrowsExactly<ArgumentException>(() => compiler.Compile(new("MATCH (n)-[r {x:1,x:2}]-(m) RETURN n")));
        Assert.ThrowsExactly<ArgumentException>(() => compiler.Compile(new("MATCH (n)-[r {x:n.age}]-(m) RETURN n")));
        Assert.AreEqual(1, compiler.Compile(new("MATCH (n)-[r {x:1}]-{1,2}(m) RETURN n")).Pipeline!.Stages.Single(stage => stage.MatchPlan is not null).MatchPlan!.Query.Relationships.Single().EdgePredicates.Count);
        Assert.ThrowsExactly<ArgumentException>(() => compiler.Compile(new("MATCH (n) RETURN SUM(m.age*2) AS answer")));
        Assert.ThrowsExactly<ArgumentException>(() => compiler.Compile(new("MATCH (n) RETURN COUNT(DISTINCT *) AS answer")));
        string properties = String.Join(",", Enumerable.Range(0, 33).Select(index => $"p{index}:1"));
        Assert.ThrowsExactly<ArgumentException>(() => compiler.Compile(new($"MATCH (n)-[r {{{properties}}}]-(m) RETURN n")));
    }
}
