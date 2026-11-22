using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("toInteger('42')", "ERROR")]
    [DataRow("toInteger('-3.9')", "ERROR")]
    [DataRow("toInteger(3.9)", "ERROR")]
    [DataRow("toInteger(true)", "ERROR")]
    [DataRow("toInteger(false)", "ERROR")]
    [DataRow("toInteger('invalid')", "ERROR")]
    [DataRow("toInteger(NULL)", "ERROR")]
    [DataRow("toInteger('9223372036854775807')", "ERROR")]
    [DataRow("toInteger('9223372036854775808')", "ERROR")]
    [DataRow("toInteger('1e99')", "ERROR")]
    [DataRow("toInteger('NaN')", "ERROR")]
    [DataRow("toIntegerOrNull('9223372036854775808')", "ERROR")]
    [DataRow("toInteger(9223372036854775808.0)", "ERROR")]
    [DataRow("toIntegerOrNull({a:1})", "ERROR")]
    [DataRow("toInteger([1])", "ERROR")]
    [DataRow("toIntegerList(['2',true,NULL,{},[1],'bad',-3.5])", "ERROR")]
    [DataRow("toIntegerList(NULL)", "ERROR")]
    [DataRow("toIntegerList(1)", "ERROR")]
    [DataRow("toBoolean(0)", "ERROR")]
    [DataRow("toBoolean(-2)", "ERROR")]
    [DataRow("toBoolean('TRUE')", "ERROR")]
    [DataRow("toBoolean('false')", "ERROR")]
    [DataRow("toBoolean('bad')", "ERROR")]
    [DataRow("toBoolean(1.5)", "ERROR")]
    [DataRow("toBooleanOrNull(1.5)", "ERROR")]
    [DataRow("toBooleanOrNull({a:1})", "ERROR")]
    [DataRow("toBooleanList([0,-1,true,'FALSE',NULL,1.5,{}])", "ERROR")]
    [DataRow("LOWER('ABC')", "\"abc\"")]
    [DataRow("UPPER('abc')", "\"ABC\"")]
    public async Task Gql_standard_case_functions_and_rejected_vendor_conversions(string expression, string expected)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "RETURN " + expression + " AS answer"
        });
        if (expected == "ERROR")
        {
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, await response.Content.ReadAsStringAsync());
            return;
        }
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        using var wanted = JsonDocument.Parse(expected);
        Assert.AreEqual(ScalarKey(wanted.RootElement), ScalarKey(result.GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer")));
    }
}
