using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Gql_repeated_product_correlation_controls_updates_and_replay(bool negate)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (a:P {name:'A'})-[:r]->(b:P {name:'B'}),(a)-[:r]->(c:P {name:'C'})"
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode, await seed.Content.ReadAsStringAsync());
        var request = new
        {
            statement = "MATCH (c:P {name:'C'}) MATCH (a:P)((x)-[:r]->(y)){1}(b WHERE " +
                (negate ? "NOT " : "") + "EXISTS { MATCH (c:P {name:'B'}) }),(:P {name:'C'}) SET b.visited=TRUE RETURN b.name AS answer",
            transactionId = Guid.NewGuid()
        };
        using var updated = await client.PostAsJsonAsync("/v1/graph/gql/update", request);
        Assert.AreEqual(HttpStatusCode.OK, updated.StatusCode, await updated.Content.ReadAsStringAsync());
        var result = await updated.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual(negate ? 2 : 0, result.GetProperty("result").GetProperty("matches").GetArrayLength());
        using var replay = await client.PostAsJsonAsync("/v1/graph/gql/update", request);
        Assert.AreEqual(HttpStatusCode.OK, replay.StatusCode, await replay.Content.ReadAsStringAsync());
        Assert.AreEqual(await updated.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync());
        using var check = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (n:P WHERE n.visited IS TRUE) RETURN COUNT(*) AS answer"
        });
        Assert.AreEqual(HttpStatusCode.OK, check.StatusCode, await check.Content.ReadAsStringAsync());
        Assert.AreEqual(negate ? 2 : 0, (await check.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetInt32());
    }

    [TestMethod]
    [DataRow("MATCH (a:P)((x)-[:r]->(y)){1}(b WHERE EXISTS { MATCH (c:P {name:'B'}) }),(c:P {name:'C'}) RETURN count(*) AS answer", 0)]
    [DataRow("MATCH (c:P {name:'C'}),(a:P)((x)-[:r]->(y)){1}(b WHERE NOT EXISTS { MATCH (c:P {name:'B'}) }) RETURN count(*) AS answer", 2)]
    [DataRow("MATCH (c:P {name:'C'}) MATCH (a:P)((x WHERE EXISTS { MATCH (c:P {name:'B'}) })-[:r]->(y)){1}(b)((u)-[:r]->(v)){0}(z) RETURN count(*) AS answer", 0)]
    [DataRow("MATCH (c:P {name:'C'}) MATCH (a:P)((x WHERE EXISTS { MATCH (c:P {name:'C'}) })-[:r]->(y)){1}(b)((u)-[:r]->(v)){0}(z) RETURN count(*) AS answer", 2)]
    [DataRow("MATCH (c:P {name:'C'}) MATCH (a:P)((x)-[:r]->(y)){1}(b WHERE EXISTS { MATCH (c:P {name:'B'}) })((u)-[:r]->(v)){0}(z) RETURN count(*) AS answer", 0)]
    [DataRow("MATCH (c:P {name:'C'}) MATCH (a:P)((x)-[:r]->(y)){1}(b WHERE EXISTS { MATCH (c:P {name:'C'}) })((u)-[:r]->(v)){0}(z) RETURN count(*) AS answer", 2)]
    [DataRow("MATCH (c:P {name:'C'}) MATCH (a:P)((x)-[:r]->(y)){1}(b WHERE EXISTS { MATCH (c:P {name:'B'}) }),(:P {name:'C'}) RETURN count(*) AS answer", 0)]
    [DataRow("MATCH (c:P {name:'C'}) MATCH (a:P)((x)-[:r]->(y)){1}(b WHERE EXISTS { MATCH (c:P {name:'C'}) }),(:P {name:'C'}) RETURN count(*) AS answer", 2)]
    [DataRow("MATCH (c:P {name:'C'}) MATCH (a:P)((x)-[:r]->(y)){1}(b WHERE NOT EXISTS { MATCH (c:P {name:'B'}) }),(:P {name:'C'}) RETURN count(*) AS answer", 2)]
    [DataRow("MATCH (a:P)-[:r]->(b:P),(c:P)-[:r]->(d:P) RETURN count(*) AS answer", 2)]
    [DataRow("MATCH (a:P)-[:r]->(b:P) MATCH (c:P)-[:r]->(d:P) RETURN count(*) AS answer", 4)]
    [DataRow("OPTIONAL { MATCH (a:P)-[:r]->(b) MATCH (b)-[:r]->(c) } RETURN COUNT(a) AS answer", 0)]
    [DataRow("OPTIONAL ( MATCH (a:P)-[:r]->(b) MATCH (c:P) ) RETURN COUNT(*) AS answer", 6)]
    [DataRow("MATCH (a:P) OPTIONAL { MATCH (a)-[:r]->(b) MATCH (b)-[:r]->(c) } RETURN COUNT(a) AS answer", 3)]
    [DataRow("MATCH (a:P) OPTIONAL { MATCH (a)-[:r]->(b) MATCH (b)-[:r]->(c) } RETURN COUNT(b) AS answer", 0)]
    [DataRow("MATCH (a:P) OPTIONAL { MATCH (a)-[:r]->(b) OPTIONAL MATCH (b)-[:r]->(c) } RETURN COUNT(*) AS answer", 4)]
    [DataRow("OPTIONAL { MATCH (:Absent) MATCH (b:P) } RETURN COUNT(*) AS answer", 1)]
    [DataRow("OPTIONAL { MATCH (:P) MATCH (:P) } RETURN COUNT(*) AS answer", 9)]
    [DataRow("OPTIONAL { MATCH (:Absent) MATCH (:P) } RETURN COUNT(*) AS answer", 1)]
    [DataRow("FOR x IN [1,1] OPTIONAL { MATCH (a:P) WHERE x=1 MATCH (b:P) } RETURN COUNT(*) AS answer", 18)]
    [DataRow("MATCH (a:P) OPTIONAL { MATCH (a) MATCH (:P) } RETURN COUNT(*) AS answer", 9)]
    [DataRow("OPTIONAL { MATCH p=(a:P)-[:r]->(b) MATCH (b) } RETURN SUM(PATH_LENGTH(p)) AS answer", 2)]
    [DataRow("OPTIONAL { MATCH (a:P)-[:r]->(b) MATCH (b) WHERE false } RETURN COUNT(a) AS answer", 0)]
    [DataRow("OPTIONAL { MATCH (a:P) OPTIONAL { MATCH (a)-[:r]->(b) MATCH (b)-[:r]->(c) } } RETURN COUNT(a) AS answer", 3)]
    [DataRow("MATCH (a:P)-[:r]->(b:P) MATCH (a)-[:r]->(c:P) RETURN count(*) AS answer", 4)]
    [DataRow("MATCH (a:P {name:'A'}) MATCH p=(a)-[:r]->(b),q=(a)-[:r]->(c) RETURN count(*) AS answer", 2)]
    [DataRow("MATCH (a:P {name:'A'}) MATCH REPEATABLE ELEMENTS p=(a)-[:r]->(b),q=(a)-[:r]->(c) RETURN count(*) AS answer", 4)]
    [DataRow("OPTIONAL MATCH (a:Absent) OPTIONAL MATCH p=(a)-[:r]->(b),q=(a)-[:r]->(c) RETURN count(*) AS answer", 1)]
    [DataRow("MATCH (a:P)-[:r]->(b:P) OPTIONAL MATCH (a)-[:r]->(c:P) WHERE false RETURN count(*) AS answer", 2)]
    [DataRow("MATCH (a:P {name:'A'}) MATCH (a:P {name:'B'}) RETURN count(*) AS answer", 0)]
    [DataRow("MATCH (a:P)-[:r]->(b:P) MATCH (b) RETURN count(*) AS answer", 2)]
    [DataRow("OPTIONAL MATCH (a:Absent) MATCH (a)-[:r]->(b:P) RETURN count(*) AS answer", 0)]
    [DataRow("OPTIONAL MATCH (a:Absent) OPTIONAL MATCH (a)-[:r]->(b:P) RETURN count(*) AS answer", 1)]
    [DataRow("MATCH (a:P)-[r:r]->(b:P),(c:P)-[r:r]->(d:P) RETURN count(*) AS answer", 0)]
    [DataRow("MATCH (a:P)-[:r]->(b:P),(a)-[:r]->(c:P) RETURN count(*) AS answer", 2)]
    [DataRow("MATCH (a:P),(b:P) RETURN count(*) AS answer", 9)]
    [DataRow("MATCH (a:P)-[:r]->(b:P),(c:Absent) RETURN count(*) AS answer", 0)]
    [DataRow("OPTIONAL MATCH (a:Absent)-[:r]->(b:P),(c:P) RETURN count(*) AS answer", 1)]
    [DataRow("MATCH (a:P) OPTIONAL MATCH (a)-[:r]->(b:P),(c:P) WHERE false RETURN count(*) AS answer", 3)]
    [DataRow("MATCH (a:P) OPTIONAL MATCH (a)-[:r]->(b:P),(a)-[:r]->(c:P) RETURN count(*) AS answer", 4)]
    [DataRow("MATCH p=(a:P)-[:r]->(b:P),q=(c:P)-[:r]->(d:P) RETURN sum(PATH_LENGTH(p)+PATH_LENGTH(q)) AS answer", 4)]
    [DataRow("MATCH (:P)-[:r]->(:P),(:P)-[:r]->(:P) RETURN count(*) AS answer", 2)]
    public async Task Gql_standard_match_products_enforce_clause_wide_trails_and_optional_extension(string query, int expected)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seeded = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (a:P {name:'A'})-[:r]->(b:P {name:'B'}),(a)-[:r]->(c:P {name:'C'})"
        });
        Assert.AreEqual(HttpStatusCode.OK, seeded.StatusCode, await seeded.Content.ReadAsStringAsync());
        using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query
        });
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var rows = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
        Assert.AreEqual(1, rows.GetArrayLength());
        Assert.AreEqual(expected, rows[0].GetProperty("bindings").GetProperty("answer").GetInt32());
    }
}
