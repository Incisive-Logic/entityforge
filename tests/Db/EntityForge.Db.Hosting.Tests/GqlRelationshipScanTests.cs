using EntityForge.Db.Gql;
using System.Net;
using System.Net.Http.Json;
using EntityForge.Db.Graph.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("MATCH (a:EdgeScan {name:'A'})-[e:link]->(b) WHERE e.score>1 RETURN b.name AS answer", "C")]
    [DataRow("MATCH (a:EdgeScan {name:'C'})<-[e:link]-(b) RETURN b.name AS answer", "A,B")]
    [DataRow("MATCH (a:EdgeScan)-[e:link]-(b) RETURN a.name||b.name AS answer", "AB,BA,AC,CA,BC,CB,BB")]
    [DataRow("MATCH (a:EdgeScan)-[e:link]->(b) RETURN b.name AS answer", "B,C,C,B")]
    [DataRow("MATCH (a:Missing)-[e:link]->(b) RETURN b.name AS answer", "")]
    [DataRow("MATCH (a:EdgeScan)-[e:link]->(b) RETURN b.name AS answer LIMIT 0", "")]
    [DataRow("MATCH (a:EdgeScan)-[e:link]->(b) RETURN b.name AS answer ORDER BY e.score DESC", "B,C,C,B")]
    [DataRow("MATCH (a:EdgeScan)-[e:link]->(b) RETURN e.score*2 AS answer ORDER BY answer DESC OFFSET 1 LIMIT 2", "6,4")]
    [DataRow("MATCH (a:EdgeScan)-[e:link]->(b) RETURN DISTINCT b.name AS answer ORDER BY answer DESC", "C,B")]
    [DataRow("MATCH (a:EdgeScan)-[e:link]->(b) RETURN DISTINCT b.name AS answer ORDER BY answer OFFSET 1 LIMIT 1", "C")]
    [DataRow("MATCH (a:Missing)-[e:link]->(b) RETURN b.name AS answer ORDER BY answer", "")]
    [DataRow("MATCH (a:EdgeScan)-[e:link]->(b) RETURN DISTINCT b.name AS answer", "B,C")]
    [DataRow("MATCH (a:EdgeScan)-[e:link]->(b) RETURN e.score AS answer ORDER BY CASE WHEN e.score=2 THEN NULL ELSE e.score END ASC NULLS FIRST", "2,1,3,4")]
    [DataRow("MATCH (a:EdgeScan)-[e:link]->(b) RETURN e.score AS answer ORDER BY CASE WHEN e.score=2 THEN NULL ELSE e.score END DESC NULLS LAST", "4,3,1,2")]
    [DataRow("MATCH (a:EdgeScan)-[e:link]->(b) RETURN COUNT(*) AS answer", "4")]
    [DataRow("MATCH (a:EdgeScan)-[e:link]->(b) RETURN COUNT(e) AS answer", "4")]
    [DataRow("MATCH (a:EdgeScan)-[e:link]->(b) RETURN COUNT(DISTINCT e) AS answer", "4")]
    [DataRow("MATCH (a:EdgeScan)-[e:link]->(b) RETURN COUNT(DISTINCT a) AS answer", "2")]
    [DataRow("MATCH (a:EdgeScan)-[e:link]->(b) RETURN COUNT(DISTINCT b) AS answer", "2")]
    [DataRow("MATCH (a:EdgeScan)-[e:link]->(b) RETURN SUM(e.score) AS answer", "10")]
    [DataRow("MATCH (a:EdgeScan)-[e:link]->(b) RETURN MIN(e.score)+MAX(e.score) AS answer", "5")]
    [DataRow("MATCH (a:EdgeScan)-[e:link]->(b) RETURN a.name AS source, SUM(e.score) AS answer GROUP BY source ORDER BY source", "3,7")]
    [DataRow("MATCH (a:EdgeScan)-[e:link]->(b) RETURN SUM(e.score) AS s NEXT FILTER s>5 RETURN s+1 AS answer", "11")]
    [DataRow("MATCH (a:Missing)-[e:link]->(b) RETURN COUNT(*) AS answer", "0")]
    [DataRow("MATCH (a:EdgeScan)-[e:link]->(b) RETURN SUM(1/0) AS answer LIMIT 0", "")]
    [DataRow("MATCH (a:EdgeScan)-[e:link]->(b) RETURN AVG(e.score) AS answer", "2.5")]
    [DataRow("MATCH (a:EdgeScan)-[e:link]->(b) RETURN COUNT(e.absent) AS answer", "0")]
    [DataRow("MATCH (a:EdgeScan)-[e:link]->(b) RETURN SUM(e.absent) AS answer", "null")]
    [DataRow("MATCH (a:EdgeScan)-[e:link]-(b) RETURN COUNT(*) AS answer", "7")]
    [DataRow("MATCH (a:EdgeScan)-[e:link]-(b) RETURN COUNT(DISTINCT e) AS answer", "4")]
    [DataRow("MATCH (a:EdgeScan)-[e:link]->(b) WHERE e.score>2 RETURN SUM(e.score) AS answer", "7")]
    [DataRow("MATCH (a:EdgeScan)-[e:link]->(b)-[f:link]->(c) RETURN c.name AS answer", "C,B,C")]
    [DataRow("MATCH REPEATABLE ELEMENTS (a:EdgeScan)-[e:link]->(b)-[f:link]->(c) RETURN c.name AS answer", "C,B,C,B")]
    [DataRow("MATCH (a:EdgeScan)-[e:link]->(b)-[f:link]->(c) WHERE a.name='A' RETURN c.name AS answer", "C,B")]
    [DataRow("MATCH (a:EdgeScan)-[e:link]->(b)-[f:link]->(c) RETURN DISTINCT c.name AS answer ORDER BY answer", "B,C")]
    [DataRow("MATCH (a:EdgeScan)-[e:link]->(b)-[f:link]->(c) RETURN e.score+f.score AS answer ORDER BY answer DESC OFFSET 1 LIMIT 1", "5")]
    [DataRow("MATCH (a:EdgeScan)-[e:link]->(b),(a)-[f:link]->(c) RETURN a.name||b.name||c.name AS answer", "ABC,ACB,BCB,BBC")]
    [DataRow("MATCH (a:EdgeScan)-[e:link]-(b)-[f:link]-(c) RETURN a.name||c.name AS answer", "AC,AB,AB,BC,BA,BA,BC,CB,CA,CB")]
    [DataRow("MATCH (a:EdgeScan)-[e:link]->(b)-[f:link]->(a) RETURN a.name AS answer", "")]
    [DataRow("MATCH (a:EdgeScan)-[e:link]->(b)-[f:link]->(c) RETURN c.name AS answer LIMIT 0", "")]
    [DataRow("MATCH (a:EdgeScan)-[e:link]->(b)-[f:link]->(c) RETURN COUNT(*) AS answer", "3")]
    [DataRow("MATCH REPEATABLE ELEMENTS (a:EdgeScan)-[e:link]->(b)-[f:link]->(c) RETURN COUNT(*) AS answer", "4")]
    [DataRow("MATCH (a:EdgeScan)-[e:link]->(b)-[f:link]->(c) RETURN SUM(e.score+f.score) AS answer", "16")]
    [DataRow("MATCH (a:EdgeScan)-[e:link]->(b)-[f:link]->(c) RETURN COUNT(DISTINCT f) AS answer", "2")]
    [DataRow("MATCH (a:EdgeScan)-[e:link]->(b)-[f:link]->(c) RETURN COUNT(DISTINCT b) AS answer", "1")]
    [DataRow("MATCH (a:EdgeScan)-[e:link]->(b)-[f:link]->(c) RETURN a.name AS source, SUM(f.score) AS answer GROUP BY source ORDER BY source", "7,3")]
    [DataRow("MATCH (a:EdgeScan)-[e:link]->(b)-[f:link]->(c) RETURN COUNT(*) AS n NEXT FILTER n>2 RETURN n+1 AS answer", "4")]
    [DataRow("MATCH (a:Missing)-[e:link]->(b)-[f:link]->(c) RETURN COUNT(*) AS answer", "0")]
    [DataRow("MATCH (a:EdgeScan)-[e:link]->(b)-[f:link]->(c) RETURN SUM(1/0) AS answer LIMIT 0", "")]
    [DataRow("MATCH (a:EdgeScan)-[e:link]->(b)-[f:link]->(c) WHERE a.name='A' RETURN SUM(f.score) AS answer", "7")]
    public async Task Gql_relationship_scan_preserves_bags_and_snapshot_during_writes(string query, string expected)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (a:EdgeScan {name:'A'}),(b:EdgeScan {name:'B'}),(c:EdgeScan {name:'C'})," +
                "(a)-[:link {score:1}]->(b),(a)-[:link {score:2}]->(c),(b)-[:link {score:3}]->(c),(b)-[:link {score:4}]->(b)"
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode, await seed.Content.ReadAsStringAsync());
        var database = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>().GraphDatabase;
        var answers = new List<string>();
        DatabaseVersion? version = null;
        bool changed = false;
        int completions = 0;
        await foreach (var page in GraphGqlScan.ExecuteNativeAsync(new(query), database, new(PageSize: 1, MaximumSortTemporaryBytes: 1024 * 1024)))
        {
            version ??= page.Version;
            Assert.AreEqual(version.Value, page.Version);
            Assert.IsTrue(page.Rows.Count <= 1);
            Assert.AreEqual(0, completions);
            completions += page.Complete ? 1 : 0;
            answers.AddRange(page.Rows.Select(row => (row.Bindings["answer"] as EntityForge.Query.Values.LiteralValue)?.LexicalValue ?? "null"));
            if (!changed && page.Rows.Count > 0)
            {
                using var update = await client.PostAsJsonAsync("/v1/graph/gql/update", new
                {
                    statement = "MATCH (n:EdgeScan) SET n.name='changed'"
                });
                Assert.AreEqual(HttpStatusCode.OK, update.StatusCode, await update.Content.ReadAsStringAsync());
                changed = true;
            }
        }
        Assert.AreEqual(1, completions);
        if (query.Contains("ORDER BY", StringComparison.Ordinal))
        {
            CollectionAssert.AreEqual(expected.Split(',', StringSplitOptions.RemoveEmptyEntries), answers);
        }
        else
        {
            CollectionAssert.AreEquivalent(expected.Split(',', StringSplitOptions.RemoveEmptyEntries), answers);
        }
    }
}
