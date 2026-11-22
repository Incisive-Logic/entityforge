using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("FOR x IN [NULL,1] RETURN SUM(x) AS total NEXT INSERT (:Diagnostic {amount:total}) RETURN total", "00000", true)]
    [DataRow("INSERT (:Diagnostic) FOR x IN [NULL,1] RETURN SUM(x) AS total", "00000", true)]
    [DataRow("CALL { FOR x IN [NULL,1] RETURN SUM(x) AS total NEXT INSERT (:Diagnostic {amount:total}) RETURN total } RETURN total", "00000", true)]
    [DataRow("INSERT (:Diagnostic) RETURN 1 AS total LIMIT 0", "02000", false)]
    [DataRow("INSERT (:Diagnostic) FINISH", "00001", false)]
    [DataRow("INSERT (:Diagnostic)", "00001", false)]
    [DataRow("MATCH (n:MissingDiagnostic) SET n.amount=1", "00001", false)]
    [DataRow("FOR x IN [NULL,1] RETURN SUM(x) AS total NEXT INSERT (:Diagnostic {amount:total})", "00001", true)]
    [DataRow("INSERT (:Diagnostic) INSERT (:Diagnostic)", "00001", false)]
    public async Task Gql_update_result_diagnostics_survive_replay(string statement, string completion, bool warning)
    {
        _directory = NewDirectory();
        var request = new
        {
            statement,
            transactionId = Guid.NewGuid()
        };
        string? first = null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
                .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
            await host.StartAsync();
            using var client = Client(host);
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/update", request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            var result = body.GetProperty("result");
            if (completion == "00001")
            {
                Assert.AreEqual(0, result.GetProperty("matches").GetArrayLength());
            }
            var codes = result.GetProperty("diagnostics").EnumerateArray().Select(item => item.GetProperty("code").GetString()).ToArray();
            Assert.IsTrue(codes.Contains(completion), result.ToString());
            Assert.AreEqual(warning ? 1 : 0, codes.Count(code => code == "01G11"));
            if (first is not null)
            {
                Assert.AreEqual(first, result.GetRawText());
            }
            first = result.GetRawText();
        }
    }

    [TestMethod]
    [DataRow("RETURN 1 AS answer", "00000")]
    [DataRow("RETURN 1 AS answer LIMIT 0", "02000")]
    [DataRow("RETURN NULL AS answer", "00000")]
    [DataRow("RETURN 1 AS __finished LIMIT 0", "02000")]
    [DataRow("FINISH", "00001")]
    [DataRow("LET stamp=CURRENT_TIMESTAMP FINISH", "00001")]
    [DataRow("RETURN 1 AS answer NEXT FINISH", "00001")]
    [DataRow("RETURN VALUE { MATCH (:Absent) RETURN COUNT(*) AS total } AS answer", "00000")]
    [DataRow("CALL { MATCH (:Absent) RETURN 1 AS valueNumber } RETURN valueNumber", "02000")]
    [DataRow("USE CURRENT_GRAPH { RETURN 1 AS answer }", "00000")]
    [DataRow("USE CURRENT_GRAPH { FINISH }", "00001")]
    public async Task Gql_completion_describes_only_the_outer_result(string query, string expected)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query
        });
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        var codes = result.GetProperty("diagnostics").EnumerateArray().Select(item => item.GetProperty("code").GetString())
            .Where(code => code is "00000" or "00001" or "02000").ToArray();
        CollectionAssert.AreEqual(new[] { expected }, codes);
    }

    [TestMethod]
    [DataRow("FOR x IN [1,NULL,2] RETURN SUM(x) AS total", true)]
    [DataRow("FOR x IN [NULL,NULL] RETURN COUNT(x) AS total", true)]
    [DataRow("FOR x IN [NULL,NULL] RETURN COUNT(*) AS total", false)]
    [DataRow("FOR x IN [] RETURN SUM(x) AS total", false)]
    [DataRow("FOR x IN [1,2] RETURN SUM(x) AS total", false)]
    [DataRow("FOR x IN [[NULL],[1]] RETURN COLLECT_LIST(x) AS total", false)]
    [DataRow("FOR x IN [NULL,1] RETURN SUM(x) AS total LIMIT 0", true)]
    [DataRow("CALL { FOR x IN [NULL,1] RETURN SUM(x) AS total } RETURN total", true)]
    [DataRow("RETURN VALUE { FOR x IN [NULL,1] RETURN SUM(x) AS total } AS total", true)]
    [DataRow("RETURN CASE WHEN TRUE THEN 1 ELSE VALUE { FOR x IN [NULL,1] RETURN SUM(x) AS total } END AS total", false)]
    [DataRow("FOR x IN [NULL,1] RETURN SUM(x) AS total UNION ALL RETURN 2 AS total", true)]
    [DataRow("FOR x IN [NULL,1] RETURN SUM(x) AS total NEXT RETURN total + 1 AS total", true)]
    [DataRow("MATCH (n:Diagnostic) RETURN COUNT(n.missing) AS total", true)]
    [DataRow("MATCH (n:Diagnostic) RETURN SUM(n.missing) AS total, COUNT(n.missing) AS tally", true)]
    [DataRow("OPTIONAL MATCH (n:Absent) RETURN COUNT(n) AS total", true)]
    [DataRow("MATCH (n:Absent) RETURN COUNT(n.missing) AS total", false)]
    public async Task Gql_null_elimination_diagnostic_follows_executed_aggregates(string query, bool warning)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (:Diagnostic)"
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode, await seed.Content.ReadAsStringAsync());
        foreach (bool includeSupportingGraph in new[] { false, true })
        {
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query,
                includeSupportingGraph
            });
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            var result = await response.Content.ReadFromJsonAsync<JsonElement>();
            var diagnostics = result.GetProperty("diagnostics").EnumerateArray()
                .Where(item => item.GetProperty("code").GetString() == "01G11").ToArray();
            Assert.AreEqual(warning ? 1 : 0, diagnostics.Length, query);
        }
    }
}
