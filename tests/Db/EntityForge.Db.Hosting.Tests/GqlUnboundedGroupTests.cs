using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    public async Task Gql_standard_unbounded_multi_edge_groups_validate_each_direction_and_label()
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        foreach (string statement in new[]
        {
            "FOR ordinal IN [0,1,2,3,4] INSERT (:Zigzag {ordinal:ordinal})",
            "MATCH (a:Zigzag),(b:Zigzag) WHERE MOD(a.ordinal,2)=0 AND b.ordinal=a.ordinal+1 INSERT (a)-[:outward]->(b)",
            "MATCH (a:Zigzag),(b:Zigzag) WHERE MOD(a.ordinal,2)=0 AND b.ordinal=a.ordinal-1 INSERT (a)-[:inward {allowed:TRUE}]->(b)",
            "MATCH (a:Zigzag {ordinal:2}),(b:Zigzag {ordinal:1}) INSERT (a)-[:inward {allowed:FALSE}]->(b)"
        })
        {
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/update", new
            {
                statement,
                transactionId = Guid.NewGuid()
            });
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        }
        foreach (var (query, expected) in new[]
        {
            ("MATCH p=(origin:Zigzag {ordinal:0})((a)-[e:outward]->(b)<-[f:inward {allowed:TRUE}]-(c))+(destination:Zigzag {ordinal:4}) RETURN CASE WHEN PATH_LENGTH(p)=4 AND SIZE(a)=2 AND SIZE(b)=2 AND SIZE(c)=2 AND SIZE(e)=2 AND SIZE(f)=2 THEN 1 ELSE 0 END AS answer", 1),
            ("MATCH p=(origin:Zigzag {ordinal:0})((a)-[e:outward]->(b)<-[f:inward {allowed:TRUE}]-(c))+(destination:Zigzag {ordinal:4}) FOR middle IN b RETURN SUM(middle.ordinal) AS answer", 4),
            ("MATCH p=(origin:Zigzag {ordinal:0})((a)-[e:outward]->(b)<-[f:outward]-(c))+(destination:Zigzag {ordinal:4}) RETURN COUNT(*) AS answer", 0),
            ("MATCH p=(origin:Zigzag {ordinal:4})((a)-[f:inward {allowed:TRUE}]->(b)<-[e:outward]-(c)){2,}(destination:Zigzag {ordinal:0}) RETURN COUNT(*) AS answer", 1)
        })
        {
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query
            });
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, query + ": " + await response.Content.ReadAsStringAsync());
            var rows = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
            Assert.AreEqual(1, rows.GetArrayLength(), query);
            Assert.AreEqual(expected, rows[0].GetProperty("bindings").GetProperty("answer").GetInt32(), query);
        }
        await host.StopAsync();
    }

    [TestMethod]
    public async Task Gql_standard_unbounded_single_edge_groups_preserve_lists_predicates_and_replay()
    {
        _directory = NewDirectory();
        var vertices = new
        {
            statement = "FOR ordinal IN [" + String.Join(',', Enumerable.Range(0, 19)) + "] INSERT (:LongGroup {ordinal:ordinal})",
            transactionId = Guid.NewGuid()
        };
        var edges = new
        {
            statement = "MATCH (a:LongGroup),(b:LongGroup) WHERE b.ordinal=a.ordinal+1 INSERT (a)-[:groupStep {allowed:TRUE}]->(b)",
            transactionId = Guid.NewGuid()
        };
        var parallel = new
        {
            statement = "MATCH (a:LongGroup {ordinal:0}),(b:LongGroup {ordinal:1}) INSERT (a)-[:groupStep {allowed:FALSE}]->(b)",
            transactionId = Guid.NewGuid()
        };
        const string start = "(origin:LongGroup {ordinal:0})";
        const string end = "(destination:LongGroup {ordinal:18})";
        const string group = "((starts:LongGroup)-[links:groupStep WHERE links.allowed IS TRUE]->(ends:LongGroup))";
        var write = new
        {
            statement = "MATCH p=" + start + group + "{18,}" + end + " SET destination.visited=TRUE RETURN COUNT(*) AS answer",
            transactionId = Guid.NewGuid()
        };
        string? receipt = null;
        var quantifiedWrite = new
        {
            statement = "MATCH p=" + start + "-[links IS groupStep|otherStep WHERE links.allowed IS TRUE]->{18,}" + end +
                " SET destination.quantified=TRUE RETURN SIZE(links) AS answer",
            transactionId = Guid.NewGuid()
        };
        string? quantifiedReceipt = null;
        for (int restart = 0; restart < 2; restart++)
        {
            await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
                .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
            await host.StartAsync();
            using var client = Client(host);
            foreach (var seed in new[] { vertices, edges, parallel })
            {
                using var seeded = await client.PostAsJsonAsync("/v1/graph/gql/update", seed);
                Assert.AreEqual(HttpStatusCode.OK, seeded.StatusCode, await seeded.Content.ReadAsStringAsync());
            }
            foreach (var (query, expected) in new[]
            {
                ("MATCH p=" + start + group + "{18,}" + end + " RETURN COUNT(*) AS answer", 1),
                ("MATCH p=" + start + group + "{18,}" + end + " RETURN CASE WHEN p IS TYPED PATH NOT NULL AND [p] IS TYPED PATH NOT NULL LIST NOT NULL AND p IS NOT TYPED NODE THEN 1 ELSE 0 END AS answer", 1),
                ("MATCH p=" + start + group + "{18,}" + end + " RETURN PATH_LENGTH(CAST(p AS PATH NOT NULL)) AS answer", 18),
                ("OPTIONAL MATCH p=" + start + "-[links:missingStep]->+(destination:LongGroup) RETURN CASE WHEN p IS TYPED PATH AND p IS NOT TYPED PATH NOT NULL AND [p] IS NOT TYPED LIST<PATH NOT NULL> THEN 1 ELSE 0 END AS answer", 1),
                ("MATCH p=" + start + "-[links IS groupStep|otherStep WHERE links.allowed IS TRUE]->{18,}" + end + " RETURN SIZE(links) AS answer", 18),
                ("MATCH p=ALL SHORTEST " + start + "-[links IS groupStep&!missingStep WHERE links.allowed IS TRUE]->+(destination:LongGroup {ordinal:2}) RETURN SUM(PATH_LENGTH(p)) AS answer", 2),
                ("MATCH p=" + start + "-[links WHERE links.allowed IS TRUE]->{1,2}(destination:LongGroup) RETURN SUM(SIZE(links)) AS answer", 3),
                ("MATCH p=" + start + "-[links WHERE FALSE]->*(destination:LongGroup {ordinal:0}) RETURN CASE WHEN PATH_LENGTH(p)=0 AND SIZE(links)=0 AND origin=destination THEN 1 ELSE 0 END AS answer", 1),
                ("OPTIONAL MATCH p=" + start + "-[links IS missingStep|otherMissing WHERE links.allowed IS TRUE]->+(destination:LongGroup {ordinal:18}) RETURN CASE WHEN COUNT(*)=1 AND COUNT(p)=0 THEN 1 ELSE 0 END AS answer", 1),
                ("MATCH p=" + start + "((a)-[e:groupStep {allowed:TRUE}]->(b)-[f:groupStep {allowed:TRUE}]->(c)){9,}" + end + " RETURN CASE WHEN PATH_LENGTH(p)=18 AND SIZE(a)=9 AND SIZE(b)=9 AND SIZE(c)=9 AND SIZE(e)=9 AND SIZE(f)=9 THEN 1 ELSE 0 END AS answer", 1),
                ("MATCH p=" + start + "((a)-[e:groupStep {allowed:TRUE}]->(b)-[f:groupStep {allowed:TRUE}]->(c)){9,}" + end + " FOR middle IN b RETURN SUM(middle.ordinal) AS answer", 81),
                ("MATCH p=" + start + "((a)-[e:groupStep {allowed:TRUE}]->(b)-[f:groupStep {allowed:TRUE}]->(c))+(destination:LongGroup {ordinal:17}) RETURN COUNT(*) AS answer", 0),
                ("MATCH p=ALL SHORTEST " + start + "((a)-[e:groupStep {allowed:TRUE}]->(b)-[f:groupStep {allowed:TRUE}]->(c))+(destination:LongGroup {ordinal:4}) RETURN SUM(PATH_LENGTH(p)) AS answer", 4),
                ("MATCH p=" + start + "((a:Missing)-[e:missingStep]->(b)-[f:missingStep]->(c))*(destination:LongGroup {ordinal:0}) RETURN CASE WHEN PATH_LENGTH(p)=0 AND SIZE(a)=0 AND SIZE(b)=0 AND SIZE(c)=0 AND SIZE(e)=0 AND SIZE(f)=0 THEN 1 ELSE 0 END AS answer", 1),
                ("MATCH p=" + start + group + "{18,}" + end + " RETURN CASE WHEN PATH_LENGTH(p)=18 AND SIZE(starts)=18 AND SIZE(ends)=18 AND SIZE(links)=18 THEN 1 ELSE 0 END AS answer", 1),
                ("MATCH p=" + start + group + "{18,}" + end + " FOR itemNode IN starts RETURN SUM(itemNode.ordinal) AS answer", 153),
                ("MATCH p=" + start + group + "{18,}" + end + " FOR itemNode IN ends RETURN SUM(itemNode.ordinal) AS answer", 171),
                ("MATCH p=" + start + "((starts)-[:groupStep {allowed:TRUE}]->(ends)){18,}" + end + " RETURN COUNT(*) AS answer", 1),
                ("MATCH p=" + start + "((starts)->(ends)){18,}" + end + " RETURN COUNT(*) AS answer", 2),
                ("MATCH p=(farEnd:LongGroup {ordinal:18})((starts)<-[links:groupStep WHERE links.allowed IS TRUE]-(ends)){18,}(nearEnd:LongGroup {ordinal:0}) FOR itemNode IN starts RETURN SUM(itemNode.ordinal) AS answer", 171),
                ("MATCH p=" + group + "+ RETURN COUNT(*) AS answer", 171),
                ("CALL { MATCH p=" + start + group + "{18,}" + end + " RETURN SIZE(links) AS spanSize } RETURN spanSize AS answer", 18),
                ("RETURN 1 AS marker NEXT MATCH p=" + start + group + "{18,}" + end + " RETURN SIZE(links)+marker AS answer", 19),
                ("CALL { MATCH p=" + start + group + "{18,}" + end + " RETURN SIZE(links) AS spanSize UNION ALL MATCH p=" + start + group + "{18,}" + end + " RETURN SIZE(links) AS spanSize } RETURN SUM(spanSize) AS answer", 36),
                ("MATCH p=ALL SHORTEST " + start + group + "+(destination:LongGroup {ordinal:2}) RETURN SUM(PATH_LENGTH(p)) AS answer", 2),
                ("MATCH p=" + start + "((starts:Missing)-[links:groupStep WHERE FALSE]->(ends:Missing))*(destination:LongGroup {ordinal:0}) RETURN CASE WHEN PATH_LENGTH(p)=0 AND SIZE(starts)=0 AND SIZE(ends)=0 AND SIZE(links)=0 AND origin=destination THEN 1 ELSE 0 END AS answer", 1),
                ("MATCH p=" + start + "((starts)-[links:groupStep]->(ends) WHERE starts.ordinal<ends.ordinal){18,}" + end + " RETURN COUNT(*) AS answer", 2),
                ("OPTIONAL MATCH p=" + start + "((starts)-[links:missingStep]->(ends))+" + end + " RETURN CASE WHEN COUNT(*)=1 AND COUNT(p)=0 THEN 1 ELSE 0 END AS answer", 1)
            })
            {
                using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
                {
                    query
                });
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, query + ": " + await response.Content.ReadAsStringAsync());
                var rows = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
                Assert.AreEqual(1, rows.GetArrayLength(), query);
                Assert.AreEqual(expected, rows[0].GetProperty("bindings").GetProperty("answer").GetInt32(), query);
            }
            using var visible = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = "MATCH " + group + "{18,} RETURN *"
            });
            Assert.AreEqual(HttpStatusCode.OK, visible.StatusCode, await visible.Content.ReadAsStringAsync());
            var visibleRows = (await visible.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
            Assert.AreEqual(1, visibleRows.GetArrayLength());
            CollectionAssert.AreEquivalent(new[] { "starts", "ends", "links" },
                visibleRows[0].GetProperty("bindings").EnumerateObject().Select(property => property.Name).ToArray());
            using var updated = await client.PostAsJsonAsync("/v1/graph/gql/update", write);
            Assert.AreEqual(HttpStatusCode.OK, updated.StatusCode, await updated.Content.ReadAsStringAsync());
            receipt ??= await updated.Content.ReadAsStringAsync();
            Assert.AreEqual(receipt, await updated.Content.ReadAsStringAsync());
            Assert.AreEqual(1, (await updated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result").GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetInt32());
            using var quantifiedUpdated = await client.PostAsJsonAsync("/v1/graph/gql/update", quantifiedWrite);
            Assert.AreEqual(HttpStatusCode.OK, quantifiedUpdated.StatusCode, await quantifiedUpdated.Content.ReadAsStringAsync());
            quantifiedReceipt ??= await quantifiedUpdated.Content.ReadAsStringAsync();
            Assert.AreEqual(quantifiedReceipt, await quantifiedUpdated.Content.ReadAsStringAsync());
            Assert.AreEqual(18, (await quantifiedUpdated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result").GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetInt32());
            using var persisted = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = "MATCH (n:LongGroup {ordinal:18}) RETURN n.visited AS answer"
            });
            Assert.AreEqual(HttpStatusCode.OK, persisted.StatusCode, await persisted.Content.ReadAsStringAsync());
            Assert.IsTrue((await persisted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetBoolean());
            using var quantifiedPersisted = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = "MATCH (n:LongGroup {ordinal:18}) RETURN n.quantified AS answer"
            });
            Assert.AreEqual(HttpStatusCode.OK, quantifiedPersisted.StatusCode, await quantifiedPersisted.Content.ReadAsStringAsync());
            Assert.IsTrue((await quantifiedPersisted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetBoolean());
            await host.StopAsync();
        }
    }
}
