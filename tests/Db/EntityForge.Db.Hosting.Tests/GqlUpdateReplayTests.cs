using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    public async Task Gql_mutation_subquery_receipts_replay_without_reevaluation_after_restart()
    {
        _directory = NewDirectory();
        string[] statements =
        [
            "INSERT (n:ReplayCreated {amount:VALUE { MATCH (s:ReplaySeed) RETURN COUNT(*) AS total }}) RETURN n.amount AS answer",
            "MATCH (n:ReplaySeed) SET n.score=VALUE { MATCH (c:ReplayCreated) RETURN COUNT(*) AS total } RETURN COUNT(*) AS answer",
            "DELETE VALUE { MATCH (n:ReplaySeed) WHERE n.rank=1 RETURN n } RETURN 7 AS answer"
        ];
        Guid[] identities = statements.Select(_ => Guid.NewGuid()).ToArray();
        var receipts = new string?[statements.Length];
        for (int restart = 0; restart < 2; restart++)
        {
            await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
                .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
            await host.StartAsync();
            using var client = Client(host);
            async Task Seed(string statement)
            {
                using var response = await client.PostAsJsonAsync("/v1/graph/gql/update", new
                {
                    statement
                });
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            }
            if (restart == 0)
            {
                await Seed("FOR rank IN [1,2] INSERT (:ReplaySeed {rank:rank})");
            }
            for (int index = 0; index < statements.Length; index++)
            {
                using var response = await client.PostAsJsonAsync("/v1/graph/gql/update", new
                {
                    statement = statements[index],
                    transactionId = identities[index]
                });
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
                string receipt = await response.Content.ReadAsStringAsync();
                receipts[index] ??= receipt;
                Assert.AreEqual(receipts[index], receipt);
            }
            if (restart == 0)
            {
                // A rerun would now create another summary, change score to 2,
                // and fail the delete subquery's singleton cardinality check.
                await Seed("INSERT (:ReplayCreated {amount:99}),(:ReplaySeed {rank:1}),(:ReplaySeed {rank:1})");
            }
            else
            {
                using var read = await client.PostAsJsonAsync("/v1/graph/gql/query", new
                {
                    query = "MATCH (n:ReplaySeed {rank:2}) RETURN n.score AS score, VALUE { MATCH (c:ReplayCreated) RETURN COUNT(*) AS total } AS created, VALUE { MATCH (s:ReplaySeed) RETURN COUNT(*) AS total } AS seeds"
                });
                Assert.AreEqual(HttpStatusCode.OK, read.StatusCode, await read.Content.ReadAsStringAsync());
                var bindings = (await read.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings");
                Assert.AreEqual(1, bindings.GetProperty("score").GetInt32());
                Assert.AreEqual(2, bindings.GetProperty("created").GetInt32());
                Assert.AreEqual(3, bindings.GetProperty("seeds").GetInt32());
            }
            await host.StopAsync();
        }
    }

    [TestMethod]
    public async Task Gql_standard_update_replays_frozen_selection_after_restart_and_rejects_changed_requests()
    {
        _directory = NewDirectory();
        Guid transactionId = Guid.NewGuid(), emptyId = Guid.NewGuid();
        string? receipt = null, emptyReceipt = null;
        const string statement = "MATCH (n:Person) WHERE n.rank=1 SET n.rank=n.rank+1";
        const string emptyStatement = "MATCH (n:Absent) SET n.mark=true";
        for (int restart = 0; restart < 2; restart++)
        {
            await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
                .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
            await host.StartAsync();
            using var client = Client(host);
            if (restart == 0)
            {
                using var initiallyEmpty = await client.PostAsJsonAsync("/v1/graph/gql/update", new
                {
                    statement = emptyStatement,
                    transactionId = emptyId
                });
                Assert.AreEqual(HttpStatusCode.OK, initiallyEmpty.StatusCode, await initiallyEmpty.Content.ReadAsStringAsync());
                emptyReceipt = await initiallyEmpty.Content.ReadAsStringAsync();
                using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
                {
                    statement = "INSERT (:Person {rank:1})"
                });
                Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode, await seed.Content.ReadAsStringAsync());
            }
            using var update = await client.PostAsJsonAsync("/v1/graph/gql/update", new
            {
                statement,
                transactionId
            });
            Assert.AreEqual(HttpStatusCode.OK, update.StatusCode, await update.Content.ReadAsStringAsync());
            receipt ??= await update.Content.ReadAsStringAsync();
            Assert.AreEqual(receipt, await update.Content.ReadAsStringAsync());
            using var empty = await client.PostAsJsonAsync("/v1/graph/gql/update", new
            {
                statement = emptyStatement,
                transactionId = emptyId
            });
            Assert.AreEqual(HttpStatusCode.OK, empty.StatusCode, await empty.Content.ReadAsStringAsync());
            emptyReceipt ??= await empty.Content.ReadAsStringAsync();
            Assert.AreEqual(emptyReceipt, await empty.Content.ReadAsStringAsync());
            using var changed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
            {
                statement = "MATCH (n:Person) SET n.rank=99",
                transactionId
            });
            Assert.AreEqual(HttpStatusCode.Conflict, changed.StatusCode);
            using var result = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = "MATCH (n:Person) RETURN n.rank AS answer"
            });
            Assert.AreEqual(2, (await result.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetInt32());
            if (restart == 0)
            {
                using var added = await client.PostAsJsonAsync("/v1/graph/gql/update", new
                {
                    statement = "INSERT (:Absent)"
                });
                Assert.AreEqual(HttpStatusCode.OK, added.StatusCode);
            }
            else
            {
                using var absent = await client.PostAsJsonAsync("/v1/graph/gql/query", new
                {
                    query = "MATCH (n:Absent) RETURN n.mark AS answer"
                });
                Assert.AreEqual(JsonValueKind.Null, (await absent.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").ValueKind);
            }
            await host.StopAsync();
        }
    }
}
