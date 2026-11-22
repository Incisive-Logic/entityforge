using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("MATCH p=(a:Route {name:'A'})-[:link {weight:2}]->{1,3}(b:Route {name:'D'}) RETURN PATH_LENGTH(p) AS answer", 2)]
    [DataRow("MATCH p=(a:Route {name:'A'})-[:link {weight:2.0}]->{1,3}(b:Route {name:'D'}) RETURN PATH_LENGTH(p) AS answer", 2)]
    [DataRow("MATCH p=SHORTEST 1 (a:Route {name:'A'})-[:link {weight:2}]->+(b:Route {name:'D'}) RETURN PATH_LENGTH(p) AS answer", 2)]
    [DataRow("MATCH p=(a:Route {name:'D'})<-[:link {weight:2}]-{1,3}(b:Route {name:'A'}) RETURN PATH_LENGTH(p) AS answer", 2)]
    [DataRow("MATCH p=(a:Route {name:'A'})-[:link {weight:2}]-{1,3}(b:Route {name:'D'}) RETURN PATH_LENGTH(p) AS answer", 2)]
    [DataRow("MATCH p=(a:Route {name:'A'})-[:link {weight:null}]->{0,1}(b:Route {name:'A'}) RETURN PATH_LENGTH(p) AS answer", 0)]
    [DataRow("MATCH (a:Route {name:'A'}) OPTIONAL MATCH (a)-[:link {weight:99}]->{1,3}(b) RETURN count(b) AS answer", 0)]
    [DataRow("MATCH (a:Route {name:'A'})-[:link {weight:null}]->{1,3}(b) RETURN count(b) AS answer", 0)]
    public async Task Gql_standard_quantified_relationship_properties_apply_at_every_hop_before_selection(string query, int expected)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (a:Route {name:'A'}),(b:Route {name:'B'}),(c:Route {name:'C'}),(d:Route {name:'D'})," +
                "(a)-[:link {weight:9}]->(d),(a)-[:link {weight:2}]->(b),(b)-[:link {weight:2}]->(d)," +
                "(a)-[:link {weight:2}]->(c),(c)-[:link {weight:3}]->(d)"
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode, await seed.Content.ReadAsStringAsync());
        using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query
        });
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var rows = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
        Assert.AreEqual(1, rows.GetArrayLength());
        Assert.AreEqual(expected, rows[0].GetProperty("bindings").GetProperty("answer").GetInt32());
    }
}
