using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    public async Task Gql_standard_stored_scalar_arrays_preserve_types_and_restart()
    {
        _directory = NewDirectory();
        for (int restart = 0; restart < 2; restart++)
        {
            await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
                .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
            await host.StartAsync();
            using var client = Client(host);
            if (restart == 0)
            {
                foreach (string statement in new[]
                {
                    "INSERT (n:Arrays {ints:[1,2,3],floats:[1e0,2.5e0],decimals:[1.0,2.5],words:['a','b'],flags:[true,false],empty:[]})",
                    "MATCH (n:Arrays) SET n.ints=n.ints||[4]",
                    "INSERT (n:ArrayKey {items:[1,2],hits:1})",
                    "MATCH (n:ArrayKey {items:[1.0,2.0]}) SET n.hits=n.hits+1"
                })
                {
                    using var response = await client.PostAsJsonAsync("/v1/graph/gql/update", new
                    {
                        statement
                    });
                    Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, statement + ": " + await response.Content.ReadAsStringAsync());
                }
            }
            foreach ((string query, string expected) in new[]
            {
                ("MATCH (n:Arrays) RETURN n.ints AS answer", "[1,2,3,4]"),
                ("MATCH (n:Arrays) RETURN n.floats IS TYPED LIST<FLOAT NOT NULL> AS answer", "true"),
                ("MATCH (n:Arrays) RETURN n.words AS answer", "[\"a\",\"b\"]"),
                ("MATCH (n:Arrays) RETURN n.flags AS answer", "[true,false]"),
                ("MATCH (n:Arrays) RETURN size(n.empty) AS answer", "0"),
                ("MATCH (n:Arrays) RETURN n.decimals IS TYPED LIST<DECIMAL> AS answer", "true"),
                ("MATCH (n:Arrays) FOR i IN n.ints RETURN sum(i) AS answer", "10"),
                ("MATCH (n:ArrayKey) RETURN sum(n.hits) AS answer", "2"),
                ("MATCH (n:Arrays {ints:[1.0,2,3,4]}) RETURN size(n.ints) AS answer", "4")
            })
            {
                using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
                {
                    query
                });
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, query + ": " + await response.Content.ReadAsStringAsync());
                var result = await response.Content.ReadFromJsonAsync<JsonElement>();
                using var wanted = JsonDocument.Parse(expected);
                Assert.AreEqual(ScalarKey(wanted.RootElement), ScalarKey(result.GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer")), query);
            }
            await host.StopAsync();
        }
    }

    [TestMethod]
    [DataRow("[1,null]")]
    [DataRow("[1,'two']")]
    [DataRow("[[1],[2]]")]
    [DataRow("[RECORD {item:1}]")]
    public async Task Gql_standard_stored_arrays_reject_invalid_elements_atomically(string expression)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var response = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (n:Arrays) SET n.items=" + expression
        });
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, await response.Content.ReadAsStringAsync());
        using var query = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (n:Arrays) RETURN count(*) AS answer"
        });
        var result = await query.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual(0, result.GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetInt32());
        await host.StopAsync();
    }

    [TestMethod]
    public void Stored_array_values_own_elements_and_roundtrip_native_codec()
    {
        DatabaseValue[] values = [new DatabaseValue.Int64(1), new DatabaseValue.Int64(2)];
        var array = new DatabaseValue.Array(values);
        values[0] = new DatabaseValue.Int64(9);
        Assert.AreEqual(new DatabaseValue.Int64(1), array.Value[0]);
        Guid transaction = Guid.NewGuid();
        var id = new GraphVertexId(Guid.NewGuid());
        var decoded = GraphTransactionCodec.Decode(transaction, GraphTransactionCodec.Encode(new(transaction,
            [new CreateVertex(id, ["Arrays"]), new SetVertexProperty(id, "values", array)])));
        Assert.AreEqual(array, ((SetVertexProperty)decoded.Mutations[1]).Value);
        Assert.AreEqual(array.GetHashCode(), ((SetVertexProperty)decoded.Mutations[1]).Value.GetHashCode());
    }
}
