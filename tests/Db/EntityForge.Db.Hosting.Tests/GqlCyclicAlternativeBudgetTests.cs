using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    public async Task Gql_cyclic_alternative_rechecks_enforce_work_limits_and_rollback()
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        string seed = "INSERT (:BudgetStart {name:'0'})" + string.Concat(Enumerable.Range(1, 40)
            .Select(index => "-[:step]->(:BudgetNode {name:'" + index + "'})"));
        using (var response = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = seed
        }))
        {
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        }
        string Pattern(bool bounded)
        {
            string label = bounded ? ":BudgetStart" : "";
            string Branch(string own, string other) => "(" + own + label +
                " WHERE " + own + ".name=" + other + ".name)-[:step]->()";
            return "MATCH REPEATABLE ELEMENTS p=(" + Branch("x", "c") + " |+| " + Branch("x", "c") +
                "),q=(" + Branch("c", "x") + " | " + Branch("c", "x") + ")";
        }
        async Task CheckBoundedQuery()
        {
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = Pattern(true) + " RETURN COUNT(*) AS answer"
            });
            string body = await response.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, body);
            using var result = JsonDocument.Parse(body);
            Assert.AreEqual(2, result.RootElement.GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetInt32());
        }
        await CheckBoundedQuery();
        foreach (bool update in new[] { false, true })
        {
            using var response = update
                ? await client.PostAsJsonAsync("/v1/graph/gql/update", new
                {
                    statement = "INSERT (:BudgetMarker) " + Pattern(false) + " SET c.changed=TRUE",
                    transactionId = Guid.NewGuid()
                })
                : await client.PostAsJsonAsync("/v1/graph/gql/query", new
                {
                    query = Pattern(false) + " RETURN p,q"
                });
            string body = await response.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, body);
            Assert.IsTrue(body.Contains("1,000 subquery", StringComparison.Ordinal), body);
            using var result = JsonDocument.Parse(body);
            Assert.IsFalse(result.RootElement.TryGetProperty("matches", out _));
            await CheckBoundedQuery();
        }
        foreach (string condition in new[] { "n:BudgetMarker", "n WHERE n.changed IS NOT NULL" })
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
