using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EntityForge.Db.Rdf.Contracts;
using EntityForge.Db.Graph.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow(0, true, false)]
    [DataRow(0, true, true)]
    [DataRow(1, true, false)]
    [DataRow(1, true, true)]
    [DataRow(65536, true, false)]
    [DataRow(65536, true, true)]
    [DataRow(0, false, false)]
    [DataRow(0, false, true)]
    [DataRow(1, false, false)]
    [DataRow(1, false, true)]
    [DataRow(65536, false, false)]
    [DataRow(65536, false, true)]
    public async Task Query_sorted_scan_HTTP_enforces_operator_budget_and_explicit_completion(int diskBudget, bool rdf, bool distinct)
    {
        _directory = NewDirectory();
        var builder = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory)
            .UseQueryScans(new()
            {
                MaximumSortTemporaryBytes = diskBudget,
                PageSize = 1
            })
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0"));
        if (rdf)
        {
            builder.UseRdfProfile();
        }
        else
        {
            builder.UsePropertyGraphProfile();
        }
        await using var host = builder.Build();
        await host.StartAsync();
        var runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
        if (rdf)
        {
            await runtime.RdfDatabase.BulkLoadAsync(Guid.NewGuid(), new[] { 3, 1, 2, 1, 2 }.Select((value, index) =>
                new RdfQuad(new RdfIri("urn:" + index), new("urn:p"), new RdfLiteral(value.ToString(),
                    "http://www.w3.org/2001/XMLSchema#integer"))).ToArray());
        }
        else
        {
            await runtime.GraphDatabase.BulkLoadAsync(Guid.NewGuid(), new[] { 3, 1, 2, 1, 2 }.Select((value, index) =>
                new GraphVertex(GraphVertexId.New(), new HashSet<string>(), new Dictionary<string, DatabaseValue>
                {
                    ["score"] = new DatabaseValue.Int64(value)
                })).ToArray(), []);
        }
        using var client = Client(host);
        using var response = await client.PostAsJsonAsync(rdf ? "/v1/rdf/sparql/scan" : "/v1/graph/gql/query/scan",
            new
            {
                query = rdf ? "SELECT " + (distinct ? "DISTINCT " : "") + "?o { ?s <urn:p> ?o } ORDER BY ?o LIMIT 2" : "MATCH (n) RETURN " + (distinct ? "DISTINCT " : "") + "n.score AS score ORDER BY score LIMIT 2"
            });
        Assert.AreEqual(diskBudget < 2 ? HttpStatusCode.UnprocessableEntity : HttpStatusCode.OK, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        if (diskBudget < 2)
        {
            Assert.IsFalse(body.Contains("\"complete\":true", StringComparison.Ordinal));
            if (diskBudget == 1)
            {
                using var error = JsonDocument.Parse(body);
                Assert.AreEqual("query_scan_sort_budget_exceeded", error.RootElement.GetProperty("code").GetString());
            }
        }
        else
        {
            int completions = 0;
            var values = new List<string>();
            foreach (string line in body.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                using var frame = JsonDocument.Parse(line);
                if (frame.RootElement.GetProperty("type").GetString() == "complete")
                {
                    completions++;
                }
                else
                {
                    Assert.AreEqual(0, completions);
                    values.AddRange(frame.RootElement.GetProperty("rows").EnumerateArray()
                        .Select(row => rdf ? row.GetProperty("o").GetProperty("lexicalForm").GetString()! :
                            row.GetProperty("bindings").GetProperty("score").ToString()));
                }
            }
            CollectionAssert.AreEqual(distinct ? new[] { "1", "2" } : new[] { "1", "1" }, values);
            Assert.AreEqual(1, completions);
        }
        Assert.AreEqual(0, runtime.ActiveQueryScans);
    }
}
