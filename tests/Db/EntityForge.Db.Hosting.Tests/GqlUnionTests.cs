using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Gql_standard_NEXT_composites_preserve_available_column_scope(bool lexicalImports)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        string columns = String.Join(',', Enumerable.Range(0, lexicalImports ? 31 : 32).Select(i => $"{i} AS n{i}"));
        string query = "RETURN " + columns + " NEXT " + (lexicalImports
            ? "CALL { RETURN * UNION ALL RETURN * NEXT RETURN COUNT(*) AS answer } RETURN answer"
            : "RETURN * UNION ALL RETURN * NEXT RETURN COUNT(*) AS answer");
        using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query
        });
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var matches = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
        Assert.AreEqual(1, matches.GetArrayLength());
        Assert.AreEqual(2, matches[0].GetProperty("bindings").GetProperty("answer").GetInt32());
        using var tooWide = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "RETURN " + String.Join(',', Enumerable.Range(0, 33).Select(i => $"{i} AS n{i}")) +
                " NEXT RETURN * UNION ALL RETURN *"
        });
        Assert.AreEqual(HttpStatusCode.BadRequest, tooWide.StatusCode);
        using var ordinaryCall = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "RETURN " + String.Join(',', Enumerable.Range(0, 32).Select(i => $"{i} AS n{i}")) +
                " NEXT CALL { RETURN 1 AS extra } RETURN extra"
        });
        Assert.AreEqual(HttpStatusCode.BadRequest, ordinaryCall.StatusCode);
    }

    [TestMethod]
    public async Task Gql_standard_NEXT_composites_share_table_input_within_result_bounds()
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var aggregated = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "FOR a IN [1,2,3,4,5,6,7,8,9,10,11,12,13,14,15] FOR b IN [1,2,3,4,5,6,7,8,9,10] FOR c IN [1,2,3,4,5,6,7,8,9,10] RETURN a AS n NEXT RETURN COUNT(*) AS answer UNION RETURN 1500 AS answer"
        });
        Assert.AreEqual(HttpStatusCode.OK, aggregated.StatusCode, await aggregated.Content.ReadAsStringAsync());
        var matches = (await aggregated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
        Assert.AreEqual(1, matches.GetArrayLength());
        Assert.AreEqual(1500, matches[0].GetProperty("bindings").GetProperty("answer").GetInt32());
        using var bounded = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "FOR a IN [1,2,3,4,5,6] FOR b IN [1,2,3,4,5,6,7,8,9,10] FOR c IN [1,2,3,4,5,6,7,8,9,10] FOR d IN [1,2,3,4,5,6,7,8,9,10] RETURN a AS n NEXT RETURN n AS answer UNION ALL RETURN n AS answer"
        });
        Assert.AreEqual(HttpStatusCode.BadRequest, bounded.StatusCode);
        var problem = await bounded.Content.ReadFromJsonAsync<JsonElement>();
        StringAssert.Contains(problem.GetProperty("detail").GetString()!, "10,000");
        Assert.IsFalse(problem.TryGetProperty("gqlStatus", out var status) && status.GetString() == "42001");
    }

    [TestMethod]
    [DataRow("OPTIONAL MATCH (n) RETURN n AS answer", "[null]")]
    [DataRow("OPTIONAL MATCH () RETURN 42 AS answer", "[42]")]
    [DataRow("LET answer=1 FILTER answer RETURN answer", "ERROR")]
    [DataRow("LET answer=1 OPTIONAL MATCH (n:Absent) RETURN answer", "[1]")]
    [DataRow("LET answer=1 OPTIONAL MATCH () RETURN answer", "[1]")]
    [DataRow("LET n=NULL OPTIONAL MATCH (n) RETURN n AS answer", "[null]")]
    [DataRow("LET n=NULL MATCH (n) RETURN n AS answer", "[]")]
    [DataRow("LET n=1 MATCH (n) RETURN n AS answer", "ERROR")]
    [DataRow("FOR n IN [] MATCH (n) RETURN n AS answer", "[]")]
    [DataRow("RETURN 1 AS answer UNION RETURN 1 AS answer", "[1]")]
    [DataRow("RETURN 1 AS answer UNION ALL RETURN 1 AS answer", "[1,1]")]
    [DataRow("RETURN NULL AS answer UNION DISTINCT RETURN NULL AS answer", "[null]")]
    [DataRow("RETURN 1 AS answer UNION RETURN 1.0 AS answer", "[1]")]
    [DataRow("RETURN {a:1,b:2} AS answer UNION RETURN {b:2,a:1} AS answer", "[{\"a\":1,\"b\":2}]")]
    [DataRow("FOR x IN [1,2,2] RETURN x AS answer UNION RETURN 2 AS answer", "[1,2]")]
    [DataRow("RETURN 1 AS answer LIMIT 0 UNION ALL RETURN 2 AS answer", "[2]")]
    [DataRow("RETURN 'UNION; ALL' AS answer UNION RETURN 'UNION; ALL' AS answer", "[\"UNION; ALL\"]")]
    [DataRow("RETURN 1 AS answer UNION RETURN 2 AS different", "ERROR")]
    [DataRow("RETURN 1 AS answer UNION RETURN 2 AS answer UNION ALL RETURN 3 AS answer", "SYNTAX")]
    [DataRow("RETURN 1 AS answer UNION ALL RETURN 1 AS answer UNION DISTINCT RETURN 2 AS answer", "SYNTAX")]
    [DataRow("RETURN 1 AS answer UNION RETURN 1 AS answer UNION ALL RETURN 1 AS answer", "SYNTAX")]
    [DataRow("RETURN NULL AS answer UNION ALL RETURN NULL AS answer UNION RETURN NULL AS answer", "SYNTAX")]
    [DataRow("RETURN 1 AS answer UNION ALL RETURN 1 AS answer UNION RETURN 2 AS answer UNION ALL RETURN 2 AS answer", "SYNTAX")]
    [DataRow("RETURN 1 AS answer UNION ALL RETURN 1 AS answer UNION RETURN 2 AS answer LIMIT 0", "SYNTAX")]
    [DataRow("FOR x IN [1,2] CALL (x) { RETURN x AS y UNION ALL RETURN x AS y UNION RETURN x+1 AS y } RETURN y AS answer", "SYNTAX")]
    [DataRow("RETURN 1 AS answer UNION DISTINCT RETURN 1 AS answer UNION RETURN 2 AS answer", "[1,2]")]
    [DataRow("CALL () { RETURN 1 AS answer UNION RETURN 2 AS answer } RETURN answer UNION ALL RETURN 3 AS answer", "[1,2,3]")]
    [DataRow("CALL () { RETURN 1 AS answer UNION ALL RETURN 1 AS answer } RETURN answer UNION DISTINCT RETURN 2 AS answer", "[1,2]")]
    [DataRow("CALL () { RETURN 1 AS answer UNION RETURN 1 AS answer } RETURN answer UNION ALL RETURN 1 AS answer", "[1,1]")]
    [DataRow("CALL () { RETURN NULL AS answer UNION ALL RETURN NULL AS answer } RETURN answer UNION RETURN NULL AS answer", "[null]")]
    [DataRow("CALL () { CALL () { RETURN 1 AS answer UNION ALL RETURN 1 AS answer } RETURN answer UNION RETURN 2 AS answer } RETURN answer UNION ALL RETURN 2 AS answer", "[1,2,2]")]
    [DataRow("CALL () { RETURN 1 AS answer UNION ALL RETURN 1 AS answer } RETURN answer UNION RETURN 2 AS answer LIMIT 0", "[1]")]
    [DataRow("FOR x IN [1,2] CALL (x) { CALL (x) { RETURN x AS y UNION ALL RETURN x AS y } RETURN y UNION RETURN x+1 AS y } RETURN y AS answer", "[1,2,2,3]")]
    [DataRow("RETURN 1 AS n UNION ALL RETURN 1 AS n NEXT RETURN n AS answer UNION RETURN 2 AS answer", "[1,2]")]
    [DataRow("FOR n IN [1,2,2] RETURN n NEXT RETURN COUNT(*) AS answer UNION ALL RETURN SUM(n) AS answer", "[3,5]")]
    [DataRow("FOR n IN [] RETURN n NEXT RETURN COUNT(*) AS answer UNION ALL RETURN COUNT(n) AS answer", "[0,0]")]
    [DataRow("FOR n IN [1,3,2] RETURN n NEXT RETURN n AS answer ORDER BY answer DESC LIMIT 1 UNION ALL RETURN n AS answer ORDER BY answer LIMIT 1", "[3,1]")]
    [DataRow("FOR n IN [1,2,2] RETURN n NEXT RETURN n AS answer UNION ALL RETURN n AS answer", "[1,2,2,1,2,2]")]
    [DataRow("FOR n IN [] RETURN n NEXT RETURN n AS answer UNION ALL RETURN 1 AS answer", "[]")]
    [DataRow("FOR x IN [10,20] CALL (x) { FOR n IN [1,2,2] RETURN n NEXT RETURN SUM(n)+x AS y UNION ALL RETURN COUNT(*)+x AS y } RETURN y AS answer", "[15,13,25,23]")]
    [DataRow("RETURN VALUE { FOR n IN [1,2,2] RETURN n NEXT RETURN SUM(n) AS total UNION RETURN 5 AS total } AS answer", "[5]")]
    [DataRow("CALL TABLE { RETURN 1 AS answer } RETURN answer", "SYNTAX")]
    [DataRow("RETURN 1 AS answer UNION", "ERROR")]
    [DataRow("RETURN 1 AS answer UNION MATCH (n) RETURN GRAPH", "ERROR")]
    [DataRow("FOR a IN [1,2,3,4,5] FOR b IN [1,2,3,4,5] FOR c IN [1,2,3,4,5] FOR d IN [1,2,3,4,5] FOR e IN [1,2,3,4,5] FOR f IN [1,2,3,4,5] RETURN a AS answer UNION ALL RETURN 1 AS answer", "ERROR")]
    public async Task Gql_standard_union_preserves_column_contracts_bags_and_bounds(string query, string expected)
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
        if (expected is "ERROR" or "SYNTAX")
        {
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, await response.Content.ReadAsStringAsync());
            if (expected == "SYNTAX")
            {
                Assert.AreEqual("42001", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("gqlStatus").GetString());
            }
            return;
        }
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        using var wanted = JsonDocument.Parse(expected);
        CollectionAssert.AreEquivalent(wanted.RootElement.EnumerateArray().Select(ScalarKey).ToArray(),
            result.GetProperty("matches").EnumerateArray().Select(row => ScalarKey(row.GetProperty("bindings").GetProperty("answer"))).ToArray());
    }
}
