using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("WALK", "[1,2,2,3,3,3]")]
    [DataRow("TRAIL", "[1,2,3,3]")]
    [DataRow("SIMPLE", "[1,2]")]
    [DataRow("ACYCLIC", "[]")]
    public async Task Gql_standard_whole_group_modes_restrict_complete_paths(string mode, string expected)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (a:CycleStart),(b:CycleMiddle),(a)-[:route]->(a),(a)-[:route]->(b),(b)-[:route]->(a)"
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode, await seed.Content.ReadAsStringAsync());
        foreach (string pattern in new[]
        {
            $"(p={mode} (:CycleStart)-[:route]->{{1,3}}(:CycleStart))",
            $"(p={mode} (:CycleStart)-[:route|missing]->{{1,3}}(:CycleStart))",
            $"WALK (p={mode} (:CycleStart)-[:route]->{{1,3}}(:CycleStart))",
            $"{mode} (p=WALK (:CycleStart)-[:route]->{{1,3}}(:CycleStart))",
            "p=" + mode + " " + new string('(', 24) + "(:CycleStart)-[:route]->{1,3}(:CycleStart)" + new string(')', 24),
            $"p={mode} (:CycleStart)-[:route]->{{1,3}}(:CycleStart)"
        })
        {
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = "MATCH REPEATABLE ELEMENTS " + pattern + " RETURN PATH_LENGTH(p) AS answer ORDER BY answer"
            });
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            var rows = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
            Assert.AreEqual(expected, JsonSerializer.Serialize(rows.EnumerateArray().Select(row => row.GetProperty("bindings").GetProperty("answer"))));
        }
    }

    [TestMethod]
    [DataRow("ACYCLIC", "TRAIL")]
    [DataRow("SIMPLE", "ACYCLIC")]
    [DataRow("ACYCLIC", "SIMPLE")]
    [DataRow("TRAIL", "ACYCLIC")]
    public async Task Gql_standard_whole_group_mixed_modes_preserve_the_stronger_acyclic_constraint(string outer, string inner)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (a:ModeStart),(b:ModeEnd),(a)-[:route]->(b),(b)-[:route]->(a)"
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode);
        foreach (string end in new[] { "ModeStart", "ModeEnd" })
        {
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = $"MATCH REPEATABLE ELEMENTS {outer} (p={inner} (:ModeStart)-[:route]->{{1,3}}(:{end})) RETURN PATH_LENGTH(p) AS answer"
            });
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            var rows = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
            Assert.AreEqual(end == "ModeStart" ? 0 : 1, rows.GetArrayLength());
            if (rows.GetArrayLength() != 0)
            {
                Assert.AreEqual(1, rows[0].GetProperty("bindings").GetProperty("answer").GetInt32());
            }
        }
    }

    [TestMethod]
    [DataRow("MATCH ANY SHORTEST (p=ACYCLIC (:PrefilterStart)-[:route]->+(:PrefilterEnd) WHERE PATH_LENGTH(p)>1) RETURN PATH_LENGTH(p) AS answer", "[2]")]
    [DataRow("MATCH ANY SHORTEST (p=(a:PrefilterStart)-[edges:route]->+(b:PrefilterEnd) WHERE CARDINALITY(edges)>1) RETURN PATH_LENGTH(p) AS answer", "[2]")]
    [DataRow("MATCH ANY SHORTEST ((a:PrefilterStart)-[edges:route]->+(b:PrefilterEnd) WHERE CARDINALITY(edges)>1) RETURN CARDINALITY(edges) AS answer", "[2]")]
    [DataRow("MATCH ANY SHORTEST ((a:PrefilterStart)-[edges:route]->+(b:PrefilterEnd) WHERE CARDINALITY(edges)>1) RETURN *", "bindings")]
    [DataRow("LET __pathPrefilterPath=99 MATCH ANY SHORTEST ((a:PrefilterStart)-[edges:route]->+(b:PrefilterEnd) WHERE CARDINALITY(edges)>1) RETURN __pathPrefilterPath AS answer", "[99]")]
    [DataRow("MATCH ALL SHORTEST ((a:PrefilterStart)-[edges:route]->+(b:PrefilterEnd) WHERE CARDINALITY(edges)>1) RETURN CARDINALITY(edges) AS answer", "[2,2]")]
    [DataRow("OPTIONAL MATCH ANY SHORTEST ((a:PrefilterStart)-[edges:route]->+(b:PrefilterEnd) WHERE CARDINALITY(edges)>5) RETURN edges AS answer", "[null]")]
    [DataRow("MATCH ANY SHORTEST (p=(a:PrefilterStart)-[edges:route]->+(b:PrefilterEnd) WHERE VALUE { FOR e IN edges RETURN COUNT(*) AS total }>1) RETURN PATH_LENGTH(p) AS answer", "[2]")]
    [DataRow("MATCH ALL SHORTEST (p=(a:PrefilterStart)-[edges:route]->+(b:PrefilterEnd) WHERE b IS LABELED PrefilterEnd) RETURN PATH_LENGTH(p) AS answer", "[1]")]
    [DataRow("MATCH ALL SHORTEST PATHS (p=TRAIL (:PrefilterStart)-[:route]->+(:PrefilterEnd) WHERE PATH_LENGTH(p)>1) RETURN PATH_LENGTH(p) AS answer", "[2,2]")]
    [DataRow("MATCH SHORTEST 1 GROUPS (p=SIMPLE (:PrefilterStart)-[:route]->+(:PrefilterEnd) WHERE PATH_LENGTH(p)>1) RETURN PATH_LENGTH(p) AS answer", "[2,2]")]
    [DataRow("MATCH SHORTEST 1 PATH GROUP (p=TRAIL (:PrefilterStart)-[:route]->+(:PrefilterEnd) WHERE PATH_LENGTH(p)>1) RETURN PATH_LENGTH(p) AS answer", "[2,2]")]
    [DataRow("MATCH ANY SHORTEST ACYCLIC (p=ACYCLIC (:PrefilterStart)-[:route]->+(:PrefilterEnd) WHERE PATH_LENGTH(p)>1) RETURN PATH_LENGTH(p) AS answer", "[2]")]
    [DataRow("MATCH ALL SHORTEST (p=(:PrefilterStart)-[:route]->+(:PrefilterEnd) WHERE PATH_LENGTH(p)>1) RETURN PATH_LENGTH(p) AS answer", "[2,2]")]
    [DataRow("MATCH SHORTEST 2 (p=(:PrefilterStart)-[:route]->+(:PrefilterEnd) WHERE PATH_LENGTH(p)>1) RETURN PATH_LENGTH(p) AS answer", "[2,2]")]
    [DataRow("MATCH SHORTEST 1 GROUPS (p=(:PrefilterStart)-[:route]->+(:PrefilterEnd) WHERE PATH_LENGTH(p)>1) RETURN PATH_LENGTH(p) AS answer", "[2,2]")]
    [DataRow("MATCH ANY SHORTEST (p=(a:PrefilterStart)-[:route]->+(b:PrefilterEnd) WHERE PATH_LENGTH(p)>1) RETURN PATH_LENGTH(p) AS answer", "[2]")]
    [DataRow("MATCH p=ANY SHORTEST ((:PrefilterStart)-[:route]->+(:PrefilterEnd) WHERE PATH_LENGTH(p)>1) RETURN PATH_LENGTH(p) AS answer", "[2]")]
    [DataRow("MATCH p=ANY SHORTEST (:PrefilterStart)-[:route]->+(:PrefilterEnd) WHERE PATH_LENGTH(p)>1 RETURN PATH_LENGTH(p) AS answer", "[]")]
    [DataRow("MATCH ALL SHORTEST (p=(a:PrefilterStart)-[:route]->+(b:PrefilterEnd) WHERE PATH_LENGTH(p)>1) WHERE PATH_LENGTH(p)<2 RETURN p AS answer", "[]")]
    [DataRow("OPTIONAL MATCH ANY SHORTEST (p=(a:PrefilterStart)-[:route]->+(b:PrefilterEnd) WHERE PATH_LENGTH(p)>5) RETURN p AS answer", "[null]")]
    [DataRow("LET minimumLength=1 MATCH ANY SHORTEST (p=(a:PrefilterStart)-[:route]->+(b:PrefilterEnd) WHERE PATH_LENGTH(p)>minimumLength) RETURN PATH_LENGTH(p) AS answer", "[2]")]
    [DataRow("MATCH (a:PrefilterStart) CALL (a) { MATCH ANY SHORTEST (p=(a)-[:route]->+(b:PrefilterEnd) WHERE PATH_LENGTH(p)>1) RETURN PATH_LENGTH(p) AS answer } RETURN answer", "[2]")]
    [DataRow("MATCH ANY SHORTEST (p=(:PrefilterStart) WHERE PATH_LENGTH(p)=0) RETURN PATH_LENGTH(p) AS answer", "[0]")]
    public async Task Gql_standard_whole_path_prefilters_precede_selection(string query, string expected)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (a:PrefilterStart),(b:PrefilterMiddle),(c:PrefilterEnd),(a)-[:route]->(c),(a)-[:route]->(b),(a)-[:route]->(b),(b)-[:route]->(c)"
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode, await seed.Content.ReadAsStringAsync());
        using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query
        });
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var rows = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
        if (expected == "bindings")
        {
            Assert.AreEqual(1, rows.GetArrayLength());
            var bindings = rows[0].GetProperty("bindings");
            CollectionAssert.AreEquivalent(new[] { "a", "b", "edges" }, bindings.EnumerateObject().Select(property => property.Name).ToArray());
            Assert.AreEqual(2, bindings.GetProperty("edges").GetArrayLength());
            return;
        }
        Assert.AreEqual(expected, JsonSerializer.Serialize(rows.EnumerateArray().Select(row => row.GetProperty("bindings").GetProperty("answer"))));
        foreach (var row in rows.EnumerateArray())
        {
            Assert.AreEqual(1, row.GetProperty("bindings").EnumerateObject().Count());
        }
    }

    [TestMethod]
    public async Task Gql_standard_path_prefilter_updates_replay_without_changing_selection()
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (a:PrefilterStart),(b:PrefilterMiddle),(c:PrefilterEnd {visits:0}),(a)-[:route]->(c),(a)-[:route]->(b),(b)-[:route]->(c)"
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode, await seed.Content.ReadAsStringAsync());
        var request = new
        {
            statement = "MATCH ANY SHORTEST (p=(a:PrefilterStart)-[:route]->+(b:PrefilterEnd) WHERE PATH_LENGTH(p)>1) SET b.visits=b.visits+1 RETURN PATH_LENGTH(p) AS answer",
            transactionId = Guid.NewGuid()
        };
        string? receipt = null;
        for (int retry = 0; retry < 2; retry++)
        {
            using var update = await client.PostAsJsonAsync("/v1/graph/gql/update", request);
            Assert.AreEqual(HttpStatusCode.OK, update.StatusCode, await update.Content.ReadAsStringAsync());
            receipt ??= await update.Content.ReadAsStringAsync();
            Assert.AreEqual(receipt, await update.Content.ReadAsStringAsync());
            Assert.AreEqual(2, (await update.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result").GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetInt32());
        }
        using var verified = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (b:PrefilterEnd) RETURN b.visits AS answer"
        });
        Assert.AreEqual(HttpStatusCode.OK, verified.StatusCode, await verified.Content.ReadAsStringAsync());
        Assert.AreEqual(1, (await verified.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetInt32());
    }
}
