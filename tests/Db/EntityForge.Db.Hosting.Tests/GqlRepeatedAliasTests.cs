using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("MATCH p=((a:RepeatAlias)(b)-[:hop]->(c)){1,2}", "1,1,2")]
    [DataRow("MATCH p=((a:RepeatAlias)(b)-[:hop]->(c)){0,2}", "0,0,0,1,1,2")]
    [DataRow("MATCH p=((a:RepeatAlias)(b)-[:hop]->(c)){2}", "2")]
    [DataRow("MATCH p=((a:RepeatAlias)(b)-[:hop]->(c)){1,2} RETURN a,b,p NEXT", "1,1,2")]
    [DataRow("MATCH p=((a:RepeatAlias)(b)-[:hop]->(c) WHERE b.name='A'){1,2}", "1")]
    [DataRow("MATCH p=((a:RepeatAlias)(b)-[:hop]->(c) WHERE EXISTS { (b)-[:hop]->(c) }){1,2}", "1,1,2")]
    [DataRow("MATCH p=((a:RepeatAlias)(b)-[:hop]->(c) WHERE VALUE { RETURN b.name AS n }='A'){1,2}", "1")]
    [DataRow("MATCH p=((a:RepeatAlias)(b)-[:hop]->(c) WHERE VALUE { RETURN b.name AS n }='A'){0,2}", "0,0,0,1")]
    [DataRow("MATCH p=ALL SHORTEST ((a:RepeatAlias)(b)-[:hop]->(c)){1,2}", "1,1,2")]
    [DataRow("MATCH p=(segments=(a:RepeatAlias)(b)-[:hop]->(c)){1,2}", "1,1,2", false, true)]
    [DataRow("MATCH p=((c:RepeatAlias)-[:hop]->(a)(b)){1,2}", "1,1,2")]
    [DataRow("MATCH p=((a:RepeatAlias)(b)-[:hop]->(c)(d)){1,2}", "1,1,2")]
    [DataRow("OPTIONAL MATCH p=((a:Missing)(b)-[:hop]->(c)){1,2}", "", true)]
    public async Task Gql_repeated_adjacent_aliases_preserve_collection_identity_and_scope(
        string match, string expectedLengths, bool absent = false, bool namedSegments = false)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (:RepeatAlias {name:'A'})-[:hop]->(:RepeatAlias {name:'B'})-[:hop]->(:RepeatAlias {name:'C'})"
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode, await seed.Content.ReadAsStringAsync());
        using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = match + " RETURN a,b," + (namedSegments ? "SIZE(segments)" : "PATH_LENGTH(p)") + " AS length"
        });
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var rows = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
        if (absent)
        {
            Assert.AreEqual(1, rows.GetArrayLength());
            Assert.IsTrue(rows[0].GetProperty("bindings").EnumerateObject().All(property => property.Value.ValueKind == JsonValueKind.Null));
            return;
        }
        CollectionAssert.AreEqual(expectedLengths.Split(',').Select(Int32.Parse).ToArray(),
            rows.EnumerateArray().Select(row => row.GetProperty("bindings").GetProperty("length").GetInt32()).Order().ToArray());
        foreach (var row in rows.EnumerateArray())
        {
            var values = row.GetProperty("bindings");
            CollectionAssert.AreEquivalent(new[] { "a", "b", "length" }, values.EnumerateObject().Select(property => property.Name).ToArray());
            Assert.AreEqual(JsonValueKind.Array, values.GetProperty("a").ValueKind);
            Assert.AreEqual(values.GetProperty("a").GetRawText(), values.GetProperty("b").GetRawText());
            Assert.AreEqual(values.GetProperty("length").GetInt32(), values.GetProperty("a").GetArrayLength());
        }
    }
}
