using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("SHORTEST 2", false, "[3,4]")]
    [DataRow("SHORTEST 2 GROUPS", false, "[3,4,4]")]
    [DataRow("SHORTEST 2 GROUPS", true, "[4,4,5,5,5,5]")]
    public async Task Gql_compound_shortest_walks_preserve_imports_update_replay_and_rollback(
        string selector, bool twoSegments, string expected)
    {
        _directory = NewDirectory();
        var seed = new
        {
            statement = "INSERT (a:CompoundStart)-[:enter]->(b),(b)-[:step]->(c)," +
                "(b)-[:step]->(d),(b)-[:step]->(d),(d)-[:step]->(d),(d)-[:step]->(c)," +
                "(c)-[:exit]->(z:CompoundEnd)",
            transactionId = Guid.NewGuid()
        };
        string middle = twoSegments ? "-[e1:step]->+(m)-[e2:step]->+" : "-[e:step]->+";
        string pattern = "MATCH REPEATABLE ELEMENTS p=" + selector +
            " (a:CompoundStart)-[:enter]->(b)" + middle + "(c)-[:exit]->(z:CompoundEnd)";
        var update = new
        {
            statement = pattern + " INSERT (:CompoundReceipt {length:PATH_LENGTH(p)}) RETURN PATH_LENGTH(p) AS answer",
            transactionId = Guid.NewGuid()
        };
        int[] wanted = JsonSerializer.Deserialize<int[]>(expected)!;
        string? receipt = null;
        for (int restart = 0; restart < 2; restart++)
        {
            await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
                .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
            await host.StartAsync();
            using var client = Client(host);
            using var seeded = await client.PostAsJsonAsync("/v1/graph/gql/update", seed);
            Assert.AreEqual(HttpStatusCode.OK, seeded.StatusCode, await seeded.Content.ReadAsStringAsync());
            using var imported = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = "MATCH (a:CompoundStart) CALL (a) { " + pattern +
                    " RETURN PATH_LENGTH(p) AS length } RETURN length AS answer"
            });
            Assert.AreEqual(HttpStatusCode.OK, imported.StatusCode, await imported.Content.ReadAsStringAsync());
            CollectionAssert.AreEqual(wanted, (await imported.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")
                .EnumerateArray().Select(row => row.GetProperty("bindings").GetProperty("answer").GetInt32()).Order().ToArray());
            using var changed = await client.PostAsJsonAsync("/v1/graph/gql/update", update);
            Assert.AreEqual(HttpStatusCode.OK, changed.StatusCode, await changed.Content.ReadAsStringAsync());
            string currentReceipt = await changed.Content.ReadAsStringAsync();
            receipt ??= currentReceipt;
            Assert.AreEqual(receipt, currentReceipt);
            using var written = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = "MATCH (n:CompoundReceipt) RETURN n.length AS answer"
            });
            Assert.AreEqual(HttpStatusCode.OK, written.StatusCode);
            CollectionAssert.AreEqual(wanted, (await written.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")
                .EnumerateArray().Select(row => row.GetProperty("bindings").GetProperty("answer").GetInt32()).Order().ToArray());
            using var failed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
            {
                statement = pattern + " INSERT (:CompoundRollback) RETURN CARDINALITY(TRIM([1],-1)) AS invalid"
            });
            Assert.AreEqual(HttpStatusCode.BadRequest, failed.StatusCode, await failed.Content.ReadAsStringAsync());
            using var rollback = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = "MATCH (n:CompoundRollback) RETURN COUNT(*) AS answer"
            });
            Assert.AreEqual(HttpStatusCode.OK, rollback.StatusCode);
            Assert.AreEqual(0, (await rollback.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0]
                .GetProperty("bindings").GetProperty("answer").GetInt32());
            await host.StopAsync();
        }
    }

    [TestMethod]
    [DataRow("SHORTEST 2", false, false)]
    [DataRow("SHORTEST 3", false, false)]
    [DataRow("SHORTEST 2 GROUPS", true, false)]
    [DataRow("SHORTEST 3 GROUPS", true, false)]
    [DataRow("SHORTEST 2", false, true)]
    [DataRow("SHORTEST 3", false, true)]
    [DataRow("SHORTEST 2 GROUPS", true, true)]
    [DataRow("SHORTEST 3 GROUPS", true, true)]
    public async Task Gql_compound_shortest_walks_match_finite_two_segment_bags(string selector, bool groups, bool bindMiddle)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (a:MultiStart)-[:step]->(b:MultiMiddle),(a)-[:step]->(b)," +
                "(b)-[:step]->(b),(b)-[:step]->(c),(c)-[:step]->(c),(c)-[:step]->(z:MultiEnd)"
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode, await seed.Content.ReadAsStringAsync());
        async Task<string[]> Paths(string maximum)
        {
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = (bindMiddle ? "MATCH (m:MultiMiddle) " : "") + "MATCH REPEATABLE ELEMENTS p=" + selector +
                    " (a:MultiStart)-[e1:step]->{1," + maximum + "}(m)-[e2:step]->{1," + maximum +
                    "}(z:MultiEnd) RETURN p, PATH_LENGTH(p) AS length"
            });
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches").EnumerateArray()
                .Select(row => row.GetProperty("bindings").GetProperty(groups ? "p" : "length").GetRawText())
                .Order(StringComparer.Ordinal).ToArray();
        }
        // The first three complete length groups end at length five, so no
        // contributing segment exceeds four hops. Tied ANY paths need not
        // share identities; GROUPS must preserve the complete path bag.
        string[] expected = await Paths("4");
        Assert.IsTrue(expected.Length > 0);
        CollectionAssert.AreEqual(expected, await Paths(""));
        await host.StopAsync();
    }

    [TestMethod]
    [DataRow("SHORTEST 2", "[3,4]")]
    [DataRow("SHORTEST 3", "[3,4,4]")]
    [DataRow("SHORTEST 2 GROUPS", "[3,4,4]")]
    [DataRow("SHORTEST 3 GROUPS", "[3,4,4,5,5]")]
    public async Task Gql_compound_shortest_walks_select_complete_paths_with_fixed_exteriors(string selector, string expected)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (a:CompoundStart)-[:enter]->(b),(b)-[:step]->(c)," +
                "(b)-[:step]->(d),(b)-[:step]->(d),(d)-[:step]->(d),(d)-[:step]->(c)," +
                "(c)-[:exit]->(z:CompoundEnd)"
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode, await seed.Content.ReadAsStringAsync());
        foreach (string maximum in new[] { "4", "" })
        {
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = "MATCH REPEATABLE ELEMENTS p=" + selector +
                    " (a:CompoundStart)-[:enter]->(b)-[e:step]->{1," + maximum +
                    "}(c)-[:exit]->(z:CompoundEnd) RETURN PATH_LENGTH(p) AS length"
            });
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            int[] lengths = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches").EnumerateArray()
                .Select(row => row.GetProperty("bindings").GetProperty("length").GetInt32()).Order().ToArray();
            CollectionAssert.AreEqual(JsonSerializer.Deserialize<int[]>(expected)!, lengths);
        }
        await host.StopAsync();
    }
}
