using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("LET seed=RECORD {a:1} MATCH ANY (a:SelectStart)-[e:choose WHERE e.weight>1]->(m)-[f:tailLink]->(d:SelectEnd) RETURN m.name AS answer", "[\"good\"]")]
    [DataRow("MATCH (a:SelectStart)-[e:choose]->(m)-[f:tailLink]->(d:SelectEnd) MATCH ANY (a)-[e WHERE e.weight>1]->(m)-[f]->(d) RETURN m.name AS answer", "[\"good\"]")]
    [DataRow("MATCH (a:SelectStart)-[e:choose]->(m)-[f:tailLink]->(d:SelectEnd) OPTIONAL MATCH ANY (a)-[e WHERE FALSE]->(m)-[f]->(d) RETURN m.name AS answer ORDER BY answer", "[\"bad\",\"good\"]")]
    [DataRow("MATCH p=ANY (:SelectStart)-[:choose WHERE TRUE]->(:SelectMiddle & !Blocked)-[:tailLink]->(:SelectEnd) RETURN PATH_LENGTH(p) AS answer", "[2]")]
    [DataRow("MATCH ANY (:SelectStart)-[:choose WHERE TRUE]->(:SelectMiddle & !Blocked)-[:tailLink]->(:SelectEnd) RETURN COUNT(*) AS answer", "[1]")]
    [DataRow("LET retained=7 OPTIONAL MATCH ANY (:SelectStart)-[:choose WHERE FALSE]->()-[:tailLink]->(:SelectEnd) RETURN retained AS answer", "[7]")]
    [DataRow("MATCH (a:SelectStart) MATCH ANY (a)-[:choose WHERE TRUE]->(:SelectMiddle & !Blocked)-[:tailLink]->(:SelectEnd) RETURN COUNT(*) AS answer", "[1]")]
    [DataRow("MATCH (a:SelectStart) CALL (a) { MATCH ANY (a)-[:choose WHERE TRUE]->(:SelectMiddle & !Blocked)-[:tailLink]->(:SelectEnd) RETURN COUNT(*) AS amount } RETURN amount AS answer", "[1]")]
    [DataRow("MATCH (a:SelectStart) RETURN a AS origin NEXT MATCH ANY (origin)-[:choose WHERE TRUE]->(:SelectMiddle & !Blocked)-[:tailLink]->(:SelectEnd) RETURN COUNT(*) AS answer", "[1]")]
    [DataRow("MATCH (a:SelectStart) LET minimumWeight=2 MATCH ANY (a)-[e:choose {weight:minimumWeight}]->(m)-[f:tailLink]->(d:SelectEnd) RETURN m.name AS answer", "[\"good\"]")]
    [DataRow("MATCH ()-[e:choose]->() WHERE e.weight=2 LET minimumWeight=2 MATCH ANY (a:SelectStart)-[e:choose {weight:minimumWeight}]->(m)-[f:tailLink]->(d:SelectEnd) RETURN m.name AS answer", "[\"good\"]")]
    [DataRow("MATCH (a:SelectStart) MATCH ANY (a)-[e:choose WHERE e.weight>1]->(m)-[f:tailLink]->(d:SelectEnd) RETURN m.name AS answer", "[\"good\"]")]
    [DataRow("MATCH (m:SelectMiddle {name:'good'}) MATCH ANY (a:SelectStart)-[e:choose WHERE e.weight>1]->(m)-[f:tailLink]->(d:SelectEnd) RETURN m.name AS answer", "[\"good\"]")]
    [DataRow("MATCH ()-[e:choose]->() WHERE e.weight=2 MATCH ANY (a:SelectStart)-[e:choose WHERE e.weight>1]->(m)-[f:tailLink]->(d:SelectEnd) RETURN m.name AS answer", "[\"good\"]")]
    [DataRow("MATCH (m:SelectMiddle {name:'good'}) MATCH ALL SHORTEST ((a:SelectStart)-[e:choose]->(m)-[f:tailLink]->(d:SelectEnd) WHERE m.score>1 OR m.name='bad') RETURN m.name AS answer", "[\"good\"]")]
    [DataRow("MATCH (a:SelectStart) OPTIONAL MATCH ANY (a)-[e:choose WHERE e.weight>10]->(m)-[f:tailLink]->(d:SelectEnd) RETURN m.name AS answer", "[null]")]
    [DataRow("MATCH ANY (a:SelectStart)-[e:choose WHERE EXISTS { MATCH (x)-[e]->(y) WHERE y.score>1 }]->(m)-[f:tailLink]->(d:SelectEnd) RETURN m.name AS answer", "[\"good\"]")]
    [DataRow("MATCH ALL SHORTEST (a:SelectStart)-[e:choose]->(m WHERE EXISTS { MATCH (m)-[inner:tailLink]->(target:SelectEnd) WHERE m.score>1 })-[f:tailLink]->(d:SelectEnd) RETURN m.name AS answer", "[\"good\"]")]
    [DataRow("MATCH ANY (a:SelectStart)-[e:choose]->(m WHERE NOT EXISTS { MATCH (m:Blocked) })-[f:tailLink]->(d:SelectEnd) RETURN m.name AS answer", "[\"good\"]")]
    [DataRow("MATCH ANY (a:SelectStart)-[e:choose WHERE e.weight>1]->(m)-[f:tailLink]->(d:SelectEnd) RETURN m.name AS answer", "[\"good\"]")]
    [DataRow("MATCH SHORTEST 1 (a:SelectStart)-[e:choose]->(m WHERE m.score>1)-[f:tailLink]->(d:SelectEnd) RETURN m.name AS answer", "[\"good\"]")]
    [DataRow("MATCH ANY (a:SelectStart)-[e:choose]->(m IS SelectMiddle & !Blocked)-[f:tailLink]->(d:SelectEnd) RETURN m.name AS answer", "[\"good\"]")]
    [DataRow("MATCH ALL SHORTEST (a:SelectStart)-[e IS choose|missing]->(m)-[f:tailLink]->(d:SelectEnd) RETURN m.name AS answer ORDER BY answer", "[\"bad\",\"good\"]")]
    [DataRow("MATCH ANY ((a:SelectStart)-[e:choose]->(m)-[f:tailLink]->(d:SelectEnd) WHERE m.score>1) RETURN m.name AS answer", "[\"good\"]")]
    [DataRow("LET floorScore=1 MATCH ANY ((a:SelectStart)-[e:choose]->(m)-[f:tailLink]->(d:SelectEnd) WHERE m.score>floorScore) RETURN m.name AS answer", "[\"good\"]")]
    [DataRow("MATCH (threshold:Threshold) MATCH ANY ((a:SelectStart)-[e:choose]->(m)-[f:tailLink]->(d:SelectEnd) WHERE m.score>threshold.amount) RETURN m.name AS answer", "[\"good\"]")]
    [DataRow("MATCH p=ANY ((a:SelectStart)-[e:choose]->(m)-[f:tailLink]->(d:SelectEnd) WHERE m.score>1) RETURN PATH_LENGTH(p)=2 AND m IS TYPED NODE AND e IS TYPED EDGE AS answer", "[true]")]
    [DataRow("MATCH ANY ((a:SelectStart)-[e:choose]->(m)-[f:tailLink]->(d:SelectEnd) WHERE m.score>1) WHERE m.score<0 RETURN m.name AS answer", "[]")]
    [DataRow("OPTIONAL MATCH ANY (a:SelectStart)-[e:choose]->(m WHERE m.score>10)-[f:tailLink]->(d:SelectEnd) RETURN m.name AS answer", "[null]")]
    public async Task Gql_standard_selective_predicates_filter_candidates_before_selection(string query, string expected)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (:Threshold {amount:1}),(a:SelectStart),(b:SelectMiddle & Blocked {name:'bad',score:0}),(c:SelectMiddle {name:'good',score:2}),(d:SelectEnd)," +
                "(a)-[:choose {weight:0}]->(b),(b)-[:tailLink]->(d),(a)-[:choose {weight:2}]->(c),(c)-[:tailLink]->(d)"
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode, await seed.Content.ReadAsStringAsync());
        using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query
        });
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var rows = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
        Assert.AreEqual(expected, JsonSerializer.Serialize(rows.EnumerateArray().Select(row => row.GetProperty("bindings").GetProperty("answer"))));
        foreach (var row in rows.EnumerateArray())
        {
            Assert.AreEqual(1, row.GetProperty("bindings").EnumerateObject().Count());
        }
        await host.StopAsync();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Gql_standard_selective_predicates_choose_an_eligible_alternative_and_replay_updates(bool callBinding)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (a:SelectStart),(b:SelectMiddle {name:'B'}),(c:SelectMiddle {name:'C'}),(d:SelectEnd)," +
                "(a)-[:choose {weight:1}]->(b),(b)-[:tailLink]->(d),(a)-[:choose {weight:1}]->(c),(c)-[:tailLink]->(d)"
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode, await seed.Content.ReadAsStringAsync());
        const string pattern = "(a:SelectStart)-[e:choose]->(m)-[f:tailLink]->(d:SelectEnd)";
        using var baseline = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH ANY " + pattern + " RETURN ELEMENT_ID(e) AS selected"
        });
        Assert.AreEqual(HttpStatusCode.OK, baseline.StatusCode, await baseline.Content.ReadAsStringAsync());
        string selected = (await baseline.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("selected").GetString()!;
        using var exclude = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "MATCH ()-[e:choose]->() WHERE ELEMENT_ID(e)=$selected SET e.weight=0",
            parameters = new
            {
                selected
            }
        });
        Assert.AreEqual(HttpStatusCode.OK, exclude.StatusCode, await exclude.Content.ReadAsStringAsync());
        string eligible = pattern.Replace("[e:choose]", "[e:choose WHERE e.weight=1]", StringComparison.Ordinal);
        using var chosen = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH p=ANY " + eligible + " RETURN *"
        });
        Assert.AreEqual(HttpStatusCode.OK, chosen.StatusCode, await chosen.Content.ReadAsStringAsync());
        var rows = (await chosen.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
        Assert.AreEqual(1, rows.GetArrayLength());
        CollectionAssert.AreEquivalent(new[] { "a", "e", "m", "f", "d", "p" },
            rows[0].GetProperty("bindings").EnumerateObject().Select(property => property.Name).ToArray());
        string prefix = callBinding
            ? "CALL () { MATCH ()-[edge:choose]->() WHERE edge.weight=1 RETURN edge AS e } "
            : "MATCH ()-[e:choose]->() WHERE e.weight=1 ";
        var request = new
        {
            statement = prefix + "MATCH ANY " + eligible + " SET e.checked=TRUE RETURN e.weight AS answer",
            transactionId = Guid.NewGuid()
        };
        string? receipt = null;
        for (int retry = 0; retry < 2; retry++)
        {
            using var update = await client.PostAsJsonAsync("/v1/graph/gql/update", request);
            Assert.AreEqual(HttpStatusCode.OK, update.StatusCode, await update.Content.ReadAsStringAsync());
            receipt ??= await update.Content.ReadAsStringAsync();
            Assert.AreEqual(receipt, await update.Content.ReadAsStringAsync());
            Assert.AreEqual(1, (await update.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result").GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetInt32());
        }
        var failedRequest = new
        {
            statement = prefix + "MATCH ANY " + eligible + " SET e.weight=99 RETURN 1/0 AS answer",
            transactionId = Guid.NewGuid()
        };
        for (int retry = 0; retry < 2; retry++)
        {
            using var rejected = await client.PostAsJsonAsync("/v1/graph/gql/update", failedRequest);
            Assert.AreEqual(HttpStatusCode.BadRequest, rejected.StatusCode, await rejected.Content.ReadAsStringAsync());
            Assert.AreEqual("22012", (await rejected.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("gqlStatus").GetString());
        }
        using var verified = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH ()-[e:choose]->() WHERE e.checked IS TRUE RETURN COUNT(*)=1 AND SUM(e.weight)=1 AS answer"
        });
        Assert.AreEqual(HttpStatusCode.OK, verified.StatusCode, await verified.Content.ReadAsStringAsync());
        Assert.IsTrue((await verified.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetBoolean());
        await host.StopAsync();
    }
}
