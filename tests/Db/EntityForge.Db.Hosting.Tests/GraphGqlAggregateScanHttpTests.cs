using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EntityForge.Db.Graph.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow(0, false)]
    [DataRow(1, false)]
    [DataRow(65536, false)]
    [DataRow(0, true)]
    [DataRow(1, true)]
    [DataRow(65536, true)]
    public async Task Gql_aggregate_scan_HTTP_preserves_diagnostics_budget_and_completion(int budget, bool distinct)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .UseQueryScans(new()
            {
                MaximumSortTemporaryBytes = budget,
                PageSize = 1
            })
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        var runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
        await runtime.GraphDatabase.BulkLoadAsync(Guid.NewGuid(), Enumerable.Range(0, 4).Select(i => new GraphVertex(GraphVertexId.New(),
            new HashSet<string>(), i == 0 ? new Dictionary<string, DatabaseValue>() : new Dictionary<string, DatabaseValue>
            {
                ["score"] = new DatabaseValue.Int64(i)
            })).ToArray(), []);
        using var client = Client(host);
        string query = distinct ? "MATCH (n) RETURN COUNT(DISTINCT n.score) AS c, SUM(DISTINCT n.score) AS s" :
            "MATCH (n) RETURN COUNT(n.score) AS c, SUM(n.score) AS s";
        using var response = await client.PostAsJsonAsync("/v1/graph/gql/query/scan",
            new
            {
                query
            });
        string body = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(budget < 2 ? HttpStatusCode.UnprocessableEntity : HttpStatusCode.OK, response.StatusCode, body);
        if (budget >= 2)
        {
            int rows = 0, completed = 0, warnings = 0;
            foreach (string line in body.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                using var frame = JsonDocument.Parse(line);
                if (frame.RootElement.GetProperty("type").GetString() == "complete")
                {
                    completed++;
                    continue;
                }
                Assert.AreEqual(0, completed);
                warnings += frame.RootElement.GetProperty("diagnostics").EnumerateArray().Count(item => item.GetProperty("code").GetString() == "01G11");
                foreach (var row in frame.RootElement.GetProperty("rows").EnumerateArray())
                {
                    Assert.AreEqual("3", row.GetProperty("bindings").GetProperty("c").ToString());
                    Assert.AreEqual("6", row.GetProperty("bindings").GetProperty("s").ToString());
                    rows++;
                }
            }
            Assert.AreEqual(1, rows);
            Assert.AreEqual(1, completed);
            Assert.AreEqual(1, warnings);
        }
        else if (budget == 1)
        {
            using var error = JsonDocument.Parse(body);
            Assert.AreEqual("query_scan_sort_budget_exceeded", error.RootElement.GetProperty("code").GetString());
        }
        Assert.AreEqual(0, runtime.ActiveQueryScans);
    }
}
