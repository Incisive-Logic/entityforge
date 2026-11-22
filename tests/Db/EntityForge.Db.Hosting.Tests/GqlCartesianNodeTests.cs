using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("MATCH (a:Person),(b:Team) RETURN COUNT(*) AS answer", 4)]
    [DataRow("MATCH (a:Person),(b:Team) WHERE a.rank=b.rank RETURN COUNT(*) AS answer", 2)]
    [DataRow("MATCH (a:Person),(b:Absent) RETURN COUNT(*) AS answer", 0)]
    [DataRow("MATCH (a:Person),(b:Person) WHERE a=b RETURN COUNT(*) AS answer", 2)]
    [DataRow("MATCH (a:Person),(a:Person) RETURN COUNT(*) AS answer", 2)]
    [DataRow("LET rank=1 MATCH (a:Person),(b:Team) WHERE a.rank=rank RETURN COUNT(*) AS answer", 2)]
    [DataRow("MATCH (a:Person) CALL (a) { MATCH (a),(b:Team) RETURN b } RETURN COUNT(*) AS answer", 4)]
    public async Task Gql_standard_comma_node_patterns_preserve_cartesian_and_shared_binding_semantics(string query, int expected)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (:Person {rank:1}),(:Person {rank:2}),(:Team {rank:1}),(:Team {rank:2})"
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode, await seed.Content.ReadAsStringAsync());
        using var result = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query
        });
        Assert.AreEqual(HttpStatusCode.OK, result.StatusCode, await result.Content.ReadAsStringAsync());
        Assert.AreEqual(expected, (await result.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetInt32());
        using var update = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "MATCH (a:Person),(b:Team) WHERE a.rank=b.rank SET a.matched=b.rank"
        });
        Assert.AreEqual(HttpStatusCode.OK, update.StatusCode, await update.Content.ReadAsStringAsync());
        using var updated = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (n:Person) WHERE n.matched=n.rank RETURN COUNT(*) AS answer"
        });
        Assert.AreEqual(2, (await updated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetInt32());
    }
}
