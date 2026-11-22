using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("SHORTEST 0", "[]")]
    [DataRow("SHORTEST 0 GROUPS", "[]")]
    [DataRow("SHORTEST 2", "[1,2]")]
    [DataRow("SHORTEST 3", "[1,2,2]")]
    [DataRow("SHORTEST 2 GROUPS", "[1,2,2]")]
    [DataRow("SHORTEST 3 GROUPS", "[1,2,2,3,3]")]
    public async Task Gql_counted_shortest_walk_oracle_distinguishes_paths_from_length_groups(string selector, string expected)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (a:CountSource)-[:step]->(z:CountTarget)," +
                "(a)-[:step]->(b),(a)-[:step]->(b),(b)-[:step]->(b),(b)-[:step]->(z)"
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode, await seed.Content.ReadAsStringAsync());
        // One direct path, two paths through the parallel edges, then two
        // paths for each additional traversal of the middle self-loop.
        foreach (string maximum in new[] { "4", "" })
        {
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = "MATCH REPEATABLE ELEMENTS p=" + selector +
                    " (a:CountSource)-[e:step]->{1," + maximum + "}(z:CountTarget) RETURN PATH_LENGTH(p) AS length"
            });
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            int[] lengths = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches").EnumerateArray()
                .Select(row => row.GetProperty("bindings").GetProperty("length").GetInt32()).Order().ToArray();
            CollectionAssert.AreEqual(JsonSerializer.Deserialize<int[]>(expected)!, lengths);
        }
        await host.StopAsync();
    }

    [TestMethod]
    [DataRow("MATCH (a:WalkSource) MATCH REPEATABLE ELEMENTS p=ALL SHORTEST (a)-[e:step]->+(b:WalkTarget) RETURN SUM(PATH_LENGTH(p)) AS answer", 1)]
    [DataRow("MATCH (b:WalkTarget) MATCH REPEATABLE ELEMENTS p=ALL SHORTEST (a:WalkSource)-[e:step]->+(b) RETURN SUM(PATH_LENGTH(p)) AS answer", 1)]
    [DataRow("MATCH (a:WalkSource) CALL (a) { MATCH REPEATABLE ELEMENTS p=ALL SHORTEST (a)-[e:step]->+(b:WalkTarget) RETURN PATH_LENGTH(p) AS length } RETURN SUM(length) AS answer", 1)]
    [DataRow("MATCH REPEATABLE ELEMENTS p=ALL SHORTEST (a:WalkSource)-[e:step]->+(b:WalkTarget) WHERE PATH_LENGTH(p)>1 RETURN COUNT(*) AS answer", 0)]
    [DataRow("MATCH REPEATABLE ELEMENTS p=ALL SHORTEST (a:WalkSource)-[e:step {active:TRUE}]->+(b:WalkTarget) RETURN SUM(PATH_LENGTH(p)) AS answer", 2)]
    [DataRow("MATCH (a:WalkSource) CALL (a) { MATCH REPEATABLE ELEMENTS p=SHORTEST 3 (a)-[e:step]->+(b:WalkTarget) RETURN PATH_LENGTH(p) AS length } RETURN SUM(length) AS answer", 6)]
    [DataRow("MATCH (b:WalkTarget) MATCH REPEATABLE ELEMENTS p=SHORTEST 3 GROUPS (a:WalkSource)-[e:step]->+(b) RETURN SUM(PATH_LENGTH(p)) AS answer", 6)]
    public async Task Gql_unbounded_shortest_walks_compose_with_imports_and_filter_stages(string query, int expected)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (a:WalkSource)-[:step {active:FALSE}]->(z:WalkTarget)," +
                "(a)-[:step {active:TRUE}]->(b),(b)-[:step {active:TRUE}]->(z),(b)-[:step {active:TRUE}]->(b)"
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode, await seed.Content.ReadAsStringAsync());
        using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query
        });
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var rows = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
        Assert.AreEqual(1, rows.GetArrayLength());
        Assert.AreEqual(expected, rows[0].GetProperty("bindings").GetProperty("answer").GetInt32());
        await host.StopAsync();
    }

    [TestMethod]
    [DataRow(0, 1)]
    [DataRow(1, 1)]
    [DataRow(3, 1)]
    [DataRow(0, 2)]
    [DataRow(1, 3)]
    [DataRow(3, 2)]
    public async Task Gql_unbounded_shortest_walks_match_exhaustive_finite_path_bags(int minimum, int groups)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (a:WalkOracle)-[:edge]->(a),(a)-[:edge]->(b:WalkOracle)," +
                "(a)-[:edge]->(b),(b)-[:edge]->(c:WalkOracle),(c)-[:edge]->(a)"
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode, await seed.Content.ReadAsStringAsync());
        // With three vertices, each additional shortest length group can
        // require at most three more hops. The finite route enumerates without
        // the unbounded executor's vertex-depth pruning.
        async Task<string[]> Paths(string maximum)
        {
            string query = "MATCH REPEATABLE ELEMENTS p=SHORTEST " + groups + " GROUPS (a:WalkOracle)-[e:edge]->{" +
                minimum + "," + maximum + "}(b:WalkOracle) RETURN p";
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query
            });
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches").EnumerateArray()
                .Select(row => row.GetProperty("bindings").GetProperty("p").GetRawText()).Order(StringComparer.Ordinal).ToArray();
        }
        CollectionAssert.AreEqual(await Paths((minimum + groups * 3 - 1).ToString(System.Globalization.CultureInfo.InvariantCulture)), await Paths(""));
        await host.StopAsync();
    }

    [TestMethod]
    [DataRow("ALL SHORTEST", 1)]
    [DataRow("SHORTEST 2 GROUPS", 2)]
    [DataRow("SHORTEST 3", 3)]
    public async Task Gql_unbounded_shortest_walks_preserve_zero_paths_edge_reuse_and_update_replay(string selector, int count)
    {
        _directory = NewDirectory();
        var seed = new
        {
            statement = "INSERT (:WalkPair {name:'A'})-[:pair]->(:WalkPair {name:'B'})",
            transactionId = Guid.NewGuid()
        };
        const string start = "(a:WalkPair {name:'A'})";
        const string end = "(b:WalkPair {name:'A'})";
        string prefix = "MATCH REPEATABLE ELEMENTS p=" + selector + " ";
        var update = new
        {
            statement = prefix + start + "-[e:pair]-+" + end + " SET b.checked=TRUE RETURN PATH_LENGTH(p) AS answer",
            transactionId = Guid.NewGuid()
        };
        string? receipt = null;
        for (int restart = 0; restart < 2; restart++)
        {
            await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
                .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
            await host.StartAsync();
            using var client = Client(host);
            using var seeded = await client.PostAsJsonAsync("/v1/graph/gql/update", seed);
            Assert.AreEqual(HttpStatusCode.OK, seeded.StatusCode, await seeded.Content.ReadAsStringAsync());
            foreach (var (query, expected) in new[]
            {
                (prefix + start + "-[e:pair]-*" + end + " RETURN SUM(PATH_LENGTH(p)) AS answer", count * (count - 1)),
                (prefix + start + "-[e:pair]-+" + end + " RETURN SUM(PATH_LENGTH(p)) AS answer", count * (count + 1)),
                (prefix + start + "-[e:pair]-{3,}" + end + " RETURN SUM(PATH_LENGTH(p)) AS answer", count * (count + 3)),
                (prefix + start + "-[e:pair]-*(b) RETURN SUM(PATH_LENGTH(p)) AS answer", 2 * count * count - count),
                (prefix + start + "-[e:pair]->+" + end + " RETURN COUNT(*) AS answer", 0),
                (prefix + start + "-[e:pair]-+" + end + " RETURN CASE WHEN CURRENT_DATE=DATE() THEN SUM(PATH_LENGTH(p)) ELSE -1 END AS answer", count * (count + 1))
            })
            {
                using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
                {
                    query
                });
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, query + ": " + await response.Content.ReadAsStringAsync());
                var rows = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
                Assert.AreEqual(1, rows.GetArrayLength(), query);
                Assert.AreEqual(expected, rows[0].GetProperty("bindings").GetProperty("answer").GetInt32(), query);
            }
            using var changed = await client.PostAsJsonAsync("/v1/graph/gql/update", update);
            Assert.AreEqual(HttpStatusCode.OK, changed.StatusCode, await changed.Content.ReadAsStringAsync());
            receipt ??= await changed.Content.ReadAsStringAsync();
            Assert.AreEqual(receipt, await changed.Content.ReadAsStringAsync());
            Assert.AreEqual(2, (await changed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result").GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetInt32());
            using var failed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
            {
                statement = prefix + start + "-[e:pair]-+" + end +
                    " INSERT (:WalkRollback) RETURN CARDINALITY(TRIM([1],-1)) AS invalid"
            });
            Assert.AreEqual(HttpStatusCode.BadRequest, failed.StatusCode, await failed.Content.ReadAsStringAsync());
            using var rollback = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = "MATCH (n:WalkRollback) RETURN COUNT(*) AS answer"
            });
            Assert.AreEqual(HttpStatusCode.OK, rollback.StatusCode);
            Assert.AreEqual(0, (await rollback.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetInt32());
            await host.StopAsync();
        }
    }

    [TestMethod]
    [DataRow("ALL SHORTEST", "+", 2, 2)]
    [DataRow("ANY SHORTEST", "+", 1, 2)]
    [DataRow("SHORTEST 1", "+", 1, 2)]
    [DataRow("SHORTEST 1 GROUPS", "+", 2, 2)]
    [DataRow("ALL SHORTEST", "*", 2, 2)]
    [DataRow("ANY SHORTEST", "*", 1, 2)]
    [DataRow("ALL SHORTEST", "{4,}", 2, 4)]
    [DataRow("ANY SHORTEST", "{4,}", 1, 4)]
    [DataRow("SHORTEST 1", "{4,}", 1, 4)]
    [DataRow("SHORTEST 1 GROUP", "{4,}", 6, 4)]
    public async Task Gql_unbounded_shortest_walks_terminate_on_cycles_and_preserve_minimum_length(
        string selector, string quantifier, int count, int length)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (a:WalkStart)-[:step]->(a),(a)-[:step]->(b)," +
                "(b)-[:step]->(z:WalkEnd),(b)-[:step]->(z),(z)-[:step]->(z),(:Unreachable)"
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode, await seed.Content.ReadAsStringAsync());
        // For length >=4, there are three distributions of the two extra loop
        // traversals between the start and end, with two parallel final edges.
        int expectedCount = selector == "ALL SHORTEST" && quantifier == "{4,}" ? 6 : count;
        string pattern = "MATCH REPEATABLE ELEMENTS p=" + selector +
            " (a:WalkStart)-[e:step]->" + quantifier + "(b:WalkEnd) ";
        using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = pattern + "RETURN PATH_LENGTH(p) AS length"
        });
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var rows = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
        Assert.AreEqual(expectedCount, rows.GetArrayLength());
        Assert.IsTrue(rows.EnumerateArray().All(row => row.GetProperty("bindings").GetProperty("length").GetInt32() == length));
        using var absent = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = pattern.Replace("(b:WalkEnd)", "(b:Unreachable)", StringComparison.Ordinal) + "RETURN p"
        });
        Assert.AreEqual(HttpStatusCode.OK, absent.StatusCode, await absent.Content.ReadAsStringAsync());
        Assert.AreEqual(0, (await absent.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches").GetArrayLength());
        await host.StopAsync();
    }
}
