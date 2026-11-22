using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("FOR x IN [3,1,2] RETURN x ORDER BY VALUE { FOR y IN [1,2,3] FILTER y<=x RETURN COUNT(*) AS total }", "x", "[1,2,3]")]
    [DataRow("FOR x IN [3,1,2] RETURN x ORDER BY VALUE { FOR y IN [1,2,3] FILTER y<=x RETURN COUNT(*) AS total } DESC", "x", "[3,2,1]")]
    [DataRow("FOR x IN [3,1,2] RETURN x ORDER BY VALUE { RETURN 1 AS total },VALUE { FOR y IN [1,2,3] FILTER y<=x RETURN COUNT(*) AS total } DESC NEXT RETURN *", "x", "[3,2,1]")]
    [DataRow("FOR x IN [3,1,2] RETURN x ORDER BY CASE WHEN true THEN x ELSE VALUE { RETURN 1/0 AS total } END", "x", "[1,2,3]")]
    [DataRow("LET temp=1 RETURN temp LIMIT 1", "temp", "[1]")]
    [DataRow("LET temp=1 RETURN temp ORDER BY temp LIMIT 1", "temp", "[1]")]
    [DataRow("RETURN $x AS \"$x\"", "$x", "[42]")]
    [DataRow("RETURN count(*)+1 AS \"count(*)+1\"", "count(*)+1", "[2]")]
    [DataRow("RETURN 'a' AS \"'a'\" UNION RETURN 'a' AS \"'a'\"", "'a'", "[\"a\"]")]
    [DataRow("RETURN 'a,b RETURN' AS \"'a,b RETURN'\"", "'a,b RETURN'", "[\"a,b RETURN\"]")]
    [DataRow("RETURN 1+1 AS \"1+1\"", "1+1", "[2]")]
    [DataRow("RETURN 1 + 1 AS \"1 + 1\"", "1 + 1", "[2]")]
    [DataRow("RETURN UPPER('a') AS \"UPPER('a')\"", "UPPER('a')", "[\"A\"]")]
    [DataRow("RETURN count(*) AS \"count(*)\"", "count(*)", "[1]")]
    [DataRow("RETURN CASE WHEN true THEN 1 END AS \"CASE WHEN true THEN 1 END\"", "CASE WHEN true THEN 1 END", "[1]")]
    [DataRow("FOR x IN [3,1,2] RETURN x*2 AS \"x*2\" ORDER BY x*2", "x*2", "[2,4,6]")]
    [DataRow("FOR x IN [3,1,2] RETURN x*2 AS \"x*2\" ORDER BY x*2 DESC", "x*2", "[6,4,2]")]
    [DataRow("LET person=RECORD {name:'A'} RETURN person.name AS \"person.name\"", "person.name", "[\"A\"]")]
    public async Task Gql_standard_binding_columns_and_explicit_expression_aliases_preserve_names(string query, string column, string expected)
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
                x = 42
            }
        });
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        using var wanted = JsonDocument.Parse(expected);
        CollectionAssert.AreEqual(wanted.RootElement.EnumerateArray().Select(ScalarKey).ToArray(),
            result.GetProperty("matches").EnumerateArray().Select(row => ScalarKey(row.GetProperty("bindings").GetProperty(column))).ToArray());
        Assert.IsTrue(result.GetProperty("matches").EnumerateArray().All(row => row.GetProperty("bindings").EnumerateObject().Count() == 1));
    }
}
