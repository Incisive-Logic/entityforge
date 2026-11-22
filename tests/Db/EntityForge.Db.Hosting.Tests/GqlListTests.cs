using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("RANGE(1,10001)", "ERROR")]
    [DataRow("RANGE(1,10,0)", "ERROR")]
    [DataRow("RANGE(1,2,0.5)", "ERROR")]
    [DataRow("RANGE(-2147483649,0)", "ERROR")]
    [DataRow("[RANGE(1,6000),RANGE(1,6000)]", "ERROR")]
    [DataRow("HEAD(1)", "ERROR")]
    [DataRow("[1,2][NULL..]", "ERROR")]
    [DataRow("[1]+NULL", "ERROR")]
    [DataRow("[1,NULL,[2,3]]", "[1,null,[2,3]]")]
    [DataRow("[]", "[]")]
    [DataRow("[1,2,3][-1]", "ERROR")]
    [DataRow("[1,2,3][-4]", "ERROR")]
    [DataRow("[1,2,3][1..]", "ERROR")]
    [DataRow("[1,2,3][..-1]", "ERROR")]
    [DataRow("[1,2,3][-9..9]", "ERROR")]
    [DataRow("[1,2,3][2..1]", "ERROR")]
    [DataRow("[[1,2],[3,4]][1][0]", "ERROR")]
    [DataRow("1 IN [1,NULL]", "ERROR")]
    [DataRow("2 IN [1,NULL]", "ERROR")]
    [DataRow("NULL IN []", "ERROR")]
    [DataRow("[1,NULL] = [2,NULL]", "false")]
    [DataRow("[1,NULL] = [1,NULL]", "null")]
    [DataRow("[1] = [1.0]", "true")]
    [DataRow("[1] <> [2]", "true")]
    [DataRow("HEAD([])", "ERROR")]
    [DataRow("LAST([1,2])", "ERROR")]
    [DataRow("TAIL([1,NULL,3])", "ERROR")]
    [DataRow("REVERSE([1,NULL,3])", "ERROR")]
    [DataRow("SIZE([1,NULL,3])", "3")]
    [DataRow("ISEMPTY([])", "ERROR")]
    [DataRow("ISEMPTY('')", "ERROR")]
    [DataRow("RANGE(3,1,-1)", "ERROR")]
    [DataRow("RANGE(3,1)", "ERROR")]
    [DataRow("RANGE(1,5,2)", "ERROR")]
    [DataRow("[1] || [2,NULL]", "[1,2,null]")]
    [DataRow("0 + [1,2]", "ERROR")]
    [DataRow("[1,2] + 3", "ERROR")]
    public async Task Gql_standard_list_expressions_and_rejected_vendor_forms(string expression, string expected)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = $"INSERT (n:Person {{id:'{Guid.NewGuid()}',age:1}})"
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode, await seed.Content.ReadAsStringAsync());
        using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = $"MATCH (n:Person) WHERE n.age=1 OR n.age=2 RETURN {expression} AS answer"
        });
        if (expected == "ERROR")
        {
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, await response.Content.ReadAsStringAsync());
            return;
        }
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        JsonElement value = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer");
        using var wanted = JsonDocument.Parse(expected);
        Assert.AreEqual(ScalarKey(wanted.RootElement), ScalarKey(value));
    }
}
