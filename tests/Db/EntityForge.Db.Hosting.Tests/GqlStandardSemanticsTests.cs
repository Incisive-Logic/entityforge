using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    public async Task Gql_standard_set_evaluates_all_rows_before_applying_assignments()
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        foreach (string statement in new[]
        {
            "INSERT (a:SetInput {amount:1})-[:setInput {amount:10}]->(:SetOther)",
            "MATCH (n:SetInput)-[e:setInput]->() FOR x IN [1,2,3] SET n.amount=n.amount+1,n.before=n.amount,e.amount=e.amount+1,e.before=e.amount",
            "MATCH (n:SetInput) SET n.first=n.amount SET n.followingAmount=n.first+1",
            "MATCH (n:SetInput) REMOVE n.first SET n.removed=n.first IS NULL"
        })
        {
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/update", new
            {
                statement
            });
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, statement + ": " + await response.Content.ReadAsStringAsync());
        }
        using var invalid = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "MATCH (n:SetInput) FOR divisor IN [1,0] SET n.amount=100/divisor"
        });
        Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode, await invalid.Content.ReadAsStringAsync());
        using var checkedResult = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (n:SetInput)-[e:setInput]->() RETURN n.amount=2 AND n.before=1 AND e.amount=11 AND e.before=10 AND n.followingAmount=3 AND n.removed=TRUE AS answer"
        });
        Assert.AreEqual(HttpStatusCode.OK, checkedResult.StatusCode, await checkedResult.Content.ReadAsStringAsync());
        Assert.IsTrue((await checkedResult.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetBoolean());
    }

    [TestMethod]
    public async Task Gql_standard_updating_call_imports_outer_scope_unless_explicitly_restricted()
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var inserted = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "FOR amount IN [1,2] CALL { INSERT (:ImplicitCall {amount:amount}) } FINISH"
        });
        Assert.AreEqual(HttpStatusCode.OK, inserted.StatusCode, await inserted.Content.ReadAsStringAsync());
        using var rejected = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "FOR amount IN [3,4] CALL () { INSERT (:ImplicitCall {amount:amount}) } FINISH"
        });
        Assert.AreEqual(HttpStatusCode.BadRequest, rejected.StatusCode, await rejected.Content.ReadAsStringAsync());
        using var selected = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (n:ImplicitCall) RETURN SUM(n.amount) AS answer"
        });
        Assert.AreEqual(HttpStatusCode.OK, selected.StatusCode, await selected.Content.ReadAsStringAsync());
        Assert.AreEqual(3, (await selected.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetInt32());
    }

    [TestMethod]
    public async Task Gql_standard_delete_expressions_preserve_atomicity_and_scope()
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "FOR rank IN [1,2,3] INSERT (:DeleteExpression {rank:rank})"
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode, await seed.Content.ReadAsStringAsync());
        using var rejected = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "MATCH (n:DeleteExpression) SET n.touched=TRUE DELETE CASE WHEN n.rank=1 THEN n ELSE 42 END"
        });
        Assert.AreEqual(HttpStatusCode.BadRequest, rejected.StatusCode, await rejected.Content.ReadAsStringAsync());
        using var checkedRollback = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (n:DeleteExpression) WHERE n.touched IS NULL RETURN COUNT(*) AS answer"
        });
        Assert.AreEqual(HttpStatusCode.OK, checkedRollback.StatusCode, await checkedRollback.Content.ReadAsStringAsync());
        Assert.AreEqual(3, (await checkedRollback.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetInt32());
        foreach (string statement in new[]
        {
            "MATCH (n:DeleteExpression) DELETE NULL",
            "MATCH (n:DeleteExpression) DELETE CASE WHEN n.rank=1 THEN n ELSE NULL END",
            "MATCH (n:DeleteExpression {rank:2}) DELETE COALESCE(NULL,n)",
            "MATCH (n:DeleteExpression) RETURN RECORD {target:n} AS item NEXT DELETE item.target FINISH"
        })
        {
            using var removed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
            {
                statement
            });
            Assert.AreEqual(HttpStatusCode.OK, removed.StatusCode, statement + ": " + await removed.Content.ReadAsStringAsync());
        }
        using var remaining = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (n:DeleteExpression) RETURN COUNT(*) AS answer"
        });
        Assert.AreEqual(HttpStatusCode.OK, remaining.StatusCode, await remaining.Content.ReadAsStringAsync());
        Assert.AreEqual(0, (await remaining.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetInt32());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Gql_standard_delete_expression_respects_detach_mode(bool detach)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (:DeleteEndpoint {name:'A'})-[:deleteLink]->(:DeleteEndpoint {name:'B'})"
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode, await seed.Content.ReadAsStringAsync());
        using var removal = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "MATCH (a:DeleteEndpoint {name:'A'}) " + (detach ? "DETACH" : "NODETACH") + " DELETE COALESCE(NULL,a)"
        });
        Assert.AreEqual(detach ? HttpStatusCode.OK : HttpStatusCode.BadRequest, removal.StatusCode, await removal.Content.ReadAsStringAsync());
        if (!detach)
        {
            using var edgeRemoval = await client.PostAsJsonAsync("/v1/graph/gql/update", new
            {
                statement = "MATCH (a:DeleteEndpoint)-[e:deleteLink]->(b) DELETE CASE WHEN a.name='A' THEN e ELSE NULL END"
            });
            Assert.AreEqual(HttpStatusCode.OK, edgeRemoval.StatusCode, await edgeRemoval.Content.ReadAsStringAsync());
        }
        using var nodes = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (n:DeleteEndpoint) RETURN COUNT(*) AS answer"
        });
        Assert.AreEqual(detach ? 1 : 2, (await nodes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetInt32());
        using var edges = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH ()-[e:deleteLink]->() RETURN COUNT(*) AS answer"
        });
        Assert.AreEqual(0, (await edges.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetInt32());
    }

    [TestMethod]
    public async Task Gql_standard_endpoint_predicates_follow_stored_direction()
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (a:Endpoint {name:'A'})-[:link]->(b:Endpoint {name:'B'}), (a)-[:loop]->(a)"
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode, await seed.Content.ReadAsStringAsync());
        foreach (string query in new[]
        {
            "MATCH (a)-[e:link]->(b) RETURN a IS SOURCE OF e AND b IS DESTINATION OF e AND e IS DIRECTED AS answer",
            "MATCH (b)<-[e:link]-(a) RETURN b IS NOT SOURCE OF e AND a IS NOT DESTINATION OF e AND NOT e IS NOT DIRECTED AS answer",
            "MATCH (a)-[e:loop]->(b) RETURN a IS SOURCE OF e AND a IS DESTINATION OF e AND b IS SOURCE OF e AS answer",
            "MATCH (a)-[e:link]-(b) WHERE a IS SOURCE OF e RETURN a.name='A' AND b.name='B' AS answer",
            "MATCH (a)-[e:link]-(b) WHERE a IS DESTINATION OF e RETURN a.name='B' AND b.name='A' AS answer",
            "MATCH (a:Endpoint {name:'A'}) OPTIONAL MATCH (a)-[e:missing]->(b) RETURN (a IS SOURCE OF e) IS UNKNOWN AND (e IS DIRECTED) IS UNKNOWN AS answer",
            "MATCH (a)-[e:link]->(b) RETURN a,e NEXT RETURN a IS SOURCE OF e AS answer",
            "RETURN EXISTS { MATCH (a:Endpoint {name:'A'}) MATCH (a)-[:link]->(b:Endpoint {name:'B'}) } AS answer",
            "MATCH (a:Endpoint {name:'A'}) RETURN EXISTS { MATCH (a)-[:link]->(b) MATCH (b)<-[:link]-(c) WHERE c.name='A' } AS answer",
            "MATCH (a:Endpoint {name:'A'}) RETURN NOT EXISTS { MATCH (a)-[:link]->(b) MATCH (b)-[:link]->(c) } AS answer",
            "MATCH ((a)-[e:link]->(b) WHERE a.name='A' AND b.name='B') RETURN e IS DIRECTED AS answer",
            "LET expected='A' MATCH (((a)-[e:link]->(b) WHERE a.name=expected) WHERE b.name='B') RETURN e IS DIRECTED AS answer",
            "MATCH (a:Endpoint {name:'A'}) OPTIONAL MATCH ((a)-[e:link]->(b) WHERE b.name='absent') RETURN e IS NULL AND b IS NULL AS answer",
            "MATCH (a)-[e:link]->(b) RETURN CASE e WHEN IS DIRECTED THEN TRUE ELSE FALSE END AS answer",
            "MATCH (a)-[e:link]->(b) RETURN CASE a WHEN IS SOURCE OF e THEN TRUE ELSE FALSE END AS answer",
            "MATCH (a)-[e:link]->(b) RETURN CASE b WHEN IS NOT SOURCE OF e THEN TRUE ELSE FALSE END AS answer",
            "MATCH (a)-[e:link]->(b) RETURN CASE b WHEN IS DESTINATION OF e THEN TRUE ELSE FALSE END AS answer",
            "MATCH (a)-[e:link]->(b) RETURN CASE a WHEN IS NOT DESTINATION OF e THEN TRUE ELSE FALSE END AS answer",
            "MATCH (a)-[e:link]->(b) RETURN CASE a WHEN IS LABELED Missing THEN FALSE WHEN IS LABELED Endpoint&!Missing THEN TRUE ELSE FALSE END AS answer",
            "MATCH (a)-[e:link]->(b) RETURN CASE e WHEN IS NOT LABELED missing THEN TRUE ELSE FALSE END AS answer",
            "MATCH (a:Endpoint {name:'A'}) OPTIONAL MATCH (a)-[e:missing]->(b) RETURN CASE e WHEN IS DIRECTED THEN FALSE WHEN IS NOT DIRECTED THEN FALSE ELSE TRUE END AS answer"
        })
        {
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query
            });
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, query + ": " + await response.Content.ReadAsStringAsync());
            var rows = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
            Assert.AreEqual(1, rows.GetArrayLength(), query);
            Assert.IsTrue(rows[0].GetProperty("bindings").GetProperty("answer").GetBoolean(), query);
        }
        using var updated = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "MATCH ((a)-[e:link]-(b) WHERE a IS SOURCE OF e) SET a.source=TRUE"
        });
        Assert.AreEqual(HttpStatusCode.OK, updated.StatusCode, await updated.Content.ReadAsStringAsync());
        using var checkedUpdate = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (n:Endpoint) WHERE n.source=TRUE RETURN n.name AS answer"
        });
        Assert.AreEqual(HttpStatusCode.OK, checkedUpdate.StatusCode, await checkedUpdate.Content.ReadAsStringAsync());
        var updatedRows = (await checkedUpdate.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
        Assert.AreEqual(1, updatedRows.GetArrayLength());
        Assert.AreEqual("A", updatedRows[0].GetProperty("bindings").GetProperty("answer").GetString());
    }

    [TestMethod]
    public async Task Gql_standard_scalar_subquery_writes_reject_later_cardinality_failure_atomically()
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var inserted = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "FOR x IN [1,2] LET computed=VALUE { RETURN x*10 AS result } INSERT (:ScalarWrite {amount:computed})"
        });
        Assert.AreEqual(HttpStatusCode.OK, inserted.StatusCode, await inserted.Content.ReadAsStringAsync());
        using var rejected = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "FOR x IN [3,4] INSERT (:ScalarWrite {amount:x}) RETURN x NEXT LET computed=VALUE { FOR y IN [1,2] FILTER y<=x-2 RETURN y } FINISH"
        });
        Assert.AreEqual(HttpStatusCode.BadRequest, rejected.StatusCode, await rejected.Content.ReadAsStringAsync());
        using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (n:ScalarWrite) RETURN COUNT(*)=2 AND SUM(n.amount)=30 AS answer"
        });
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        Assert.IsTrue((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetBoolean());
    }

    [TestMethod]
    public async Task Gql_standard_element_predicates_preserve_optional_scope_and_literal_lowering()
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var inserted = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (:PredicateNode {name:'A',age:30})-[:predicateLink {amount:5}]->(:PredicateNode {name:'B',age:20})"
        });
        Assert.AreEqual(HttpStatusCode.OK, inserted.StatusCode, await inserted.Content.ReadAsStringAsync());
        foreach (var (query, count) in new[]
        {
            ("MATCH (n:PredicateNode WHERE n.age>=0x1E) RETURN COUNT(*) AS answer", 1),
            ("MATCH (n IS PredicateNode|Absent WHERE n.age>=30) WHERE n.name='A' OR n.name='B' RETURN COUNT(*) AS answer", 1),
            ("MATCH (n:PredicateNode WHERE DATE '2024-01-01'=DATE('2024-01-01')) RETURN COUNT(*) AS answer", 2),
            ("MATCH (a:PredicateNode)-[e:predicateLink WHERE e.amount<a.age]->(b WHERE b.age<a.age) RETURN COUNT(*) AS answer", 1),
            ("MATCH (a:PredicateNode {name:'A'}) OPTIONAL MATCH (a)-[e:predicateLink WHERE e.amount>100]->(b) RETURN COUNT(*) AS answer", 1),
            ("RETURN 25 AS minimumAge NEXT MATCH (n:PredicateNode WHERE n.age>minimumAge) RETURN COUNT(*) AS answer", 1),
            ("RETURN 25 AS minimumAge NEXT YIELD minimumAge AS threshold MATCH (n:PredicateNode WHERE n.age>threshold) RETURN COUNT(*) AS answer", 1),
            ("RETURN 100 AS minimumAge NEXT OPTIONAL MATCH (n:PredicateNode WHERE n.age>minimumAge) RETURN COUNT(*) AS answer", 1),
            ("RETURN 25 AS minimumAge NEXT MATCH ((n:PredicateNode WHERE n.age>minimumAge)) RETURN COUNT(*) AS answer", 1),
            ("RETURN 25 AS minimumAge NEXT MATCH (n:PredicateNode WHERE EXISTS { MATCH (n)-[:predicateLink]->(b) WHERE n.age>minimumAge }) RETURN COUNT(*) AS answer", 1),
            ("CALL () { MATCH (a:PredicateNode {name:'A'}) RETURN a } MATCH (n:PredicateNode WHERE EXISTS { MATCH (a)-[:predicateLink]->(n) }) RETURN COUNT(*) AS answer", 1),
            ("RETURN 25 AS minimumAge NEXT MATCH (n:PredicateNode WHERE VALUE { RETURN n.age>minimumAge AS eligible }) RETURN COUNT(*) AS answer", 1),
            ("RETURN 25 AS minimumAge UNION ALL RETURN 15 AS minimumAge NEXT MATCH (n:PredicateNode WHERE n.age>minimumAge) RETURN COUNT(*) AS answer", 3),
            ("RETURN 25 AS minimumAge UNION ALL RETURN 15 AS minimumAge NEXT YIELD minimumAge AS threshold MATCH (n:PredicateNode WHERE EXISTS { FILTER n.age>threshold RETURN 1 AS eligible }) RETURN COUNT(*) AS answer", 3),
            ("MATCH ((n:PredicateNode WHERE n.age=30)) RETURN COUNT(*) AS answer", 1),
            ("MATCH (n:PredicateNode) OPTIONAL MATCH (n:Missing) RETURN COUNT(n) AS answer", 2),
            ("MATCH (n:PredicateNode) OPTIONAL MATCH (n:PredicateNode {age:100})-[:predicateLink]->(b) RETURN COUNT(n) AS answer", 2),
            ("MATCH (n:PredicateNode) OPTIONAL MATCH (n WHERE n.age=100)-[:predicateLink]->(b) RETURN COUNT(b) AS answer", 0),
            ("MATCH (n:PredicateNode) OPTIONAL MATCH (n WHERE n.age=30)-[:predicateLink]->(b) RETURN COUNT(b) AS answer", 1),
            ("OPTIONAL MATCH (n:Missing) OPTIONAL MATCH (n:PredicateNode) RETURN COUNT(n) AS answer", 0),
            ("OPTIONAL MATCH (n:Missing) RETURN n NEXT MATCH (n:PredicateNode) RETURN COUNT(*) AS answer", 0),
            ("OPTIONAL MATCH (n:Missing) RETURN n NEXT YIELD n AS anchor OPTIONAL MATCH (anchor:PredicateNode) RETURN COUNT(anchor) AS answer", 0),
            ("MATCH (n:PredicateNode) WHERE EXISTS ((n)-[:predicateLink]->()) RETURN COUNT(*) AS answer", 1),
            ("MATCH (n:PredicateNode WHERE EXISTS (MATCH (n)-[:predicateLink]->())) RETURN COUNT(*) AS answer", 1),
            ("MATCH (n:PredicateNode) WHERE NOT EXISTS ((n)-[:predicateLink]->()) RETURN COUNT(*) AS answer", 1),
            ("MATCH (n:PredicateNode) WHERE VALUE { RETURN n.age AS age }>25 RETURN COUNT(*) AS answer", 1)
        })
        {
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query
            });
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, query + ": " + await response.Content.ReadAsStringAsync());
            Assert.AreEqual(count, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetInt32(), query);
        }
        using var updated = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "MATCH (n:PredicateNode WHERE n.age>=30) SET n.checked=TRUE"
        });
        Assert.AreEqual(HttpStatusCode.OK, updated.StatusCode, await updated.Content.ReadAsStringAsync());
        using var checkedRows = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (n:PredicateNode) WHERE n.checked IS TRUE RETURN COUNT(*) AS answer"
        });
        Assert.AreEqual(HttpStatusCode.OK, checkedRows.StatusCode, await checkedRows.Content.ReadAsStringAsync());
        Assert.AreEqual(1, (await checkedRows.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetInt32());
    }

    [TestMethod]
    public async Task Gql_standard_quantified_edge_variables_are_lists_and_named_paths_remain_paths()
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var inserted = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (:GroupNode {name:'A'})-[:groupLink {amount:1}]->(:GroupNode {name:'B'})-[:groupLink {amount:2}]->(:GroupNode {name:'C'})"
        });
        Assert.AreEqual(HttpStatusCode.OK, inserted.StatusCode, await inserted.Content.ReadAsStringAsync());
        foreach (var (query, count) in new[]
        {
            ("MATCH p=(a:GroupNode {name:'A'})-[edges:groupLink]->{0,2}(b) RETURN SIZE(edges)=PATH_LENGTH(p) AND edges IS TYPED LIST<EDGE NOT NULL> AS answer", 3),
            ("MATCH p=((a:GroupNode {name:'A'})-[edges:groupLink]->{0,2}(b)) RETURN SIZE(edges)=PATH_LENGTH(p) AS answer", 3),
            ("MATCH p=(()-[edges:groupLink]->()){1,2} RETURN SIZE(edges)=PATH_LENGTH(p) AND edges IS TYPED LIST<EDGE NOT NULL> AS answer", 3),
            ("MATCH p=(()-[edges:groupLink]->()){0,2} RETURN SIZE(edges)=PATH_LENGTH(p) AS answer", 6),
            ("MATCH p=(()-[edges:groupLink]->())+ RETURN SIZE(edges)=PATH_LENGTH(p) AS answer", 3),
            ("MATCH p=(()-[edges:groupLink {amount:0x1}]->()){1,2} RETURN SIZE(edges)=1 AS answer", 1),
            ("OPTIONAL MATCH p=(()-[edges:missing]->()){1,2} RETURN edges IS NULL AND p IS NULL AS answer", 1),
            ("MATCH REPEATABLE ELEMENTS p=WALK (()-[edges:groupLink]-()){2} RETURN SIZE(edges)=2 AND PATH_LENGTH(p)=2 AS answer", 6),
            ("MATCH REPEATABLE ELEMENTS p=TRAIL (()-[edges:groupLink]-()){2} RETURN SIZE(edges)=2 AND PATH_LENGTH(p)=2 AS answer", 2),
            ("MATCH p=(((a:GroupNode {name:'A'})-[e:groupLink]->(b)-[f:groupLink]->(c))) RETURN PATH_LENGTH(p)=2 AS answer", 1),
            ("MATCH ((a IS GroupNode|Absent {name:'A'})-[e:groupLink]->(b)) RETURN e.amount=1 AS answer", 1),
            ("MATCH (a:GroupNode {name:'A'})-[edges:groupLink]->{0,1}(b) WHERE a=b RETURN edges=[] AS answer", 1),
            ("MATCH (a:GroupNode {name:'A'})-[edges:groupLink]->{1}(b) FOR e IN edges RETURN e.amount=1 AS answer", 1),
            ("MATCH (a:GroupNode {name:'A'})-[edges:groupLink]->{2}(b) RETURN edges NEXT FOR e IN edges RETURN SUM(e.amount)=3 AS answer", 1),
            ("MATCH (a:GroupNode {name:'A'})-[edges:groupLink]->+(b) RETURN edges IS TYPED LIST<EDGE> AS answer", 2),
            ("MATCH (a:GroupNode {name:'A'}) OPTIONAL MATCH (a)-[edges:missing]->{1,2}(b) RETURN edges IS NULL AS answer", 1),
            ("MATCH (a:GroupNode {name:'A'})-[e:groupLink]->(b) RETURN e IS TYPED EDGE AS answer", 1)
        })
        {
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query
            });
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, query + ": " + await response.Content.ReadAsStringAsync());
            var rows = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
            Assert.AreEqual(count, rows.GetArrayLength(), query);
            foreach (var row in rows.EnumerateArray())
            {
                Assert.IsTrue(row.GetProperty("bindings").GetProperty("answer").GetBoolean(), query);
            }
        }
        using var updated = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "MATCH (()-[edges:groupLink]->()){2} FOR e IN edges SET e.checked=TRUE"
        });
        Assert.AreEqual(HttpStatusCode.OK, updated.StatusCode, await updated.Content.ReadAsStringAsync());
        using var checkedRows = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH ()-[e:groupLink]->() WHERE e.checked IS TRUE RETURN COUNT(*) AS answer"
        });
        Assert.AreEqual(HttpStatusCode.OK, checkedRows.StatusCode, await checkedRows.Content.ReadAsStringAsync());
        Assert.AreEqual(2, (await checkedRows.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetInt32());
    }

    [TestMethod]
    public async Task Gql_standard_cast_persists_target_types_and_rejects_invalid_writes_atomically()
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var inserted = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "FOR x IN [1,2] INSERT (:CastValue {amount:CAST(x AS DECIMAL),approximate:CAST(x AS FLOAT)})"
        });
        Assert.AreEqual(HttpStatusCode.OK, inserted.StatusCode, await inserted.Content.ReadAsStringAsync());
        using var rejected = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "FOR x IN [3,NULL] INSERT (:CastValue {amount:CAST(x AS DECIMAL NOT NULL)})"
        });
        Assert.AreEqual(HttpStatusCode.BadRequest, rejected.StatusCode, await rejected.Content.ReadAsStringAsync());
        using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (n:CastValue) RETURN COUNT(*)=2 AND SUM(n.amount)=3M AND SUM(CASE WHEN n.amount IS TYPED DECIMAL AND n.approximate IS TYPED FLOAT THEN 1 ELSE 0 END)=2 AS answer"
        });
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        Assert.IsTrue((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetBoolean());
    }

    [TestMethod]
    public async Task Gql_standard_named_edge_label_expressions_preserve_boolean_and_optional_scope()
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var inserted = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (a:Person {name:'A'})-[e:knows {rank:1}]->(b:Person {name:'B'}), (a)-[:likes {rank:2}]->(b), (b)-[:other {rank:3}]->(a)"
        });
        Assert.AreEqual(HttpStatusCode.OK, inserted.StatusCode, await inserted.Content.ReadAsStringAsync());
        foreach (var (query, expected) in new[]
        {
            ("MATCH ()-[e IS %]->() RETURN COUNT(*) AS answer", 3),
            ("MATCH ()-[e IS knows|likes]->() RETURN COUNT(*) AS answer", 2),
            ("MATCH ()-[e IS knows&likes]->() RETURN COUNT(*) AS answer", 0),
            ("MATCH ()-[e IS knows&knows]->() RETURN COUNT(*) AS answer", 1),
            ("MATCH ()<-[e IS knows]->() RETURN COUNT(*) AS answer", 2),
            ("MATCH ()<-[e IS knows|likes]->() RETURN COUNT(*) AS answer", 4),
            ("MATCH ()<->() RETURN COUNT(*) AS answer", 6),
            ("MATCH (a:Person {name:'A'})<-[e]->(b) RETURN COUNT(*) AS answer", 3),
            ("MATCH ()-[e IS !knows]->() RETURN COUNT(*) AS answer", 2),
            ("MATCH ()-[e IS (knows|likes)&!likes]->() RETURN COUNT(*) AS answer", 1),
            ("MATCH ()-[e IS knows|likes]->() WHERE e.rank=1 OR e.rank=3 RETURN COUNT(*) AS answer", 1),
            ("MATCH (a:Person {name:'A'}) OPTIONAL MATCH (a)-[e IS missing|absent]->(b) RETURN COUNT(*) AS answer", 1),
            ("MATCH (a IS Person|Missing)-[e IS knows|likes]->(b IS Person&!Missing) RETURN COUNT(*) AS answer", 2)
        })
        {
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query
            });
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, query + ": " + await response.Content.ReadAsStringAsync());
            Assert.AreEqual(expected, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetInt32(), query);
        }
        using var updated = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "MATCH ()-[e IS knows|likes]->() SET e.checked=TRUE"
        });
        Assert.AreEqual(HttpStatusCode.OK, updated.StatusCode, await updated.Content.ReadAsStringAsync());
        using var checkedRows = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH ()-[e]->() WHERE e.checked IS TRUE RETURN COUNT(*) AS answer"
        });
        Assert.AreEqual(HttpStatusCode.OK, checkedRows.StatusCode, await checkedRows.Content.ReadAsStringAsync());
        Assert.AreEqual(2, (await checkedRows.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetInt32());
    }

    [TestMethod]
    public async Task Gql_standard_named_node_label_expressions_preserve_boolean_and_optional_scope()
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var inserted = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (:Person & Customer {name:'A'}),(:Person {name:'B'}),({name:'C'})"
        });
        Assert.AreEqual(HttpStatusCode.OK, inserted.StatusCode, await inserted.Content.ReadAsStringAsync());
        foreach (var (query, expected) in new[]
        {
            ("MATCH (n IS %) RETURN COUNT(*) AS answer", 2),
            ("MATCH (n IS Person|Customer) RETURN COUNT(*) AS answer", 2),
            ("MATCH (n IS !Customer) RETURN COUNT(*) AS answer", 2),
            ("MATCH (n IS % & !Customer) RETURN COUNT(*) AS answer", 1),
            ("MATCH (n IS (Person|Company)&Customer) RETURN COUNT(*) AS answer", 1),
            ("MATCH (n IS Person|Company) WHERE n.name='A' OR n.name='C' RETURN COUNT(*) AS answer", 1),
            ("MATCH (a:Person {name:'A'}) OPTIONAL MATCH (b IS Company|Missing) RETURN COUNT(*) AS answer", 1)
        })
        {
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query
            });
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, query + ": " + await response.Content.ReadAsStringAsync());
            Assert.AreEqual(expected, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetInt32(), query);
        }
        using var updated = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "MATCH (n IS Person|Company) SET n.checked=TRUE"
        });
        Assert.AreEqual(HttpStatusCode.OK, updated.StatusCode, await updated.Content.ReadAsStringAsync());
        using var checkedRows = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (n) WHERE n.checked IS TRUE RETURN COUNT(*) AS answer"
        });
        Assert.AreEqual(HttpStatusCode.OK, checkedRows.StatusCode, await checkedRows.Content.ReadAsStringAsync());
        Assert.AreEqual(2, (await checkedRows.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetInt32());
    }

    [TestMethod]
    public async Task Gql_standard_NODETACH_delete_preserves_connected_nodes_on_failure()
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (:DeleteSample {name:'A'})-[:link]->(:DeleteSample {name:'B'}),(:DeleteSample {name:'isolated'})"
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode, await seed.Content.ReadAsStringAsync());
        using var rejected = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "MATCH (n:DeleteSample {name:'A'}) SET n.name='changed' NODETACH DELETE n"
        });
        Assert.AreEqual(HttpStatusCode.BadRequest, rejected.StatusCode, await rejected.Content.ReadAsStringAsync());
        var problem = await rejected.Content.ReadFromJsonAsync<JsonElement>();
        Assert.IsTrue(problem.TryGetProperty("gqlStatus", out var status), problem.ToString());
        Assert.AreEqual("G1001", status.GetString());
        using var removed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "MATCH (n:DeleteSample {name:'isolated'}) NODETACH DELETE n"
        });
        Assert.AreEqual(HttpStatusCode.OK, removed.StatusCode, await removed.Content.ReadAsStringAsync());
        using var remaining = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (:DeleteSample {name:'A'})-[:link]->(:DeleteSample {name:'B'}) RETURN COUNT(*) AS answer"
        });
        Assert.AreEqual(HttpStatusCode.OK, remaining.StatusCode, await remaining.Content.ReadAsStringAsync());
        Assert.AreEqual(1, (await remaining.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetInt32());
        using var removedTogether = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "MATCH (a:DeleteSample {name:'A'})-[e:link]->(b) NODETACH DELETE a,e"
        });
        Assert.AreEqual(HttpStatusCode.OK, removedTogether.StatusCode, await removedTogether.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public async Task Gql_standard_IS_label_introducers_and_insert_label_sets()
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        foreach (string statement in new[]
        {
            "INSERT (a IS Person & Customer {name:'Ada'})-[e IS knows {amount:1}]->(b IS Person {name:'Bob'})",
            "MATCH (n IS Customer) SET n IS Verified",
            "MATCH (n IS Customer) REMOVE n IS Customer"
        })
        {
            using var update = await client.PostAsJsonAsync("/v1/graph/gql/update", new
            {
                statement
            });
            Assert.AreEqual(HttpStatusCode.OK, update.StatusCode, statement + ": " + await update.Content.ReadAsStringAsync());
        }
        foreach (var (query, expected) in new[]
        {
            ("MATCH (n IS Person & Verified) RETURN COUNT(*) AS answer", 1),
            ("MATCH (n IS Customer) RETURN COUNT(*) AS answer", 0),
            ("MATCH (a IS Person)-[e IS knows]->(b IS Person) RETURN COUNT(*) AS answer", 1),
            ("MATCH (n : Person) RETURN COUNT(*) AS answer", 2)
        })
        {
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query
            });
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, query + ": " + await response.Content.ReadAsStringAsync());
            Assert.AreEqual(expected, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetInt32());
        }
    }

    [TestMethod]
    public async Task Gql_standard_id_is_an_ordinary_property_independent_of_element_identity()
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        var request = new
        {
            statement = "INSERT (:BusinessKey {id:'same'})-[:link {id:42}]->(:BusinessKey {id:'same'}), (:BusinessKey {id:NULL})",
            transactionId = Guid.NewGuid()
        };
        for (int attempt = 0; attempt < 2; attempt++)
        {
            using var inserted = await client.PostAsJsonAsync("/v1/graph/gql/update", request);
            Assert.AreEqual(HttpStatusCode.OK, inserted.StatusCode, await inserted.Content.ReadAsStringAsync());
        }
        using var rows = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "FOR key IN [7,7] INSERT (:RowBusinessKey {id:key}) FINISH"
        });
        Assert.AreEqual(HttpStatusCode.OK, rows.StatusCode, await rows.Content.ReadAsStringAsync());
        foreach (var (query, expected) in new[]
        {
            ("MATCH (n:BusinessKey) RETURN COUNT(*) AS answer", 3),
            ("MATCH (n:BusinessKey {id:'same'}) RETURN COUNT(DISTINCT ELEMENT_ID(n)) AS answer", 2),
            ("MATCH (n:BusinessKey) WHERE n.id IS NULL RETURN COUNT(*) AS answer", 1),
            ("MATCH (:BusinessKey)-[e:link {id:42}]->(:BusinessKey) RETURN COUNT(*) AS answer", 1),
            ("MATCH (n:RowBusinessKey {id:7}) RETURN COUNT(DISTINCT ELEMENT_ID(n)) AS answer", 2)
        })
        {
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query
            });
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, query + ": " + await response.Content.ReadAsStringAsync());
            Assert.AreEqual(expected, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetInt32(), query);
        }
    }

    [TestMethod]
    public async Task Gql_standard_for_positions_persist_through_updates_and_next()
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var inserted = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "FOR amount IN [10,20,30] WITH ORDINALITY position RETURN amount,position NEXT INSERT (:PositionSample {amount:amount,position:position}) FINISH"
        });
        Assert.AreEqual(HttpStatusCode.OK, inserted.StatusCode, await inserted.Content.ReadAsStringAsync());
        using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (n:PositionSample) RETURN SUM(n.amount*n.position) AS answer"
        });
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        Assert.AreEqual(140, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetInt32());
    }

    [TestMethod]
    public async Task Gql_standard_comments_are_never_executed_as_vendor_syntax()
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (:CommentNode {name:'A'})-[:link]->(:CommentNode {name:'B'}) -- CREATE (:Forbidden)"
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode, await seed.Content.ReadAsStringAsync());
        foreach (var (query, expected) in new[]
        {
            ("MATCH (:Forbidden) RETURN COUNT(*) AS answer", 0),
            ("MATCH (:CommentNode {name:'A'})->(b) RETURN COUNT(*) AS answer", 1),
            ("MATCH (:CommentNode {name:'B'})<-(b) RETURN COUNT(*) AS answer", 1),
            ("MATCH (:CommentNode {name:'A'})-(b) RETURN COUNT(*) AS answer", 1),
            ("RETURN 1 AS answer -- NEXT RETURN 2 AS answer", 1),
            ("RETURN /* discarded */ 1 AS answer // trailing text", 1),
            ("RETURN 1 AS n NEXT /* boundary */ YIELD n AS x RETURN x AS answer", 1)
        })
        {
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query
            });
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, query + ": " + await response.Content.ReadAsStringAsync());
            Assert.AreEqual(expected, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetInt32(), query);
        }
        using var hiddenReturn = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (n)-->(b) RETURN NODES(n) AS answer"
        });
        Assert.AreEqual(HttpStatusCode.BadRequest, hiddenReturn.StatusCode);
    }

    [TestMethod]
    public async Task Gql_standard_exact_decimal_properties_preserve_precision_and_arrays()
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        const string statement = "INSERT (:ExactNumber {amount:0.1234567890123456789012345678M, negative:-0.1234567890123456789012345678M, amounts:[1e-1M,2e-1M], radix:0xFF})";
        using var inserted = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement
        });
        Assert.AreEqual(HttpStatusCode.OK, inserted.StatusCode, await inserted.Content.ReadAsStringAsync());
        foreach (string expression in new[]
        {
            "n.amount=0.1234567890123456789012345678",
            "n.negative=-0.1234567890123456789012345678",
            "n.amounts=[0.1,0.2]",
            "n.radix=255",
            "SUM(n.amount)=0.1234567890123456789012345678"
        })
        {
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = $"MATCH (n:ExactNumber) RETURN {expression} AS answer"
            });
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            Assert.IsTrue((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetBoolean(), expression);
        }
    }

    [TestMethod]
    [DataRow("WALK", 2, 1)]
    [DataRow("TRAIL", 2, 0)]
    [DataRow("ACYCLIC", 2, 0)]
    [DataRow("SIMPLE", 2, 1)]
    [DataRow("SIMPLE", 4, 0)]
    [DataRow("WALK", 4, 1)]
    [DataRow("ACYCLIC", 1, 1)]
    [DataRow("TRAIL", 1, 1)]
    [DataRow("ALL SIMPLE", 2, 1)]
    [DataRow("ANY WALK", 2, 1)]
    [DataRow("ANY 0 WALK", 2, 0)]
    [DataRow("SHORTEST 0 WALK", 2, 0)]
    [DataRow("ANY SHORTEST ACYCLIC", 2, 0)]
    [DataRow("ALL SHORTEST SIMPLE", 2, 1)]
    [DataRow("SHORTEST 1 SIMPLE PATH GROUP", 2, 1)]
    public async Task Gql_standard_path_modes_apply_to_the_whole_path(string mode, int hops, int expected)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (:ModeNode {name:'A'})-[:link]->(:ModeNode {name:'B'})"
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode, await seed.Content.ReadAsStringAsync());
        foreach (string assignment in new[] { "p = ", "" })
        {
            string query = $"MATCH REPEATABLE ELEMENTS {assignment}{mode} (a:ModeNode {{name:'A'}})-[:link]-{{{hops}}}(b) RETURN COUNT(*) AS total";
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query
            });
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            Assert.AreEqual(expected, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("total").GetInt32(), query);
        }
        using var elements = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH p=(a:ModeNode {name:'A'})-[e:link]->(b) RETURN ELEMENTS(p)=[a,e,b] AS answer"
        });
        Assert.AreEqual(HttpStatusCode.OK, elements.StatusCode, await elements.Content.ReadAsStringAsync());
        Assert.IsTrue((await elements.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetBoolean());
    }

    [TestMethod]
    public async Task Gql_standard_queries_do_not_apply_an_unrequested_hundred_row_limit()
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        string numbers = String.Join(',', Enumerable.Range(1, 125));
        using var inserted = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = $"FOR x IN [{numbers}] INSERT (:Many {{amount:x}})"
        });
        Assert.AreEqual(HttpStatusCode.OK, inserted.StatusCode, await inserted.Content.ReadAsStringAsync());
        foreach (string query in new[]
        {
            "MATCH (n:Many) RETURN n",
            "MATCH (n:Many) RETURN ALL n",
            "MATCH (n:Many) RETURN n.amount AS amount",
            "MATCH (n:Many) RETURN n.amount AS amount, COUNT(*) AS total GROUP BY amount",
            $"FOR x IN [{numbers}] RETURN x AS answer",
            "MATCH (n:Many) RETURN n.amount AS amount NEXT RETURN amount AS answer"
        })
        {
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query
            });
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            Assert.AreEqual(125, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches").GetArrayLength(), query);
        }
    }

    [TestMethod]
    public async Task Gql_standard_finish_discards_rows_but_preserves_mutations()
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var inserted = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (:Finished {name:'Ada'}) FINISH"
        });
        Assert.AreEqual(HttpStatusCode.OK, inserted.StatusCode, await inserted.Content.ReadAsStringAsync());
        using var finished = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (n:Finished) FINISH"
        });
        Assert.AreEqual(HttpStatusCode.OK, finished.StatusCode, await finished.Content.ReadAsStringAsync());
        Assert.AreEqual(0, (await finished.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches").GetArrayLength());
        using var count = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (n:Finished) RETURN COUNT(*) AS total"
        });
        Assert.AreEqual(HttpStatusCode.OK, count.StatusCode, await count.Content.ReadAsStringAsync());
        Assert.AreEqual(1, (await count.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("total").GetInt32());
    }

    [TestMethod]
    public async Task Gql_standard_boundary_rejects_extensions_before_execution()
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        foreach (string statement in new[]
        {
            "CREATE (:Unexpected)", "MERGE (:Unexpected)",
            "INSERT (:Unexpected) FOREACH (x IN [1] | INSERT (:Unexpected))"
        })
        {
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/update", new
            {
                statement
            });
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, await response.Content.ReadAsStringAsync());
        }
        foreach (string query in new[]
        {
            "UNWIND [1] AS x RETURN x", "WITH 1 AS x RETURN x", "RETURN 'a' =~ 'a' AS answer",
            "RETURN string.regexReplace('a','a','b') AS answer", "RETURN 1 AS value"
        })
        {
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query
            });
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, await response.Content.ReadAsStringAsync());
            using var explain = await client.PostAsJsonAsync("/v1/graph/gql/explain", new
            {
                query = new
                {
                    query
                }
            });
            Assert.AreEqual(HttpStatusCode.BadRequest, explain.StatusCode, await explain.Content.ReadAsStringAsync());
        }
        using var checkedGraph = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (n:Unexpected) RETURN COUNT(*) AS total"
        });
        Assert.AreEqual(HttpStatusCode.OK, checkedGraph.StatusCode, await checkedGraph.Content.ReadAsStringAsync());
        Assert.AreEqual(0, (await checkedGraph.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("total").GetInt32());
        using var inserted = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (:Standard {name:'Ada'})"
        });
        Assert.AreEqual(HttpStatusCode.OK, inserted.StatusCode, await inserted.Content.ReadAsStringAsync());
        using var identity = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (n:Standard) RETURN ELEMENT_ID(n) AS identity"
        });
        Assert.AreEqual(HttpStatusCode.OK, identity.StatusCode, await identity.Content.ReadAsStringAsync());
        Assert.IsFalse(String.IsNullOrEmpty((await identity.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("identity").GetString()));
    }

    // Expected results follow GQL, including deliberate differences from Cypher.
    [TestMethod]
    [DataRow("RETURN TRUE OR FALSE XOR TRUE AS answer", "false")]
    [DataRow("RETURN 1 IS TYPED ANY VALUE AS answer", "true")]
    [DataRow("RETURN NULL IS TYPED ANY VALUE NOT NULL AS answer", "false")]
    [DataRow("RETURN RECORD {name:'Ada'} IS TYPED RECORD AS answer", "true")]
    [DataRow("RETURN RECORD {name:'Ada'} IS TYPED ANY RECORD AS answer", "true")]
    [DataRow("RETURN ARRAY[1,NULL] IS TYPED ARRAY<INTEGER> AS answer", "true")]
    [DataRow("RETURN ARRAY[1,NULL] IS TYPED LIST<INTEGER NOT NULL> AS answer", "false")]
    [DataRow("RETURN [] IS :: LIST AS answer", "true")]
    [DataRow("RETURN NULL IS :: INTEGER NOT NULL | STRING AS answer", "true")]
    [DataRow("RETURN [1,'text'] IS TYPED LIST AS answer", "true")]
    [DataRow("RETURN NULL IS TYPED ANY VALUE<INTEGER NOT NULL | STRING> AS answer", "true")]
    [DataRow("RETURN NULL IS TYPED ANY<INTEGER NOT NULL | STRING NOT NULL> AS answer", "false")]
    [DataRow("RETURN 'text' IS TYPED INTEGER NOT NULL | STRING AS answer", "true")]
    [DataRow("RETURN LIST[1,2,3]=[1,2,3] AS answer", "true")]
    [DataRow("RETURN ARRAY[1,2,3]=LIST[1,2,3] AS answer", "true")]
    [DataRow("FOR x IN LIST[1,2,3] RETURN SUM(x) AS answer", "6")]
    [DataRow("RETURN RECORD {name:'Ada'}.name AS answer", "\"Ada\"")]
    [DataRow("LET r=RECORD {items:ARRAY[1,2,3]} FOR x IN r.items RETURN SUM(x) AS answer", "6")]
    [DataRow("RETURN \"a\"\"b\" = 'a\"b' AS answer", "true")]
    [DataRow("RETURN '\\U01F600' = '😀' AS answer", "true")]
    [DataRow("RETURN CHAR_LENGTH('\\U01F600') AS answer", "1")]
    [DataRow("RETURN '\\`' = '`' AS answer", "true")]
    [DataRow("RETURN \"\\'\" = '''' AS answer", "true")]
    [DataRow("RETURN '\\U110000' AS answer", "ERROR")]
    [DataRow("RETURN '\\U00D800' AS answer", "ERROR")]
    [DataRow("RETURN 0xFF AS answer", "255")]
    [DataRow("RETURN 0o77 AS answer", "63")]
    [DataRow("RETURN 0b1010 AS answer", "10")]
    [DataRow("RETURN 0x_FF + 0b1_0 + 0o1_0 AS answer", "265")]
    [DataRow("RETURN 1_000 AS answer", "1000")]
    [DataRow("RETURN .5 AS answer", "0.5")]
    [DataRow("RETURN 1. AS answer", "1.0")]
    [DataRow("RETURN 12.34M AS answer", "12.34")]
    [DataRow("RETURN 1_2.3_4M AS answer", "12.34")]
    [DataRow("RETURN 0.1M+0.2M=0.3M AS answer", "true")]
    [DataRow("RETURN 1e2M AS answer", "100.0")]
    [DataRow("RETURN 1e-2M AS answer", "0.01")]
    [DataRow("RETURN 1e-29M AS answer", "ERROR")]
    [DataRow("RETURN 1e999999M AS answer", "ERROR")]
    [DataRow("RETURN -0x8000000000000000 AS answer", "-9223372036854775808")]
    [DataRow("RETURN 0x10000000000000000 AS answer", "ERROR")]
    [DataRow("FOR x IN [1,2,3] WITH ORDINALITY i FILTER i=0x2 RETURN x AS answer", "2")]
    [DataRow("FOR x IN [10,20,30] WITH ORDINALITY i RETURN SUM(x*i) AS answer", "140")]
    [DataRow("FOR x IN [10,20,30] WITH OFFSET i RETURN SUM(x*i) AS answer", "80")]
    [DataRow("FOR xs IN [[10,20],[30]] FOR x IN xs WITH ORDINALITY i RETURN SUM(i) AS answer", "4")]
    [DataRow("FOR x IN [NULL,5] WITH ORDINALITY i RETURN SUM(i) AS answer", "3")]
    [DataRow("FOR x IN [] WITH OFFSET i RETURN COUNT(*) AS answer", "0")]
    [DataRow("FOR x IN NULL WITH ORDINALITY i RETURN COUNT(*) AS answer", "0")]
    [DataRow("FOR x IN [1] WITH ORDINALITY x RETURN x AS answer", "ERROR")]
    [DataRow("LET i=42 FOR x IN [1] WITH OFFSET i RETURN i AS answer", "ERROR")]
    [DataRow("FOR x IN [DATE '2024-02-29'] WITH ORDINALITY i RETURN i AS answer", "1")]
    [DataRow("RETURN DATE '2024-02-29'=DATE('2024-02-29') AS answer", "true")]
    [DataRow("RETURN DATE /* literal */ '2024-02-29'=DATE('2024-02-29') AS answer", "true")]
    [DataRow("RETURN DURATION 'P2D'=DURATION('P2D') AS answer", "true")]
    [DataRow("RETURN DATE '2023-02-29' AS answer", "ERROR")]
    [DataRow("RETURN CURRENT_DATE=DATE() AS answer", "true")]
    [DataRow("RETURN CURRENT_TIME=ZONED_TIME() AS answer", "true")]
    [DataRow("RETURN CURRENT_TIMESTAMP=ZONED_DATETIME() AS answer", "true")]
    [DataRow("RETURN LOCAL_TIME=LOCAL_TIME() AS answer", "true")]
    [DataRow("RETURN LOCAL_TIMESTAMP=LOCAL_DATETIME() AS answer", "true")]
    [DataRow("RETURN CURRENT_TIMESTAMP AS stamp NEXT RETURN stamp=CURRENT_TIMESTAMP AS answer", "true")]
    [DataRow("RETURN 1 AS n NEXT RETURN n+1 AS answer", "2")]
    [DataRow("RETURN 1 AS n, 2 AS m NEXT YIELD n AS x RETURN x+1 AS answer", "2")]
    [DataRow("RETURN 1 AS n, 2 AS m NEXT YIELD n RETURN m AS answer", "ERROR")]
    [DataRow("RETURN 1 AS n NEXT YIELD absent RETURN absent AS answer", "ERROR")]
    [DataRow("FOR x IN [1,2,3] RETURN SUM(x) AS total NEXT YIELD total AS subtotal RETURN subtotal*2 AS answer", "12")]
    [DataRow("FOR x IN [1,2,3] RETURN SUM(x) AS total NEXT RETURN total*2 AS answer", "12")]
    [DataRow("RETURN 1 AS n NEXT RETURN n+1 AS n NEXT RETURN n+1 AS answer", "3")]
    [DataRow("RETURN GRAPH", "ERROR")]
    [DataRow("RETURN 0.1234567890123456789012345678 AS answer", "0.1234567890123456789012345678")]
    [DataRow("RETURN 0.1+0.2=0.3 AS answer", "true")]
    [DataRow("RETURN 0.00000000000000000000000000001 AS answer", "ERROR")]
    [DataRow("RETURN 0.12345678901234567890123456789 AS answer", "ERROR")]
    [DataRow("RETURN SIZE('abc') AS answer", "ERROR")]
    [DataRow("RETURN SIZE([1,2,3]) AS answer", "3")]
    [DataRow("RETURN SIZE(NULL) AS answer", "null")]
    [DataRow("RETURN TRIM('  x  ') AS answer", "\"x\"")]
    [DataRow("RETURN TRIM(FROM '  x  ') AS answer", "\"x\"")]
    [DataRow("RETURN TRIM(LEADING FROM '  x  ') AS answer", "\"x  \"")]
    [DataRow("RETURN TRIM(TRAILING FROM '  x  ') AS answer", "\"  x\"")]
    [DataRow("RETURN TRIM(BOTH FROM '  x  ') AS answer", "\"x\"")]
    [DataRow("RETURN TRIM('x' FROM 'xxabcxx') AS answer", "\"abc\"")]
    [DataRow("RETURN TRIM(LEADING 'x' FROM 'xxabcxx') AS answer", "\"abcxx\"")]
    [DataRow("RETURN TRIM(TRAILING 'x' FROM 'xxabcxx') AS answer", "\"xxabc\"")]
    [DataRow("RETURN TRIM(BOTH '😀' FROM '😀x😀') AS answer", "\"x\"")]
    [DataRow("RETURN TRIM(BOTH NULL FROM 'x') AS answer", "null")]
    [DataRow("RETURN TRIM(BOTH 'x' FROM NULL) AS answer", "null")]
    [DataRow("RETURN TRIM(BOTH 1 FROM 'x') AS answer", "ERROR")]
    [DataRow("RETURN TRIM(BOTH 'x' FROM 1) AS answer", "ERROR")]
    [DataRow("RETURN BTRIM('  x  ') AS answer", "\"x\"")]
    [DataRow("RETURN BTRIM('\\t x \\t')='\\t x \\t' AS answer", "true")]
    [DataRow("RETURN BTRIM('\\u2003x\\u2003')='\\u2003x\\u2003' AS answer", "true")]
    [DataRow("RETURN LTRIM('  \\tx  ')='\\tx  ' AS answer", "true")]
    [DataRow("RETURN RTRIM('  x\\t  ')='  x\\t' AS answer", "true")]
    [DataRow("RETURN BTRIM('\\t x \\t',' \\t') AS answer", "\"x\"")]
    [DataRow("RETURN BTRIM('😀x😀','😀') AS answer", "\"x\"")]
    [DataRow("RETURN BTRIM('xyx','') AS answer", "\"xyx\"")]
    [DataRow("RETURN BTRIM(NULL) AS answer", "null")]
    [DataRow("RETURN BTRIM('x',NULL) AS answer", "null")]
    [DataRow("RETURN CARDINALITY(RECORD {name:'Ada',age:37}) AS answer", "2")]
    [DataRow("RETURN CARDINALITY([1,NULL,3]) AS answer", "3")]
    [DataRow("RETURN CARDINALITY('abc') AS answer", "ERROR")]
    [DataRow("RETURN CHAR_LENGTH('abc') AS answer", "3")]
    [DataRow("RETURN 'a'+1 AS answer", "ERROR")]
    [DataRow("RETURN 'a'+'b' AS answer", "ERROR")]
    [DataRow("RETURN 'a'||'b' AS answer", "\"ab\"")]
    [DataRow("RETURN [1]+[2] AS answer", "ERROR")]
    [DataRow("RETURN [1]+2 AS answer", "ERROR")]
    [DataRow("RETURN [1]||[2] AS answer", "[1,2]")]
    [DataRow("FOR x IN 1 RETURN x AS answer", "ERROR")]
    [DataRow("RETURN DURATION_BETWEEN(DATE('2026-02-01'),DATE('2026-03-02')) = DURATION('P29D') AS answer", "true")]
    [DataRow("RETURN DURATION_BETWEEN(DATE('2026-03-02'),DATE('2026-02-01')) = DURATION('-P29D') AS answer", "true")]
    [DataRow("RETURN DURATION_BETWEEN(DATE('2024-02-01'),DATE('2024-03-01')) DAY TO SECOND = DURATION('P29D') AS answer", "true")]
    [DataRow("RETURN DURATION_BETWEEN(DATE('2026-02-01'),DATE('2026-03-02')) YEAR TO MONTH = DURATION('P1M') AS answer", "true")]
    [DataRow("RETURN DURATION_BETWEEN(NULL,DATE('2026-03-02')) AS answer", "null")]
    [DataRow("RETURN TRUE OR (FALSE XOR TRUE) AS answer", "true")]
    [DataRow("RETURN FALSE XOR TRUE AND FALSE AS answer", "false")]
    [DataRow("RETURN UNKNOWN AS answer", "null")]
    [DataRow("RETURN UNKNOWN IS UNKNOWN AS answer", "true")]
    [DataRow("RETURN UNKNOWN IS NOT TRUE AS answer", "true")]
    [DataRow("RETURN FALSE IS FALSE AS answer", "true")]
    [DataRow("RETURN NOT FALSE IS TRUE AS answer", "true")]
    [DataRow("RETURN UNKNOWN IS FALSE AS answer", "false")]
    [DataRow("RETURN TRUE IS NOT UNKNOWN AS answer", "true")]
    [DataRow("RETURN 1 IS TRUE AS answer", "ERROR")]
    [DataRow("RETURN CASE 'abc' WHEN IS NORMALIZED THEN 1 ELSE 0 END AS answer", "1")]
    [DataRow("RETURN CASE 'e\\u0301' WHEN IS NOT NFC NORMALIZED THEN 1 ELSE 0 END AS answer", "1")]
    [DataRow("RETURN CASE 'e\\u0301' WHEN IS NFD NORMALIZED THEN 1 ELSE 0 END AS answer", "1")]
    [DataRow("RETURN CASE NULL WHEN IS NORMALIZED THEN 0 WHEN IS NOT NORMALIZED THEN 0 ELSE 1 END AS answer", "1")]
    [DataRow("RETURN CASE 'abc' WHEN IS NORMALIZED THEN 1/1 ELSE 1/0 END AS answer", "1")]
    [DataRow("RETURN CASE 1 WHEN IS DIRECTED THEN 1 ELSE 0 END AS answer", "ERROR")]
    [DataRow("RETURN -DURATION('P1D')=DURATION('P-1D') AS answer", "true")]
    [DataRow("RETURN [DURATION('P1D')]=[DURATION('PT24H')] AS answer", "true")]
    [DataRow("RETURN RECORD {span:DURATION('P1D')}=RECORD {span:DURATION('PT24H')} AS answer", "true")]
    [DataRow("RETURN DURATION('P9223372036854775807D')>DURATION('P9223372036854775806D') AS answer", "true")]
    [DataRow("RETURN DURATION('P1D')=DURATION('PT24H') AS answer", "true")]
    [DataRow("RETURN DURATION('-P1D')=DURATION('-PT24H') AS answer", "true")]
    [DataRow("RETURN DURATION('P1DT-24H')=DURATION('PT0S') AS answer", "true")]
    [DataRow("RETURN DURATION('PT23H59M59.999999999S')<DURATION('P1D') AS answer", "true")]
    [DataRow("RETURN DURATION('P1D')<>DURATION('PT24H0.000000001S') AS answer", "true")]
    [DataRow("RETURN DURATION('P2M')>DURATION('P1M') AS answer", "true")]
    [DataRow("RETURN DURATION('P1Y')=DURATION('P12M') AS answer", "true")]
    [DataRow("FOR span IN [DURATION('P1D'),DURATION('PT24H')] RETURN COUNT(DISTINCT span) AS answer", "1")]
    [DataRow("FOR spans IN [[DURATION('P1D')],[DURATION('PT24H')]] RETURN COUNT(DISTINCT spans) AS answer", "1")]
    [DataRow("FOR span IN [DURATION('P1D'),DURATION('PT24H')] RETURN span AS grouped GROUP BY grouped NEXT RETURN COUNT(*) AS answer", "1")]
    [DataRow("FOR span IN [DURATION('P2D'),DURATION('PT24H')] RETURN MIN(span)=DURATION('P1D') AS answer", "true")]
    [DataRow("FOR span IN [DURATION('P2D'),DURATION('PT24H')] RETURN span AS selected ORDER BY span LIMIT 1 NEXT RETURN selected=DURATION('P1D') AS answer", "true")]
    [DataRow("RETURN DURATION('P1M')<DURATION('P30D') AS answer", "ERROR")]
    [DataRow("RETURN DURATION('P1M1D')=DURATION('P1M1D') AS answer", "ERROR")]
    [DataRow("RETURN DURATION('P0.5M') AS answer", "ERROR")]
    [DataRow("RETURN DURATION('P0.5Y') AS answer", "ERROR")]
    [DataRow("RETURN DURATION('P1.5D') AS answer", "ERROR")]
    [DataRow("RETURN DURATION('PT1.5H') AS answer", "ERROR")]
    [DataRow("RETURN DURATION('PT1.5M') AS answer", "ERROR")]
    [DataRow("RETURN DURATION('PT1.5S')=DURATION('PT1S')+DURATION('PT0.5S') AS answer", "true")]
    [DataRow("RETURN DURATION('P1M1D') AS answer", "ERROR")]
    [DataRow("RETURN DURATION('P1M')/2 AS answer", "ERROR")]
    [DataRow("RETURN DURATION('P1M')*0.5 AS answer", "ERROR")]
    [DataRow("RETURN DURATION(RECORD {months:0.5}) AS answer", "ERROR")]
    [DataRow("RETURN DURATION(RECORD {months:1,days:1}) AS answer", "ERROR")]
    [DataRow("RETURN DURATION('P2M')/2=DURATION('P1M') AS answer", "true")]
    [DataRow("RETURN DURATION('P1M')*2=DURATION('P2M') AS answer", "true")]
    [DataRow("RETURN DURATION('P1D')/2=DURATION('PT12H') AS answer", "true")]
    [DataRow("RETURN DURATION('P1M')+DURATION('P1D') AS answer", "ERROR")]
    [DataRow("LET spanValue=DURATION('P0M') RETURN spanValue IS TYPED DURATION(YEAR TO MONTH) AS answer", "true")]
    [DataRow("LET spanValue=DURATION('PT0S') RETURN spanValue IS TYPED DURATION(DAY TO SECOND) AS answer", "true")]
    [DataRow("LET spanValue=DURATION('P0M') RETURN spanValue IS TYPED DURATION(DAY TO SECOND) AS answer", "false")]
    [DataRow("RETURN NULL IS TYPED DURATION(YEAR TO MONTH) AS answer", "true")]
    [DataRow("RETURN NULL IS TYPED DURATION(YEAR TO MONTH) NOT NULL AS answer", "false")]
    [DataRow("LET spanValue=DURATION(RECORD {months:0}) RETURN spanValue IS TYPED DURATION(YEAR TO MONTH) AS answer", "true")]
    [DataRow("LET spanValue=DURATION(RECORD {seconds:0}) RETURN spanValue IS TYPED DURATION(DAY TO SECOND) AS answer", "true")]
    [DataRow("RETURN DURATION('P0M')=DURATION('PT0S') AS answer", "ERROR")]
    [DataRow("RETURN +DURATION('P1M')=DURATION('P1M') AS answer", "true")]
    [DataRow("RETURN ABS(DURATION('-P1Y2M'))=DURATION('P14M') AS answer", "true")]
    [DataRow("RETURN ABS(DURATION('P2M'))=DURATION('P2M') AS answer", "true")]
    [DataRow("RETURN ABS(DURATION('-P2DT3S'))=DURATION('P2DT3S') AS answer", "true")]
    [DataRow("RETURN ABS(DURATION('-PT0.000000001S'))=DURATION('PT0.000000001S') AS answer", "true")]
    [DataRow("RETURN ABS(DURATION('PT0S'))=DURATION('PT0S') AS answer", "true")]
    [DataRow("RETURN ABS(DURATION('P1D')-DURATION('PT25H'))=DURATION('PT1H') AS answer", "true")]
    [DataRow("RETURN ABS(DURATION('-P1000000000000000DT0.000000001S'))=DURATION('P1000000000000000DT0.000000001S') AS answer", "true")]
    [DataRow("RETURN ABS(DURATION('P-9223372036854775808D')) AS answer", "ERROR")]
    [DataRow("RETURN ABS(DURATION('P-9223372036854775808M')) AS answer", "ERROR")]
    [DataRow("RETURN ABS(DURATION('P1M1D')) AS answer", "ERROR")]
    [DataRow("RETURN ABS(NULL) AS answer", "null")]
    [DataRow("RETURN -DURATION('PT0.000000001S')=DURATION('PT-0.000000001S') AS answer", "true")]
    [DataRow("RETURN -(-DURATION('PT0.000000001S'))=DURATION('PT0.000000001S') AS answer", "true")]
    [DataRow("RETURN -DURATION('P-9223372036854775808M') AS answer", "ERROR")]
    [DataRow("FOR d IN [NULL] RETURN -d AS answer", "null")]
    [DataRow("LET d=DATE('2020-01-02') RETURN d.ordinalDay AS answer", "ERROR")]
    [DataRow("RETURN (DATE('2020-01-02')).ordinalDay AS answer", "ERROR")]
    [DataRow("RETURN RECORD {item:DATE('2020-01-02')}.item.ordinalDay AS answer", "ERROR")]
    [DataRow("FOR d IN [DURATION('P1D')] RETURN d.days AS answer", "ERROR")]
    [DataRow("RETURN RECORD {calendarYear:2020}.calendarYear AS answer", "2020")]
    [DataRow("RETURN LOG(2,8) AS answer", "3")]
    [DataRow("RETURN 42 IS TYPED SIGNED INTEGER AS answer", "true")]
    [DataRow("RETURN NULL IS TYPED SIGNED INTEGER AS answer", "true")]
    [DataRow("RETURN NULL IS TYPED SIGNED INTEGER NOT NULL AS answer", "false")]
    [DataRow("RETURN [1,2] IS TYPED SIGNED INTEGER LIST NOT NULL AS answer", "true")]
    [DataRow("RETURN 42 IS TYPED ANY<STRING|SIGNED INTEGER> AS answer", "true")]
    [DataRow("RETURN CAST(42 AS SIGNED INTEGER) AS answer", "42")]
    [DataRow("RETURN CAST('42' AS SIGNED INTEGER) AS answer", "ERROR")]
    [DataRow("RETURN 42 IS TYPED SIGNED INT AS answer", "ERROR")]
    [DataRow("RETURN LOG(10,100) AS answer", "2")]
    [DataRow("RETURN LOG(1,10) AS answer", "ERROR")]
    [DataRow("RETURN LOG(0,10) AS answer", "ERROR")]
    [DataRow("RETURN LOG(2,0) AS answer", "ERROR")]
    [DataRow("RETURN LOG(10) AS answer", "ERROR")]
    [DataRow("RETURN LN(0) AS answer", "ERROR")]
    [DataRow("RETURN LN(-1) AS answer", "ERROR")]
    [DataRow("RETURN LOG10(0) AS answer", "ERROR")]
    [DataRow("RETURN LN(NULL) AS answer", "null")]
    [DataRow("MATCH (n:Absent) RETURN SUM(1) AS answer", "null")]
    [DataRow("MATCH (n:Absent) RETURN SUM(n.amount) AS answer", "null")]
    [DataRow("FOR x IN [NULL,NULL] RETURN SUM(x) AS answer", "null")]
    [DataRow("FOR x IN [] RETURN SUM(x) AS answer", "null")]
    [DataRow("FOR x IN [] RETURN SUM(x) AS answer GROUP BY ()", "null")]
    [DataRow("FOR x IN [1,2,3] RETURN COUNT(*) AS answer GROUP BY ()", "3")]
    [DataRow("FOR x IN [1,2,3] RETURN SUM(x)+COUNT(*) AS answer GROUP BY ()", "9")]
    [DataRow("FOR x IN [] RETURN COALESCE(SUM(x),0)+COUNT(*) AS answer GROUP BY ()", "0")]
    [DataRow("FOR x IN [1,2,3] RETURN SUM(x)+1 AS subtotal GROUP BY () NEXT RETURN subtotal*2 AS answer", "14")]
    [DataRow("FOR x IN [1,2,3] RETURN MOD(x,2) AS parity,SUM(x)+1 AS subtotal GROUP BY parity NEXT RETURN SUM(subtotal) AS answer", "8")]
    [DataRow("FOR x IN [1,2,3] RETURN SUM(x)+1 AS subtotal GROUP BY subtotal NEXT RETURN subtotal AS answer", "ERROR")]
    [DataRow("RETURN ALL 1 AS answer", "1")]
    [DataRow("FOR x IN [1,3] RETURN STDDEV_POP(x) AS answer", "1")]
    [DataRow("FOR x IN [1] RETURN STDDEV_SAMP(x) AS answer", "null")]
    [DataRow("FOR x IN [1.1] RETURN STDDEV_SAMP(x) AS answer", "null")]
    [DataRow("FOR x IN [1e0] RETURN STDDEV_SAMP(x) AS answer", "null")]
    [DataRow("FOR x IN [1] RETURN STDDEV_POP(x) AS answer", "0")]
    [DataRow("FOR x IN [] RETURN STDDEV_SAMP(x) AS answer", "null")]
    [DataRow("FOR x IN [] RETURN STDDEV_POP(x) AS answer", "null")]
    [DataRow("FOR x IN [NULL,1] RETURN STDDEV_SAMP(x) AS answer", "null")]
    [DataRow("FOR x IN [1,3] RETURN ABS(STDDEV_SAMP(x)-SQRT(2))<0.000000000001 AS answer", "true")]
    [DataRow("RETURN CAST(42 AS INTEGER) AS answer", "42")]
    [DataRow("RETURN EXISTS ((n:Absent)) AS answer", "false")]
    [DataRow("RETURN EXISTS (MATCH (n:Absent)) AS answer", "false")]
    [DataRow("RETURN VALUE { RETURN 42 AS result } AS answer", "42")]
    [DataRow("RETURN VALUE { MATCH (n:Absent) RETURN n.name AS result } AS answer", "null")]
    [DataRow("RETURN VALUE { RETURN VALUE { RETURN 3 AS innerResult } AS outerResult } AS answer", "3")]
    [DataRow("RETURN COALESCE(VALUE { MATCH (n:Absent) RETURN n.name AS result },'empty') AS answer", "\"empty\"")]
    [DataRow("FOR x IN [1,2] RETURN SUM(VALUE { RETURN x*2 AS doubled }) AS answer", "6")]
    [DataRow("FOR x IN [1,1,2] RETURN SUM(DISTINCT VALUE { RETURN x*2 AS doubled }) AS answer", "6")]
    [DataRow("FOR x IN [1,1,2] RETURN SUM(ALL VALUE { RETURN x*2 AS doubled }) AS answer", "8")]
    [DataRow("FOR x IN [1,2] RETURN SUM(VALUE { RETURN x AS result })+COUNT(*) AS answer", "5")]
    [DataRow("FOR x IN [1,2] RETURN COUNT(VALUE { MATCH (n:Absent) RETURN n.name AS result }) AS answer", "0")]
    [DataRow("FOR x IN VALUE { RETURN [1,2,3] AS items } RETURN SUM(x) AS answer", "6")]
    [DataRow("RETURN CASE WHEN TRUE THEN 7 ELSE VALUE { FOR x IN [1,2] RETURN x } END AS answer", "7")]
    [DataRow("RETURN VALUE { FOR x IN [1,2] RETURN x } AS answer", "ERROR")]
    [DataRow("RETURN VALUE { RETURN 1 AS firstColumn,2 AS secondColumn } AS answer", "ERROR")]
    [DataRow("RETURN VALUE { RETURN NULL AS result } AS answer", "null")]
    [DataRow("RETURN VALUE { RETURN 1 AS result UNION ALL RETURN 1 AS result } AS answer", "ERROR")]
    [DataRow("RETURN VALUE { RETURN 1 AS result UNION RETURN 1 AS result } AS answer", "1")]
    [DataRow("RETURN VALUE { MATCH (n:Absent) RETURN COUNT(*) AS total } AS answer", "0")]
    [DataRow("RETURN VALUE { FOR x IN [1,2] RETURN x ORDER BY x DESC LIMIT 1 } AS answer", "2")]
    [DataRow("RETURN VALUE { RETURN 1 AS x NEXT RETURN x+1 AS y } AS answer", "2")]
    [DataRow("RETURN VALUE { RETURN [1,NULL] AS items } AS answer", "[1,null]")]
    [DataRow("RETURN VALUE { RETURN RECORD {amount:1} AS item }.amount AS answer", "1")]
    [DataRow("RETURN VALUE { RETURN 42 AS \"42\" } AS answer", "42")]
    [DataRow("FOR x IN [1,2] CALL { RETURN x*2 AS doubled } RETURN SUM(doubled) AS answer", "6")]
    [DataRow("FOR x IN [1] CALL () { RETURN x AS y } RETURN y AS answer", "ERROR")]
    [DataRow("FOR x IN [1] LET y=2 CALL (x) { RETURN y AS result } RETURN result AS answer", "ERROR")]
    [DataRow("FOR x IN [1] CALL { RETURN x AS y UNION ALL RETURN x+1 AS y } RETURN SUM(y) AS answer", "3")]
    [DataRow("FOR x IN [2] CALL { CALL { RETURN x+1 AS innerResult } RETURN innerResult+1 AS y } RETURN y AS answer", "4")]
    [DataRow("FOR x IN [2] CALL { RETURN 1 AS y NEXT RETURN x+y AS z } RETURN z AS answer", "3")]
    [DataRow("FOR x IN [2] OPTIONAL CALL { FILTER x=1 RETURN x AS y } RETURN COALESCE(y,99) AS answer", "99")]
    [DataRow("RETURN TRIM([1,2,3],1) AS answer", "[1,2]")]
    [DataRow("RETURN TRIM([1,NULL,3],1) AS answer", "[1,null]")]
    [DataRow("RETURN TRIM([[1,2],[3,4]],1) AS answer", "[[1,2]]")]
    [DataRow("RETURN TRIM([1,2],0) AS answer", "[1,2]")]
    [DataRow("RETURN TRIM([1,2],2) AS answer", "[]")]
    [DataRow("RETURN TRIM([],0) AS answer", "[]")]
    [DataRow("RETURN TRIM(NULL,1) AS answer", "null")]
    [DataRow("RETURN TRIM(NULL,-1) AS answer", "null")]
    [DataRow("RETURN TRIM(NULL,9223372036854775807) AS answer", "null")]
    [DataRow("RETURN TRIM([1,2],NULL) AS answer", "null")]
    [DataRow("RETURN TRIM([1,2],-1) AS answer", "ERROR")]
    [DataRow("RETURN TRIM([1,2],3) AS answer", "ERROR")]
    [DataRow("RETURN TRIM([1,2],1.5) AS answer", "ERROR")]
    [DataRow("RETURN TRIM('abc',1) AS answer", "ERROR")]
    [DataRow("RETURN TRIM(VALUE { RETURN [1,2,3] AS items },1) AS answer", "[1,2]")]
    [DataRow("FOR item IN TRIM([1,2,3],1) RETURN SUM(item) AS answer", "3")]
    [DataRow("RETURN VALUE { RETURN RECORD {nested:RECORD {amount:2}} AS item }.nested.amount AS answer", "2")]
    [DataRow("RETURN VALUE { MATCH (n:Absent) RETURN RECORD {amount:1} AS item }.amount AS answer", "null")]
    [DataRow("RETURN CAST(TRUE AS BOOLEAN) AS answer", "true")]
    [DataRow("RETURN CAST('hello' AS STRING) AS answer", "\"hello\"")]
    [DataRow("RETURN CAST(NULL AS INTEGER) AS answer", "null")]
    [DataRow("RETURN CAST(42 AS DECIMAL) IS TYPED DECIMAL AS answer", "true")]
    [DataRow("RETURN CAST(9223372036854775807 AS DECIMAL)=9223372036854775807M AS answer", "true")]
    [DataRow("RETURN CAST(42 AS FLOAT) IS TYPED FLOAT AS answer", "true")]
    [DataRow("RETURN CAST(0.125M AS FLOAT)=0.125e0 AS answer", "true")]
    [DataRow("RETURN CAST([1,NULL] AS LIST<INTEGER>) AS answer", "[1,null]")]
    [DataRow("RETURN CAST(RECORD {amount:1} AS RECORD).amount AS answer", "1")]
    [DataRow("RETURN CAST(NULL AS INTEGER NOT NULL) AS answer", "ERROR")]
    [DataRow("RETURN CAST([1,NULL] AS LIST<INTEGER NOT NULL>) AS answer", "ERROR")]
    [DataRow("RETURN [1,2] IS TYPED INTEGER LIST AS answer", "true")]
    [DataRow("RETURN [1,NULL] IS TYPED INTEGER ARRAY NOT NULL AS answer", "true")]
    [DataRow("RETURN [1,NULL] IS TYPED INTEGER NOT NULL LIST AS answer", "false")]
    [DataRow("RETURN NULL IS TYPED INTEGER LIST AS answer", "true")]
    [DataRow("RETURN NULL IS TYPED INTEGER LIST NOT NULL AS answer", "false")]
    [DataRow("RETURN [[1],[2,NULL]] IS TYPED INTEGER LIST ARRAY AS answer", "true")]
    [DataRow("RETURN [[1],NULL] IS TYPED INTEGER LIST NOT NULL ARRAY AS answer", "false")]
    [DataRow("RETURN [[1],NULL] IS TYPED INTEGER NOT NULL LIST ARRAY AS answer", "true")]
    [DataRow("RETURN [1,2] IS TYPED ANY VALUE<STRING | INTEGER ARRAY> AS answer", "true")]
    [DataRow("RETURN [1,'x'] IS TYPED ANY VALUE<INTEGER | STRING> LIST AS answer", "true")]
    [DataRow("RETURN [[1]] IS TYPED ANY VALUE<LIST<INTEGER>> LIST AS answer", "true")]
    [DataRow("RETURN CAST([1,2] AS INTEGER ARRAY) AS answer", "[1,2]")]
    [DataRow("RETURN CAST([1,NULL] AS INTEGER NOT NULL LIST) AS answer", "ERROR")]
    [DataRow("RETURN CAST('not a number' AS INTEGER) AS answer", "ERROR")]
    [DataRow("LET clockValue=LOCAL_TIMESTAMP RETURN clockValue IS TYPED TIMESTAMP AS answer", "true")]
    [DataRow("LET clockValue=LOCAL_TIMESTAMP RETURN clockValue IS TYPED TIMESTAMP WITHOUT TIME ZONE NOT NULL AS answer", "true")]
    [DataRow("LET clockValue=CURRENT_TIMESTAMP RETURN clockValue IS TYPED TIMESTAMP WITH TIME ZONE NOT NULL AS answer", "true")]
    [DataRow("LET clockValue=CURRENT_TIMESTAMP RETURN clockValue IS TYPED TIMESTAMP AS answer", "false")]
    [DataRow("LET clockValue=LOCAL_TIMESTAMP RETURN clockValue IS TYPED TIMESTAMP WITH TIME ZONE AS answer", "false")]
    [DataRow("RETURN NULL IS TYPED TIMESTAMP AS answer", "true")]
    [DataRow("RETURN NULL IS TYPED TIMESTAMP NOT NULL AS answer", "false")]
    [DataRow("LET clockValue=CURRENT_TIMESTAMP RETURN clockValue IS TYPED ANY VALUE<INTEGER | TIMESTAMP WITH TIME ZONE> AS answer", "true")]
    [DataRow("LET clockValue=CURRENT_TIMESTAMP RETURN clockValue IS TYPED ANY VALUE<INTEGER | ZONED DATETIME> AS answer", "true")]
    [DataRow("LET clockValue=CURRENT_TIME RETURN clockValue IS TYPED ANY VALUE<INTEGER | TIME WITH TIME ZONE> AS answer", "true")]
    [DataRow("LET clockValue=LOCAL_TIME RETURN clockValue IS TYPED ANY VALUE<INTEGER | TIME WITHOUT TIME ZONE> AS answer", "true")]
    [DataRow("RETURN CAST(LOCAL_TIMESTAMP AS TIMESTAMP) IS TYPED LOCAL DATETIME AS answer", "true")]
    [DataRow("RETURN CAST(CURRENT_TIMESTAMP AS TIMESTAMP WITH TIME ZONE) IS TYPED ZONED DATETIME AS answer", "true")]
    [DataRow("RETURN CAST(CURRENT_TIMESTAMP AS TIMESTAMP) AS answer", "ERROR")]
    [DataRow("RETURN NULL IS TYPED NULL NOT NULL AS answer", "false")]
    [DataRow("RETURN 1 IS TYPED NULL NOT NULL AS answer", "false")]
    [DataRow("RETURN [] IS TYPED LIST<NULL NOT NULL> AS answer", "true")]
    [DataRow("RETURN [NULL] IS TYPED LIST<NULL NOT NULL> AS answer", "false")]
    [DataRow("RETURN [NULL,NULL] IS TYPED LIST<NULL> AS answer", "true")]
    [DataRow("RETURN [NULL,1] IS TYPED LIST<NULL> AS answer", "false")]
    [DataRow("RETURN [] IS TYPED NOTHING LIST NOT NULL AS answer", "true")]
    [DataRow("RETURN NULL IS TYPED ANY VALUE<NOTHING | INTEGER NOT NULL> AS answer", "false")]
    [DataRow("RETURN NULL IS TYPED ANY VALUE<NOTHING | NULL> AS answer", "true")]
    [DataRow("RETURN 1 IS TYPED ANY VALUE<NOTHING | INTEGER NOT NULL> AS answer", "true")]
    [DataRow("RETURN CAST(NULL AS NULL) IS NULL AS answer", "true")]
    [DataRow("RETURN CAST([] AS NOTHING LIST NOT NULL) AS answer", "[]")]
    [DataRow("RETURN CAST(NULL AS NOTHING) AS answer", "ERROR")]
    [DataRow("RETURN CAST(NULL AS NULL NOT NULL) AS answer", "ERROR")]
    public async Task Gql_standard_expressions_use_standard_semantics(string query, string expected)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query
        });
        string body = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(expected == "ERROR" ? HttpStatusCode.BadRequest : HttpStatusCode.OK, response.StatusCode, body);
        if (expected != "ERROR")
        {
            using var document = JsonDocument.Parse(body);
            Assert.AreEqual(expected, document.RootElement.GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetRawText());
        }
    }
}
