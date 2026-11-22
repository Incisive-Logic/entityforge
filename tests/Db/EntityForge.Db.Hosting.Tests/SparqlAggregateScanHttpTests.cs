using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EntityForge.Db.Rdf.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    public async Task Sparql_aggregate_scan_HTTP_counts_filtered_source_candidates_and_recovers()
    {
        // An empty result must not bypass the storage candidate budget.
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UseRdfProfile()
            .UseQueryScans(new()
            {
                MaximumExaminedElements = 19,
                MaximumSortTemporaryBytes = 65536,
                PageSize = 3
            })
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        var runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
        await runtime.RdfDatabase.CommitAsync(new(Guid.NewGuid(), Enumerable.Range(0, 20).Select(index =>
            (RdfMutation)new AssertRdfQuad(new(new RdfIri("urn:shared"), new("urn:present"), new RdfLiteral(index.ToString())))).ToArray()));
        using var client = Client(host);
        using var rejected = await client.PostAsJsonAsync("/v1/rdf/sparql/scan",
            new
            {
                query = "SELECT (COUNT(*) AS ?total) { <urn:shared> <urn:absent> ?o }"
            });
        string body = await rejected.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, rejected.StatusCode, body);
        Assert.IsFalse(body.Contains("\"complete\":true", StringComparison.Ordinal));
        Assert.AreEqual(0, runtime.ActiveQueryScans);
        await runtime.RdfDatabase.CreateCheckpointAsync();
        using var recovered = await client.PostAsJsonAsync("/v1/rdf/sparql/scan",
            new
            {
                query = "SELECT (COUNT(*) AS ?total) { <urn:missing> <urn:absent> ?o }"
            });
        Assert.AreEqual(HttpStatusCode.OK, recovered.StatusCode);
        StringAssert.Contains(await recovered.Content.ReadAsStringAsync(), "\"complete\":true");
    }

    [TestMethod]
    [DataRow(0, false)]
    [DataRow(1, false)]
    [DataRow(65536, false)]
    [DataRow(0, true)]
    [DataRow(1, true)]
    [DataRow(65536, true)]
    public async Task Sparql_aggregate_scan_HTTP_enforces_budget_and_returns_complete_aggregate(int budget, bool distinct)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UseRdfProfile()
            .UseQueryScans(new()
            {
                MaximumSortTemporaryBytes = budget,
                PageSize = 1
            })
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        var runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
        await runtime.RdfDatabase.BulkLoadAsync(Guid.NewGuid(), Enumerable.Range(1, 3).Select(i =>
            new RdfQuad(new RdfIri("urn:s:" + i), new("urn:p"), new RdfLiteral(i.ToString(),
                "http://www.w3.org/2001/XMLSchema#integer"))).ToArray());
        using var client = Client(host);
        string query = distinct ? "SELECT (COUNT(DISTINCT *) AS ?c) (SUM(DISTINCT ?o) AS ?sum) { ?s <urn:p> ?o }" :
            "SELECT (COUNT(*) AS ?c) (SUM(?o) AS ?sum) { ?s <urn:p> ?o }";
        using var response = await client.PostAsJsonAsync("/v1/rdf/sparql/scan",
            new
            {
                query
            });
        Assert.AreEqual(budget < 2 ? HttpStatusCode.UnprocessableEntity : HttpStatusCode.OK, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        if (budget >= 2)
        {
            int rows = 0, completed = 0;
            foreach (string line in body.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                using var frame = JsonDocument.Parse(line);
                if (frame.RootElement.GetProperty("type").GetString() == "complete")
                {
                    completed++;
                    continue;
                }
                Assert.AreEqual(0, completed);
                foreach (var row in frame.RootElement.GetProperty("rows").EnumerateArray())
                {
                    Assert.AreEqual("3", row.GetProperty("c").GetProperty("lexicalForm").GetString());
                    Assert.AreEqual("6", row.GetProperty("sum").GetProperty("lexicalForm").GetString());
                    rows++;
                }
            }
            Assert.AreEqual(1, rows);
            Assert.AreEqual(1, completed);
        }
        else if (budget == 1)
        {
            using var error = JsonDocument.Parse(body);
            Assert.AreEqual("query_scan_sort_budget_exceeded", error.RootElement.GetProperty("code").GetString());
        }
        Assert.AreEqual(0, runtime.ActiveQueryScans);
        Assert.AreEqual(0, runtime.RdfDatabase.RetainedQueryContinuationCount);
    }
}
