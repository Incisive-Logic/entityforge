using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    public async Task Gql_standard_repeated_predicates_correlate_exists_to_each_occurrence()
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (a:RepeatExists {name:'A'})-[:step]->(b:RepeatExists {name:'B'})-[:step]->(:RepeatExists {name:'C'}), (a)-[:approved]->(b)",
            transactionId = Guid.NewGuid()
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode, await seed.Content.ReadAsStringAsync());
        foreach (var (query, expected) in new[]
        {
            ("MATCH p=(origin:RepeatExists {name:'A'}) ((x)-[e:step]->(y) WHERE VALUE { RETURN x NEXT YIELD \"x\" AS selectedNode RETURN selectedNode.name AS \"selectedNode.name\" }='A'){1,3}(destination) RETURN SUM(PATH_LENGTH(p)) AS answer", 1),
            ("MATCH p=(origin:RepeatExists {name:'A'}) ((x)-[e:step]->(y) WHERE VALUE { RETURN x NEXT YIELD `x` AS selectedNode RETURN selectedNode.name AS \"selectedNode.name\" }='A')+(destination) RETURN SUM(PATH_LENGTH(p)) AS answer", 1),
            ("MATCH p=(origin:RepeatExists {name:'A'}) ((x)-[e:step]->(y) WHERE VALUE { RETURN x NEXT YIELD \"\\u0078\" AS selectedNode RETURN selectedNode.name AS \"selectedNode.name\" }='A')+(destination) RETURN SUM(PATH_LENGTH(p)) AS answer", 1),
            ("MATCH p=(origin:RepeatExists {name:'A'}) ((x)-[e:step]->(y) WHERE EXISTS { RETURN * })+(destination) RETURN SUM(PATH_LENGTH(p)) AS answer", 3),
            ("MATCH p=(origin:RepeatExists {name:'A'}) ((x)-[e:step]->(y) WHERE EXISTS { FILTER x.name='A' RETURN * UNION FILTER y.name='B' RETURN * })+(destination) RETURN SUM(PATH_LENGTH(p)) AS answer", 1),
            ("MATCH p=(origin:RepeatExists {name:'A'}) ((x)-[e:step]->(y) WHERE VALUE { RETURN x.name AS chosen NEXT RETURN * UNION RETURN * NEXT RETURN chosen }='A')+(destination) RETURN SUM(PATH_LENGTH(p)) AS answer", 1),
            ("MATCH p=(origin:RepeatExists {name:'A'}) ((x)-[e:step]->(y) WHERE VALUE { RETURN x NEXT RETURN * NEXT YIELD x AS selectedNode RETURN selectedNode.name AS \"selectedNode.name\" }='A'){1,3}(destination) RETURN SUM(PATH_LENGTH(p)) AS answer", 1),
            ("MATCH p=(origin:RepeatExists {name:'A'}) ((x)-[e:step]->(y) WHERE VALUE { CALL () { LET selectedName='A' RETURN * } RETURN selectedName }='A')+(destination) RETURN SUM(PATH_LENGTH(p)) AS answer", 3),
            ("MATCH p=(origin:RepeatExists {name:'A'}) ((x)-[e:step]->(y) WHERE VALUE { RETURN x NEXT YIELD x AS selectedNode RETURN selectedNode.name AS \"selectedNode.name\" }='A'){1,3}(destination) RETURN SUM(PATH_LENGTH(p)) AS answer", 1),
            ("MATCH p=(origin:RepeatExists {name:'A'}) ((x)-[e:step]->(y) WHERE VALUE { RETURN x NEXT YIELD x AS selectedNode RETURN selectedNode.name AS \"selectedNode.name\" }='A')+(destination) RETURN SUM(PATH_LENGTH(p)) AS answer", 1),
            ("MATCH p=ALL SHORTEST (origin:RepeatExists {name:'A'}) ((x)-[e:step]->(y) WHERE VALUE { RETURN x NEXT YIELD x RETURN x.name AS \"x.name\" }='A')+(destination) RETURN SUM(PATH_LENGTH(p)) AS answer", 1),
            ("MATCH p=(origin:RepeatExists {name:'A'}) ((x)-[e:step]->(y) WHERE VALUE { RETURN x.name AS chosen NEXT RETURN chosen }='A')+(destination) RETURN SUM(PATH_LENGTH(p)) AS answer", 1),
            ("MATCH p=(origin:RepeatExists {name:'A'}) ((x)-[e:step]->(y) WHERE VALUE { RETURN x NEXT RETURN x.name AS chosen }='A'){1,3}(destination) RETURN SUM(PATH_LENGTH(p)) AS answer", 1),
            ("MATCH p=(origin:RepeatExists {name:'A'}) ((x)-[e:step]->(y) WHERE VALUE { RETURN x.name AS chosen NEXT YIELD chosen AS selectedName RETURN selectedName }='A')+(destination) RETURN SUM(PATH_LENGTH(p)) AS answer", 1),
            ("MATCH p=(origin:RepeatExists {name:'A'}) ((x)-[e:step]->(y) WHERE VALUE { RETURN x.name AS chosen UNION RETURN x.name AS chosen NEXT RETURN chosen }='A')+(destination) RETURN SUM(PATH_LENGTH(p)) AS answer", 1),
            ("MATCH p=(origin:RepeatExists {name:'A'}) ((x)-[e:step]->(y) WHERE VALUE { RETURN x.name AS chosen UNION RETURN x.name AS chosen }='A')+(destination) RETURN SUM(PATH_LENGTH(p)) AS answer", 1),
            ("MATCH p=(origin:RepeatExists {name:'A'}) ((x)-[e:step]->(y) WHERE VALUE { RETURN y.name AS chosen UNION ALL FILTER FALSE RETURN x.name AS chosen }='B'){1,3}(destination) RETURN SUM(PATH_LENGTH(p)) AS answer", 1),
            ("MATCH p=(origin:RepeatExists {name:'A'}) ((x)-[e:step]->(y) WHERE EXISTS { FILTER x.name='A' RETURN x.name AS chosen UNION ALL FILTER y.name='B' RETURN y.name AS chosen })+(destination) RETURN SUM(PATH_LENGTH(p)) AS answer", 1),
            ("MATCH p=(origin:RepeatExists {name:'A'}) ((x)-[e:step]->(y) WHERE VALUE { CALL (x) { RETURN x.name AS chosen UNION RETURN x.name AS chosen } RETURN chosen }='A')+(destination) RETURN SUM(PATH_LENGTH(p)) AS answer", 1),
            ("MATCH p=(origin:RepeatExists {name:'A'}) ((x)-[e:step]->(y) WHERE VALUE { CALL (x,y) { RETURN y.name AS chosen } RETURN chosen }='B')+(destination) RETURN SUM(PATH_LENGTH(p)) AS answer", 1),
            ("MATCH p=(origin:RepeatExists {name:'A'}) ((x)-[e:step]->(y) WHERE VALUE { CALL { RETURN y.name AS chosen } RETURN chosen }='B'){1,3}(destination) RETURN SUM(PATH_LENGTH(p)) AS answer", 1),
            ("MATCH p=(origin:RepeatExists {name:'A'}) ((x)-[e:step]->(y) WHERE VALUE { CALL () { RETURN 1 AS marker } RETURN marker }=1)+(destination) RETURN SUM(PATH_LENGTH(p)) AS answer", 3),
            ("MATCH p=(origin:RepeatExists {name:'A'}) ((x)-[e:step]->(y) WHERE VALUE { OPTIONAL CALL (x) { FILTER FALSE RETURN x.name AS chosen } RETURN chosen } IS NULL)+(destination) RETURN SUM(PATH_LENGTH(p)) AS answer", 3),
            ("MATCH p=(origin:RepeatExists {name:'A'}) ((x)-[e:step]->(y) WHERE VALUE { FOR item IN [x,y] RETURN item.name AS x ORDER BY x DESC LIMIT 1 }='B')+(destination) RETURN SUM(PATH_LENGTH(p)) AS answer", 1),
            ("MATCH p=(origin:RepeatExists {name:'A'}) ((x)-[e:step]->(y) WHERE EXISTS { FOR item IN [x,y] RETURN item.name AS x, COUNT(*) AS total GROUP BY x ORDER BY x LIMIT 1 })+(destination) RETURN SUM(PATH_LENGTH(p)) AS answer", 3),
            ("MATCH p=(origin:RepeatExists {name:'A'}) ((x)-[e:step]->(y) WHERE VALUE { FOR item IN [x,y] RETURN item.name AS chosen ORDER BY chosen DESC LIMIT 1 }='B')+(destination) RETURN SUM(PATH_LENGTH(p)) AS answer", 1),
            ("MATCH p=(origin:RepeatExists {name:'A'}) ((x)-[e:step]->(y) WHERE VALUE { FOR item IN [x,y] RETURN item.name AS chosen ORDER BY chosen ASC OFFSET 1 LIMIT 1 }='B'){1,3}(destination) RETURN SUM(PATH_LENGTH(p)) AS answer", 1),
            ("MATCH p=(origin:RepeatExists {name:'A'}) ((x)-[e:step]->(y) WHERE VALUE { FOR item IN [x,y] RETURN COUNT(*) AS total GROUP BY () ORDER BY total LIMIT 1 }=2)+(destination) RETURN SUM(PATH_LENGTH(p)) AS answer", 3),
            ("MATCH p=(origin:RepeatExists {name:'A'}) ((x)-[e:step]->(y) WHERE VALUE { RETURN y.name AS chosen LIMIT 0 } IS NULL)+(destination) RETURN SUM(PATH_LENGTH(p)) AS answer", 3),
            ("MATCH p=(origin:RepeatExists {name:'A'}) ((x)-[e:step]->(y) WHERE VALUE { LET destinationName=VALUE { RETURN y.name AS \"y.name\" } RETURN destinationName }='B'){1,3}(destination) RETURN SUM(PATH_LENGTH(p)) AS answer", 1),
            ("MATCH p=(origin:RepeatExists {name:'A'}) ((x)-[e:step]->(y) WHERE VALUE { MATCH (x)-[:approved]->(approvedNode WHERE EXISTS { MATCH (approvedNode:RepeatExists) }) RETURN approvedNode.name AS \"approvedNode.name\" }='B')+(destination) RETURN SUM(PATH_LENGTH(p)) AS answer", 1),
            ("MATCH p=(origin:RepeatExists {name:'A'}) ((x)-[e:step]->(y) WHERE VALUE { RETURN VALUE { FOR item IN [x,y] FILTER item.name='B' RETURN COUNT(*) AS total } AS \"VALUE { FOR item IN [x,y] FILTER item.name='B' RETURN COUNT(*) AS total }\" }=1)+(destination) RETURN SUM(PATH_LENGTH(p)) AS answer", 3),
            ("MATCH p=(origin:RepeatExists {name:'A'}) ((x)-[e:step]->(y) WHERE VALUE { MATCH (x)-[:approved]->(approvedNode) RETURN approvedNode.name AS \"approvedNode.name\" }='B'){1,3}(destination) RETURN SUM(PATH_LENGTH(p)) AS answer", 1),
            ("MATCH p=(origin:RepeatExists {name:'A'}) ((x)-[e:step]->(y) WHERE VALUE { FOR item IN [x,y] FILTER item.name='A' RETURN COUNT(*) AS valueColumn }=1)+(destination) RETURN SUM(PATH_LENGTH(p)) AS answer", 1),
            ("MATCH p=(origin:RepeatExists {name:'A'}) ((x)-[e:step]->(y) WHERE EXISTS { FOR item IN [x,y] WITH ORDINALITY ordinal FILTER ordinal=2 AND item.name='B' RETURN item })+(destination) RETURN SUM(PATH_LENGTH(p)) AS answer", 1),
            ("MATCH p=(origin:RepeatExists {name:'A'}) ((x)-[e:step]->(y) WHERE VALUE { OPTIONAL MATCH (y)-[:approved]->(approvedNode) RETURN approvedNode.name AS \"approvedNode.name\" } IS NULL)+(destination) RETURN SUM(PATH_LENGTH(p)) AS answer", 3),
            ("MATCH p=(origin:RepeatExists {name:'A'}) ((x)-[e:step]->(y) WHERE VALUE { LET destinationName=y.name FILTER x.name='A' RETURN destinationName }='B'){1,3}(destination) RETURN SUM(PATH_LENGTH(p)) AS answer", 1),
            ("MATCH p=(origin:RepeatExists {name:'A'}) ((x)-[e:step]->(y) WHERE VALUE { LET sourceName=x.name LET destinationName=y.name FILTER sourceName='A' RETURN destinationName }='B')+(destination) RETURN SUM(PATH_LENGTH(p)) AS answer", 1),
            ("MATCH p=(origin:RepeatExists {name:'A'}) ((x)-[e:step]->(y) WHERE EXISTS { LET localName=y.name FILTER localName='B' RETURN localName })+(destination) RETURN SUM(PATH_LENGTH(p)) AS answer", 1),
            ("MATCH p=(origin:RepeatExists {name:'A'}) ((x)-[e:step]->(y) WHERE VALUE { FILTER FALSE LET localName=y.name RETURN COUNT(*) AS valueColumn }=0)+(destination) RETURN SUM(PATH_LENGTH(p)) AS answer", 3),
            ("MATCH p=ALL SHORTEST (origin:RepeatExists {name:'A'}) ((x)-[firstEdges:step]->(y)-[secondEdges:step]->(z) WHERE VALUE { LET localName=z.name RETURN localName }='C')+(destination) RETURN SUM(PATH_LENGTH(p)) AS answer", 2),
            ("MATCH p=(origin:RepeatExists {name:'A'}) ((x)-[e:step]->(y) WHERE VALUE { FILTER x.name='A' RETURN y.name AS \"y.name\" }='B'){1,3}(destination) RETURN SUM(PATH_LENGTH(p)) AS answer", 1),
            ("MATCH p=(origin:RepeatExists {name:'A'}) ((x)-[e:step]->(y) WHERE VALUE { FILTER FALSE RETURN y.name AS \"y.name\" } IS NULL)+(destination) RETURN SUM(PATH_LENGTH(p)) AS answer", 3),
            ("MATCH p=(origin:RepeatExists {name:'A'}) ((x)-[e:step]->(y) WHERE EXISTS { FILTER x.name='A' FILTER y.name='B' RETURN 1 AS valueColumn }){1,3}(destination) RETURN SUM(PATH_LENGTH(p)) AS answer", 1),
            ("MATCH p=(origin:RepeatExists {name:'A'}) ((x)-[e:step]->(y) WHERE VALUE { FILTER FALSE RETURN COUNT(*) AS valueColumn }=0)+(destination) RETURN SUM(PATH_LENGTH(p)) AS answer", 3),
            ("MATCH p=ALL SHORTEST (origin:RepeatExists {name:'A'}) ((x)-[firstEdges:step]->(y)-[secondEdges:step]->(z) WHERE VALUE { FILTER x.name='A' RETURN z.name AS \"z.name\" }='C')+(destination) RETURN SUM(PATH_LENGTH(p)) AS answer", 2),
            ("MATCH p=(origin:RepeatExists {name:'A'}) ((x)-[e:step]->(y) WHERE VALUE { RETURN x.name AS \"x.name\" }='A'){1,3}(destination) RETURN SUM(PATH_LENGTH(p)) AS answer", 1),
            ("MATCH p=(origin:RepeatExists {name:'A'}) ((x)-[e:step]->(y) WHERE VALUE { RETURN y.name AS destinationName }='B')+(destination) RETURN SUM(PATH_LENGTH(p)) AS answer", 1),
            ("MATCH p=(origin:RepeatExists {name:'A'}) ((x)-[e:step]->(y) WHERE VALUE { RETURN x.name AS \"x.name\" }='missing')+(destination) RETURN COUNT(*) AS answer", 0),
            ("MATCH p=(origin:RepeatExists {name:'A'}) ((x)-[e:step]->(y) WHERE EXISTS { RETURN x.name AS \"x.name\" }){1,2}(destination) RETURN SUM(PATH_LENGTH(p)) AS answer", 3),
            ("MATCH p=(origin:RepeatExists {name:'A'}) ((x)-[firstEdges:step]->(y)-[secondEdges:step]->(z) WHERE VALUE { RETURN z.name AS \"z.name\" }='C'){1,3}(destination) RETURN SUM(PATH_LENGTH(p)) AS answer", 2),
            ("MATCH p=ALL SHORTEST (origin:RepeatExists {name:'A'}) ((x)-[firstEdges:step]->(y)-[secondEdges:step]->(z) WHERE VALUE { RETURN x.name AS \"x.name\" }='A')+(destination) RETURN SUM(PATH_LENGTH(p)) AS answer", 2),
            ("MATCH p=(origin:RepeatExists {name:'A'}) ((x)-[e:step]->(y) WHERE EXISTS { MATCH (x)-[:approved]->(y) }){1,3}(destination) RETURN SUM(PATH_LENGTH(p)) AS answer", 1),
            ("MATCH p=(origin:RepeatExists {name:'A'}) ((x)-[e:step]->(y) WHERE EXISTS { MATCH (x)-[:approved]->(y) })+(destination) RETURN SUM(PATH_LENGTH(p)) AS answer", 1),
            ("MATCH p=(origin:RepeatExists {name:'A'}) ((x)-[e:step]->(y) WHERE EXISTS { MATCH (x)-[:approved]->(y) })+(destination:RepeatExists {name:'C'}) RETURN COUNT(*) AS answer", 0),
            ("MATCH p=ALL SHORTEST (origin:RepeatExists {name:'A'}) ((x)-[e:step]->(y) WHERE EXISTS { MATCH (x)-[:approved]->(y) })+(destination) RETURN SUM(PATH_LENGTH(p)) AS answer", 1),
            ("MATCH p=(origin:RepeatExists {name:'B'}) ((x)-[e:step]->(y) WHERE NOT EXISTS { MATCH (x)-[:approved]->(y) })+(destination) RETURN SUM(PATH_LENGTH(p)) AS answer", 1),
            ("MATCH p=(origin:RepeatExists {name:'A'}) ((x)-[e:step]->(y) WHERE EXISTS { MATCH (x)-[:approved]->(z) MATCH (z) WHERE z=y })+(destination) RETURN SUM(PATH_LENGTH(p)) AS answer", 1),
            ("MATCH p=(origin:RepeatExists {name:'A'}) ((x)-[e:step WHERE EXISTS { MATCH (x)-[:approved]->(y) }]->(y))+(destination) RETURN SUM(PATH_LENGTH(p)) AS answer", 1),
            ("MATCH p=(origin:RepeatExists {name:'A'}) ((x)-[e:step]->(y) WHERE EXISTS { MATCH (x)-[:missing]->(y) })*(destination) RETURN COUNT(*) AS answer", 1),
            ("MATCH p=(origin:RepeatExists {name:'A'})-[e:step WHERE EXISTS { MATCH (x)-[e]->(y) MATCH (x)-[:approved]->(y) }]->+(destination) RETURN SUM(PATH_LENGTH(p)) AS answer", 1)
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
        var update = new
        {
            statement = "MATCH p=(origin:RepeatExists {name:'A'}) ((x)-[e:step]->(y) WHERE EXISTS { MATCH (x)-[:approved]->(y) } AND VALUE { FOR originNode IN [x] MATCH (originNode)-[:approved]->(approvedNode) CALL (originNode) { RETURN originNode.name AS sourceName UNION RETURN originNode.name AS sourceName NEXT RETURN sourceName } FILTER y.name='B' RETURN sourceName NEXT RETURN * NEXT RETURN sourceName }='A')+(destination) SET destination.checked=TRUE RETURN destination.name AS answer",
            transactionId = Guid.NewGuid()
        };
        using var changed = await client.PostAsJsonAsync("/v1/graph/gql/update", update);
        Assert.AreEqual(HttpStatusCode.OK, changed.StatusCode, await changed.Content.ReadAsStringAsync());
        using var replay = await client.PostAsJsonAsync("/v1/graph/gql/update", update);
        Assert.AreEqual(HttpStatusCode.OK, replay.StatusCode, await replay.Content.ReadAsStringAsync());
        Assert.AreEqual(await changed.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync());
        Assert.AreEqual("B", (await changed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result").GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetString());
        foreach (string invalid in new[]
        {
            "MATCH ((x)-[e:step]->(y) WHERE VALUE { RETURN x NEXT YIELD x AS y RETURN y.name AS \"y.name\" }='A')+ RETURN y",
            "MATCH ((x)-[e:step]->(y) WHERE VALUE { RETURN * } IS NOT NULL)+ RETURN y",
            "MATCH ((x)-[e:step]->(y) WHERE VALUE { RETURN x.name AS y NEXT RETURN y }='A')+ RETURN y",
            "MATCH ((x)-[e:step]->(y) WHERE EXISTS { LET localName=x.name RETURN localName AS chosen UNION ALL RETURN localName AS chosen })+ RETURN y",
            "MATCH ((x)-[e:step]->(y) WHERE VALUE { CALL () { RETURN x.name AS chosen } RETURN chosen }='A')+ RETURN y",
            "MATCH ((x)-[e:step]->(y) WHERE VALUE { CALL () { LET x=1 RETURN x AS chosen } RETURN chosen }=1)+ RETURN y",
            "MATCH ((x)-[e:step]->(y) WHERE VALUE { CALL () { RETURN 1 AS x } RETURN x }=1)+ RETURN y",
            "MATCH ((x)-[e:step]->(y) WHERE VALUE { CALL () { INSERT (:Unexpected) RETURN 1 AS chosen } RETURN chosen }=1)+ RETURN y",
            "MATCH ((x)-[e:step]->(y) WHERE VALUE { LET localName=y.name RETURN localName }='B')+ RETURN localName",
            "MATCH ((x)-[e:step]->(y) WHERE VALUE { LET x=1 RETURN x }=1)+ RETURN y",
            "MATCH p=((x)-[e:step]->(y) WHERE VALUE { LET p=1 RETURN p }=1)+ RETURN y"
        })
        {
            using var rejected = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = invalid
            });
            Assert.AreEqual(HttpStatusCode.BadRequest, rejected.StatusCode, invalid + ": " + await rejected.Content.ReadAsStringAsync());
        }
        await host.StopAsync();
    }
}
