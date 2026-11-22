using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("/* 😀 */ MATCH ANY $takeLong (a)((x)-[e]->(y)){1,2}(b) RETURN COUNT(*)+$value AS answer")]
    [DataRow("CALL () { MATCH ANY $takeLong (a)((x)-[e]->(y)){1,2}(b) RETURN COUNT(*) AS n } CALL () { MATCH SHORTEST $take GROUPS (c)((u)-[f]->(v)){1,2}(d) RETURN COUNT(*) AS m } RETURN n+m+$value AS answer")]
    [DataRow("/* 😀 $take */ MATCH ANY $`takeLong` (a)((x)-[e]->(y)){1,2}(b) RETURN COUNT(*)+$value AS answer")]
    [DataRow("MATCH ANY $takeLong (a)((x)-[e]->(y)){1,2}(b) RETURN COUNT(*)+$value AS answer UNION ALL MATCH ANY $take (c)((u)-[f]->(v)){1,2}(d) RETURN COUNT(*)+$value AS answer")]
    public Task Gql_parameter_count_rewrites_preserve_unicode_offsets_and_nested_values(string query)
        => AssertCallQuery(query, query.Contains("UNION ALL", StringComparison.Ordinal) ? "[7,7]" : "[7]",
            new Dictionary<string, object> { ["takeLong"] = 12, ["take"] = 1, ["value"] = 7 });

    [TestMethod]
    [DataRow("RETURN $value AS answer")]
    [DataRow("RETURN $value AS answer /* ANY $take */")]
    [DataRow("RETURN CASE WHEN SESSION_USER = 'anonymous' THEN $value ELSE -1 END AS answer")]
    [DataRow("CALL () { RETURN $value AS answer } RETURN answer")]
    [DataRow("RETURN VALUE { RETURN $value AS answer } AS answer")]
    [DataRow("RETURN $value AS `answer`")]
    public async Task Gql_parameter_preparation_preserves_nested_results_and_session_binding(string query)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query,
            parameters = new
            {
                value = 7
            }
        });
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var rows = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
        Assert.AreEqual(1, rows.GetArrayLength());
        Assert.AreEqual(7, rows[0].GetProperty("bindings").GetProperty("answer").GetInt32());
    }
}
