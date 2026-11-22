using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("MATCH (n) RETURN n:Person AS answer", "[true,true,false]")]
    [DataRow("MATCH (n) WHERE n:Person&!Employee RETURN n.name AS answer", "[\"B\"]")]
    [DataRow("MATCH (n) WHERE n:Person&Employee RETURN n.name AS answer", "[\"A\"]")]
    [DataRow("MATCH (n) RETURN n IS NOT LABELED Person AS answer", "[false,false,true]")]
    [DataRow("MATCH (n) WHERE n:!(Employee|Person) RETURN n.name AS answer", "[\"C\"]")]
    [DataRow("MATCH (n) RETURN n:% AS answer", "[true,true,false]")]
    [DataRow("MATCH (n)-[r:knows]->(m) RETURN r:knows AS answer", "[true]")]
    [DataRow("LET absent=NULL RETURN absent IS LABELED Employee AS answer", "[null]")]
    [DataRow("LET item='text' RETURN item IS LABELED Employee AS answer", "ERROR")]
    public async Task Gql_standard_label_predicates_handle_boolean_labels_and_nulls(string query, string expected)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seeded = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (a:Person&Employee {name:'A'}),(b:Person {name:'B'}),({name:'C'}),(a)-[:knows]->(b)"
        });
        Assert.AreEqual(HttpStatusCode.OK, seeded.StatusCode, await seeded.Content.ReadAsStringAsync());
        using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query
        });
        Assert.AreEqual(expected == "ERROR" ? HttpStatusCode.BadRequest : HttpStatusCode.OK, response.StatusCode,
            await response.Content.ReadAsStringAsync());
        if (expected != "ERROR")
        {
            var result = await response.Content.ReadFromJsonAsync<JsonElement>();
            using var wanted = JsonDocument.Parse(expected);
            CollectionAssert.AreEquivalent(wanted.RootElement.EnumerateArray().Select(ScalarKey).ToArray(),
                result.GetProperty("matches").EnumerateArray().Select(row => ScalarKey(row.GetProperty("bindings").GetProperty("answer"))).ToArray());
        }
    }
}
