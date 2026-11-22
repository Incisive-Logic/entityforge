using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("MATCH (n:Person {age:0})-[r:nextStep]->{,}(m) RETURN COUNT(*) AS answer", 8)]
    [DataRow("MATCH (n:Person {age:0})-[r:nextStep]->{,2}(m) RETURN COUNT(*) AS answer", 3)]
    [DataRow("MATCH (n:Person {age:0})-[r:nextStep]->+(m) RETURN COUNT(*) AS answer", 7)]
    [DataRow("MATCH (n:Person {age:0})-[r:nextStep]->*(m) RETURN COUNT(*) AS answer", 8)]
    [DataRow("MATCH (n:Person {age:0})-[r:nextStep]->{5,}(m) RETURN COUNT(*) AS answer", 3)]
    [DataRow("MATCH (n:Person {age:0})-[r:nextStep]->{6}(m) RETURN m.age AS answer", 6)]
    [DataRow("MATCH (n:Person {age:0})-[r:nextStep]->{0,1}(m:Person {age:0}) RETURN m.age AS answer", 0)]
    [DataRow("MATCH (n:Person {age:0})-[r:nextStep]->*(n) RETURN COUNT(r) AS answer", 2)]
    [DataRow("MATCH (n:Person {age:0})-[r:nextStep]->{0,1}(n) WHERE r IS NOT NULL RETURN COUNT(r) AS answer", 1)]
    [DataRow("MATCH (n:Person {age:0})-[r:nextStep]-+(m) RETURN COUNT(*) AS answer", 14)]
    [DataRow("MATCH ANY SHORTEST (n:Person {age:0})-[r:nextStep]->*(n) RETURN COUNT(*) AS answer", 1)]
    public async Task Gql_standard_open_ended_trails_terminate_cycles_without_a_depth_prefix(string query, int expected)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        string nodes = String.Join(",", Enumerable.Range(0, 7).Select(index =>
            $"(n{index}:Person {{id:'{Guid.NewGuid()}',age:{index}}})"));
        string edges = String.Join(",", Enumerable.Range(0, 7).Select(index =>
            $"(n{index})-[r{index}:nextStep {{id:'{Guid.NewGuid()}'}}]->(n{(index + 1) % 7})"));
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = $"INSERT {nodes},{edges}"
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode, await seed.Content.ReadAsStringAsync());
        using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query
        });
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        JsonElement rows = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
        Assert.AreEqual(1, rows.GetArrayLength());
        Assert.AreEqual(expected, rows[0].GetProperty("bindings").GetProperty("answer").GetInt32());
    }
}
