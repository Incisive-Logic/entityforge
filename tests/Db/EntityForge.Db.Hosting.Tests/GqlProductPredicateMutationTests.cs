using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("MATCH (a:Person {name:'A'})((x WHERE x.name='A' AND c.name='B')-[:knows]->()){1,2}(b),(c:Person WHERE a.name='A')", "a,b,c,x")]
    [DataRow("MATCH (a:Person {name:'A'})(()-[:knows]->()){1}(b {name:c.name}),(c:Person WHERE c.name=b.name)", "a,b,c")]
    [DataRow("MATCH (a:Person)(()-[:knows]->()){1}({name:c.name}),(c:Person WHERE a.name='A')", "a,c")]
    [DataRow("MATCH (a:Person WHERE EXISTS { MATCH (c:Person {name:'B'}) })(()-[:knows]->()){1}(b),(c:Person WHERE EXISTS { MATCH (a:Person {name:'A'}) })", "a,b,c")]
    public async Task Gql_cyclic_product_predicates_preserve_update_scope_rollback_and_replay(string pattern, string columns)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        async Task<string> Update(string statement, Guid? transaction = null, HttpStatusCode expected = HttpStatusCode.OK)
        {
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/update", new
            {
                statement,
                transactionId = transaction ?? Guid.NewGuid()
            });
            string body = await response.Content.ReadAsStringAsync();
            Assert.AreEqual(expected, response.StatusCode, body);
            return body;
        }
        async Task<int> Count(string condition)
        {
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = "MATCH (n:Person WHERE " + condition + ") RETURN COUNT(*) AS answer"
            });
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0]
                .GetProperty("bindings").GetProperty("answer").GetInt32();
        }
        await Update("INSERT (:Person {name:'A'})-[:knows]->(:Person {name:'B'})-[:knows]->(:Person {name:'C'})");
        Guid transaction = Guid.NewGuid();
        string statement = pattern + " SET c.score=COALESCE(c.score,0)+1 RETURN *";
        string receipt = await Update(statement, transaction);
        using (var document = JsonDocument.Parse(receipt))
        {
            var rows = document.RootElement.GetProperty("result").GetProperty("matches");
            Assert.AreEqual(1, rows.GetArrayLength());
            CollectionAssert.AreEquivalent(columns.Split(','), rows[0].GetProperty("bindings")
                .EnumerateObject().Select(property => property.Name).ToArray());
        }
        Assert.AreEqual(1, await Count("n.name='B' AND n.score=1"));
        Assert.AreEqual(2, await Count("n.score IS NULL"));
        await Update(pattern + " SET c.marker=TRUE SET c.score=1/0", expected: HttpStatusCode.BadRequest);
        Assert.AreEqual(1, await Count("n.name='B' AND n.score=1 AND n.marker IS NULL"));
        await Update("MATCH (n:Person {name:'B'}) SET n.name='Changed'");
        Assert.AreEqual(receipt, await Update(statement, transaction));
        Assert.AreEqual(1, await Count("n.name='Changed' AND n.score=1 AND n.marker IS NULL"));
        await host.StopAsync();
    }
}
