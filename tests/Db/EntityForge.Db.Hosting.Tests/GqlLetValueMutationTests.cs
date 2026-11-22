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
    public async Task Gql_let_value_replay_retains_original_result_after_restart(bool explicitValue)
    {
        _directory = NewDirectory();
        string declaration = explicitValue ? "LET VALUE " : "LET ";
        var update = new
        {
            transactionId = Guid.NewGuid(),
            statement = "MATCH (n:LetReplay) SET n.amount=" + declaration +
                "previous=n.amount IN previous+3 END RETURN n.amount AS answer"
        };
        string? receipt = null;
        for (int restart = 0; restart < 2; restart++)
        {
            await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
                .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
            await host.StartAsync();
            using var client = Client(host);
            if (restart == 0)
            {
                using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
                {
                    statement = "INSERT (:LetReplay {amount:2})",
                    transactionId = Guid.NewGuid()
                });
                Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode, await seed.Content.ReadAsStringAsync());
            }
            for (int retry = 0; retry < 2; retry++)
            {
                using var response = await client.PostAsJsonAsync("/v1/graph/gql/update", update);
                string body = await response.Content.ReadAsStringAsync();
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, body);
                receipt ??= body;
                Assert.AreEqual(receipt, body);
                using var parsed = JsonDocument.Parse(body);
                Assert.AreEqual(5, parsed.RootElement.GetProperty("result").GetProperty("matches")[0]
                    .GetProperty("bindings").GetProperty("answer").GetInt32());
            }
            using var read = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = "MATCH (n:LetReplay) RETURN n.amount AS answer"
            });
            Assert.AreEqual(HttpStatusCode.OK, read.StatusCode, await read.Content.ReadAsStringAsync());
            var rows = (await read.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
            Assert.AreEqual(1, rows.GetArrayLength());
            Assert.AreEqual(restart == 0 ? 5 : 100, rows[0].GetProperty("bindings").GetProperty("answer").GetInt32());
            if (restart == 0)
            {
                using var later = await client.PostAsJsonAsync("/v1/graph/gql/update", new
                {
                    statement = "MATCH (n:LetReplay) SET n.amount=100",
                    transactionId = Guid.NewGuid()
                });
                Assert.AreEqual(HttpStatusCode.OK, later.StatusCode, await later.Content.ReadAsStringAsync());
            }
            await host.StopAsync();
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Gql_let_value_mutations_preserve_scope_and_atomicity(bool explicitValue)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        async Task<JsonElement> Update(string statement, HttpStatusCode expected = HttpStatusCode.OK)
        {
            if (explicitValue)
            {
                statement = statement.Replace("LET ", "LET VALUE ", StringComparison.Ordinal);
            }
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/update", new
            {
                statement,
                transactionId = Guid.NewGuid()
            });
            Assert.AreEqual(expected, response.StatusCode, await response.Content.ReadAsStringAsync());
            return await response.Content.ReadFromJsonAsync<JsonElement>();
        }
        async Task<int> Read(string query)
        {
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query
            });
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0]
                .GetProperty("bindings").GetProperty("answer").GetInt32();
        }
        await Update("INSERT (:LetMutation {amount:LET seedValue=2 IN seedValue END})");
        var visible = await Update("MATCH (n:LetMutation) SET n.amount=LET oldValue=n.amount IN oldValue+3 END RETURN *");
        var row = visible.GetProperty("result").GetProperty("matches")[0].GetProperty("bindings");
        CollectionAssert.AreEquivalent(new[] { "n" }, row.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.AreEqual(5, await Read("MATCH (n:LetMutation) RETURN n.amount AS answer"));
        await Update("MATCH (n:LetMutation) SET n.mark=TRUE SET n.amount=LET badValue=1/0 IN badValue END", HttpStatusCode.BadRequest);
        Assert.AreEqual(1, await Read("MATCH (n:LetMutation) WHERE n.mark IS NULL AND n.amount=5 RETURN COUNT(*) AS answer"));
        await Update("MATCH (n:LetMutation) SET n.mark=TRUE SET n.amount=LET localValue=1, dependentValue=localValue+1 IN dependentValue END", HttpStatusCode.BadRequest);
        Assert.AreEqual(1, await Read("MATCH (n:LetMutation) WHERE n.mark IS NULL AND n.amount=5 RETURN COUNT(*) AS answer"));
        await Update("MATCH (n:MissingLetMutation) SET n.amount=LET badValue=1/0 IN badValue END");
        await Update("MATCH (n:LetMutation) DELETE LET target=n IN target END SET n.mark=LET badValue=1/0 IN badValue END", HttpStatusCode.BadRequest);
        Assert.AreEqual(1, await Read("MATCH (n:LetMutation) RETURN COUNT(*) AS answer"));
        await Update("MATCH (n:LetMutation) DELETE LET target=n IN target END");
        Assert.AreEqual(0, await Read("MATCH (n:LetMutation) RETURN COUNT(*) AS answer"));
        await host.StopAsync();
    }
}
