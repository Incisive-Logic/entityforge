using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    public async Task Gql_standard_relationship_lists_drive_atomic_FOR_updates_and_replay()
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var created = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (:Legacy {name:'A'})-[:step {hits:0}]->(:Legacy {name:'B'})-[:step {hits:0}]->(:Legacy {name:'C'})"
        });
        Assert.AreEqual(HttpStatusCode.OK, created.StatusCode, await created.Content.ReadAsStringAsync());
        var update = new
        {
            transactionId = Guid.NewGuid(),
            statement = "MATCH (:Legacy {name:'A'})-[r:step]->{2}(:Legacy {name:'C'}) FOR e IN r SET e.hits=e.hits+1 RETURN size(r) AS answer"
        };
        string? first = null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/update", update);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            var result = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result");
            first ??= result.GetRawText();
            Assert.AreEqual(first, result.GetRawText());
            Assert.AreEqual(2, result.GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetInt32());
        }
        using var queried = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (:Legacy)-[r:step]->() RETURN sum(r.hits) AS answer"
        });
        Assert.AreEqual(HttpStatusCode.OK, queried.StatusCode, await queried.Content.ReadAsStringAsync());
        Assert.AreEqual(2, (await queried.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetInt32());
    }

    [TestMethod]
    [DataRow("MATCH (a:Walk {name:'A'})-[r:step]->{1}(b) MATCH (a)-[r:step]->{1}(b) RETURN count(*) AS answer", "1")]
    [DataRow("MATCH (a:Walk {name:'A'})-[r:step]->{0,1}(b) WHERE a=b MATCH (a)-[r:step]->{0,1}(b) WHERE a=b RETURN count(*) AS answer", "1")]
    [DataRow("MATCH (a:Walk {name:'A'})-[r:step]->+(b) RETURN VALUE { MATCH (a)-[s:step]->+(c) RETURN COUNT(*) AS total } AS answer", "1")]
    [DataRow("MATCH (a:Walk {name:'A'})-[r:step]->+(b) RETURN size(r) AS answer", "1")]
    [DataRow("MATCH (a:Walk {name:'A'})-[r:step]->{1}(b) RETURN r IS TYPED LIST<EDGE NOT NULL> AS answer", "true")]
    [DataRow("MATCH (a:Walk {name:'A'})-[r:step]->{0,1}(b) WHERE a=b RETURN size(r)=0 AND a=b AS answer", "true")]
    [DataRow("MATCH (a:Walk {name:'A'})-[r:step]->{0,1}(b) RETURN size(r) AS n ORDER BY n NEXT RETURN COLLECT_LIST(n) AS answer", "[0,1]")]
    [DataRow("MATCH (a:Walk {name:'A'})-[r:step]->{1}(b) RETURN size(r) AS answer", "1")]
    [DataRow("MATCH (a:Walk {name:'A'})-[r:step]->{1,}(b) RETURN size(r) AS answer", "1")]
    [DataRow("MATCH p=(a:Walk {name:'A'})-[r:step]->{0,1}(b) RETURN PATH_LENGTH(p)=size(r) AS valid NEXT RETURN COLLECT_LIST(valid) AS answer", "[true,true]")]
    [DataRow("MATCH (a:Walk {name:'A'})-[r:step]->+(b) WHERE NOT EXISTS { FOR e IN r FILTER NOT (e IS LABELED step) RETURN e } RETURN b.name AS answer", "\"B\"")]
    [DataRow("MATCH (a:Walk {name:'A'}) OPTIONAL MATCH (a)-[r:missing]->+(b) RETURN r AS answer", "null")]
    [DataRow("MATCH (a:Walk {name:'A'}) OPTIONAL MATCH (a)-[r:step]->+(b) WHERE size(r)=2 RETURN r AS answer", "null")]
    [DataRow("MATCH (a:Walk {name:'A'})-[r:step]->+(b) FOR e IN r RETURN e IS LABELED step AS answer", "true")]
    [DataRow("MATCH (a:Walk)-[:step]->{2,1}(b) RETURN count(*) AS answer", "ERROR")]
    [DataRow("MATCH (a:Walk)-[:step*1]->{1,2}(b) RETURN count(*) AS answer", "ERROR")]
    [DataRow("MATCH (a:Walk)-[:step]->{999999999999}(b) RETURN count(*) AS answer", "ERROR")]
    [DataRow("MATCH REPEATABLE ELEMENTS (a:Walk {name:'A'})-[r:step]->(b)<-[s:step]-(a) RETURN count(*) AS answer", "1")]
    [DataRow("MATCH DIFFERENT RELATIONSHIPS (a:Walk {name:'A'})-[r:step]->(b)<-[s:step]-(a) RETURN count(*) AS answer", "0")]
    [DataRow("MATCH DIFFERENT EDGE (a:Walk)-[r:step]->(b)<-[s:step]-(a) RETURN COUNT(*) AS answer", "0")]
    [DataRow("MATCH DIFFERENT EDGES (a:Walk)-[r:step]->(b)<-[s:step]-(a) RETURN COUNT(*) AS answer", "0")]
    [DataRow("MATCH DIFFERENT EDGE BINDINGS (a:Walk)-[r:step]->(b)<-[s:step]-(a) RETURN COUNT(*) AS answer", "0")]
    [DataRow("MATCH DIFFERENT RELATIONSHIP (a:Walk)-[r:step]->(b)<-[s:step]-(a) RETURN COUNT(*) AS answer", "0")]
    [DataRow("MATCH DIFFERENT RELATIONSHIP BINDINGS (a:Walk)-[r:step]->(b)<-[s:step]-(a) RETURN COUNT(*) AS answer", "0")]
    [DataRow("MATCH REPEATABLE ELEMENT (a:Walk)-[r:step]->(b)<-[s:step]-(a) RETURN COUNT(*) AS answer", "1")]
    [DataRow("MATCH REPEATABLE ELEMENT BINDINGS (a:Walk)-[r:step]->(b)<-[s:step]-(a) RETURN COUNT(*) AS answer", "1")]
    [DataRow("MATCH REPEATABLE ELEMENT BINDINGS ((a:Walk)-[r:step]->(b)<-[s:step]-(c)){1} RETURN COUNT(*) AS answer", "1")]
    [DataRow("MATCH DIFFERENT EDGE BINDINGS ((a:Walk)-[r:step]->(b)<-[s:step]-(c)){1} RETURN COUNT(*) AS answer", "0")]
    [DataRow("MATCH REPEATABLE ELEMENTS (a:Walk {name:'A'})-[:step]-{2}(b) RETURN b.name AS answer", "\"A\"")]
    [DataRow("MATCH REPEATABLE ELEMENTS p=(a:Walk {name:'A'})-[:step]-{0,3}(b) RETURN PATH_LENGTH(p) AS hops ORDER BY hops NEXT RETURN COLLECT_LIST(hops) AS answer", "[0,1,2,3]")]
    [DataRow("MATCH REPEATABLE ELEMENTS (a:Walk)-[r:step]->(b),(c:Walk)-[s:step]->(d) RETURN count(*) AS answer", "1")]
    [DataRow("MATCH (a:Walk)-[r:step]->(b),(c:Walk)-[s:step]->(d) RETURN count(*) AS answer", "0")]
    [DataRow("LET seed=1 MATCH REPEATABLE ELEMENTS (a:Walk {name:'A'})-[:step]-{2}(b) RETURN b.name AS answer", "\"A\"")]
    [DataRow("MATCH (a:Walk {name:'A'}) OPTIONAL MATCH REPEATABLE ELEMENTS (a)-[:step]-{2}(b) RETURN b.name AS answer", "\"A\"")]
    [DataRow("MATCH REPEATABLE ELEMENTS (a:Walk)-[:step]->+(b) RETURN count(*) AS answer", "ERROR")]
    public async Task Gql_standard_repeatable_elements_preserve_bounded_walk_multiplicity(string query, string expected)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var created = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (:Walk {name:'A'})-[:step]->(:Walk {name:'B'})"
        });
        Assert.AreEqual(HttpStatusCode.OK, created.StatusCode, await created.Content.ReadAsStringAsync());
        using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query
        });
        if (expected == "ERROR")
        {
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, await response.Content.ReadAsStringAsync());
            return;
        }
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var rows = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
        Assert.AreEqual(1, rows.GetArrayLength());
        Assert.AreEqual(expected, rows[0].GetProperty("bindings").GetProperty("answer").GetRawText());
    }
}
