using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("EXISTS { (b)-[:knows]->() }", 1)]
    [DataRow("EXISTS { (a)-[:knows]->() }", 1)]
    [DataRow("NOT EXISTS { (b)-[:knows]->() }", 1)]
    [DataRow("VALUE { RETURN b.name AS n }='A'", 1)]
    [DataRow("VALUE { LET localName=b.name RETURN localName }='A'", 1)]
    [DataRow("VALUE { RETURN b.name AS b ORDER BY b }='A'", 1)]
    [DataRow("VALUE { RETURN b NEXT RETURN b.name AS n }='A'", 1)]
    [DataRow("VALUE { RETURN b NEXT YIELD b AS chosen RETURN chosen.name AS n }='A'", 1)]
    [DataRow("VALUE { CALL (b) { RETURN b.name AS n } RETURN n }='A'", 1)]
    [DataRow("VALUE { CALL (b) { MATCH (a:NestedAlias {name:'B'}) RETURN b.name AS n } RETURN n }='A'", 1)]
    [DataRow("VALUE { CALL (b) { MATCH (a:NestedAlias {name:'B'}) RETURN b.name AS n } RETURN n }='A'", 1, "MATCH (a:NestedAlias {name:'A'}) ")]
    [DataRow("EXISTS { (b)-[:knows]->() }", 1, "MATCH (b:NestedAlias {name:'A'}) ")]
    [DataRow("VALUE { CALL () { MATCH (b:NestedAlias {name:'B'}) RETURN b.name AS n } RETURN n }='B'", 2)]
    [DataRow("VALUE { CALL () { MATCH (a:NestedAlias {name:'A'}),(b:NestedAlias {name:'B'}) RETURN b.name AS n } RETURN n }='B'", 2)]
    public async Task Gql_nested_alias_predicates_preserve_correlated_and_local_bindings(string predicate, int expectedCount, string prefix = "")
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (:NestedAlias {name:'A'})-[:knows]->(:NestedAlias {name:'B'})"
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode, await seed.Content.ReadAsStringAsync());
        using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = prefix + "MATCH (a:NestedAlias)(b WHERE " + predicate + ") RETURN a.name AS firstName,b.name AS secondName"
        });
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var rows = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
        Assert.AreEqual(expectedCount, rows.GetArrayLength());
        CollectionAssert.AreEqual(expectedCount == 2 ? new[] { "A", "B" } :
                new[] { predicate.StartsWith("NOT", StringComparison.Ordinal) ? "B" : "A" },
            rows.EnumerateArray().Select(row => row.GetProperty("bindings").GetProperty("firstName").GetString()).Order().ToArray());
        foreach (var row in rows.EnumerateArray())
        {
            var bindings = row.GetProperty("bindings");
            CollectionAssert.AreEquivalent(new[] { "firstName", "secondName" }, bindings.EnumerateObject().Select(item => item.Name).ToArray());
            Assert.AreEqual(bindings.GetProperty("firstName").GetString(), bindings.GetProperty("secondName").GetString());
        }
    }
}
