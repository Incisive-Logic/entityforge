using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("MATCH p=(()-[:knows]->()-[:knows]->() | ()-[:Missing]->()-[:Missing]->()){1,2} RETURN *", "p", 1)]
    [DataRow("LET keep=7 MATCH p=(()-[:knows]->()-[:knows]->() | ()-[:Missing]->()-[:Missing]->()){1,2} RETURN *", "keep,p", 1)]
    [DataRow("LET keep=7 MATCH ((:Person)-[:knows]->() | (:Missing)-[:Missing]->()){1,2} RETURN *", "keep", 3)]
    [DataRow("CALL { MATCH p=(()-[:knows]->()-[:knows]->() | ()-[:Missing]->()-[:Missing]->()){1,2} RETURN * } RETURN *", "p", 1)]
    [DataRow("LET __repeatedLabel=7 MATCH p=(()-[:knows]->()-[:knows]->() | ()-[:Missing]->()-[:Missing]->()){1,2} RETURN *", "__repeatedLabel,p", 1)]
    [DataRow("OPTIONAL MATCH p=((:Missing)-[:knows]->() | (:Person)-[:Missing]->()){1,2} RETURN *", "p", 1)]
    [DataRow("MATCH p=(()-[:knows]->() | ()<-[:knows]-()){1,2} RETURN *", "p", 1, "INSERT (a:Person)-[:knows]->(a)")]
    [DataRow("MATCH p=(()<-[:knows]->() | ()-[:knows]->()){1,2} RETURN *", "p", 1, "INSERT (a:Person)-[:knows]->(a)")]
    [DataRow("MATCH p=(()-[:knows]->() | ()~[:knows]~>()){1,2} RETURN *", "p", 3)]
    [DataRow("MATCH p=(()<~[:knows]~() | ()<-[:knows]-()){1,2} RETURN *", "p", 3)]
    [DataRow("MATCH p=(()<-[:knows]->() | ()-[:knows]-()){1,2} RETURN *", "p", 6)]
    [DataRow("MATCH p=(()~[:knows]~>() | ()<~[:knows]~()){1,2} RETURN *", "p", 6)]
    [DataRow("MATCH p=(()-[:knows]->() | ()<-[:Missing]-()){1,2} RETURN *", "p", 3)]
    [DataRow("OPTIONAL MATCH p=(()-[:Missing]->() | ()<-[:Missing]-()){1,2} RETURN *", "p", 1)]
    [DataRow("MATCH p=((a WHERE EXISTS { MATCH (a)-[:knows]->(innerNode {name:'C'}) })-[:knows]->(b) | (a WHERE FALSE)-[:knows]->(b)){1,2} RETURN *", "a,b,p", 1,
        "INSERT (:Person {name:'A'})-[:knows]->(:Person {name:'B'})-[:knows]->(:Person {name:'C'})")]
    [DataRow("LET answer=LET localValue=2 IN localValue END RETURN *", "answer", 1)]
    public async Task Gql_anonymous_repeated_label_unions_hide_internal_bindings(string query, string columns, int count,
        string seedStatement = "INSERT (:Person)-[:knows]->(:Person)-[:knows]->(:Person)")
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = seedStatement
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode, await seed.Content.ReadAsStringAsync());
        using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query
        });
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var matches = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
        Assert.AreEqual(count, matches.GetArrayLength());
        foreach (var match in matches.EnumerateArray())
        {
            var bindings = match.GetProperty("bindings");
            CollectionAssert.AreEquivalent(columns.Split(','), bindings.EnumerateObject().Select(item => item.Name).ToArray());
            if (query.StartsWith("OPTIONAL", StringComparison.Ordinal))
            {
                Assert.AreEqual(JsonValueKind.Null, bindings.GetProperty("p").ValueKind);
            }
        }
        await host.StopAsync();
    }
}
