using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("ASC NULLS FIRST", -1)]
    [DataRow("DESC NULLS LAST", 2)]
    [DataRow("DESCENDING NULLS FIRST", -1)]
    [DataRow("ASCENDING NULLS LAST", 1)]
    [DataRow("ASC", 1)]
    [DataRow("DESC", -1)]
    public async Task Gql_standard_null_order_selects_mutation_rows_before_writes(string ordering, int expected)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (:NullOrder {rank:2}),(:NullOrder),(:NullOrder {rank:1})"
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode, await seed.Content.ReadAsStringAsync());
        var request = new
        {
            statement = "MATCH (n:NullOrder) ORDER BY n.rank " + ordering + " LIMIT 1 SET n.chosen=TRUE RETURN COALESCE(n.rank,-1) AS answer",
            transactionId = Guid.NewGuid()
        };
        using var update = await client.PostAsJsonAsync("/v1/graph/gql/update", request);
        Assert.AreEqual(HttpStatusCode.OK, update.StatusCode, await update.Content.ReadAsStringAsync());
        var result = await update.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual(expected, result.GetProperty("result").GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetInt32());
        using var retry = await client.PostAsJsonAsync("/v1/graph/gql/update", request);
        Assert.AreEqual(await update.Content.ReadAsStringAsync(), await retry.Content.ReadAsStringAsync());
        using var query = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (n:NullOrder) WHERE n.chosen IS TRUE RETURN COUNT(*) AS total"
        });
        Assert.AreEqual(HttpStatusCode.OK, query.StatusCode, await query.Content.ReadAsStringAsync());
        Assert.AreEqual(1, (await query.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("total").GetInt32());
        await host.StopAsync();
    }

    [TestMethod]
    [DataRow("FOR x IN [2,NULL,1,NULL] RETURN x AS answer ORDER BY answer", "[1,2,null,null]")]
    [DataRow("FOR x IN [2,NULL,1,NULL] RETURN x AS answer ORDER BY answer DESC", "[null,null,2,1]")]
    [DataRow("MATCH (n:NullOrder) RETURN n.rank AS answer ORDER BY answer", "[1,2,null]")]
    [DataRow("MATCH (n:NullOrder) RETURN n.rank AS answer ORDER BY answer DESC", "[null,2,1]")]
    [DataRow("MATCH (n:NullOrder) RETURN n.rank AS answer,COUNT(*) AS total GROUP BY answer ORDER BY answer", "[1,2,null]")]
    [DataRow("MATCH (n:NullOrder) RETURN n.rank AS answer,COUNT(*) AS total GROUP BY answer ORDER BY answer DESC", "[null,2,1]")]
    [DataRow("RETURN NULL AS n UNION ALL RETURN 2 AS n UNION ALL RETURN 1 AS n NEXT RETURN n AS answer ORDER BY answer", "[1,2,null]")]
    [DataRow("RETURN NULL AS n UNION ALL RETURN 2 AS n UNION ALL RETURN 1 AS n NEXT RETURN n AS answer ORDER BY answer DESC", "[null,2,1]")]
    [DataRow("FOR x IN [2,NULL,1,NULL] RETURN x AS answer ORDER BY answer NULLS FIRST", "[null,null,1,2]")]
    [DataRow("FOR x IN [2,NULL,1,NULL] RETURN x AS answer ORDER BY answer DESC NULLS FIRST", "[null,null,2,1]")]
    [DataRow("FOR x IN [2,NULL,1,NULL] RETURN x AS answer ORDER BY answer ASC NULLS LAST", "[1,2,null,null]")]
    [DataRow("FOR x IN [2,NULL,1,NULL] RETURN x AS answer ORDER BY answer DESCENDING NULLS LAST", "[2,1,null,null]")]
    [DataRow("FOR x IN [2,NULL,1,NULL] RETURN x AS answer ORDER BY answer ASCENDING NULLS FIRST", "[null,null,1,2]")]
    [DataRow("FOR x IN [2,1] RETURN x AS answer ORDER BY answer DESCENDING", "[2,1]")]
    [DataRow("FOR x IN [2,1] RETURN x AS answer ORDER BY answer ASCENDING", "[1,2]")]
    [DataRow("FOR x IN [2,NULL,1,NULL] ORDER BY x NULLS FIRST OFFSET 1 LIMIT 2 RETURN x AS answer", "[null,1]")]
    [DataRow("FOR x IN [RECORD {majorKey:NULL,minorKey:2},RECORD {majorKey:NULL,minorKey:1},RECORD {majorKey:1,minorKey:0}] RETURN x.minorKey AS answer ORDER BY x.majorKey NULLS FIRST,x.minorKey DESC", "[2,1,0]")]
    [DataRow("MATCH (n:NullOrder) RETURN n.rank AS answer ORDER BY answer NULLS FIRST", "[null,1,2]")]
    [DataRow("MATCH (n:NullOrder) RETURN n.rank AS answer ORDER BY answer DESCENDING NULLS LAST", "[2,1,null]")]
    [DataRow("MATCH (n:NullOrder) RETURN n.rank AS answer,COUNT(*) AS total GROUP BY answer ORDER BY answer DESC NULLS FIRST", "[null,2,1]")]
    [DataRow("MATCH (n:NullOrder) RETURN n.rank AS answer GROUP BY answer ORDER BY answer NULLS LAST", "[1,2,null]")]
    [DataRow("RETURN NULL AS n UNION ALL RETURN 2 AS n UNION ALL RETURN 1 AS n NEXT RETURN n AS answer ORDER BY answer NULLS FIRST", "[null,1,2]")]
    public async Task Gql_standard_null_order_and_direction_synonyms_preserve_sequence(string query, string expected)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (:NullOrder {rank:2}),(:NullOrder),(:NullOrder {rank:1})"
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode, await seed.Content.ReadAsStringAsync());
        using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query
        });
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var rows = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
        Assert.AreEqual(expected, JsonSerializer.Serialize(rows.EnumerateArray().Select(row => row.GetProperty("bindings").GetProperty("answer"))));
        await host.StopAsync();
    }
}
