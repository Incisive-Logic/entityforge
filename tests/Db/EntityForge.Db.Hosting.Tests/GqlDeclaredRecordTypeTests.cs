using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("RECORD {} IS TYPED RECORD {}", true)]
    [DataRow("RECORD {age:3} IS TYPED RECORD {age INTEGER}", true)]
    [DataRow("RECORD {age:3} IS TYPED {age :: INTEGER NOT NULL}", true)]
    [DataRow("RECORD {age:'3'} IS TYPED RECORD {age INTEGER}", false)]
    [DataRow("RECORD {age:NULL} IS TYPED RECORD {age INTEGER}", true)]
    [DataRow("RECORD {age:NULL} IS TYPED RECORD {age INTEGER NOT NULL}", false)]
    [DataRow("RECORD {} IS TYPED RECORD {age INTEGER}", false)]
    [DataRow("RECORD {age:3,name:'Ada'} IS TYPED RECORD {age INTEGER}", false)]
    [DataRow("RECORD {age:3,name:'Ada'} IS TYPED RECORD {name STRING,age INTEGER}", true)]
    [DataRow("NULL IS TYPED RECORD {age INTEGER}", true)]
    [DataRow("NULL IS TYPED RECORD {age INTEGER} NOT NULL", false)]
    [DataRow("3 IS TYPED RECORD {age INTEGER}", false)]
    [DataRow("RECORD {items:[1,2]} IS TYPED RECORD {items LIST<INTEGER>[2]}", true)]
    [DataRow("RECORD {items:[1,2]} IS TYPED RECORD {items LIST<INTEGER>[1]}", false)]
    [DataRow("[RECORD {age:3}] IS TYPED LIST<RECORD {age INTEGER}>", true)]
    [DataRow("RECORD {child:RECORD {age:3}} IS TYPED RECORD {child RECORD {age INTEGER}}", true)]
    [DataRow("RECORD {age:3} IS TYPED STRING|RECORD {age INTEGER}", true)]
    [DataRow("RECORD {`display name`:3} IS TYPED RECORD {`display name` INTEGER}", true)]
    [DataRow("RECORD {café:3} IS TYPED RECORD {café INTEGER}", true)]
    [DataRow("RECORD {Age:3} IS TYPED RECORD {age INTEGER}", false)]
    [DataRow("RECORD {age:3} IS TYPED RECORD {age INTEGER|STRING}", true)]
    [DataRow("RECORD {`a;}:b`:3} IS TYPED RECORD {`a;}:b` INTEGER}", true)]
    [DataRow("CAST(RECORD {age:3} AS RECORD {age DEC}) IS TYPED RECORD {age DECIMAL}", true)]
    [DataRow("CAST([RECORD {age:3}] AS LIST<RECORD {age DEC}>) IS TYPED LIST<RECORD {age DECIMAL}>", true)]
    [DataRow("CAST(RECORD {child:RECORD {age:3}} AS RECORD {child RECORD {age FLOAT}}) IS TYPED RECORD {child RECORD {age FLOAT}}", true)]
    [DataRow("CAST(RECORD {age:NULL} AS RECORD {age INTEGER}) IS TYPED RECORD {age INTEGER}", true)]
    public async Task Gql_declared_record_types_match_exact_fields_and_nested_value_types(string expression, bool expected)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "RETURN " + expression + " AS answer"
        });
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual(expected, result.GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetBoolean());
    }

    [TestMethod]
    [DataRow("NULL IS TYPED RECORD {age INTEGER,age STRING}")]
    [DataRow("[] IS TYPED LIST<RECORD {age INTEGER,`age` STRING}>")]
    [DataRow("NULL IS TYPED RECORD {age UNKNOWN}")]
    [DataRow("CAST(RECORD {age:NULL} AS RECORD {age INTEGER NOT NULL})")]
    [DataRow("CAST(RECORD {} AS RECORD {age INTEGER})")]
    [DataRow("CAST(RECORD {age:3,extra:4} AS RECORD {age INTEGER})")]
    [DataRow("CAST(RECORD {age:'bad'} AS RECORD {age INTEGER})")]
    [DataRow("CAST(RECORD {items:[1,2]} AS RECORD {items INTEGER LIST[1]})")]
    public async Task Gql_declared_record_invalid_types_and_casts_reject_before_update_publication(string expression)
    {
        _directory = NewDirectory();
        var seed = new
        {
            statement = "INSERT (:TypedRecord {amount:CAST(RECORD {amount:3} AS RECORD {amount DEC}).amount})",
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
            using var rejected = await client.PostAsJsonAsync("/v1/graph/gql/update", new
            {
                statement = "MATCH (n:TypedRecord) SET n.amount=9 RETURN " + expression + " AS rejected",
                transactionId = Guid.NewGuid()
            });
            Assert.AreEqual(HttpStatusCode.BadRequest, rejected.StatusCode, await rejected.Content.ReadAsStringAsync());
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = "MATCH (n:TypedRecord) WHERE RECORD {amount:n.amount} IS TYPED RECORD {amount DEC} RETURN n.amount AS answer"
            });
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            var rows = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
            Assert.AreEqual(1, rows.GetArrayLength());
            Assert.AreEqual(3m, rows[0].GetProperty("bindings").GetProperty("answer").GetDecimal());
            await host.StopAsync();
        }
    }
}
