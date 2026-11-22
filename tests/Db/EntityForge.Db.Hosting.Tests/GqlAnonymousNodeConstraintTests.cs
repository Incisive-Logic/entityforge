using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("MATCH (:Person {name:'A'})(:Person {name:'A'}) RETURN *", 1, "")]
    [DataRow("MATCH (:Person {name:'A'})(:Person {name:'B'}) RETURN *", 0, "")]
    [DataRow("OPTIONAL MATCH (:Person {name:'A'})(:Person {name:'B'}) RETURN *", 1, "")]
    [DataRow("MATCH (:Person|Missing) RETURN *", 2, "")]
    [DataRow("MATCH p=(:Person {name:'A'})(:Person {name:'A'})(:Person {name:'A'}) WHERE PATH_LENGTH(p)=0 RETURN *", 1, "p")]
    [DataRow("MATCH p=((:Person {name:'A'})(:Person {name:'A'})-[:knows]->()){1,2} RETURN *", 1, "p")]
    [DataRow("LET __subpathElement0=7 MATCH (:Person {name:'A'})(:Person {name:'A'}) RETURN *", 1, "__subpathElement0")]
    public async Task Gql_anonymous_node_constraints_preserve_rows_without_exporting_internal_bindings(
        string query, int expectedRows, string columns)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (:Person {name:'A'})-[:knows]->(:Person {name:'B'})"
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode, await seed.Content.ReadAsStringAsync());
        using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query
        });
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var rows = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
        Assert.AreEqual(expectedRows, rows.GetArrayLength());
        foreach (var row in rows.EnumerateArray())
        {
            var bindings = row.GetProperty("bindings");
            CollectionAssert.AreEquivalent(columns.Split(',', StringSplitOptions.RemoveEmptyEntries),
                bindings.EnumerateObject().Select(property => property.Name).ToArray());
            if (columns == "__subpathElement0")
            {
                Assert.AreEqual(7, bindings.GetProperty(columns).GetInt32());
            }
        }
    }
}
