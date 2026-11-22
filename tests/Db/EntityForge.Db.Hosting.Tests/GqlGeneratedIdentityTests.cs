using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("INSERT (a:Person),(b:Person),(a)-[:knows]->(b)", 2)]
    [DataRow("INSERT (a:Person {name:'A'}),(b:Person {name:'B'}),(a)-[e:knows {weight:2}]->(b)", 2)]
    [DataRow("INSERT (),(:Person),(:Person {name:'anonymous'})", 3)]
    [DataRow("INSERT (a:Person&Employee {name:'-['})", 1)]
    [DataRow("INSERT (a:Person {name:'A'})-[:knows]->(b:Person {name:'B'})-[:knows]->(:Person {name:'C'})", 3)]
    [DataRow("INSERT (:Person)<-[:knows]-(a:Person)", 2)]
    public async Task Gql_standard_generated_identities_are_distinct_and_stable_on_retry(string statement, int nodes)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        Guid transactionId = Guid.NewGuid();
        long? version = null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/update", new
            {
                statement,
                transactionId
            });
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            var result = await response.Content.ReadFromJsonAsync<JsonElement>();
            long actualVersion = result.GetProperty("version").GetInt64();
            Assert.AreEqual(version ?? actualVersion, actualVersion);
            version = actualVersion;
        }
        using var second = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement,
            transactionId = Guid.NewGuid()
        });
        Assert.AreEqual(HttpStatusCode.OK, second.StatusCode, await second.Content.ReadAsStringAsync());
        using var query = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (n) RETURN COUNT(DISTINCT n) AS answer"
        });
        Assert.AreEqual(HttpStatusCode.OK, query.StatusCode, await query.Content.ReadAsStringAsync());
        var answer = await query.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual(nodes * 2, answer.GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetInt32());
    }
}
