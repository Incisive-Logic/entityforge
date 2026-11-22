using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    public async Task Gql_standard_repeated_groups_preserve_endpoint_anchors_selection_and_replay()
    {
        _directory = NewDirectory();
        var seed = new
        {
            statement = "INSERT (a:Anchor {name:'A'})-[:anchorHop {allowed:FALSE}]->(c:Anchor {name:'C'})," +
                "(a)-[:anchorHop {allowed:TRUE}]->(:Anchor {name:'B'})-[:anchorHop {allowed:TRUE}]->(c)," +
                "(a)-[:anchorHop {allowed:TRUE}]->(:Anchor {name:'D'})-[:anchorHop {allowed:TRUE}]->(c)",
            transactionId = Guid.NewGuid()
        };
        const string start = "(startNode:Anchor {name:'A'})";
        const string end = "(endNode:Anchor {name:'C'})";
        const string group = "((x)-[links:anchorHop]->(y)){1,2}";
        const string eligible = "((x)-[links:anchorHop WHERE links.allowed IS TRUE]->(y)){1,2}";
        var update = new
        {
            statement = "MATCH " + start + "," + end + " MATCH p=ALL SHORTEST (startNode)" + eligible + "(endNode) SET endNode.visited=TRUE RETURN COUNT(*) AS answer",
            transactionId = Guid.NewGuid()
        };
        string? receipt = null;
        for (int restart = 0; restart < 2; restart++)
        {
            await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
                .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
            await host.StartAsync();
            using var client = Client(host);
            using var seeded = await client.PostAsJsonAsync("/v1/graph/gql/update", seed);
            Assert.AreEqual(HttpStatusCode.OK, seeded.StatusCode, await seeded.Content.ReadAsStringAsync());
            foreach (var (query, expected) in new[]
            {
                ("MATCH " + start + " MATCH p=(startNode)-[links:anchorHop WHERE links.allowed IS TRUE]->+(endNode:Anchor {name:'C'}) RETURN SUM(PATH_LENGTH(p)) AS answer", 4),
                ("MATCH " + start + "," + end + " MATCH p=ALL SHORTEST (startNode)-[links:anchorHop WHERE links.allowed IS TRUE]->+(endNode) RETURN COUNT(*) AS answer", 2),
                ("MATCH " + start + " LET wanted=TRUE MATCH p=(startNode)((x)-[links:anchorHop {allowed:wanted}]->(y))+(endNode:Anchor {name:'C'}) RETURN COUNT(*) AS answer", 2),
                ("MATCH " + start + " MATCH p=(startNode)" + eligible + end + " RETURN COUNT(*) AS answer", 2),
                ("MATCH " + end + " MATCH p=" + start + eligible + "(endNode) RETURN SUM(PATH_LENGTH(p)) AS answer", 4),
                ("MATCH " + start + "," + end + " MATCH p=ALL SHORTEST (startNode)" + eligible + "(endNode) RETURN COUNT(*) AS answer", 2),
                ("MATCH " + start + " MATCH p=(startNode)((x)-[links:anchorHop WHERE links.allowed IS TRUE]->(y))+(endNode:Anchor {name:'C'}) RETURN COUNT(*) AS answer", 2),
                ("MATCH " + start + " MATCH (endNode:Anchor {name:'A'}) MATCH p=(startNode)((x)-[links:anchorHop]->(y)){0,1}(endNode) RETURN SUM(PATH_LENGTH(p)) AS answer", 0),
                ("MATCH " + start + " OPTIONAL MATCH p=(startNode)" + group + "(endNode:Missing) RETURN CASE WHEN COUNT(*)=1 AND COUNT(endNode)=0 AND COUNT(startNode)=1 THEN 1 ELSE 0 END AS answer", 1),
                ("MATCH (startNode:Anchor) RETURN COUNT(*) AS previousCount NEXT MATCH p=" + start + group + end + " RETURN COUNT(*) AS answer", 3),
                ("MATCH (startNode:Anchor) RETURN COUNT(*) AS total UNION ALL MATCH p=" + start + group + end + " RETURN COUNT(*) AS total NEXT RETURN SUM(total) AS answer", 7),
                ("LET marker=EXISTS { MATCH (startNode:Anchor) } MATCH p=" + start + group + end + " RETURN COUNT(*) AS answer", 3),
                ("MATCH p=" + start + group + end + " RETURN COUNT(*) AS answer", 3),
                ("MATCH p=" + start + eligible + end + " RETURN COUNT(*) AS answer", 2),
                ("MATCH p=ALL SHORTEST " + start + eligible + end + " RETURN SUM(PATH_LENGTH(p)) AS answer", 4),
                ("MATCH p=ALL SHORTEST " + start + group + end + " WHERE PATH_LENGTH(p)>1 RETURN COUNT(*) AS answer", 0),
                ("MATCH p=" + start + "((x)-[links:anchorHop]->(y) WHERE startNode.name='A'){1,2}" + end + " RETURN COUNT(*) AS answer", 3),
                ("MATCH p=" + start + "((x)-[links:anchorHop]->(y)){0,1} RETURN COUNT(*) AS answer", 4),
                ("MATCH p=((x)-[links:anchorHop]->(y)){0,1}" + end + " RETURN COUNT(*) AS answer", 4),
                ("MATCH p=(:Anchor {name:'A'})" + group + "(:Anchor {name:'C'}) RETURN COUNT(*) AS answer", 3),
                ("MATCH p=" + start + "((x)-[links:anchorHop]->(y)){0,2}(endNode:Anchor {name:'A'}) RETURN CASE WHEN PATH_LENGTH(p)=0 AND SIZE(x)=0 AND SIZE(y)=0 AND SIZE(links)=0 AND startNode=endNode THEN 1 ELSE 0 END AS answer", 1),
                ("OPTIONAL MATCH p=(startNode:Missing)" + group + end + " RETURN CASE WHEN COUNT(*)=1 AND COUNT(startNode)=0 THEN 1 ELSE 0 END AS answer", 1)
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
                query = "MATCH p=" + start + group + end + " RETURN *"
            });
            Assert.AreEqual(HttpStatusCode.OK, visible.StatusCode, await visible.Content.ReadAsStringAsync());
            var first = (await visible.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings");
            CollectionAssert.AreEquivalent(new[] { "p", "startNode", "endNode", "x", "y", "links" }, first.EnumerateObject().Select(property => property.Name).ToArray());
            using var written = await client.PostAsJsonAsync("/v1/graph/gql/update", update);
            Assert.AreEqual(HttpStatusCode.OK, written.StatusCode, await written.Content.ReadAsStringAsync());
            receipt ??= await written.Content.ReadAsStringAsync();
            Assert.AreEqual(receipt, await written.Content.ReadAsStringAsync());
            Assert.AreEqual(2, (await written.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result").GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetInt32());
            using var persisted = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = "MATCH (n:Anchor {name:'C'}) RETURN n.visited AS answer"
            });
            Assert.AreEqual(HttpStatusCode.OK, persisted.StatusCode, await persisted.Content.ReadAsStringAsync());
            Assert.IsTrue((await persisted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetBoolean());
            using var previouslyBound = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = "MATCH (startNode:Anchor) MATCH p=(startNode)" + group + end + " RETURN COUNT(*) AS answer"
            });
            Assert.AreEqual(HttpStatusCode.OK, previouslyBound.StatusCode, await previouslyBound.Content.ReadAsStringAsync());
            Assert.AreEqual(5, (await previouslyBound.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0]
                .GetProperty("bindings").GetProperty("answer").GetInt32());
            await host.StopAsync();
        }
    }
}
