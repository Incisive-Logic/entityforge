using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("ALL", "[2,2,3]")]
    [DataRow("ALL PATH", "[2,2,3]")]
    [DataRow("ALL PATHS", "[2,2,3]")]
    [DataRow("ALL WALK PATHS", "[2,2,3]")]
    [DataRow("ALL TRAIL PATHS", "[2,2,3]")]
    [DataRow("ALL SIMPLE PATHS", "[2,2,3]")]
    [DataRow("ALL ACYCLIC PATHS", "[2,2,3]")]
    [DataRow("WALK PATH", "[2,2,3]")]
    [DataRow("SHORTEST 1", "[2]")]
    [DataRow("SHORTEST 2 PATHS", "[2,2]")]
    [DataRow("SHORTEST 2 GROUPS", "[2,2,3]")]
    [DataRow("SHORTEST 8 GROUPS", "[2,2,3]")]
    [DataRow("ALL SHORTEST", "[2,2]")]
    [DataRow("ANY SHORTEST", "[2]")]
    [DataRow("SHORTEST 0", "[]")]
    [DataRow("SHORTEST 0 GROUPS", "[]")]
    [DataRow("ANY 2 GROUPS", "ERROR")]
    [DataRow("SHORTEST 1", "[2]", "-[:link]->(middle)-[:link]->")]
    [DataRow("SHORTEST 2", "[2,2]", "-[:link]->(middle)-[:link]->")]
    [DataRow("SHORTEST 1 GROUP", "[2,2]", "-[:link]->(middle)-[:link]->")]
    [DataRow("SHORTEST 2 GROUPS", "[2,2,3]", "-[:link]->{1,2}(middle)-[:link]->")]
    [DataRow("SHORTEST 2 GROUPS", "[2,2,3,3]", "-[:link]->{1,2}(middle)-[:link]->{1,2}")]
    [DataRow("ALL SHORTEST", "[2,2]", "-[:link]->{1,2}(middle)-[:link]->{1,2}")]
    [DataRow("ANY SHORTEST", "[2]", "-[:link]->{1,2}(middle)-[:link]->{1,2}")]
    public async Task Gql_standard_counted_path_selectors_preserve_ties_and_length_groups(string selector, string expected, string chain = "-[:link]->+")
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (a:Route {name:'A'}),(b:Route {name:'B'}),(c:Route {name:'C'}),(d:Route {name:'D'}),(a)-[:link]->(b),(a)-[:link]->(c),(b)-[:link]->(d),(c)-[:link]->(d),(b)-[:link]->(c)"
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode, await seed.Content.ReadAsStringAsync());
        using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = $"MATCH p = {selector} (a:Route {{name:'A'}}){chain}(b:Route {{name:'D'}}) RETURN PATH_LENGTH(p) AS answer ORDER BY answer"
        });
        Assert.AreEqual(expected == "ERROR" ? HttpStatusCode.BadRequest : HttpStatusCode.OK, response.StatusCode,
            await response.Content.ReadAsStringAsync());
        if (expected != "ERROR")
        {
            var result = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.AreEqual(expected, JsonSerializer.Serialize(result.GetProperty("matches").EnumerateArray()
                .Select(row => row.GetProperty("bindings").GetProperty("answer"))));
        }
    }
}
