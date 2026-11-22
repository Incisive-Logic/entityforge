using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    public async Task Gql_different_shape_bag_traversal_limits_rollback_and_recover(int exposure)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using (var response = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (:Person {name:'Start'})" + string.Concat(Enumerable.Repeat("-[:knows]->(:Step)", 8)),
            transactionId = Guid.NewGuid()
        }))
        {
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        }
        string shortBranch = exposure != 0 ? "(a) -> (b)" : "() -> ()";
        string longBranch = exposure != 0 ? "(a) -> () -> (b)" : "() -> () -> ()";
        string declaration = exposure == 2 ? "segment=" : "";
        if (exposure == 3)
        {
            shortBranch = "(segment=" + shortBranch + ")";
            longBranch = "(segment=" + longBranch + ")";
        }
        using (var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH p=(" + declaration + shortBranch + " |+| " + longBranch + "){8} RETURN COUNT(*) AS answer"
        }))
        {
            string body = await response.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, body);
            using var result = JsonDocument.Parse(body);
            Assert.AreEqual(1, result.RootElement.GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetInt32());
        }
        string pattern = "MATCH p=(" + declaration + string.Join(" |+| ", Enumerable.Repeat(shortBranch, 4)) + " |+| " + longBranch + ")+";
        if (exposure == 4)
        {
            string CyclicPattern(int copies, string bound)
            {
                string Segment(string name, string other, string nodePrefix) => "(" + name + "=" +
                    string.Join(" |+| ", Enumerable.Repeat("(" + nodePrefix + "A) -> (" + nodePrefix + "B)", copies)) +
                    " |+| (" + nodePrefix + "A) -> () -> (" + nodePrefix + "B) WHERE CARDINALITY(" + other + ")>=0)" + bound;
                return "MATCH p=" + Segment("prefixes", "tails", "left") + Segment("tails", "prefixes", "right");
            }
            using var bounded = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = CyclicPattern(1, "{4}") + " RETURN COUNT(*) AS answer"
            });
            string boundedBody = await bounded.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, bounded.StatusCode, boundedBody);
            using var boundedResult = JsonDocument.Parse(boundedBody);
            Assert.AreEqual(1, boundedResult.RootElement.GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetInt32());
            pattern = CyclicPattern(4, "+");
        }
        foreach (bool update in new[] { false, true })
        {
            using var response = update
                ? await client.PostAsJsonAsync("/v1/graph/gql/update", new
                {
                    statement = "INSERT (:Marker) MATCH (c:Person) " + pattern + " SET c.changed=TRUE",
                    transactionId = Guid.NewGuid()
                })
                : await client.PostAsJsonAsync("/v1/graph/gql/query", new
                {
                    query = pattern + " RETURN p"
                });
            string body = await response.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, body);
            Assert.IsTrue(body.Contains("path decomposition", StringComparison.OrdinalIgnoreCase) ||
                body.Contains("10,000", StringComparison.Ordinal) || body.Contains("1,000 subquery", StringComparison.Ordinal), body);
            using var problem = JsonDocument.Parse(body);
            Assert.IsFalse(problem.RootElement.TryGetProperty("matches", out _));
        }
        foreach (string condition in new[] { "n:Marker", "n:Person WHERE n.changed IS NOT NULL" })
        {
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = "MATCH (" + condition + ") RETURN COUNT(*) AS answer"
            });
            string body = await response.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, body);
            using var result = JsonDocument.Parse(body);
            Assert.AreEqual(0, result.RootElement.GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetInt32());
        }
        await host.StopAsync();
    }
}
