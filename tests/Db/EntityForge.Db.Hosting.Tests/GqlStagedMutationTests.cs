using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("MATCH (n:Person) RETURN n ORDER BY n.age LIMIT 1 NEXT SET n.age=9", "[2,3,9]")]
    [DataRow("MATCH (n:Person) RETURN n AS p,n.age*2 AS score NEXT SET p.age=score", "[2,4,6]")]
    [DataRow("FOR age IN [1,3] MATCH (n:Person) WHERE n.age=age SET n.age=10", "[2,10,10]")]
    [DataRow("FOR age IN [1,3] MATCH (n:Person) WHERE n.age=age DELETE n", "[2]")]
    [DataRow("FOR age IN [] MATCH (n:Person) SET n.age=10", "[1,2,3]")]
    [DataRow("OPTIONAL CALL () { MATCH (n:Person) WHERE n.age=1 RETURN n } SET n.age=10", "[2,3,10]")]
    [DataRow("OPTIONAL CALL () { MATCH (n:Absent) RETURN n } SET n.age=10", "[1,2,3]")]
    [DataRow("MATCH (n:Person) RETURN n ORDER BY n.age LIMIT 1 NEXT DELETE n", "[2,3]")]
    [DataRow("MATCH (n:Person) CALL (n) { RETURN n.age*2 AS score } SET n.age=score", "[2,4,6]")]
    [DataRow("OPTIONAL MATCH (n:Absent) SET n.age=42", "[1,2,3]")]
    [DataRow("MATCH (n:Person) RETURN n.age AS age NEXT SET n.age=42", "ERROR")]
    [DataRow("MATCH (n:Person) RETURN n ORDER BY n.age NEXT SET n.age=10/(n.age-2)", "ERROR")]
    public async Task Gql_standard_staged_mutations_use_final_scope_and_commit_all_or_nothing(string statement, string expected)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (:Person {age:1}),(:Person {age:2}),(:Person {age:3})"
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode, await seed.Content.ReadAsStringAsync());
        using var response = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement
        });
        Assert.AreEqual(expected == "ERROR" ? HttpStatusCode.BadRequest : HttpStatusCode.OK, response.StatusCode,
            await response.Content.ReadAsStringAsync());
        using var selected = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (n:Person) RETURN n.age AS answer ORDER BY answer"
        });
        Assert.AreEqual(HttpStatusCode.OK, selected.StatusCode, await selected.Content.ReadAsStringAsync());
        var result = await selected.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual(expected == "ERROR" ? "[1,2,3]" : expected,
            JsonSerializer.Serialize(result.GetProperty("matches").EnumerateArray().Select(row => row.GetProperty("bindings").GetProperty("answer"))));
    }
}
