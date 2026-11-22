using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    public async Task Gql_wildcard_union_after_update_preserves_atomic_replay()
    {
        _directory = NewDirectory();
        var update = new
        {
            statement = "INSERT (n:WildcardUpdate {amount:7,name:'wildcard',amounts:[0.1,0.2],dayZero:DURATION('PT0S')}) RETURN n NEXT RETURN * UNION RETURN * NEXT LET doubled=VALUE { RETURN * UNION ALL RETURN * NEXT RETURN SUM(n.amount) AS amount } SET n.checked=TRUE,n.doubled=doubled RETURN n.amount AS answer",
            transactionId = Guid.NewGuid()
        };
        string? receipt = null;
        for (int restart = 0; restart < 2; restart++)
        {
            await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
                .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
            await host.StartAsync();
            using var client = Client(host);
            using var changed = await client.PostAsJsonAsync("/v1/graph/gql/update", update);
            string body = await changed.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, changed.StatusCode, body);
            receipt ??= body;
            Assert.AreEqual(receipt, body);
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = "MATCH (n:WildcardUpdate) FILTER n.checked=TRUE AND n.doubled=14 RETURN SUM(n.amount) AS answer"
            });
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            var rows = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
            Assert.AreEqual(1, rows.GetArrayLength());
            Assert.AreEqual(7, rows[0].GetProperty("bindings").GetProperty("answer").GetInt32());
            await host.StopAsync();
        }
    }

    [TestMethod]
    [DataRow('"')]
    [DataRow('`')]
    public async Task Gql_standard_delimited_output_aliases_preserve_names_and_replay(char delimiter)
    {
        _directory = NewDirectory();
        string Quote(string name) => delimiter + name.Replace(delimiter.ToString(), new string(delimiter, 2), StringComparison.Ordinal) + delimiter;
        string output = "display: name\"`😀";
        var update = new
        {
            statement = "INSERT (n:OutputAlias {amount:7}) RETURN n.amount AS " + Quote(output),
            transactionId = Guid.NewGuid()
        };
        string? receipt = null;
        for (int restart = 0; restart < 2; restart++)
        {
            await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
                .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
            await host.StartAsync();
            using var client = Client(host);
            using var changed = await client.PostAsJsonAsync("/v1/graph/gql/update", update);
            string body = await changed.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, changed.StatusCode, body);
            receipt ??= body;
            Assert.AreEqual(receipt, body);
            foreach (string name in new[] { output, "RETURN", "answer", "😀" })
            {
                string alias = Quote(name);
                foreach (string query in new[]
                {
                    "RETURN 7 AS " + alias,
                    "MATCH (n:OutputAlias) RETURN n.amount AS " + alias,
                    "FOR v IN [3,4] RETURN SUM(v) AS " + alias,
                    "FOR v IN [3,4] RETURN SUM(v)+COUNT(*)-2 AS " + alias,
                    "CALL { RETURN 7 AS " + alias + " } RETURN *",
                    "RETURN 7 AS " + alias + " UNION RETURN 7 AS " + alias,
                    "RETURN 7 AS " + alias + " NEXT RETURN *",
                    "RETURN 7 AS " + alias + " NEXT YIELD " + alias + " RETURN *",
                    "RETURN 7 AS " + alias + " NEXT YIELD " + alias + " AS renamed RETURN renamed AS " + alias,
                    "RETURN 7 AS " + alias + " UNION RETURN 7 AS " + alias + " NEXT YIELD " + alias + " AS renamed RETURN renamed AS " + alias,
                    "CALL { RETURN 7 AS \"amount\" } RETURN amount AS " + alias,
                    "FOR v IN [3,4] RETURN SUM(v) AS \"amount\" ORDER BY amount NEXT RETURN amount AS " + alias,
                    "RETURN 7 AS " + alias + " UNION RETURN 7 AS " + alias + " NEXT RETURN *",
                    "RETURN 1 AS seed NEXT RETURN 7 AS " + alias + " UNION RETURN 7 AS " + alias,
                    "RETURN VALUE { RETURN 7 AS " + alias + " } AS " + alias
                })
                {
                    using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
                    {
                        query
                    });
                    Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, query + ": " + await response.Content.ReadAsStringAsync());
                    var rows = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
                    Assert.AreEqual(1, rows.GetArrayLength(), query);
                    var bindings = rows[0].GetProperty("bindings");
                    Assert.AreEqual(1, bindings.EnumerateObject().Count(), query);
                    Assert.AreEqual(7, bindings.GetProperty(name).GetInt32(), query);
                }
            }
            foreach (string invalid in new[]
            {
                "RETURN 1 AS amount,2 AS \"amount\"",
                "RETURN 1 AS \"amount\",2 AS `amount`",
                "RETURN 7 AS \"display name\" NEXT YIELD \"missing name\" AS answer RETURN answer"
            })
            {
                using var rejected = await client.PostAsJsonAsync("/v1/graph/gql/query", new
                {
                    query = invalid
                });
                Assert.AreEqual(HttpStatusCode.BadRequest, rejected.StatusCode, invalid);
            }
            await host.StopAsync();
        }
    }
}
