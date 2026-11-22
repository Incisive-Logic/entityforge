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
    [DataRow(false)]
    [DataRow(true)]
    public async Task Gql_explain_retains_one_snapshot_across_branches_and_analysis(bool analyze)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureServices(services => services.AddSingleton<IEntityForgeDbAuthorizationPolicy, AllowEntityForgeDbAuthorizationPolicy>())
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        var database = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>().GraphDatabase;
        var before = await database.CommitAsync(new(Guid.NewGuid(),
            [new CreateVertex(new(Guid.NewGuid()), ["Before"])]));
        int captures = 0;
        database.AfterExplainSnapshotCaptured = async () =>
        {
            if (Interlocked.Increment(ref captures) == 1)
            {
                // The first branch owns its own lease. Once it returns, the outer
                // HTTP operation must still retain that version for later branches
                // and ANALYZE, even though a newer version has been published.
                await database.CommitAsync(new(Guid.NewGuid(),
                    [new CreateVertex(new(Guid.NewGuid()), ["Before"])]));
            }
        };
        using var client = Client(host);
        try
        {
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/explain",
                new GraphGqlExplainRequest(new("MATCH (n:Before) RETURN n UNION ALL MATCH (n:Before) RETURN n"), analyze));
            string json = await response.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, json);
            using var body = JsonDocument.Parse(json);
            Assert.AreEqual(before.Value, body.RootElement.GetProperty("snapshotVersion").GetInt64());
            Assert.IsTrue(captures >= 2);
            Assert.IsTrue(database.CurrentVersion.Value > before.Value);
            if (analyze)
            {
                Assert.AreEqual(2, body.RootElement.GetProperty("actualMatches").GetInt32());
            }
        }
        finally
        {
            database.AfterExplainSnapshotCaptured = null;
        }
    }

    [TestMethod]
    [DataRow("FOR n IN [1,2,3] FILTER n>1 RETURN n", 2)]
    [DataRow("RETURN 1 AS n UNION ALL RETURN 1 AS n", 2)]
    [DataRow("RETURN 1 AS n UNION RETURN 1 AS n", 1)]
    [DataRow("OPTIONAL MATCH (n:Absent) RETURN n", 1)]
    [DataRow("FOR x IN [1,2] CALL (x) { RETURN x AS y } RETURN y", 2)]
    [DataRow("RETURN 1 AS n UNION DISTINCT RETURN 1 AS n UNION RETURN 2 AS n", 2)]
    [DataRow("RETURN 1 AS n UNION ALL RETURN 1 AS n UNION ALL RETURN 1 AS n", 3)]
    public async Task Gql_standard_explain_stages_reports_plan_and_actual_final_cardinality(string query, int count)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureServices(services => services.AddSingleton<IEntityForgeDbAuthorizationPolicy, AllowEntityForgeDbAuthorizationPolicy>())
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        foreach (bool analyze in new[] { false, true })
        {
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/explain", new GraphGqlExplainRequest(new(query), analyze));
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.AreEqual(analyze, body.RootElement.GetProperty("analyzed").GetBoolean());
            Assert.AreEqual(0L, body.RootElement.GetProperty("snapshotVersion").GetInt64());
            Assert.IsTrue(body.RootElement.GetProperty("plan").TryGetProperty("operation", out _));
            if (query.Contains("UNION", StringComparison.Ordinal))
            {
                var plan = body.RootElement.GetProperty("plan");
                string[] expected = query.Split("UNION", StringSplitOptions.None).Skip(1)
                    .Select(branch => branch.TrimStart().StartsWith("ALL", StringComparison.Ordinal) ? "ALL" : "DISTINCT").ToArray();
                CollectionAssert.AreEqual(expected, plan.GetProperty("conjunctions").EnumerateArray().Select(item => item.GetString()).ToArray());
            }
            if (analyze)
            {
                Assert.AreEqual(count, body.RootElement.GetProperty("actualMatches").GetInt32());
            }
            else
            {
                Assert.IsTrue(!body.RootElement.TryGetProperty("actualMatches", out var actual) || actual.ValueKind == JsonValueKind.Null);
            }
        }
    }
}
