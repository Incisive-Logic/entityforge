using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow('"')]
    [DataRow('`')]
    public async Task Gql_standard_delimited_property_mutations_preserve_names_and_replay(char delimiter)
    {
        _directory = NewDirectory();
        string key = delimiter + "a:b.c" + delimiter;
        var seed = new
        {
            statement = "LET initial=2 INSERT (a:QuotedWrite {" + key + ":initial})-[e:quotedWrite {" + key + ":initial+1}]->(b:QuotedTarget)",
            transactionId = Guid.NewGuid()
        };
        var direct = new
        {
            statement = "INSERT (:QuotedDirect {" + key + ":9})",
            transactionId = Guid.NewGuid()
        };
        var update = new
        {
            statement = "MATCH (n:QuotedWrite)-[e:quotedWrite]->() SET n." + key + "=n." + key + "+1,e." + key + "=e." + key + "+1 RETURN n." + key + "+e." + key + " AS answer",
            transactionId = Guid.NewGuid()
        };
        var remove = new
        {
            statement = "MATCH (n:QuotedWrite)-[e:quotedWrite]->() REMOVE n." + key + ",e." + key,
            transactionId = Guid.NewGuid()
        };
        var receipts = new Dictionary<Guid, string>();
        for (int restart = 0; restart < 2; restart++)
        {
            await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
                .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
            await host.StartAsync();
            using var client = Client(host);
            foreach (var request in new[] { seed, direct, update, remove })
            {
                using var response = await client.PostAsJsonAsync("/v1/graph/gql/update", request);
                string body = await response.Content.ReadAsStringAsync();
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, request.statement + ": " + body);
                if (restart == 0)
                {
                    receipts.Add(request.transactionId, body);
                }
                else
                {
                    Assert.AreEqual(receipts[request.transactionId], body);
                }
                if (request.transactionId == update.transactionId)
                {
                    using var result = JsonDocument.Parse(body);
                    Assert.AreEqual(7, result.RootElement.GetProperty("result").GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetInt32());
                }
            }
            using var verified = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = "MATCH (n:QuotedWrite)-[e:quotedWrite]->() RETURN NOT PROPERTY_EXISTS(n," + key + ") AND NOT PROPERTY_EXISTS(e," + key + ") AS answer"
            });
            Assert.AreEqual(HttpStatusCode.OK, verified.StatusCode, await verified.Content.ReadAsStringAsync());
            Assert.IsTrue((await verified.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetBoolean());
            using var directRead = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = "MATCH (n:QuotedDirect) RETURN COUNT(*)=1 AND SUM(n." + key + ")=9 AS answer"
            });
            Assert.AreEqual(HttpStatusCode.OK, directRead.StatusCode, await directRead.Content.ReadAsStringAsync());
            Assert.IsTrue((await directRead.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetBoolean());
            await host.StopAsync();
        }
    }

    [TestMethod]
    [DataRow('"')]
    [DataRow('`')]
    public async Task Gql_standard_delimited_property_names_preserve_exact_keys(char delimiter)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        Guid vertex = Guid.NewGuid();
        using var created = await client.PostAsJsonAsync("/v1/graph/vertices", new GraphVertexWriteRequest(vertex, ["QuotedProperties"]));
        Assert.AreEqual(HttpStatusCode.OK, created.StatusCode, await created.Content.ReadAsStringAsync());
        foreach (string key in new[] { "full name", "a.b", "café", "😀", "a\"b", "a`b", "a'b", "a--b", "a/*b*/", "MATCH" })
        {
            using var write = await client.PostAsJsonAsync($"/v1/graph/vertices/{vertex}/properties",
                new GraphPropertyWriteRequest(key, JsonSerializer.SerializeToElement(42)));
            Assert.AreEqual(HttpStatusCode.OK, write.StatusCode, await write.Content.ReadAsStringAsync());
            string quote = delimiter.ToString();
            string identifier = quote + key.Replace(quote, quote + quote, StringComparison.Ordinal) + quote;
            string query = "MATCH (n:QuotedProperties {" + identifier + ":42}) RETURN n." + identifier + "=42 AND PROPERTY_EXISTS(n," + identifier + ") AS answer";
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query
            });
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, query + ": " + await response.Content.ReadAsStringAsync());
            var rows = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
            Assert.AreEqual(1, rows.GetArrayLength());
            Assert.IsTrue(rows[0].GetProperty("bindings").GetProperty("answer").GetBoolean(), key);
        }
        using var unicode = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (n:QuotedProperties) RETURN n.café=42 AND PROPERTY_EXISTS(n,café) AS answer"
        });
        Assert.AreEqual(HttpStatusCode.OK, unicode.StatusCode, await unicode.Content.ReadAsStringAsync());
        Assert.IsTrue((await unicode.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0]
            .GetProperty("bindings").GetProperty("answer").GetBoolean());
        await host.StopAsync();
    }

    [TestMethod]
    public async Task Gql_standard_property_exists_checks_nodes_edges_and_persisted_removal()
    {
        _directory = NewDirectory();
        var seed = new
        {
            statement = "INSERT (:Presence {zero:0,flag:FALSE,emptyText:'',removed:1})-[:presenceLink {zero:0,removed:1}]->(:PresenceTarget)",
            transactionId = Guid.NewGuid()
        };
        var removal = new
        {
            statement = "MATCH (n:Presence)-[e:presenceLink]->() REMOVE n.removed,e.removed",
            transactionId = Guid.NewGuid()
        };
        for (int restart = 0; restart < 2; restart++)
        {
            await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
                .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
            await host.StartAsync();
            using var client = Client(host);
            using var seeded = await client.PostAsJsonAsync("/v1/graph/gql/update", seed);
            Assert.AreEqual(HttpStatusCode.OK, seeded.StatusCode, await seeded.Content.ReadAsStringAsync());
            using var removed = await client.PostAsJsonAsync("/v1/graph/gql/update", removal);
            Assert.AreEqual(HttpStatusCode.OK, removed.StatusCode, await removed.Content.ReadAsStringAsync());
            foreach (string query in new[]
            {
                "MATCH (n:Presence) RETURN PROPERTY_EXISTS(n,zero) AND PROPERTY_EXISTS(n,flag) AND PROPERTY_EXISTS(n,emptyText) AS answer",
                "MATCH (n:Presence) RETURN NOT PROPERTY_EXISTS(n,missing) AND NOT PROPERTY_EXISTS(n,removed) AS answer",
                "MATCH ()-[e:presenceLink]->() RETURN PROPERTY_EXISTS(e,zero) AND NOT PROPERTY_EXISTS(e,missing) AND NOT PROPERTY_EXISTS(e,removed) AS answer",
                "MATCH (n:Presence)-[e:presenceLink]->() FILTER PROPERTY_EXISTS(n,zero) AND PROPERTY_EXISTS(e,zero) RETURN COUNT(*)=1 AS answer"
            })
            {
                using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
                {
                    query
                });
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, query + ": " + await response.Content.ReadAsStringAsync());
                var rows = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
                Assert.AreEqual(1, rows.GetArrayLength(), query);
                Assert.IsTrue(rows[0].GetProperty("bindings").GetProperty("answer").GetBoolean(), query);
            }
            await host.StopAsync();
        }
    }
}
