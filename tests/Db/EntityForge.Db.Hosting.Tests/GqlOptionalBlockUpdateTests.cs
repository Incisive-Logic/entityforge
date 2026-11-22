using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    public async Task Gql_optional_blocks_preserve_update_atomicity_and_replay_after_restart()
    {
        _directory = NewDirectory();
        var request = new
        {
            statement = "MATCH (a:OptionalBlock) OPTIONAL { MATCH (a)-[:link]->(b) MATCH (b)-[:link]->(c) } SET a.visits=a.visits+1 RETURN a.visits AS answer",
            transactionId = Guid.NewGuid()
        };
        for (int attempt = 0; attempt < 2; attempt++)
        {
            await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
                .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
            await host.StartAsync();
            using var client = Client(host);
            if (attempt == 0)
            {
                using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
                {
                    statement = "INSERT (a:OptionalBlock {visits:0})-[:link]->(:OptionalBlock {visits:0})"
                });
                Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode);
            }
            using var updated = await client.PostAsJsonAsync("/v1/graph/gql/update", request);
            Assert.AreEqual(HttpStatusCode.OK, updated.StatusCode, await updated.Content.ReadAsStringAsync());
            var rows = (await updated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result").GetProperty("matches");
            Assert.AreEqual(2, rows.GetArrayLength());
            Assert.IsTrue(rows.EnumerateArray().All(row => row.GetProperty("bindings").GetProperty("answer").GetInt32() == 1));
            using var failed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
            {
                statement = "MATCH (a:OptionalBlock) OPTIONAL { MATCH (a) MATCH (:OptionalBlock) } SET a.visits=99 RETURN 1/0 AS answer"
            });
            Assert.IsFalse(failed.IsSuccessStatusCode);
            using var unchanged = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = "MATCH (a:OptionalBlock) RETURN SUM(a.visits) AS answer"
            });
            Assert.AreEqual(HttpStatusCode.OK, unchanged.StatusCode);
            Assert.AreEqual(2, (await unchanged.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetInt32());
            await host.StopAsync();
        }
    }
}
