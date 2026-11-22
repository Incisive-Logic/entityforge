using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("MATCH (a:Pc {name:'A'}) UNWIND [(a)-[:link]->(b) | b.age] AS answer RETURN value ORDER BY value")]
    [DataRow("MATCH (a:Pc {name:'A'}) RETURN size([(a)-[:link]->(b) WHERE b.age=2 | b.name]) AS answer")]
    [DataRow("MATCH (a:Pc {name:'A'}) RETURN size([(a)-[:link]->(b) WHERE (b:Pc|Other) | b.name]) AS answer")]
    [DataRow("MATCH (a:Pc {name:'A'}) RETURN size([(a)-[:link]->(b) WHERE b:Pc|Other | b.name]) AS answer")]
    [DataRow("MATCH (a:Pc {name:'A'}) RETURN [(a)-[:link]->(b) WHERE b:Pc|Other AND b.name='C' | b:Pc|Other] AS answer")]
    [DataRow("MATCH (a:Pc {name:'A'}) RETURN [(a)-[:link]->(b) WHERE b IS TYPED NODE|STRING AND b.name='C' | b.name || '|x'] AS answer")]
    [DataRow("MATCH (a:Pc {name:'A'}) RETURN [(a)-[:link]->(b) WHERE b:Pc|Other AND b.name='C' | COUNT { MATCH (b)-[:link]->(c) }] AS answer")]
    [DataRow("MATCH (a:Pc {name:'A'}) RETURN [(a)-[:link]->(b) WHERE b:Pc|Other | missing.name] AS answer")]
    [DataRow("RETURN [(a:Pc)-[:link]->(b) WHERE b.name='C' | b.name || '|x'] AS answer")]
    [DataRow("MATCH (a:Pc {name:'A'}) RETURN [p=(a)-[:link]->(b) WHERE b.name='C' | length(p)] AS answer")]
    [DataRow("MATCH (a:Pc {name:'C'}) RETURN [(a)-[:link]->(b) | b.name] AS answer")]
    [DataRow("WITH null AS a RETURN [(a)-[:link]->(b) | b.name] AS answer")]
    [DataRow("RETURN CASE WHEN true THEN 7 ELSE [(a:Pc)-[:link]->(b) | 1/0] END AS answer")]
    [DataRow("RETURN size([(a:Pc {name:'A'})-[:link]->(b) | [(b)-[:link]->(c) | c.name]]) AS answer")]
    [DataRow("WITH 9 AS __patternValue RETURN __patternValue+size([(a:Pc)-[:link]->(b) | b]) AS answer")]
    [DataRow("RETURN [(a:Pc)-[:link]->(b) WHERE b.name='C' | {name:b.name,missing:b.absent}] AS answer")]
    [DataRow("RETURN [(a:Pc) | a] AS answer")]
    [DataRow("RETURN [(a:Pc)-[:link]->(b) | b.name] AS names,b AS answer")]
    public async Task Gql_rejects_pattern_comprehension_extensions(string query)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (a:Pc {name:'A'}),(b:Pc {name:'B',age:2}),(c:Pc {name:'C'}),(a)-[:link]->(b),(a)-[:link]->(b),(a)-[:link]->(c)"
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode);
        using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query
        });
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, await response.Content.ReadAsStringAsync());
        await host.StopAsync();
    }
}
