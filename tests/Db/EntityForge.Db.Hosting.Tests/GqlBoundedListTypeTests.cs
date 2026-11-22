using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("[1,2] IS TYPED LIST<INTEGER>[2]", "True")]
    [DataRow("CAST(3 AS DEC) IS TYPED DECIMAL", "True")]
    [DataRow("CAST(3 AS DECIMAL) IS TYPED DEC", "True")]
    [DataRow("CAST(3 AS DECIMAL) IS TYPED INTEGER|DEC", "True")]
    [DataRow("CAST([1,2] AS LIST<DEC>) IS TYPED DEC LIST", "True")]
    [DataRow("CAST([[1],[2]] AS DEC LIST LIST) IS TYPED DECIMAL LIST LIST", "True")]
    [DataRow("NULL IS TYPED DEC", "True")]
    [DataRow("CAST(2e0 AS DEC) = 2", "True")]
    [DataRow("[1,2] IS TYPED LIST<INTEGER>[1]", "False")]
    [DataRow("[1] IS TYPED INTEGER LIST[2]", "True")]
    [DataRow("[1,2] IS NOT TYPED INTEGER ARRAY[1]", "True")]
    [DataRow("[1,'a'] IS TYPED ARRAY[2]", "True")]
    [DataRow("[1,'a'] IS TYPED LIST<INTEGER|STRING>[2]", "True")]
    [DataRow("[[1,2],[3]] IS TYPED LIST<LIST<INTEGER>[2]>[2]", "True")]
    [DataRow("[[1,2],[3]] IS TYPED LIST<LIST<INTEGER>[1]>[2]", "False")]
    [DataRow("[[1],[2]] IS TYPED INTEGER ARRAY[1] ARRAY[2]", "True")]
    [DataRow("[NULL] IS TYPED INTEGER NOT NULL LIST[1]", "False")]
    [DataRow("[NULL] IS TYPED INTEGER LIST[1] NOT NULL", "True")]
    [DataRow("NULL IS TYPED INTEGER LIST[1]", "True")]
    [DataRow("NULL IS TYPED INTEGER LIST[1] NOT NULL", "False")]
    [DataRow("[1,2] IS TYPED LIST<INTEGER>[2147483648]", "True")]
    [DataRow("[1,2] IS TYPED LIST<INTEGER>[0x2]", "True")]
    [DataRow("[1,2] IS TYPED INTEGER ARRAY[0b10]", "True")]
    [DataRow("[1,2] IS TYPED ARRAY<INTEGER>[0o2]", "True")]
    [DataRow("[1,2] IS TYPED LIST<STRING>[1]|LIST<INTEGER>[2]", "True")]
    [DataRow("NULL IS TYPED LIST<STRING>[1] NOT NULL|LIST<INTEGER>[2] NOT NULL", "False")]
    [DataRow("[] IS TYPED LIST<NOTHING>[1]", "True")]
    [DataRow("CARDINALITY(CAST([1,2] AS INTEGER LIST[2]))", "2")]
    [DataRow("CAST([1,2] AS LIST<DECIMAL>) IS TYPED LIST<DECIMAL>", "True")]
    [DataRow("CAST([3,1,2] AS LIST<DECIMAL>) = [3,1,2]", "True")]
    [DataRow("CAST([[3],[1,2]] AS LIST<LIST<FLOAT>>) = [[3],[1,2]]", "True")]
    [DataRow("CAST([1,2.5] AS LIST<FLOAT>) IS TYPED LIST<FLOAT>", "True")]
    [DataRow("CAST([[1],[2,NULL]] AS LIST<LIST<DECIMAL>[2]>[2]) IS TYPED LIST<LIST<DECIMAL>[2]>[2]", "True")]
    [DataRow("CAST([NULL,1] AS DECIMAL LIST NOT NULL) IS TYPED DECIMAL LIST NOT NULL", "True")]
    [DataRow("CAST([NULL,1] AS DECIMAL NOT NULL LIST)", "ERROR")]
    [DataRow("CAST([1,2] AS DECIMAL LIST[1])", "ERROR")]
    [DataRow("CAST([1,'bad'] AS DECIMAL LIST)", "ERROR")]
    [DataRow("CAST([1] AS LIST<LIST<DECIMAL>>)", "ERROR")]
    [DataRow("CAST([1,2] AS INTEGER LIST[1])", "ERROR")]
    [DataRow("CAST([1,'a'] AS INTEGER LIST[2])", "ERROR")]
    [DataRow("CAST(NULL AS INTEGER LIST[1] NOT NULL)", "ERROR")]
    [DataRow("[1] IS TYPED LIST<INTEGER>[-1]", "ERROR")]
    [DataRow("[1] IS TYPED LIST<INTEGER>[1.5]", "ERROR")]
    public async Task Gql_list_type_maximum_checks_lengths_elements_and_nullability(string expression, string expected)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = $"RETURN {expression} AS answer"
        });
        if (expected == "ERROR")
        {
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, await response.Content.ReadAsStringAsync());
            return;
        }
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual(expected, result.GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").ToString());
    }

    [TestMethod]
    [DataRow("INTEGER")]
    [DataRow("DECIMAL")]
    [DataRow("DEC")]
    [DataRow("FLOAT")]
    public async Task Gql_list_type_maximum_survives_update_replay_and_rejects_cast_atomically(string targetType)
    {
        _directory = NewDirectory();
        var seed = new
        {
            statement = "INSERT (:TypedList {items:CAST([1,2] AS LIST<" + targetType + ">[2])})",
            transactionId = Guid.NewGuid()
        };
        string? receipt = null;
        for (int restart = 0; restart < 2; restart++)
        {
            await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
                .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
            await host.StartAsync();
            using var client = Client(host);
            using var inserted = await client.PostAsJsonAsync("/v1/graph/gql/update", seed);
            string body = await inserted.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, inserted.StatusCode, body);
            receipt ??= body;
            Assert.AreEqual(receipt, body);
            foreach (string invalid in new[] { "[1,2] AS INTEGER LIST[1]", "[1,'bad'] AS " + targetType + " LIST" })
            {
                using var rejected = await client.PostAsJsonAsync("/v1/graph/gql/update", new
                {
                    statement = "MATCH (n:TypedList) SET n.items=[9] RETURN CAST(" + invalid + ") AS bad",
                    transactionId = Guid.NewGuid()
                });
                Assert.AreEqual(HttpStatusCode.BadRequest, rejected.StatusCode, await rejected.Content.ReadAsStringAsync());
            }
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = "MATCH (n:TypedList) WHERE n.items IS TYPED " + targetType + " ARRAY[2] RETURN CARDINALITY(n.items) AS answer"
            });
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            var rows = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
            Assert.AreEqual(1, rows.GetArrayLength());
            Assert.AreEqual(2, rows[0].GetProperty("bindings").GetProperty("answer").GetInt32());
            await host.StopAsync();
        }
    }
}
