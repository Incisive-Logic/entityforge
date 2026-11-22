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
    public async Task Gql_repeated_bag_branch_growth_fails_without_partial_results_or_updates(bool traversal)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using (var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (:Person {name:'Start'})" + string.Concat(Enumerable.Repeat("-[:knows]->(:Step)", 8)),
            transactionId = Guid.NewGuid()
        }))
        {
            Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode, await seed.Content.ReadAsStringAsync());
        }
        string alternatives = string.Join(" |+| ", Enumerable.Repeat("()-[:knows]->()", 4));
        string Pattern(int count, bool scan = false) => "MATCH p=(c:Person {name:'Start'})(" + alternatives + "){" + count +
            (scan ? "," + (count + 16) : "") + "}()";
        string expectedLimit = traversal ? "RANGE exceeds 10,000 elements" : "1,000 subquery or loop invocations";
        async Task<int> Count(string query)
        {
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query
            });
            string body = await response.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, body);
            using var document = JsonDocument.Parse(body);
            return document.RootElement.GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetInt32();
        }
        Assert.AreEqual(16, await Count(Pattern(2) + " RETURN COUNT(*) AS answer"));
        using (var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = Pattern(8, traversal) + " RETURN PATH_LENGTH(p) AS answer"
        }))
        {
            string body = await response.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, body);
            StringAssert.Contains(body, expectedLimit);
            using var problem = JsonDocument.Parse(body);
            Assert.IsFalse(problem.RootElement.TryGetProperty("matches", out _));
        }
        using (var response = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (:Marker) " + Pattern(8, traversal) + " SET c.changed=TRUE",
            transactionId = Guid.NewGuid()
        }))
        {
            string body = await response.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, body);
            StringAssert.Contains(body, expectedLimit);
        }
        Assert.AreEqual(0, await Count("MATCH (n:Marker) RETURN COUNT(*) AS answer"));
        Assert.AreEqual(0, await Count("MATCH (n:Person WHERE n.changed IS NOT NULL) RETURN COUNT(*) AS answer"));
        Assert.AreEqual(16, await Count(Pattern(2) + " RETURN COUNT(*) AS answer"));
        await host.StopAsync();
    }
}
