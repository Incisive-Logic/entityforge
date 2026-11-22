using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("CAST(9223372036854775807M AS INT64) = 9223372036854775807")]
    [DataRow("CAST(-9223372036854775808M AS SIGNED INTEGER64) = -9223372036854775808")]
    [DataRow("42 IS TYPED STRING|INT64")]
    [DataRow("42 IS TYPED STRING|INTEGER64")]
    [DataRow("1e0 IS TYPED STRING|FLOAT64")]
    [DataRow("CAST([1,2] AS LIST<FLOAT64>) IS TYPED FLOAT64 LIST NOT NULL")]
    [DataRow("CAST(RECORD {n:1e0} AS RECORD {n INT64}) IS TYPED RECORD {n INTEGER64}")]
    [DataRow("NULL IS NOT TYPED INT64 NOT NULL")]
    [DataRow("NULL IS TYPED FLOAT64")]
    [DataRow("CAST(42.0M AS INTEGER) = 42")]
    [DataRow("CAST(-42.0M AS INTEGER) = -42")]
    [DataRow("CAST(42e0 AS INTEGER) IS TYPED INTEGER NOT NULL")]
    [DataRow("CAST(-0e0 AS INTEGER) = 0")]
    [DataRow("CAST(9223372036854775807M AS INTEGER) = 9223372036854775807")]
    [DataRow("CAST(-9223372036854775808M AS INTEGER) = -9223372036854775808")]
    [DataRow("CAST(-9223372036854775808e0 AS INTEGER) = -9223372036854775808")]
    [DataRow("CAST(9007199254740992e0 AS INTEGER) = 9007199254740992")]
    [DataRow("CAST(1.5e0 AS DECIMAL) = 1.5M")]
    [DataRow("CAST(-1.5e0 AS DECIMAL) IS TYPED DECIMAL")]
    [DataRow("CAST(0e0 AS DECIMAL) = 0M")]
    [DataRow("CAST(3.7252902984619140625e-9 AS DECIMAL) = 0.0000000037252902984619140625M")]
    [DataRow("CAST([[1.0M],[2e0]] AS LIST<LIST<INTEGER>>) = [[1],[2]]")]
    [DataRow("CAST([NULL,1.0M] AS LIST<INTEGER>) IS TYPED LIST<INTEGER>")]
    public async Task Gql_exact_numeric_cast_preserves_represented_value(string expression)
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
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        Assert.IsTrue((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0]
            .GetProperty("bindings").GetProperty("answer").GetBoolean());
    }

    [TestMethod]
    [DataRow("CAST(9223372036854775808M AS INT64)")]
    [DataRow("CAST(1.5M AS SIGNED INTEGER64)")]
    [DataRow("CAST(1.5M AS INTEGER)")]
    [DataRow("CAST(-1.5e0 AS INTEGER)")]
    [DataRow("CAST(9223372036854775808M AS INTEGER)")]
    [DataRow("CAST(-9223372036854775809M AS INTEGER)")]
    [DataRow("CAST(9223372036854775807e0 AS INTEGER)")]
    [DataRow("CAST(0.1e0 AS DECIMAL)")]
    [DataRow("CAST(1.86264514923095703125e-9 AS DECIMAL)")]
    [DataRow("CAST(79228162514264337593543950336e0 AS DECIMAL)")]
    [DataRow("CAST(1e300 AS DECIMAL)")]
    [DataRow("CAST(0e0/0e0 AS INTEGER)")]
    [DataRow("CAST(1e0/0e0 AS DECIMAL)")]
    [DataRow("CAST(-1e0/0e0 AS INTEGER)")]
    [DataRow("CAST([1.0M,2.5M] AS LIST<INTEGER>)")]
    public async Task Gql_exact_numeric_cast_rejects_loss_or_overflow(string expression)
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
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, await response.Content.ReadAsStringAsync());
        Assert.AreEqual("22003", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("gqlStatus").GetString());
    }

    [TestMethod]
    [DataRow("INTEGER", "[1.0M,2e0]", "[1,2]")]
    [DataRow("DECIMAL", "[1.5e0,2.5e0]", "[1.5M,2.5M]")]
    public async Task Gql_exact_numeric_cast_replays_and_precision_failure_rolls_back(string target, string source, string expected)
    {
        _directory = NewDirectory();
        var seed = new
        {
            statement = "INSERT (:ExactCast {items:CAST(" + source + " AS LIST<" + target + ">)})",
            transactionId = Guid.NewGuid()
        };
        var invalid = new
        {
            statement = "MATCH (n:ExactCast) SET n.items=[1.0M,1.5M] RETURN CAST(n.items AS LIST<INTEGER>) AS bad",
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
            using var rejected = await client.PostAsJsonAsync("/v1/graph/gql/update", invalid);
            Assert.AreEqual(HttpStatusCode.BadRequest, rejected.StatusCode, await rejected.Content.ReadAsStringAsync());
            Assert.AreEqual("22003", (await rejected.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("gqlStatus").GetString());
            using var verified = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = "MATCH (n:ExactCast) RETURN n.items = " + expected + " AND n.items IS TYPED LIST<" + target + "> AS answer"
            });
            Assert.AreEqual(HttpStatusCode.OK, verified.StatusCode, await verified.Content.ReadAsStringAsync());
            var rows = (await verified.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
            Assert.AreEqual(1, rows.GetArrayLength());
            Assert.IsTrue(rows[0].GetProperty("bindings").GetProperty("answer").GetBoolean());
            await host.StopAsync();
        }
    }
}
