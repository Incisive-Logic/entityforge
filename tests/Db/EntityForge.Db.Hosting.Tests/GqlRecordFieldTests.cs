using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("DATE({`year`:2020,`month`:2,`day`:29})=DATE('2020-02-29')")]
    [DataRow("LOCAL_TIME({`hour`:12,`minute`:30})=LOCAL_TIME('12:30')")]
    [DataRow("LOCAL_DATETIME({`year`:2020,`month`:2,`day`:29,`hour`:12})=LOCAL_DATETIME('2020-02-29T12:00')")]
    [DataRow("ZONED_TIME({`hour`:12,timezone:'Z'})=ZONED_TIME('12:00Z')")]
    [DataRow("ZONED_DATETIME({`year`:2020,`month`:2,`day`:29,`hour`:12,timezone:'Z'})=ZONED_DATETIME('2020-02-29T12:00Z')")]
    public async Task Gql_standard_temporal_record_fields_accept_delimited_component_names(string expression)
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
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        Assert.IsTrue((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetBoolean());
        await host.StopAsync();
    }

    [TestMethod]
    [DataRow("`display name`", "display name")]
    [DataRow("`a``b`", "a`b")]
    [DataRow("`year`", "year")]
    [DataRow("café", "café")]
    public async Task Gql_standard_record_field_names_preserve_delimiters_and_unicode(string field, string expected)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "RETURN RECORD {" + field + ":42} AS answer"
        });
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var record = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer");
        Assert.AreEqual(1, record.EnumerateObject().Count());
        Assert.AreEqual(42, record.GetProperty(expected).GetInt32());
        using var duplicate = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "RETURN RECORD {" + field + ":1," + JsonSerializer.Serialize(expected) + ":2} AS answer"
        });
        Assert.AreEqual(HttpStatusCode.BadRequest, duplicate.StatusCode);
        await host.StopAsync();
    }
}
