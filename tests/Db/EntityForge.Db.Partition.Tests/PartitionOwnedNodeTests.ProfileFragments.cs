using EntityForge.Db.Gql;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using dotnet_etcd;
using Etcdserverpb;
using Google.Protobuf;
using Grpc.Core;
using EntityForge.Db.Distributed.Hosting;
using EntityForge.Db.Distributed.Sharding;
using EntityForge.Db.Semantic;
using EntityForge.Db.Transport.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [TestMethod]
    public async Task Gql_standard_unbounded_multi_edge_groups_across_owners()
    {
        await WithProfileFragments(DatabaseProfileId.PropertyGraph, async (control, candidate, _, remote, _) =>
        {
            const string seed = "INSERT (a:Alternating {ordinal:0})-[:forward]->(b:Alternating {ordinal:1})<-[:backward {allowed:TRUE}]-(c:Alternating {ordinal:2})-[:forward]->(d:Alternating {ordinal:3})<-[:backward {allowed:TRUE}]-(e:Alternating {ordinal:4})";
            Guid operation = EntityOperationIds.New();
            await remote.ExecuteGqlUpdateWithResultAsync(operation, seed);
            await remote.ExecuteGqlUpdateWithResultAsync(operation, seed);
            const string pattern = "(origin:Alternating {ordinal:0})((a)-[e:forward]->(b)<-[f:backward {allowed:TRUE}]-(c))+(destination:Alternating {ordinal:4})";
            string localModePattern = pattern.Replace("((a)", "(ACYCLIC (a)", StringComparison.Ordinal);
            string update = "MATCH p=" + localModePattern + " SET destination.checked=TRUE RETURN PATH_LENGTH(p) AS answer";
            Guid modeOperation = EntityOperationIds.New();
            await remote.ExecuteGqlUpdateWithResultAsync(modeOperation, update);
            await remote.ExecuteGqlUpdateWithResultAsync(modeOperation, update);
            foreach (var (query, expected) in new[]
            {
                ("MATCH p=" + localModePattern + " RETURN PATH_LENGTH(p) AS answer", 4),
                ("MATCH p=ALL SHORTEST " + localModePattern + " RETURN PATH_LENGTH(p) AS answer", 4),
                ("MATCH p=(origin:Alternating {ordinal:0})(ACYCLIC ()-[:forward]->()<-[:backward {allowed:TRUE}]-())+(destination:Alternating {ordinal:4}) RETURN PATH_LENGTH(p) AS answer", 4),
                ("MATCH (n:Alternating WHERE n.checked IS TRUE) RETURN COUNT(*) AS answer", 1),
                ("MATCH p=" + pattern + " RETURN CASE WHEN PATH_LENGTH(p)=4 AND SIZE(a)=2 AND SIZE(b)=2 AND SIZE(c)=2 AND SIZE(e)=2 AND SIZE(f)=2 THEN 1 ELSE 0 END AS answer", 1),
                ("MATCH p=ALL SHORTEST " + pattern + " FOR middle IN b RETURN SUM(middle.ordinal) AS answer", 4),
                ("MATCH p=(origin:Alternating {ordinal:0})((a)-[e:forward]->(b)<-[f:forward]-(c))+(destination:Alternating {ordinal:4}) RETURN COUNT(*) AS answer", 0)
            })
            {
                var request = new PartitionQueryRequest(candidate.Generation, candidate.Profile, "gql", query);
                var result = await candidate.QueryAsync(request, default);
                Assert.AreEqual(Canonical((await control.QueryAsync(request, default)).Value), Canonical(result.Value));
                Assert.AreEqual(expected, result.Value.GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetInt32(), query);
            }
        });
    }

    [TestMethod]
    public async Task Gql_standard_composition_path_modes_and_update_replay_across_owners()
    {
        await WithProfileFragments(DatabaseProfileId.PropertyGraph, async (control, candidate, _, remote, _) =>
        {
            const string statement = "INSERT (a:StandardMode & 实体 & `a:b|label` {id:'shared',name:'A',值:7,amount:0.1234567890123456789012345678,amounts:[0.1,0.2],monthZero:DURATION('P0M'),dayZero:DURATION('PT0S')})-[:standardLink {id:42,`a:b.c`:7}]->(:StandardMode {id:'shared',name:'B'}) RETURN a NEXT RETURN * UNION RETURN * NEXT SET a.checked = TRUE,a.`a:b.c`=7,a:`new:label` REMOVE a:`a:b|label` FINISH";
            Guid operation = EntityOperationIds.New();
            await remote.ExecuteGqlUpdateWithResultAsync(operation, statement);
            await remote.ExecuteGqlUpdateWithResultAsync(operation, statement);
            Guid selectionOperation = EntityOperationIds.New();
            const string selectionUpdate = "MATCH (a:StandardMode {name:'A'}) MATCH ANY (a)-[e:standardLink WHERE e.id=42]->(b) SET e.`selected flag`=TRUE FINISH";
            await remote.ExecuteGqlUpdateWithResultAsync(selectionOperation, selectionUpdate);
            await remote.ExecuteGqlUpdateWithResultAsync(selectionOperation, selectionUpdate);
            await remote.ExecuteGqlUpdateWithResultAsync(EntityOperationIds.New(), "MATCH (n:StandardMode) SET n.nanValue=0e0/0e0 FINISH");
            foreach (var (query, expected) in new[]
            {
                ("MATCH (n:StandardMode) RETURN COUNT(*) AS total NEXT YIELD total AS answer RETURN answer", 2),
                ("MATCH (a:StandardMode {name:'A'}) CALL (a) { MATCH (a)((starts)-[links:standardLink]->(ends)){0,1}(z) RETURN COUNT(*) AS answer } RETURN answer", 2),
                ("MATCH (a:StandardMode {name:'A'}) CALL (a) { MATCH (a)((starts)-[links:standardLink]->(ends))+(z) RETURN COUNT(*) AS answer } RETURN answer", 1),
                ("MATCH (a:StandardMode {name:'A'}) CALL { MATCH (a)((starts)-[links:standardLink]->(ends))+(z) RETURN COUNT(*) AS answer } RETURN answer", 1),
                ("MATCH (a:StandardMode {name:'A'}) LET anchor=a CALL { MATCH (anchor)((starts)-[links:standardLink]->(ends))+(z) RETURN COUNT(*) AS answer } RETURN answer", 1),
                ("MATCH (a:StandardMode {name:'A'}) FOR anchor IN [a] MATCH (anchor)((starts)-[links:standardLink]->(ends)){1}(z) RETURN COUNT(*) AS answer", 1),
                ("MATCH (a:StandardMode {name:'A'}) RETURN a NEXT YIELD a AS anchor MATCH (anchor)((starts)-[links:standardLink]->(ends))+(z) RETURN COUNT(*) AS answer", 1),
                ("MATCH (a:StandardMode) RETURN a UNION ALL MATCH (a:StandardMode {name:'A'}) RETURN a NEXT MATCH (a)((starts)-[links:standardLink]->(ends))+(z) RETURN COUNT(*) AS answer", 2),
                ("MATCH (a:StandardMode {name:'A'}) RETURN VALUE { MATCH (a)((starts)-[links:standardLink]->(ends))+(z) RETURN COUNT(*) AS total } AS answer", 1),
                ("MATCH (a:StandardMode {name:'A'}) RETURN CASE WHEN EXISTS { MATCH (a)((starts)-[links:standardLink]->(ends)){1}(z) } THEN 1 ELSE 0 END AS answer", 1),
                ("MATCH (n:StandardMode) RETURN COUNT(DISTINCT n.nanValue) AS answer", 1),
                ("MATCH (n:StandardMode) RETURN n.nanValue AS groupedValue,COUNT(*) AS qty GROUP BY groupedValue NEXT RETURN COUNT(*) AS answer", 1),
                ("FOR x IN [0e0/0e0,0e0/0e0] RETURN COUNT(DISTINCT x) AS answer", 1),
                ("MATCH (n:StandardMode) RETURN COUNT(DISTINCT n.name) AS answer ORDER BY count ( distinct n.name )", 2),
                ("MATCH (n:StandardMode) WHERE n.checked IS TRUE RETURN COUNT(*) AS answer", 1),
                ("MATCH (变量:实体) RETURN 变量.值 AS answer", 7),
                ("MATCH (变量:实体) RETURN 变量.值 AS \"display name\"", 7),
                ("RETURN 7 AS `display name` UNION RETURN 7 AS `display name` NEXT YIELD `display name` AS answer RETURN answer", 7),
                ("MATCH (n:`new:label`) WHERE n IS LABELED !`a:b|label` RETURN COUNT(*) AS answer", 1),
                ("MATCH (:`new:label` {`a:b.c`:7})-[:standardLink]->() RETURN COUNT(*) AS answer", 1),
                ("MATCH (n:StandardMode) WHERE n IS LABELED `%` RETURN COUNT(*) AS answer", 0),
                ("FOR 𐐀CASE IN [1,2] RETURN SUM(𐐀CASE) AS answer", 3),
                ("CALL { LET GRAPH𐐀=3 RETURN GRAPH𐐀 UNION LET GRAPH𐐀=3 RETURN GRAPH𐐀 } RETURN GRAPH𐐀 AS answer", 3),
                ("LET café=1 CALL (café) { RETURN café+2 AS résultat } RETURN résultat AS answer", 3),
                ("MATCH p=(a:StandardMode {name:'A'}) ((x)-[e:standardLink]->(y) WHERE EXISTS { MATCH (x)-[sameEdge:standardLink]->(y) WHERE sameEdge.id=42 })+(b) RETURN SUM(PATH_LENGTH(p)) AS answer", 1),
                ("MATCH p=(a:StandardMode {name:'A'}) ((x)-[e:standardLink]->(y) WHERE EXISTS { MATCH (x)-[sameEdge:standardLink]->(y) WHERE sameEdge.id=0 })+(b) RETURN COUNT(*) AS answer", 0),
                ("RETURN CASE WHEN [1,2] IS TYPED SIGNED INTEGER LIST NOT NULL THEN CAST(1 AS SIGNED INTEGER) ELSE 0 END AS answer", 1),
                ("MATCH (n:StandardMode {name:'A'}) RETURN CASE WHEN [n.name] IS TYPED STRING LIST[1] THEN CARDINALITY(CAST([n.name] AS STRING LIST[1])) ELSE 0 END AS answer", 1),
                ("MATCH (n:StandardMode {name:'A'}) RETURN CASE WHEN [[n.name]] IS TYPED LIST<LIST<STRING>[1]>[1] THEN 1 ELSE 0 END AS answer", 1),
                ("MATCH (n:StandardMode {name:'A'}) RETURN CASE WHEN [n.name,n.name] IS TYPED STRING LIST[1] THEN 0 ELSE 1 END AS answer", 1),
                ("OPTIONAL { MATCH (a:StandardMode)-[:standardLink]->(b) MATCH (b)-[:standardLink]->(c) } RETURN COUNT(a) AS answer", 0),
                ("MATCH (a:StandardMode) OPTIONAL { MATCH (a)-[:standardLink]->(b) MATCH (b)-[:standardLink]->(c) } RETURN COUNT(a) AS answer", 2),
                ("OPTIONAL ( MATCH (a:StandardMode)-[:standardLink]->(b) MATCH (b) ) RETURN COUNT(b) AS answer", 1),
                ("MATCH ANY (n:StandardMode)-[e:standardLink WHERE EXISTS { MATCH (x)-[e]->(y) WHERE e.id=42 }]->(m) RETURN COUNT(*) AS answer", 1),
                ("MATCH (n:StandardMode)-[e:standardLink]->() RETURN CASE WHEN PROPERTY_EXISTS(e,\"id\") AND e.\"id\"=42 THEN 1 ELSE 0 END AS answer", 1),
                ("MATCH (n:StandardMode {`a:b.c`:7})-[e:standardLink]->() RETURN CASE WHEN PROPERTY_EXISTS(e,`a:b.c`) AND e.`a:b.c`=n.\"a:b.c\" THEN 1 ELSE 0 END AS answer", 1),
                ("MATCH (a:StandardMode {name:'A'}) MATCH p=(a)((x)-[e:standardLink]->(y))+(b:StandardMode {name:'B'}) RETURN SUM(PATH_LENGTH(p)) AS answer", 1),
                ("MATCH (a:StandardMode {name:'A'}),(b:StandardMode {name:'B'}) MATCH p=ALL SHORTEST (a)((x)-[e:standardLink WHERE e.id=42]->(y)){1,2}(b) RETURN SUM(PATH_LENGTH(p)) AS answer", 1),
                ("MATCH (a:StandardMode {name:'A'}) MATCH ANY (a)-[e:standardLink WHERE e.id=42]->(b) RETURN CASE WHEN e.`selected flag` IS TRUE THEN 1 ELSE 0 END AS answer", 1),
                ("MATCH (a:StandardMode {name:'A'}) MATCH p=(a)-[e:standardLink WHERE e.id=42]->+(b) RETURN SUM(PATH_LENGTH(p)) AS answer", 1),
                ("MATCH (a:StandardMode {name:'A'}) LET wanted=42 MATCH ANY (a)-[e:standardLink {id:wanted}]->(b) RETURN COUNT(*) AS answer", 1),
                ("MATCH (a:StandardMode {name:'A'})~[e:standardLink]~>(b) RETURN COUNT(*) AS answer", 1),
                ("MATCH (a:StandardMode {name:'A'})<~[e:standardLink]~(b) RETURN COUNT(*) AS answer", 0),
                ("MATCH p=(a:StandardMode {name:'B'})((x)<~[e:standardLink]~(y))+(b:StandardMode {name:'A'}) RETURN SUM(PATH_LENGTH(p)) AS answer", 1),
                ("MATCH REPEATABLE ELEMENTS p=SIMPLE (a:StandardMode {name:'A'})-[e:standardLink]-+(b:StandardMode {name:'A'}) RETURN SUM(PATH_LENGTH(p)) AS answer", 2),
                ("MATCH REPEATABLE ELEMENTS p=SIMPLE (TRAIL (a:StandardMode {name:'A'})-[e:standardLink]-*(b)) RETURN COUNT(*) AS answer", 2),
                ("MATCH REPEATABLE ELEMENTS TRAIL (SIMPLE (:StandardMode {name:'A'})-[:standardLink]-*()) RETURN COUNT(*) AS answer", 2),
                ("OPTIONAL MATCH SIMPLE (TRAIL (:AbsentMixedMode)-[:standardLink]-*()) RETURN COUNT(*) AS answer", 1),
                ("MATCH REPEATABLE ELEMENTS p=ALL SHORTEST SIMPLE (TRAIL (a:StandardMode {name:'A'})-[:standardLink]-*(b:StandardMode {name:'A'}) WHERE PATH_LENGTH(p)>0) RETURN COUNT(*) AS answer", 0),
                ("MATCH REPEATABLE ELEMENTS p=ALL SHORTEST SIMPLE (a:StandardMode {name:'A'})((x)-[e:standardLink]->(y)<-[f:standardLink]-(z))+(b:StandardMode {name:'A'}) RETURN SUM(SIZE(e)+SIZE(f)) AS answer", 2),
                ("MATCH REPEATABLE ELEMENTS p=SIMPLE (a:StandardMode {name:'A'})-[e:standardLink]-{3,}(b) RETURN COUNT(*) AS answer", 0),
                ("MATCH REPEATABLE ELEMENTS p=TRAIL (a:StandardMode {name:'A'})-[e:standardLink]->+(b:StandardMode {name:'B'}) RETURN SUM(PATH_LENGTH(p)) AS answer", 1),
                ("MATCH REPEATABLE ELEMENTS p=ACYCLIC (a:StandardMode {name:'A'})((x)-[e:standardLink WHERE e.id=42]->(y))+(b:StandardMode {name:'B'}) RETURN SUM(SIZE(e)) AS answer", 1),
                ("MATCH p=(a:StandardMode {name:'A'})-[e IS standardLink|missingLink WHERE e.id=42]->+(b:StandardMode {name:'B'}) RETURN SUM(SIZE(e)) AS answer", 1),
                ("MATCH p=ALL SHORTEST (a:StandardMode {name:'A'})-[e IS standardLink&!missingLink WHERE e.id=42]->{1,2}(b:StandardMode {name:'B'}) RETURN SUM(PATH_LENGTH(p)) AS answer", 1),
                ("MATCH p=(a:StandardMode {name:'A'})-[e WHERE FALSE]->*(b:StandardMode {name:'A'}) RETURN CASE WHEN SIZE(e)=0 AND a=b THEN 1 ELSE 0 END AS answer", 1),
                ("MATCH (n:StandardMode)-[e:standardLink]->() RETURN CASE WHEN PROPERTY_EXISTS(n,checked) AND PROPERTY_EXISTS(e,id) AND NOT PROPERTY_EXISTS(e,missing) THEN 1 ELSE 0 END AS answer", 1),
                ("MATCH (n:StandardMode {name:'A'}) RETURN CASE WHEN n.monthZero IS TYPED DURATION(YEAR TO MONTH) AND n.dayZero IS TYPED DURATION(DAY TO SECOND) THEN 1 ELSE 0 END AS answer", 1),
                ("MATCH (n:StandardMode {name:'A'}) FOR span IN [n.monthZero,n.dayZero] RETURN COUNT(DISTINCT span) AS answer", 2),
                ("MATCH (n:StandardMode {name:'A'}) RETURN CASE WHEN CAST(n.monthZero AS DURATION(YEAR TO MONTH)) IS TYPED DURATION(YEAR TO MONTH) THEN 1 ELSE 0 END AS answer", 1),
                ("MATCH (n:StandardMode {name:'A'}) RETURN CASE WHEN n.amounts IS TYPED DECIMAL ARRAY NOT NULL THEN 1 ELSE 0 END AS answer", 1),
                ("MATCH (n:StandardMode {name:'A'}) RETURN CASE WHEN CAST(n.amounts AS DECIMAL LIST) IS TYPED LIST<DECIMAL> THEN 1 ELSE 0 END AS answer", 1),
                ("MATCH p=(anchorStart:StandardMode {name:'A'})((x)-[edgeGroup:standardLink]->(y)){0,1}(anchorEnd:StandardMode) RETURN COUNT(*) AS answer", 2),
                ("MATCH p=ALL SHORTEST (anchorStart:StandardMode {name:'A'})((x)-[edgeGroup:standardLink WHERE edgeGroup.id=42]->(y)){1,2}(anchorEnd:StandardMode {name:'B'}) RETURN SUM(PATH_LENGTH(p)) AS answer", 1),
                ("MATCH p=(anchorStart:StandardMode {name:'A'})((x)-[edgeGroup:standardLink]->(y)){0,1}(anchorEnd:StandardMode {name:'A'}) RETURN CASE WHEN SIZE(x)=0 AND SIZE(y)=0 AND SIZE(edgeGroup)=0 AND anchorStart=anchorEnd THEN 1 ELSE 0 END AS answer", 1),
                ("MATCH p=(anchorStart:StandardMode {name:'A'})((x)-[edgeGroup:standardLink WHERE edgeGroup.id=42]->(y)){1,}(anchorEnd:StandardMode {name:'B'}) RETURN SUM(SIZE(edgeGroup)) AS answer", 1),
                ("MATCH p=ALL SHORTEST (anchorStart:StandardMode {name:'A'})((x)-[edgeGroup:standardLink WHERE edgeGroup.id=42]->(y))+(anchorEnd:StandardMode {name:'B'}) RETURN SUM(PATH_LENGTH(p)) AS answer", 1),
                ("MATCH p=(anchorStart:StandardMode {name:'A'})((x)-[edgeGroup:standardLink]->(y))*(anchorEnd:StandardMode {name:'A'}) RETURN CASE WHEN SIZE(x)=0 AND SIZE(y)=0 AND SIZE(edgeGroup)=0 AND anchorStart=anchorEnd THEN 1 ELSE 0 END AS answer", 1),
                ("MATCH (n:StandardMode) RETURN SUM(VALUE { RETURN 1 AS amount }) AS answer", 2),
                ("MATCH (a:StandardMode)-[e:standardLink]-(b) WHERE a IS SOURCE OF e AND b IS DESTINATION OF e AND e IS DIRECTED RETURN COUNT(*) AS answer", 1),
                ("MATCH (a:StandardMode)-[e:standardLink]->(b) RETURN CASE a WHEN IS SOURCE OF e THEN CASE e WHEN IS DIRECTED THEN 1 ELSE 0 END ELSE 0 END AS answer", 1),
                ("MATCH (n:StandardMode {name:'A'}) RETURN CASE n WHEN IS LABELED StandardMode&!Missing THEN 1 ELSE 0 END AS answer", 1),
                ("MATCH ((a:StandardMode)-[e:standardLink]->(b) WHERE a.name='A' AND b.name='B') RETURN COUNT(*) AS answer", 1),
                ("MATCH ANY (a:StandardMode)-[e:standardLink WHERE e.id=42]->(b) RETURN COUNT(*) AS answer", 1),
                ("MATCH ALL SHORTEST ((a:StandardMode)-[e:standardLink]->(b) WHERE a.name='A') RETURN COUNT(*) AS answer", 1),
                ("MATCH ANY (a IS StandardMode&!Missing)-[e:standardLink]->(b) RETURN COUNT(*) AS answer", 1),
                ("USE CURRENT_GRAPH MATCH (n:StandardMode) RETURN COUNT(*) AS answer", 2),
                ("CALL { USE CURRENT_PROPERTY_GRAPH MATCH (n:StandardMode) RETURN COUNT(*) AS total } RETURN total AS answer", 2),
                ("USE CURRENT_GRAPH RETURN 1 AS answer UNION ALL USE CURRENT_PROPERTY_GRAPH RETURN 2 AS answer NEXT RETURN SUM(answer) AS answer", 3),
                ("RETURN VALUE { RETURN RECORD {amount:7} AS item }.amount AS answer", 7),
                ("RETURN 1 AS answer UNION RETURN 1 AS answer", 1),
                ("FOR x IN [1,2] CALL { RETURN x*2 AS doubled } RETURN SUM(doubled) AS answer", 6),
                ("MATCH (n:StandardMode {name:'A'}) CALL { MATCH (n)-[e:standardLink]->(b) RETURN COUNT(*) AS total } RETURN total AS answer", 1),
                ("FOR x IN TRIM([1,2,3],1) RETURN SUM(x) AS answer", 3),
                ("CALL { CALL () { MATCH (n:StandardMode {name:'A'}) RETURN n AS item UNION ALL MATCH (n:StandardMode {name:'A'}) RETURN n AS item } RETURN item UNION MATCH (n:StandardMode {name:'B'}) RETURN n AS item } RETURN COUNT(*) AS answer", 2),
                ("CALL { CALL () { MATCH (n:StandardMode {name:'A'}) RETURN n AS item UNION MATCH (n:StandardMode {name:'A'}) RETURN n AS item } RETURN item UNION ALL MATCH (n:StandardMode {name:'A'}) RETURN n AS item } RETURN COUNT(*) AS answer", 2),
                ("MATCH (n:StandardMode {name:'A'}) RETURN CASE WHEN ABS(DURATION('-P1000000000000000DT0.000000001S'))=DURATION('P1000000000000000DT0.000000001S') THEN 1 ELSE 0 END AS answer", 1),
                ("RETURN CHAR_LENGTH(BTRIM('\\t x \\t')) AS answer", 5),
                ("MATCH (n:StandardMode {name:'A'}) RETURN VALUE { RETURN CASE WHEN n.checked IS TRUE THEN 1 ELSE 0 END AS result } AS answer", 1),
                ("MATCH (n:StandardMode {id:'shared'}) RETURN COUNT(DISTINCT ELEMENT_ID(n)) AS answer", 2),
                ("MATCH (:StandardMode)-[e:standardLink {id:42}]->(:StandardMode) RETURN COUNT(*) AS answer", 1),
                ("RETURN 1 AS answer -- NEXT RETURN 99 AS answer", 1),
                ("MATCH (:StandardMode {name:'A'})->(b) RETURN COUNT(*) AS answer", 1),
                ("FOR x IN [1,2,3] RETURN SUM(x)+COUNT(*) AS answer GROUP BY ()", 9),
                ("FOR x IN [10,20,30] WITH ORDINALITY i RETURN SUM(x*i) AS answer", 140),
                ("FOR x IN [10,20,30] WITH OFFSET i RETURN SUM(x*i) AS answer", 80),
                ("RETURN 0xFF+0o10+0b10 AS answer", 265),
                ("RETURN CASE WHEN 0.1M+0.2M=0.3M THEN 1 ELSE 0 END AS answer", 1),
                ("RETURN CASE WHEN CEILING(9007199254740993.1)=9007199254740994 AND FLOOR(-9007199254740993.1)=-9007199254740994 AND CEIL(9223372036854775807)=9223372036854775807 THEN 1 ELSE 0 END AS answer", 1),
                ("MATCH p=(origin:StandardMode {name:'A'}) ((x)-[edges:standardLink]->(y) WHERE VALUE { RETURN x.name AS \"x.name\" }='A')+(destination) RETURN SUM(PATH_LENGTH(p)) AS answer", 1),
                ("MATCH p=ALL SHORTEST (a:StandardMode {name:'A'})-[incoming:standardLink]->((x)-[links:missing]->(y)){0,1} RETURN PATH_LENGTH(p)+SIZE(links) AS answer", 1),
                ("MATCH p=ALL SHORTEST ((x)-[links:missing]->(y)){0,1}-[outgoing:standardLink]->(b:StandardMode {name:'B'}) RETURN PATH_LENGTH(p)+SIZE(links) AS answer", 1),
                ("MATCH p=ALL SHORTEST (a:StandardMode {name:'A'})-[incoming:standardLink]->((x)-[links:missing]->(y))* RETURN PATH_LENGTH(p)+SIZE(links) AS answer", 1),
                ("MATCH p=ALL SHORTEST ((x)-[links:missing]->(y))*-[outgoing:standardLink]->(b:StandardMode {name:'B'}) RETURN PATH_LENGTH(p)+SIZE(links) AS answer", 1),
                ("MATCH p=ALL SHORTEST (a:StandardMode {name:'A'})-[incoming:standardLink]->(x)-[links IS missing&!standardLink WHERE links IS LABELED missing]->*(y) RETURN PATH_LENGTH(p)+SIZE(links) AS answer", 1),
                ("MATCH p=ALL SHORTEST (a:StandardMode {name:'A'})(()-[forwardEdges:standardLink]->()){1}(b)(()-[emptyEdges:missing]->()){0,1}(c) RETURN PATH_LENGTH(p)+SIZE(emptyEdges) AS answer", 1),
                ("MATCH (:StandardMode {name:'A'})(()-[:standardLink]->()){1}()(()-[:missing]->()){0,1}() RETURN COUNT(*) AS answer", 1),
                ("MATCH p=(a:StandardMode {name:'A'})(()-[forwardEdges:standardLink]->()){1}(b)(()<-[backEdges:standardLink]-()){1}(c) RETURN COUNT(*) AS answer", 0),
                ("MATCH REPEATABLE ELEMENTS p=WALK (a:StandardMode {name:'A'})(()-[forwardEdges:standardLink]->()){1}(b)(()<-[backEdges:standardLink]-()){1}(c) RETURN PATH_LENGTH(p) AS answer", 2),
                ("MATCH REPEATABLE ELEMENTS p=ALL SHORTEST WALK (a:StandardMode {name:'A'})(()-[forwardEdges:standardLink]->()){1}(b)<-[fixedBack:standardLink]-(c)(()-[againEdges:standardLink]->()){1}(d) RETURN PATH_LENGTH(p) AS answer", 3),
                ("MATCH p=(a:StandardMode {name:'A'})(()-[forwardEdges:standardLink]->()){1}(b)<-[fixedBack:standardLink]-(c)(()-[againEdges:standardLink]->()){1}(d) RETURN COUNT(*) AS answer", 0),
                ("MATCH p=(a:StandardMode {name:'A'})-[incoming:standardLink]->((x)-[links:missing]->(y)){0,1}<-[outgoing:standardLink]-(b) RETURN COUNT(*) AS answer", 0),
                ("MATCH REPEATABLE ELEMENTS p=WALK (a:StandardMode {name:'A'})-[incoming:standardLink]->((x)-[links:missing]->(y)){0,1}<-[outgoing:standardLink]-(b) RETURN PATH_LENGTH(p)+SIZE(links) AS answer", 2),
                ("MATCH (n IS StandardMode) RETURN COUNT(*) AS answer", 2),
                ("MATCH (n IS StandardMode|Missing) RETURN COUNT(*) AS answer", 2),
                ("MATCH ((n:StandardMode WHERE n.name='A')) RETURN COUNT(*) AS answer", 1),
                ("MATCH (:StandardMode)-[e:standardLink WHERE e.id=0x2A]->(b) RETURN COUNT(*) AS answer", 1),
                ("MATCH (n IS %&!Missing)-[e IS standardLink|absent]->(b) RETURN COUNT(*) AS answer", 1),
                ("MATCH ()-[e IS standardLink&standardLink]->() RETURN COUNT(*) AS answer", 1),
                ("MATCH (n:StandardMode {name:'A'}) OPTIONAL MATCH (n)-[e IS absent|missing]->(b) RETURN COUNT(*) AS answer", 1),
                ("RETURN CASE WHEN NULL IS TYPED ANY VALUE<INTEGER NOT NULL | STRING> THEN 1 ELSE 0 END AS answer", 1),
                ("RETURN CASE WHEN RECORD {items:LIST[1,2]} IS TYPED RECORD THEN 1 ELSE 0 END AS answer", 1),
                ("RETURN CASE WHEN CAST(42 AS DECIMAL) IS TYPED DECIMAL AND CAST(0.125M AS FLOAT)=0.125e0 THEN 1 ELSE 0 END AS answer", 1),
                ("MATCH p=(:StandardMode {name:'A'})-[edges:standardLink]->{0,1}(b) RETURN SUM(CASE WHEN SIZE(edges)=PATH_LENGTH(p) AND edges IS TYPED LIST<EDGE> THEN 1 ELSE 0 END) AS answer", 2),
                ("MATCH (:StandardMode {name:'A'})-[edges:standardLink]->{1}(b) FOR e IN edges RETURN COUNT(*) AS answer", 1),
                ("MATCH ()-[:standardLink WHERE TRUE]->{1}() RETURN COUNT(*) AS answer", 1),
                ("MATCH ANY ()-[:standardLink WHERE TRUE]->() RETURN COUNT(*) AS answer", 1),
                ("MATCH REPEATABLE ELEMENTS p=ALL SHORTEST (:StandardMode {name:'A'})(()-[:standardLink]->()){1}(),q=ANY SHORTEST (:StandardMode {name:'A'})(()-[:standardLink]->()){1}() RETURN PATH_LENGTH(p)+PATH_LENGTH(q) AS answer", 2),
                ("MATCH REPEATABLE ELEMENTS (b:StandardMode {name:'B'}),p=ALL SHORTEST (:StandardMode {name:'A'})(()-[:standardLink]->()){1}(b) RETURN PATH_LENGTH(p) AS answer", 1),
                ("LET retained=7 OPTIONAL MATCH ANY ()-[:standardLink WHERE FALSE]->() RETURN retained AS answer", 7),
                ("LET retained=7 OPTIONAL MATCH ()-[:standardLink WHERE FALSE]->{1}() RETURN retained AS answer", 7),
                ("MATCH p=(()-[edges:standardLink]->()){0,2} FILTER SIZE(edges)=PATH_LENGTH(p) RETURN COUNT(*) AS answer", 3),
                ("MATCH REPEATABLE ELEMENTS p=WALK (()-[edges:standardLink]-()){2} RETURN COUNT(*) AS answer", 2),
                ("MATCH REPEATABLE ELEMENTS p=TRAIL (()-[edges:standardLink]-()){2} RETURN COUNT(*) AS answer", 0),
                ("MATCH (n:StandardMode {name:'A'}) RETURN n UNION ALL MATCH (n:StandardMode {name:'B'}) RETURN n NEXT RETURN COUNT(n) AS answer", 2),
                ("RETURN 10 AS seed NEXT RETURN seed+1 AS n UNION ALL RETURN seed+2 AS n NEXT RETURN SUM(n) AS answer", 23),
                ("MATCH (n:StandardMode) RETURN * UNION ALL MATCH (n:StandardMode) RETURN * NEXT RETURN COUNT(n) AS answer", 4),
                ("MATCH (n:StandardMode) RETURN * UNION MATCH (n:StandardMode) RETURN * NEXT RETURN COUNT(n) AS answer", 2),
                ("RETURN 7 AS `display name` NEXT RETURN * UNION ALL RETURN * NEXT YIELD `display name` AS amount RETURN SUM(amount) AS answer", 14),
                ("LET seed=10 CALL (seed) { RETURN * UNION ALL RETURN * NEXT RETURN SUM(seed) AS valueColumn } RETURN valueColumn AS answer", 20),
                ("MATCH (n:StandardMode) FILTER EXISTS { RETURN * UNION RETURN * NEXT FILTER n.checked IS TRUE RETURN n } RETURN COUNT(n) AS answer", 1),
                ("MATCH (n:StandardMode) RETURN SUM(VALUE { RETURN * UNION ALL RETURN * NEXT RETURN COUNT(*) AS valueColumn }) AS answer", 4),
                ("RETURN 1 AS seed UNION ALL RETURN 2 AS seed NEXT RETURN SUM(VALUE { RETURN * UNION ALL RETURN * NEXT RETURN SUM(seed) AS valueColumn }) AS answer", 6),
                ("FOR seed IN [3,1,2] RETURN -seed AS answer ORDER BY VALUE { RETURN * UNION RETURN * NEXT RETURN answer AS sortKey } LIMIT 1", -3),
                ("FOR seed IN [3,1,2] RETURN seed AS \"odd name\" ORDER BY VALUE { RETURN * UNION RETURN * NEXT YIELD \"odd name\" AS sortKey RETURN sortKey } LIMIT 1 NEXT YIELD \"odd name\" AS answer RETURN answer", 1),
                ("RETURN 0 AS answer UNION ALL LET seed=2 RETURN VALUE { RETURN * UNION ALL RETURN * NEXT RETURN SUM(seed) AS valueColumn } AS answer NEXT RETURN SUM(answer) AS answer", 4),
                ("MATCH (n:StandardMode) RETURN n.name AS name GROUP BY name NEXT RETURN COUNT(*) AS answer", 2),
                ("MATCH (n:StandardMode) LET sortKey=CASE WHEN n.name='A' THEN NULL ELSE 1 END RETURN n.name AS selected ORDER BY sortKey DESCENDING NULLS FIRST LIMIT 1 NEXT RETURN CASE WHEN selected='A' THEN 1 ELSE 0 END AS answer", 1),
                ("MATCH (n:StandardMode) LET sortKey=CASE WHEN n.name='A' THEN NULL ELSE 1 END RETURN n.name AS selected ORDER BY sortKey ASCENDING NULLS LAST LIMIT 1 NEXT RETURN CASE WHEN selected='B' THEN 1 ELSE 0 END AS answer", 1),
                ("MATCH (n:StandardMode) FILTER WHERE n.name='A' RETURN COUNT(*) AS answer", 1),
                ("MATCH REPEATABLE ELEMENTS p=WALK ((starts)-[:standardLink]-()-[:standardLink]-(ends)){0b1,0x2} RETURN SUM(SIZE(starts)) AS answer", 6),
                ("MATCH REPEATABLE ELEMENTS p=SHORTEST 0_2 WALK GROUPS (()-[:standardLink]-()-[:standardLink]-()){1,3} RETURN COUNT(*) AS answer", 4),
                ("MATCH REPEATABLE ELEMENTS p=WALK (()-[outEdges:standardLink]-()-[backEdges:standardLink]-()){1,2} WHERE SIZE(outEdges)=SIZE(backEdges) AND PATH_LENGTH(p)=2*SIZE(outEdges) RETURN COUNT(*) AS answer", 4),
                ("MATCH REPEATABLE ELEMENTS p=TRAIL (()-[outEdges:standardLink]-()-[backEdges:standardLink]-()){1} RETURN COUNT(*) AS answer", 0),
                ("MATCH REPEATABLE ELEMENTS p=WALK ((starts:StandardMode {name:'A'})-[outEdges:standardLink]-(middles {name:'B'})-[backEdges:standardLink]-(ends)){1,2} RETURN SUM(SIZE(middles)) AS answer", 3),
                ("MATCH REPEATABLE ELEMENTS p=WALK ((starts:StandardMode {name:'A'})-[outEdges:standardLink WHERE outEdges.id=42]-(middles WHERE middles.name='B' AND starts.name='A')-[backEdges:standardLink]-(ends) WHERE starts IS SOURCE OF outEdges AND ends IS SOURCE OF backEdges){1,2} RETURN SUM(SIZE(middles)) AS answer", 3),
                ("MATCH REPEATABLE ELEMENTS p=WALK ((starts)-[outEdges:standardLink]-(middles)-[backEdges:standardLink]-(ends)){0,2} WHERE SIZE(outEdges)=0 AND starts=[] AND ends=[] AND PATH_LENGTH(p)=0 RETURN COUNT(*) AS answer", 2),
                ("MATCH REPEATABLE ELEMENTS p=ANY SHORTEST WALK ((starts)-[outEdges:standardLink]-(middles)-[backEdges:standardLink]-(ends)){1,3} RETURN SUM(PATH_LENGTH(p)) AS answer", 4),
                ("MATCH REPEATABLE ELEMENTS p=SHORTEST 2 WALK GROUPS ((starts)-[outEdges:standardLink]-(middles)-[backEdges:standardLink]-(ends)){1,3} RETURN COUNT(*) AS answer", 4),
                ("MATCH REPEATABLE ELEMENTS p=ANY SHORTEST WALK ((starts)-[outEdges:standardLink]-(middles)-[backEdges:standardLink]-(ends)){1,3} WHERE SIZE(outEdges)>1 RETURN COUNT(*) AS answer", 0),
                ("MATCH REPEATABLE ELEMENTS p=WALK (a:StandardMode {name:'A'})((x)-[r:standardLink]-(y)-[s:standardLink]-(z)){1}(b)((u)-[t:standardLink]-(v)-[w:standardLink]-(q)){1}(c) RETURN PATH_LENGTH(p) AS answer", 4),
                ("MATCH REPEATABLE ELEMENTS p=WALK (a:StandardMode {name:'A'})(()-[r:standardLink]-()-[s:standardLink]-()){1}(()-[t:standardLink]-()-[w:standardLink]-()){1} RETURN PATH_LENGTH(p) AS answer", 4),
                ("MATCH REPEATABLE ELEMENTS p=WALK (a:StandardMode {name:'A'})-[r:standardLink]-{1}(b)((x)-[s:standardLink]-(y)-[t:standardLink]-(z)){1}(c) RETURN PATH_LENGTH(p) AS answer", 3),
                ("MATCH REPEATABLE ELEMENTS WALK (:StandardMode {name:'A'})(()-[:standardLink]-()-[:standardLink]-()){1}(()-[:standardLink]-()-[:standardLink]-()){1} RETURN COUNT(*) AS answer", 1),
                ("LET retained=7 MATCH REPEATABLE ELEMENTS WALK (:StandardMode {name:'A'})(()-[:standardLink]-()-[:standardLink]-()){0,1} RETURN SUM(retained) AS answer", 14),
                ("OPTIONAL MATCH (:MissingStandardMode)(()-[:standardLink]-()-[:standardLink]-()){1} RETURN COUNT(*) AS answer", 1),
                ("MATCH p=(a:StandardMode {name:'A'})(()-[firstEdges:standardLink]->()){1}(b),q=(a)(()-[secondEdges:standardLink]->()){1}(c) RETURN COUNT(*) AS answer", 0),
                ("MATCH REPEATABLE ELEMENTS p=(a:StandardMode {name:'A'})(()-[firstEdges:standardLink]->()){1}(b),q=(a)(()-[secondEdges:standardLink]->()){1}(c) RETURN COUNT(*) AS answer", 1),
                ("MATCH REPEATABLE ELEMENTS p=(a:StandardMode WHERE a.name='A' AND c.name='B')(()-[:standardLink]->()){1}(b),q=(:StandardMode {name:'A'})(()-[:standardLink]->()){1}(c) RETURN PATH_LENGTH(p)+PATH_LENGTH(q) AS answer", 2),
                ("MATCH REPEATABLE ELEMENTS p=(a:StandardMode WHERE a.name='A' AND c.name='B')(()-[:standardLink]->()){1}(b),q=(:StandardMode {name:'A'})(()-[:standardLink]->()){1}(c WHERE a.name='A') RETURN PATH_LENGTH(p)+PATH_LENGTH(q) AS answer", 2),
                ("OPTIONAL MATCH REPEATABLE ELEMENTS p=(a:StandardMode WHERE a.name='A' AND c.name='missing')(()-[:standardLink]->()){1}(b),q=(:StandardMode {name:'A'})(()-[:standardLink]->()){1}(c) RETURN CASE WHEN p IS NULL AND q IS NULL THEN 1 ELSE 0 END AS answer", 1),
                ("MATCH REPEATABLE ELEMENTS (:StandardMode {name:'A'})(()-[:standardLink]-()-[:standardLink]-()){1},(:StandardMode {name:'A'})(()-[:standardLink]-()-[:standardLink]-()){1} RETURN COUNT(*) AS answer", 1),
                ("OPTIONAL MATCH p=(a:StandardMode {name:'A'})(()-[firstEdges:standardLink]->()){1}(b),q=(a)(()-[secondEdges:standardLink]->()){1}(c) RETURN CASE WHEN p IS NULL AND q IS NULL THEN 1 ELSE 0 END AS answer", 1),
                ("MATCH REPEATABLE ELEMENTS p=TRAIL (a:StandardMode {name:'A'})((x)-[r:standardLink]-(y)-[s:standardLink]-(z)){1}(b)((u)-[t:standardLink]-(v)-[w:standardLink]-(q)){1}(c) RETURN COUNT(*) AS answer", 0),
                ("MATCH p=(a:StandardMode {name:'A'})((x)-[r:standardLink]->(y)-[s:missing]->(z)){0,1}(b)((u)-[t:standardLink]->(v)-[w:missing]->(q)){0,1}(c) RETURN PATH_LENGTH(p)+SIZE(r)+SIZE(t)+1 AS answer", 1),
                ("RETURN CHAR_LENGTH('\\U01F600') AS answer", 1),
                ("RETURN CASE WHEN CURRENT_DATE=DATE() AND CURRENT_TIMESTAMP=ZONED_DATETIME() AND LOCAL_TIMESTAMP=LOCAL_DATETIME() THEN 1 ELSE 0 END AS answer", 1),
                ("RETURN CASE WHEN DATE '2024-02-29'=DATE('2024-02-29') AND DURATION 'P2D'=DURATION('P2D') THEN 1 ELSE 0 END AS answer", 1),
                ("MATCH (n:StandardMode) WHERE n.amount=0.1234567890123456789012345678 AND n.amounts=[0.1,0.2] RETURN COUNT(*) AS answer", 1),
                ("MATCH REPEATABLE ELEMENTS p = WALK (a:StandardMode {name:'A'})-[:standardLink]-{2}(b) RETURN COUNT(*) AS answer", 1),
                ("MATCH REPEATABLE ELEMENTS p = TRAIL (a:StandardMode {name:'A'})-[:standardLink]-{2}(b) RETURN COUNT(*) AS answer", 0),
                ("MATCH REPEATABLE ELEMENT BINDINGS (a:StandardMode {name:'A'})-[:standardLink]-{2}(b) RETURN COUNT(*) AS answer", 1),
                ("MATCH DIFFERENT EDGE BINDINGS (a:StandardMode {name:'A'})-[:standardLink]-{2}(b) RETURN COUNT(*) AS answer", 0),
                ("MATCH REPEATABLE ELEMENTS p = ACYCLIC (a:StandardMode {name:'A'})-[:standardLink]-{2}(b) RETURN COUNT(*) AS answer", 0),
                ("MATCH REPEATABLE ELEMENTS p = SIMPLE (a:StandardMode {name:'A'})-[:standardLink]-{2}(b) RETURN COUNT(*) AS answer", 1)
            })
            {
                var request = new PartitionQueryRequest(candidate.Generation, candidate.Profile, "gql", query);
                var result = await candidate.QueryAsync(request, default);
                Assert.AreEqual(Canonical((await control.QueryAsync(request, default)).Value), Canonical(result.Value));
                string column = query.EndsWith(" AS \"display name\"", StringComparison.Ordinal) ? "display name" : "answer";
                Assert.AreEqual(expected, result.Value.GetProperty("matches")[0].GetProperty("bindings").GetProperty(column).GetInt32(), query);
            }
            var connectedDelete = await Assert.ThrowsExactlyAsync<ArgumentException>(() => candidate.UpdateQueryAsync(
                new(candidate.Generation, candidate.Profile, EntityOperationIds.New(), "gql",
                    "MATCH (n:StandardMode {name:'A'}) SET n.checked=FALSE NODETACH DELETE n"), default).AsTask());
            Assert.AreEqual("G1001", connectedDelete.Data["EntityForge.GqlStatus"]);
            var unionBag = await candidate.QueryAsync(new(candidate.Generation, candidate.Profile, "gql",
                "RETURN 1 AS answer UNION ALL RETURN 1 AS answer"), default);
            Assert.AreEqual(2, unionBag.Value.GetProperty("matches").GetArrayLength());
            var unchanged = await candidate.QueryAsync(new(candidate.Generation, candidate.Profile, "gql",
                "MATCH (n:StandardMode {name:'A'}) WHERE n.checked IS TRUE RETURN COUNT(*) AS answer"), default);
            Assert.AreEqual(1, unchanged.Value.GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetInt32());
            Guid deleteOperation = EntityOperationIds.New();
            await remote.ExecuteGqlUpdateWithResultAsync(EntityOperationIds.New(),
                "MATCH (n:StandardMode {name:'A'}) SET n.counter=1");
            Guid setOperation = EntityOperationIds.New();
            const string setSnapshot = "MATCH (n:StandardMode {name:'A'}) FOR x IN [1,2,3] SET n.counter=n.counter+1,n.before=n.counter SET n.after=n.counter+1";
            await remote.ExecuteGqlUpdateWithResultAsync(setOperation, setSnapshot);
            await remote.ExecuteGqlUpdateWithResultAsync(setOperation, setSnapshot);
            var setResult = await candidate.QueryAsync(new(candidate.Generation, candidate.Profile, "gql",
                "MATCH (n:StandardMode {name:'A'}) RETURN n.counter=2 AND n.before=1 AND n.after=3 AS answer"), default);
            Assert.IsTrue(setResult.Value.GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetBoolean());
            await remote.ExecuteGqlUpdateWithResultAsync(EntityOperationIds.New(),
                "MATCH (n:StandardMode {name:'A'}) CALL { SET n.fromCall=TRUE } FINISH");
            var calledUpdate = await candidate.QueryAsync(new(candidate.Generation, candidate.Profile, "gql",
                "MATCH (n:StandardMode {name:'A'}) WHERE n.fromCall IS TRUE RETURN COUNT(*) AS answer"), default);
            Assert.AreEqual(1, calledUpdate.Value.GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetInt32());
            const string deleteEdge = "MATCH ()-[e:standardLink]->() DELETE COALESCE(NULL,e)";
            await remote.ExecuteGqlUpdateWithResultAsync(deleteOperation, deleteEdge);
            await remote.ExecuteGqlUpdateWithResultAsync(deleteOperation, deleteEdge);
            var removedEdges = await candidate.QueryAsync(new(candidate.Generation, candidate.Profile, "gql",
                "MATCH ()-[e:standardLink]->() RETURN COUNT(*) AS answer"), default);
            Assert.AreEqual(0, removedEdges.Value.GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetInt32());
            var invalidTarget = await Assert.ThrowsExactlyAsync<ArgumentException>(() => candidate.UpdateQueryAsync(
                new(candidate.Generation, candidate.Profile, EntityOperationIds.New(), "gql",
                    "MATCH (n:StandardMode) SET n.checked=FALSE DELETE CASE WHEN n.name='A' THEN n ELSE 42 END"), default).AsTask());
            StringAssert.Contains(invalidTarget.Message, "DELETE expression");
            var expressionRollback = await candidate.QueryAsync(new(candidate.Generation, candidate.Profile, "gql",
                "MATCH (n:StandardMode {name:'A'}) WHERE n.checked IS TRUE RETURN COUNT(*) AS answer"), default);
            Assert.AreEqual(1, expressionRollback.Value.GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetInt32());
        });
    }

    [TestMethod]
    [DataRow("FOR i IN [1,2] INSERT (n:Uuid {amount:uuid()})")]
    [DataRow("INSERT (n:Uuid {amount:randomUUID()})")]
    [DataRow("FOREACH (i IN [1,2] | CREATE (:Loop))")]
    [DataRow("MERGE (:Loop)")]
    public async Task Graph_vendor_extensions_are_rejected_before_distributed_writes(string statement)
    {
        await WithProfileFragments(DatabaseProfileId.PropertyGraph, async (_, candidate, _, _, _) =>
        {
            var write = new PartitionQueryWrite(candidate.Generation, candidate.Profile, EntityOperationIds.New(), "gql", statement);
            for (int retry = 0; retry < 2; retry++)
            {
                await Assert.ThrowsExactlyAsync<ArgumentException>(() => candidate.UpdateQueryAsync(write, default).AsTask());
            }
            var result = await candidate.QueryAsync(new(candidate.Generation, candidate.Profile, "gql", "MATCH (n) RETURN COUNT(*) AS answer"), default);
            Assert.AreEqual(0, result.Value.GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetInt32());
        });
    }

    [TestMethod]
    public async Task Graph_relationship_endpoints_preserve_properties_across_owners_and_replay()
    {
        await WithProfileFragments(DatabaseProfileId.PropertyGraph, async (control, candidate, _, remote, _) =>
        {
            await candidate.UpdateQueryAsync(new(candidate.Generation, candidate.Profile, EntityOperationIds.New(), "gql",
                "INSERT (:Endpoint {name:'A'})-[:endpointLink]->(:Endpoint {name:'B'})"), default);
            const string query = "MATCH (a:Endpoint)-[r:endpointLink]->(b) RETURN a.name AS origin,b.name AS destination";
            var request = new PartitionQueryRequest(candidate.Generation, candidate.Profile, "gql", query);
            var actual = await candidate.QueryAsync(request, default);
            Assert.AreEqual(Canonical((await control.QueryAsync(request, default)).Value), Canonical(actual.Value));
            Assert.AreEqual("A", actual.Value.GetProperty("matches")[0].GetProperty("bindings").GetProperty("origin").GetString());
            foreach (string walk in new[]
            {
                "MATCH (a:Endpoint {name:'A'})-[r:endpointLink]->{0,1}(b) RETURN b.name AS name,size(r) AS depth ORDER BY depth",
                "MATCH p=(a:Endpoint {name:'A'})-[r:endpointLink]->{0,1}(b) RETURN PATH_LENGTH(p)=size(r) AS answer,b.name AS name ORDER BY name",
                "MATCH (a:Endpoint {name:'A'})-[r:endpointLink]->{1}(b) MATCH (a)-[r:endpointLink]->{1}(b) RETURN b.name AS answer",
                "MATCH REPEATABLE ELEMENTS (a:Endpoint {name:'A'})-[:endpointLink]-{2}(b) RETURN b.name AS answer",
                "MATCH REPEATABLE ELEMENTS p=(a:Endpoint {name:'A'})-[:endpointLink]-{0,3}(b) RETURN PATH_LENGTH(p) AS depth,b.name AS name ORDER BY depth",
                "MATCH REPEATABLE ELEMENTS (a:Endpoint)-[r:endpointLink]->(b),(c:Endpoint)-[s:endpointLink]->(d) RETURN count(*) AS answer"
            })
            {
                var walkRequest = new PartitionQueryRequest(candidate.Generation, candidate.Profile, "gql", walk);
                var walked = await candidate.QueryAsync(walkRequest, default);
                Assert.IsTrue(walked.Value.GetProperty("matches").GetArrayLength() > 0);
                Assert.AreEqual(Canonical((await control.QueryAsync(walkRequest, default)).Value), Canonical(walked.Value), walk);
            }
            Guid operation = EntityOperationIds.New();
            const string statement = "MATCH (a:Endpoint)-[r:endpointLink]->(b) SET a.name='Changed' SET b.copiedName=a.name RETURN b.copiedName AS answer";
            var first = await remote.ExecuteGqlUpdateWithResultAsync(operation, statement);
            Assert.AreEqual("Changed", first.Result!.Value.GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetString());
            var replay = await remote.ExecuteGqlUpdateWithResultAsync(operation, statement);
            Assert.AreEqual(first.Result.Value.GetRawText(), replay.Result!.Value.GetRawText());
        });
    }

    [TestMethod]
    public async Task Graph_statement_clocks_are_shared_across_owners_and_replayed_updates()
    {
        await WithProfileFragments(DatabaseProfileId.PropertyGraph, async (_, candidate, _, remote, _) =>
        {
            Guid operation = EntityOperationIds.New();
            const string statement = "FOR i IN [1,2,3,4] INSERT (:Clock {stamp:CURRENT_TIMESTAMP,localStamp:LOCAL_TIMESTAMP}) RETURN CURRENT_TIMESTAMP AS stamp";
            var first = await remote.ExecuteGqlUpdateWithResultAsync(operation, statement);
            Assert.IsNotNull(first.Result);
            var rows = first.Result.Value.GetProperty("matches");
            Assert.AreEqual(4, rows.GetArrayLength());
            string stamp = rows[0].GetProperty("bindings").GetProperty("stamp").GetRawText();
            Assert.AreNotEqual("null", stamp);
            Assert.IsTrue(rows.EnumerateArray().All(row => row.GetProperty("bindings").GetProperty("stamp").GetRawText() == stamp));
            var replay = await remote.ExecuteGqlUpdateWithResultAsync(operation, statement);
            Assert.AreEqual(first.Result.Value.GetRawText(), replay.Result!.Value.GetRawText());
            const string query = "MATCH (n:Clock) RETURN COUNT(*)=4 AND COUNT(DISTINCT n.stamp)=1 AND COUNT(DISTINCT n.localStamp)=1 " +
                "AND CURRENT_TIMESTAMP=ZONED_DATETIME() AND LOCAL_TIMESTAMP=LOCAL_DATETIME() AS answer";
            for (int repeat = 0; repeat < 2; repeat++)
            {
                var answer = await candidate.QueryAsync(new(candidate.Generation, candidate.Profile, "gql", query), default);
                Assert.IsTrue(answer.Value.GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetBoolean());
            }
        });
    }

    [TestMethod]
    public async Task Graph_calendar_dates_survive_distributed_writes_and_projection()
    {
        await WithProfileFragments(DatabaseProfileId.PropertyGraph, async (control, candidate, _, _, _) =>
        {
            await candidate.UpdateQueryAsync(new(candidate.Generation, candidate.Profile, EntityOperationIds.New(), "gql",
                "INSERT (n:Calendar {zonedStamp:ZONED_DATETIME('2020-02-29T12:30:45.123456789Z'),localStamp:LOCAL_DATETIME('2020-02-29T23:59:59.999999999'),clock:LOCAL_TIME('12:30:45.123456789'),zonedClock:ZONED_TIME('12:30:45Z'),calendarDate:DATE('2020-02-29'),monthSpan:DURATION('P1M'),dates:[DATE('2020-02-29'),DATE('2021-02-28')],intervals:[DURATION('P1D'),DURATION('PT24H')]})"), default);
            foreach (string expression in new[]
            {
                "n.zonedStamp=ZONED_DATETIME('2020-02-29T12:30:45.123456789Z')",
                "n.zonedClock IS TYPED ZONED TIME",
                "n.clock IS TYPED LOCAL TIME",
                "n.localStamp IS TYPED LOCAL DATETIME",
                "n.localStamp+DURATION('PT0.000000001S')=LOCAL_DATETIME('2020-03-01T00:00:00')",
                "n.calendarDate+ n.monthSpan=DATE('2020-03-29')",
                "n.dates IS TYPED LIST<DATE>",
                "VALUE { FOR span IN n.intervals RETURN COUNT(DISTINCT span)=1 AS equalSpans }"
            })
            {
                var query = new PartitionQueryRequest(candidate.Generation, candidate.Profile, "gql", "MATCH (n:Calendar) RETURN " + expression + " AS answer");
                var actual = await candidate.QueryAsync(query, default);
                Assert.AreEqual(1, actual.Value.GetProperty("matches").GetArrayLength());
                Assert.IsTrue(actual.Value.GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetBoolean(), expression);
                Assert.AreEqual(Canonical((await control.QueryAsync(query, default)).Value), Canonical(actual.Value), expression);
            }
        });
    }

    [TestMethod]
    public async Task Graph_update_RETURN_survives_distributed_HTTP_retry_and_later_changes()
    {
        await WithProfileFragments(DatabaseProfileId.PropertyGraph, async (_, candidate, _, remote, _) =>
        {
            var operation = EntityOperationIds.New();
            const string statement = "FOR i IN [1,2] INSERT (n:Returned {amount:i}) RETURN n.amount AS answer,CURRENT_TIMESTAMP AS stamp ORDER BY answer";
            var first = await remote.ExecuteGqlUpdateWithResultAsync(operation, statement);
            Assert.IsNotNull(first.Result);
            var matches = first.Result.Value.GetProperty("matches");
            Assert.AreEqual(2, matches.GetArrayLength());
            Assert.AreEqual(1, matches[0].GetProperty("bindings").GetProperty("answer").GetInt32());
            Assert.AreEqual(2, matches[1].GetProperty("bindings").GetProperty("answer").GetInt32());
            await candidate.UpdateQueryAsync(new(candidate.Generation, candidate.Profile, EntityOperationIds.New(), "gql",
                "MATCH (n:Returned) SET n.amount=99"), default);
            var replay = await remote.ExecuteGqlUpdateWithResultAsync(operation, statement);
            Assert.AreEqual(first.Result.Value.GetRawText(), replay.Result!.Value.GetRawText());
            Assert.AreEqual(first.Receipt.OperationId, replay.Receipt.OperationId);
            var deleted = await remote.ExecuteGqlUpdateWithResultAsync(EntityOperationIds.New(),
                "MATCH (n:Returned) LET captured=n.amount DELETE n RETURN SUM(captured) AS answer,COUNT(n) AS total");
            var returned = deleted.Result!.Value.GetProperty("matches")[0].GetProperty("bindings");
            Assert.AreEqual(198, returned.GetProperty("answer").GetInt32());
            Assert.AreEqual(2, returned.GetProperty("total").GetInt32());
            var called = await remote.ExecuteGqlUpdateWithResultAsync(EntityOperationIds.New(),
                "FOR i IN [1,2] CALL (i) { INSERT (n:Called {amount:i}) RETURN n.amount AS added } RETURN SUM(added) AS answer");
            Assert.AreEqual(3, called.Result!.Value.GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetInt32());
        });
    }

    [TestMethod]
    public async Task Graph_generated_creation_identities_agree_across_owners_and_replay()
    {
        await WithProfileFragments(DatabaseProfileId.PropertyGraph, async (control, candidate, _, _, _) =>
        {
            async Task WriteAndCheck(string statement, string query)
            {
                var write = new PartitionQueryWrite(candidate.Generation, candidate.Profile, EntityOperationIds.New(), "gql", statement);
                await candidate.UpdateQueryAsync(write, default);
                var request = new PartitionQueryRequest(candidate.Generation, candidate.Profile, "gql", query);
                var before = await candidate.QueryAsync(request, default);
                await candidate.UpdateQueryAsync(write, default);
                var after = await candidate.QueryAsync(request, default);
                Assert.AreEqual(Canonical(before.Value), Canonical(after.Value), statement);
                Assert.AreEqual(Canonical((await control.QueryAsync(request, default)).Value), Canonical(after.Value), statement);
                Assert.AreEqual(1, after.Value.GetProperty("matches").GetArrayLength(), query);
                Assert.IsTrue(after.Value.GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetBoolean(), query);
            }
            await WriteAndCheck(
                "INSERT (a:Generated {name:'A'}),(b:Generated {name:'B'}),(a)-[:generated {weight:2}]->(b)",
                "MATCH (n:Generated) RETURN COUNT(*)=2 AND COUNT(DISTINCT ELEMENT_ID(n))=2 AS answer");
            await WriteAndCheck(
                "MATCH (n:Generated) RETURN n ORDER BY n.name LIMIT 1 NEXT SET n.score=CHAR_LENGTH(n.name)",
                "MATCH (n:Generated) WHERE n.score=1 RETURN COUNT(*)=1 AND MIN(n.name)='A' AS answer");
            await WriteAndCheck(
                "MATCH (n:Generated) RETURN n ORDER BY n.name LIMIT 1 NEXT SET n IS Selected SET n IS Reviewed REMOVE n IS Reviewed REMOVE n IS Absent",
                "MATCH (n IS Generated&Selected&!Reviewed) RETURN COUNT(*)=1 AND MIN(n.name)='A' AS answer");
            await WriteAndCheck(
                "MATCH (n:Generated)-[r:generated]->(m) SET n.B=CHAR_LENGTH(m.name),r.computed=2 SET n.temporary=TRUE REMOVE n.temporary",
                "MATCH (n:Generated)-[r:generated]->(m) RETURN n.B=1 AND r.computed=2 AND n.temporary IS NULL AS answer");
            await WriteAndCheck(
                "MATCH (n:Generated)-[r:generated]->(m) SET n={name:n.name,rank:7},r.weight=NULL,r.fresh=3",
                "MATCH (n:Generated)-[r:generated]->(m) RETURN n.name='A' AND n.rank=7 AND n.B IS NULL AND r.weight IS NULL AND r.fresh=3 AS answer");
            await WriteAndCheck(
                "MATCH (n:Generated) FOR i IN [1,2] SET n.visits=COALESCE(n.visits,0)+1 SET n.finalCount=n.visits",
                "MATCH (n:Generated) RETURN COUNT(*)=2 AND MIN(n.visits)=1 AND MAX(n.finalCount)=1 AS answer");
            await WriteAndCheck(
                "MATCH (n:Generated)-[r:generated]->(m) SET n.stamp=CURRENT_TIMESTAMP,r.stamp=CURRENT_TIMESTAMP",
                "MATCH (n:Generated)-[r:generated]->(m) RETURN n.stamp=r.stamp AND n.stamp IS TYPED ZONED DATETIME AS answer,n.stamp AS capturedStamp");
            await WriteAndCheck(
                "MATCH (n:Generated) INSERT (n)-[r:rowChild]->(c:CreatedRow {originName:n.name,stamp:CURRENT_TIMESTAMP}) SET c.checked=c.originName,r.length=CHAR_LENGTH(c.originName) SET c.checked=UPPER(c.checked) REMOVE c.absent",
                "MATCH (n:Generated)-[r:rowChild]->(c:CreatedRow) RETURN COUNT(*)=2 AND SUM(CASE WHEN c.checked=UPPER(n.name) AND r.length=1 THEN 1 ELSE 0 END)=2 AS answer");
            foreach (string query in new[]
            {
                "MATCH (n:Generated) WHERE EXISTS { MATCH (n)-[:rowChild]->(c) WHERE c.originName=n.name } RETURN n.name AS answer",
                "MATCH (n:Generated) RETURN VALUE { MATCH (n)-[:rowChild]->(c) RETURN COUNT(*) AS total } AS answer",
                "MATCH (n:Generated) RETURN VALUE { MATCH (n)-[:rowChild]->(c) RETURN COLLECT_LIST(c.originName) AS names } AS answer",
                "MATCH (n:Generated) CALL (n) { MATCH (n)-[:rowChild]->(c) RETURN c.originName AS answer } RETURN answer",
                "MATCH (n:Generated) OPTIONAL MATCH (n)-[:missing]->(m) FILTER m IS NULL RETURN n.name AS answer",
                "MATCH p=SHORTEST 1 (n:Generated)-[:rowChild]->(c)-[:missing]->{0,1}(d) RETURN PATH_LENGTH(p) AS answer",
                "MATCH (n:Generated) RETURN n ORDER BY n.name NEXT RETURN n.name AS answer",
                "MATCH (n:Generated) OPTIONAL MATCH (n)-[:rowChild]->(c) WHERE EXISTS { MATCH (c)-[:absent]->(m) } RETURN c AS answer"
            })
            {
                var request = new PartitionQueryRequest(candidate.Generation, candidate.Profile, "gql", query);
                var actual = await candidate.QueryAsync(request, default);
                Assert.AreEqual(2, actual.Value.GetProperty("matches").GetArrayLength(), query);
                Assert.AreEqual(Canonical((await control.QueryAsync(request, default)).Value), Canonical(actual.Value), query);
            }
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => candidate.UpdateQueryAsync(
                new(candidate.Generation, candidate.Profile, EntityOperationIds.New(), "gql", "MATCH (n:Generated) NODETACH DELETE n"), default).AsTask());
            await WriteAndCheck("MATCH (n:Generated) DETACH DELETE n",
                "MATCH (n:Generated) RETURN COUNT(*)=0 AS answer");
            await WriteAndCheck("MATCH (n:CreatedRow) DELETE n",
                "MATCH (n) RETURN COUNT(*)=0 AS answer");
        });
    }

    [TestMethod]
    public async Task Rdf_Unicode_backreferences_preserve_distributed_results_and_update_replay()
    {
        await WithProfileFragments(DatabaseProfileId.Rdf, async (control, candidate, _, _, _) =>
        {
            var hyphenQuery = new PartitionQueryRequest(candidate.Generation, candidate.Profile, "sparql",
                "SELECT (BOUND(?invalid) AS ?bad) (REGEX('-', '[a--[b]]') AS ?good) { BIND(REGEX('-', '[0-9-.]') AS ?invalid) }");
            var hyphenResult = await candidate.QueryAsync(hyphenQuery, default);
            var hyphenExpected = await control.QueryAsync(new(candidate.Generation, candidate.Profile, "sparql",
                "SELECT (false AS ?bad) (true AS ?good) {}"), default);
            Assert.AreEqual(Canonical(hyphenExpected.Value), Canonical(hyphenResult.Value));
            await candidate.UpdateQueryAsync(new(candidate.Generation, candidate.Profile, EntityOperationIds.New(), "sparql",
                "INSERT DATA { <urn:a> <urn:label> '𐐀𐐨' . <urn:b> <urn:label> '𐐀𐐩' . <urn:c> <urn:label> 'ﬅﬆ' . <urn:d> <urn:label> 'İi' . <urn:e> <urn:label> '\\r\\r' . <urn:f> <urn:label> '\\n\\n' }"), default);
            var query = new PartitionQueryRequest(candidate.Generation, candidate.Profile, "sparql",
                """SELECT ?s (REPLACE(?text,'(.)\\1','[$1]','i') AS ?value) { ?s <urn:label> ?text FILTER(REGEX(?text,'^(.)\\1$','i')) } ORDER BY ?s""");
            var actual = await candidate.QueryAsync(query, default);
            Assert.AreEqual(3, actual.Value.GetProperty("bindings").GetArrayLength());
            var expected = await control.QueryAsync(new(candidate.Generation, candidate.Profile, "sparql",
                "SELECT ?s ?value { VALUES (?s ?value) { (<urn:a> '[𐐀]') (<urn:c> '[ﬅ]') (<urn:e> '[\\r]') } } ORDER BY ?s"), default);
            Assert.AreEqual(Canonical(expected.Value, true), Canonical(actual.Value, true));
            Assert.AreEqual(Canonical((await control.QueryAsync(query, default)).Value, true), Canonical(actual.Value, true));
            var spacedQuery = new PartitionQueryRequest(candidate.Generation, candidate.Profile, "sparql",
                """SELECT ?s (REPLACE(?text,'(.)\\ 1','[$1]','ix') AS ?value) { ?s <urn:label> ?text FILTER(REGEX(?text,'^(.)\\ 1$','ix')) } ORDER BY ?s""");
            Assert.AreEqual(Canonical(actual.Value, true), Canonical((await candidate.QueryAsync(spacedQuery, default)).Value, true));
            var update = new PartitionQueryWrite(candidate.Generation, candidate.Profile, EntityOperationIds.New(), "sparql",
                """INSERT { ?s <urn:matched> ?value } WHERE { ?s <urn:label> ?text FILTER(REGEX(?text,'^(.)\\1$','i')) BIND(REPLACE(?text,'(.)\\1','$1','i') AS ?value) }""");
            await candidate.UpdateQueryAsync(update, default);
            await candidate.UpdateQueryAsync(new(candidate.Generation, candidate.Profile, EntityOperationIds.New(), "sparql", "DELETE WHERE { ?s <urn:matched> ?value }"), default);
            await candidate.UpdateQueryAsync(update, default);
            var after = await candidate.QueryAsync(new(candidate.Generation, candidate.Profile, "sparql", "SELECT ?s { ?s <urn:matched> ?value }"), default);
            Assert.AreEqual(0, after.Value.GetProperty("bindings").GetArrayLength(), "Replay must not evaluate or reapply the regex update.");
        });
    }

    [TestMethod]
    public async Task Rdf_graph_transfers_preserve_atomic_distributed_graph_membership()
    {
        await WithProfileFragments(DatabaseProfileId.Rdf, async (control, candidate, _, _, _) =>
        {
            await candidate.UpdateQueryAsync(new(candidate.Generation, candidate.Profile, EntityOperationIds.New(), "sparql",
                "INSERT DATA { <urn:a> <urn:p> 1 . GRAPH <urn:g> { <urn:b> <urn:p> 2 . <urn:c> <urn:p> 3 } }"), default);
            string[] transfers = ["", "# no operation", "PREFIX ex:<urn:>", "CREATE GRAPH <urn:empty>; CLEAR GRAPH <urn:empty>; DROP GRAPH <urn:empty>",
                "CREATE SILENT GRAPH <urn:g>", "COPY GRAPH <urn:g> TO GRAPH <urn:h>",
                "WITH <urn:h> DELETE { ?s <urn:p> ?v } INSERT { ?s <urn:p> ?v } WHERE { ?s <urn:p> ?v }",
                "WITH <urn:using> INSERT { ?s <urn:p> ?v } USING <urn:g> WHERE { ?s <urn:p> ?v }",
                "WITH <urn:h> INSERT { ?s <urn:unexpected> ?v } USING NAMED <urn:g> WHERE { ?s <urn:p> ?v }",
                "ADD DEFAULT TO GRAPH <urn:h>", "MOVE GRAPH <urn:h> TO DEFAULT", "DROP GRAPH <urn:g>"];
            foreach (string statement in transfers)
            {
                await candidate.UpdateQueryAsync(new(candidate.Generation, candidate.Profile, EntityOperationIds.New(), "sparql", statement), default);
                var query = new PartitionQueryRequest(candidate.Generation, candidate.Profile, "sparql",
                    "SELECT ?s ?o ?g WHERE { { ?s <urn:p> ?o } UNION { GRAPH ?g { ?s <urn:p> ?o } } } ORDER BY ?s ?g");
                var baseline = await control.QueryAsync(query, default);
                var actual = await candidate.QueryAsync(query, default);
                Assert.AreEqual(Canonical(baseline.Value, true), Canonical(actual.Value, true), statement);
            }
            var answer = await candidate.QueryAsync(new(candidate.Generation, candidate.Profile, "sparql", "SELECT ?s { ?s ?p ?o }"), default);
            Assert.AreEqual(3, answer.Value.GetProperty("bindings").GetArrayLength());
            var generated = new PartitionQueryWrite(candidate.Generation, candidate.Profile, EntityOperationIds.New(), "sparql",
                "INSERT { ?id <urn:nonce> ?value . _:b <urn:link> ?id } WHERE { BIND(UUID() AS ?id) BIND(RAND() AS ?value) }");
            await candidate.UpdateQueryAsync(generated, default);
            await candidate.UpdateQueryAsync(generated, default);
            var generatedQuery = new PartitionQueryRequest(candidate.Generation, candidate.Profile, "sparql",
                "SELECT ?id ?value ?blank { ?id <urn:nonce> ?value . ?blank <urn:link> ?id }");
            var generatedActual = await candidate.QueryAsync(generatedQuery, default);
            Assert.AreEqual(1, generatedActual.Value.GetProperty("bindings").GetArrayLength());
            Assert.AreEqual(Canonical((await control.QueryAsync(generatedQuery, default)).Value), Canonical(generatedActual.Value));
            var sequence = new PartitionQueryWrite(candidate.Generation, candidate.Profile, EntityOperationIds.New(), "sparql",
                "INSERT DATA { <urn:sequence> <urn:step> 1 }; DELETE { ?s <urn:step> ?v } INSERT { ?s <urn:finished> ?v } WHERE { ?s <urn:step> ?v }");
            await candidate.UpdateQueryAsync(sequence, default);
            await candidate.UpdateQueryAsync(sequence, default);
            var sequenceQuery = new PartitionQueryRequest(candidate.Generation, candidate.Profile, "sparql",
                "SELECT ?v { <urn:sequence> <urn:finished> ?v }");
            var sequenceActual = await candidate.QueryAsync(sequenceQuery, default);
            Assert.AreEqual(1, sequenceActual.Value.GetProperty("bindings").GetArrayLength());
            Assert.AreEqual(Canonical((await control.QueryAsync(sequenceQuery, default)).Value), Canonical(sequenceActual.Value));
        });
    }

    [DataTestMethod]
    [DataRow(DatabaseProfileId.Rdf)]
    [DataRow(DatabaseProfileId.Semantic)]
    public async Task Compact_profile_timestamp_job_keeps_its_original_projection_across_a_writer(DatabaseProfileId profile)
    {
        var authority = new Authority();
        var clock = new IndependentTestClock(Guid.NewGuid(), 0);
        await using var node = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "timestamp-profile"), profile,
            authority.Lease, Routing, authority, Options, timestampClock: clock);
        Func<string, Task> write;
        PartitionQueryExchangeRequest selection;
        if (profile == DatabaseProfileId.Rdf)
        {
            var adapter = await node.CreateRdfExecutionAsync();
            write = async value =>
            {
                var capture = adapter.CaptureSave(new Item("one", value), EntityOperationIds.New());
                await adapter.ExecuteAsync(node.Domain, capture.OperationId, capture.Export());
            };
            selection = new("sparql", "SELECT ?s ?p ?o WHERE { ?s ?p ?o }", ProfileFragments: true);
        }
        else
        {
            var adapter = await node.CreateSemanticExecutionAsync();
            write = async value =>
            {
                var capture = adapter.CaptureSave(new Item("one", value), SemanticEntityWriteOperation.Create());
                await adapter.ExecuteAsync(node.Domain, capture.OperationId, capture.Export());
            };
            using var http = new HttpClient();
            var manifest = new HttpSemanticEntityClient(http, node.Domain.Generation).EntityStore<Item>().Manifests[0];
            var property = manifest.ProfileBindings.Single(p => p.MemberIdentity == manifest.Members.Single(m => m.ClrName == "Name").Identity).StoredBinding;
            selection = new("entity", EntityQuery: new(manifest.ProfileTypeBinding,
                new(manifest.ProfileTypeBinding, Fields: [new(property, "name")]), ConceptMatchMode: "direct"), ProfileFragments: true);
        }
        await write("before");
        var cut = await node.AcquireSnapshotCutAsync(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(20));
        var time = PartitionTimestampClock.Read(clock.Domain, clock);
        Guid job = Guid.NewGuid();
        try
        {
            var initial = await node.ReadQueryJobValueAsync(cut, new("query-job", QueryJob: new(job, selection), ReadTime: time));
            StringAssert.Contains(initial.Value!.Element.GetRawText(), "before");
            await write("after");
            var final = await node.ReadQueryJobValueAsync(cut, new("query-job", QueryJob: new(job, selection, Finish: true), ReadTime: time));
            Assert.IsTrue(final.Completed);
            Assert.IsNull(final.Pending);
            Assert.AreEqual(initial.Value.Element.GetRawText(), final.Value!.Element.GetRawText());
        }
        finally
        {
            await node.AbandonCutAsync(cut.Id);
        }
        Assert.AreEqual(0, node.Kernel.TimestampProtectionCount);
    }

    [DataTestMethod]
    [DataRow(DatabaseProfileId.Rdf)]
    [DataRow(DatabaseProfileId.Semantic)]
    public async Task Typed_remote_profile_timestamp_job_keeps_its_original_projection_across_a_writer(DatabaseProfileId profile)
    {
        var authority = new Authority();
        var clock = new IndependentTestClock(Guid.NewGuid(), 0);
        await using var decisions = FilePartitionTransactionAuthority.Open(Path.Combine(_root, "timestamp-remote-authority"));
        await using var node = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "timestamp-profile-remote"), profile,
            authority.Lease, Routing, authority, Options, transactionAuthority: decisions, timestampClock: clock);
        Func<string, Task> write;
        PartitionQueryExchangeRequest selection;
        if (profile == DatabaseProfileId.Rdf)
        {
            var adapter = await node.CreateRdfExecutionAsync();
            write = async value =>
            {
                var capture = adapter.CaptureSave(new Item("one", value), EntityOperationIds.New());
                await adapter.ExecuteAsync(node.Domain, capture.OperationId, capture.Export());
            };
            selection = new("sparql", "SELECT ?s ?p ?o WHERE { ?s ?p ?o }", ProfileFragments: true);
        }
        else
        {
            var adapter = await node.CreateSemanticExecutionAsync();
            write = async value =>
            {
                var capture = adapter.CaptureSave(new Item("one", value), SemanticEntityWriteOperation.Create());
                await adapter.ExecuteAsync(node.Domain, capture.OperationId, capture.Export());
            };
            using var http = new HttpClient();
            var manifest = new HttpSemanticEntityClient(http, node.Domain.Generation).EntityStore<Item>().Manifests[0];
            var property = manifest.ProfileBindings.Single(p => p.MemberIdentity == manifest.Members.Single(m => m.ClrName == "Name").Identity).StoredBinding;
            selection = new("entity", EntityQuery: new(manifest.ProfileTypeBinding,
                new(manifest.ProfileTypeBinding, Fields: [new(property, "name")]), ConceptMatchMode: "direct"), ProfileFragments: true);
        }
        using var certificate = PartitionCertificate();
        await using var service = new PartitionParticipantHttpService(node);
        await using var host = await StartPartitionHostAsync(service, certificate);
        using var peerHttp = new HttpClient(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback =
            (_, observed, _, _) => observed?.Thumbprint == certificate.Thumbprint
        });
        var peer = new HttpPartitionTransactionParticipant(peerHttp, PartitionAddress(host), PartitionToken,
            node.Domain, node.TransactionAuthorityIdentity!)
        {
            TypedQueryProtocol = true
        };
        await write("before");
        var cut = await node.AcquireSnapshotCutAsync(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(20));
        var time = PartitionTimestampClock.Read(clock.Domain, clock);
        Guid job = Guid.NewGuid();
        try
        {
            var initial = await peer.ReadQueryJobValueAsync(cut, new("query-job", QueryJob: new(job, selection), ReadTime: time));
            StringAssert.Contains(initial.Value!.Element.GetRawText(), "before");
            await write("after");
            var final = await peer.ReadQueryJobValueAsync(cut, new("query-job", QueryJob: new(job, selection, Finish: true), ReadTime: time));
            Assert.IsTrue(final.Completed);
            Assert.IsNull(final.Pending);
            Assert.AreEqual(initial.Value.Element.GetRawText(), final.Value!.Element.GetRawText());
        }
        finally
        {
            await node.AbandonCutAsync(cut.Id);
        }
        Assert.AreEqual(0, node.Kernel.TimestampProtectionCount);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Compact_rdf_pages_are_complete_and_quoted_bindings_are_checked(bool commands)
    {
        await WithProfileFragments(DatabaseProfileId.Rdf, async (control, candidate, _, _, _) =>
        {
            const int count = 600, batch = 30;
            for (int start = 0; start < count; start += batch)
            {
                await candidate.UpdateQueryAsync(new(candidate.Generation, candidate.Profile, EntityOperationIds.New(), "sparql",
                    "INSERT DATA { " + string.Join(" ", Enumerable.Range(start, batch).Select(i => $"<urn:paged:{i}> <urn:n> {i} .")) + " }"), default);
            }

            var request = new PartitionQueryRequest(candidate.Generation, candidate.Profile, "sparql", "SELECT ?s ?n WHERE { ?s <urn:n> ?n } ORDER BY ?s");
            var expected = await control.QueryAsync(request, default);
            var actual = await candidate.QueryAsync(request, default);
            Assert.AreEqual(count, actual.Value.GetProperty("bindings").GetArrayLength());
            Assert.IsTrue(actual.Statistics!.ExchangePages > 2);
            Assert.AreEqual(Canonical(expected.Value, true), Canonical(actual.Value, true));
        }, commands: commands);
        const string quoted = "SELECT ?s ?o WHERE { << ?s <urn:p> ?o >> <urn:quoted> ?n }";
        var row = new EntityForge.Db.Rdf.RdfBindingFragment(0, new()
        {
            ["s"] = new EntityForge.Db.Rdf.Contracts.RdfIri("urn:a"),
            ["o"] = new EntityForge.Db.Rdf.Contracts.RdfIri("urn:b"),
            ["n"] = new EntityForge.Db.Rdf.Contracts.RdfLiteral("value")
        });
        var quad = EntityForge.Db.Rdf.RdfSparql.ValidateBindingFragment(quoted, row);
        var raw = EntityForge.Db.Rdf.RdfSparql.ExecuteRelation([quad], quoted, default);
        var compact = EntityForge.Db.Rdf.RdfSparql.ExecuteBindingFragments(quoted, [row], default);
        Assert.AreEqual(JsonSerializer.Serialize(raw, PartitionQueryJson.Options), JsonSerializer.Serialize(compact, PartitionQueryJson.Options));
        row.Bindings["unexpected"] = new EntityForge.Db.Rdf.Contracts.RdfIri("urn:bad");
        Assert.ThrowsExactly<InvalidDataException>(() => EntityForge.Db.Rdf.RdfSparql.ValidateBindingFragment(quoted, row));
    }

    [DataTestMethod]
    [DataRow(DatabaseProfileId.Rdf, false)]
    [DataRow(DatabaseProfileId.Rdf, true)]
    [DataRow(DatabaseProfileId.Semantic, false)]
    [DataRow(DatabaseProfileId.Semantic, true)]
    public async Task Compact_profile_completion_remains_pending_until_fresh_authority(DatabaseProfileId profile, bool remote)
    {
        await WithQueryHome(async (node, placements, home, _) =>
        {
            using var certificate = PartitionCertificate();
            await using var service = new PartitionParticipantHttpService(node);
            await using var host = await StartPartitionHostAsync(service, certificate);
            using var http = new HttpClient(new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback =
                (_, observed, _, _) => observed?.Thumbprint == certificate.Thumbprint
            });
            IPartitionReadParticipant peer = remote ? new HttpPartitionTransactionParticipant(http, PartitionAddress(host),
                PartitionToken, node.Domain, node.TransactionAuthorityIdentity!)
            {
                TypedQueryProtocol = true
            }
            : node;
            var cut = await peer.AcquireSnapshotCutAsync(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(20));
            try
            {
                var selection = profile == DatabaseProfileId.Rdf
                    ? new PartitionQueryExchangeRequest("sparql", "SELECT ?s WHERE { ?s <urn:p> ?o }", ProfileFragments: true)
                    : new PartitionQueryExchangeRequest("entity", EntityQuery: new("urn:absent", new("urn:absent"), ConceptMatchMode: "direct"), ProfileFragments: true);
                var result = await peer.ReadQueryJobValueAsync(cut, new("query-job", QueryJob:
                    new(Guid.NewGuid(), selection, Finish: true, Home: home.Binding, Sequence: 1), ReadTime: TestQueryTime(cut)));
                Assert.IsTrue(result.Completed);
                Assert.IsNotNull(result.Pending);
                await placements.ReleaseAsync(node.CurrentLease);
                await Assert.ThrowsExactlyAsync<StaleShardPlacementException>(() => home.ValidateAsync([result.Pending], default).AsTask());
            }
            finally
            {
                await peer.AbandonCutAsync(cut.Id);
            }
            await using var frozen = await node.Kernel.PartitionTransactions.FreezeAsync();
        }, profile: profile);
    }

    [TestMethod]
    public async Task Compact_rdf_operators_preserve_global_algebra_and_fallbacks()
    {
        await WithProfileFragments(DatabaseProfileId.Rdf, async (control, candidate, _, _, _) =>
        {
            await candidate.UpdateQueryAsync(new(candidate.Generation, candidate.Profile, EntityOperationIds.New(), "sparql",
                "INSERT DATA { <urn:a> <urn:p> <urn:b> . <urn:b> <urn:p> <urn:c> . <urn:a> <urn:n> 2 . <urn:a> <urn:n> 3 . <urn:b> <urn:n> 4 . <urn:c> <urn:n> 2 . <urn:a> <urn:self> <urn:a> . GRAPH <urn:g> { <urn:a> <urn:n> 7 . } }"), default);
            string[] queries = [
                  "SELECT ?s ?n WHERE { ?s <urn:n> ?n } ORDER BY ?s ?n VALUES ?n { 2 4 }",
                  "SELECT (COUNT(*) AS ?count) WHERE { ?s <urn:n> ?n } VALUES () { () () }",
                  "SELECT ?g ?count WHERE { GRAPH ?g { SELECT (COUNT(*) AS ?count) WHERE { ?s ?p ?o } } }",
                  "SELECT ?g WHERE { GRAPH ?g {} }",
                  "SELECT ?s WHERE { ?s <urn:n> ?n FILTER(?n=2 && EXISTS { ?s <urn:p> ?o }) } ORDER BY ?s",
                  "SELECT ?s (EXISTS { ?s <urn:p> ?o } AS ?value) WHERE { ?s <urn:n> ?n } ORDER BY ?s",
                  "SELECT ?s ?value WHERE { ?s <urn:n> ?n BIND(IF(EXISTS { ?s <urn:p> ?o },1,0) AS ?value) } ORDER BY ?s",
                  "SELECT (SUM(IF(EXISTS { ?s <urn:p> ?o },1,0)) AS ?value) WHERE { ?s <urn:n> ?n }",
                  "ASK {} VALUES ?x {}",
                  "CONSTRUCT WHERE { ?s <urn:n> ?n } VALUES ?n { 2 }",
                  "SELECT ?s ?n FROM <urn:g> WHERE { ?s <urn:n> ?n } ORDER BY ?s",
                  "SELECT ?n WHERE { _:item <urn:n> ?n . _:item <urn:p> ?o } ORDER BY ?n",
                  "SELECT * WHERE { [] <urn:n> ?n } ORDER BY ?n",
                  "SELECT ?n WHERE { [ <urn:n> ?n; <urn:p> [ <urn:n> 4 ] ] } ORDER BY ?n",
                  "SELECT ?g ?n WHERE { GRAPH ?g { [ <urn:n> ?n ] } } ORDER BY ?n",
                  "SELECT (COUNT(DISTINCT *) AS ?count) WHERE { [] <urn:n> ?n }",
                  "SELECT (COUNT(DISTINCT *) AS ?count) WHERE { ?s <urn:n> ?n }",
                  "SELECT ?key (COUNT(*) AS ?count) WHERE { ?s <urn:n> ?n } GROUP BY (FLOOR(?n/2) AS ?key) ORDER BY ?key",
                  "SELECT (COUNT(*) AS ?count) WHERE { ?s <urn:n> ?n } GROUP BY FLOOR(?n/2) ORDER BY ?count",
                  "ASK WHERE { ?s <urn:n> ?n } GROUP BY (FLOOR(?n/2) AS ?key) HAVING(COUNT(*)>1)",
                  "ASK WHERE { [] <urn:n> 7 }",
                  "SELECT ?g ?s ?n FROM NAMED <urn:g> WHERE { GRAPH ?g { ?s <urn:n> ?n } } ORDER BY ?s",
                  "SELECT (SUM(?n) AS ?total) FROM <urn:g> WHERE { ?s <urn:n> ?n }",
                  "SELECT ?s FROM <urn:g> WHERE { { SELECT ?s { ?s <urn:n> ?n } } }",
                  "ASK FROM <urn:g> { ?s <urn:n> 7 }",
                  "CONSTRUCT { ?s <urn:copy> ?n } FROM <urn:g> WHERE { ?s <urn:n> ?n }",
                  "DESCRIBE ?s FROM <urn:g> WHERE { ?s <urn:n> ?n }",
                "SELECT ?s (NOW()=NOW() AS ?same) WHERE { ?s <urn:n> ?n FILTER(NOW()=NOW()) } ORDER BY ?s ?n",
                "SELECT (COUNT(DISTINCT NOW()) AS ?clocks) WHERE { ?s <urn:n> ?n }",
                "SELECT (COUNT(DISTINCT BNODE('x')) AS ?blanks) WHERE { ?s <urn:n> ?n }",
                "BASE <https://example.test/> SELECT ?s (IRI(CONCAT('node/',STR(?n))) AS ?value) WHERE { ?s <urn:n> ?n FILTER(IRI('x')=<x>) } ORDER BY ?s ?n",
                "BASE <https://example.test/> SELECT ?s ?value WHERE { ?s <urn:n> ?n { SELECT (IRI('child') AS ?value) {} } } ORDER BY ?s ?n",
                "PREFIX xsd: <http://www.w3.org/2001/XMLSchema#> SELECT ?s (xsd:int('7')+1 AS ?value) WHERE { ?s <urn:n> ?n } ORDER BY ?s ?n",
                "SELECT ?s (isBlank(BNODE()) AS ?blank) WHERE { ?s <urn:n> ?n FILTER(BNODE('x')=BNODE('x')) } ORDER BY ?s ?n",
                "SELECT ?s WHERE { ?s <urn:n> ?n BIND(NOW() AS ?clock) { SELECT (NOW() AS ?inner) {} } FILTER(?clock=?inner) } ORDER BY ?s ?n",
                "SELECT ?s (REPLACE(STR(?s), 'urn:(.)', 'node-$1') AS ?value) WHERE { ?s <urn:n> ?n } ORDER BY ?s ?n",
                "SELECT ?s (YEAR('2024-02-29T23:45:12Z'^^<http://www.w3.org/2001/XMLSchema#dateTime>) AS ?year) WHERE { ?s <urn:n> ?n } ORDER BY ?s ?n",
                "SELECT ?s ?value WHERE { ?s <urn:n> ?n BIND(REPLACE(STR(?s),'a','A','i') AS ?value) FILTER(REPLACE(STR(?s),'urn:','') = 'a') } ORDER BY ?s ?n",
                "SELECT ?s WHERE { ?s <urn:n> ?n } ORDER BY ?n DESC(STR(?s)) LIMIT 2",
                "SELECT ?s ?x WHERE { ?s <urn:n> ?n BIND(UCASE(STR(?s)) AS ?x) } ORDER BY ?n ?x OFFSET 1",
                "SELECT ?s ?x WHERE { ?s <urn:n> ?n BIND(STR(?missing) AS ?x) } ORDER BY ?s ?n",
                "SELECT ?s ?n WHERE { ?s <urn:p> ?o . ?s <urn:n> ?n FILTER (?n > 2) } ORDER BY ?n LIMIT 2",
                "SELECT ?s ?n WHERE { ?s <urn:p> ?o . ?o <urn:n> ?n } ORDER BY ?n",
                "SELECT ?s ?n WHERE { ?s <urn:n> ?n OPTIONAL { ?s <urn:missing> ?x } }",
                "SELECT ?s WHERE { { ?s <urn:n> 2 } UNION { ?s <urn:n> 2 } }",
                "SELECT DISTINCT ?s WHERE { ?s <urn:n> ?n } ORDER BY ?s LIMIT 10 OFFSET 1",
                "SELECT ?s WHERE { ?s <urn:n> ?n MINUS { ?s <urn:p> ?o } }",
                "SELECT ?s WHERE { ?s <urn:n> ?n FILTER NOT EXISTS { ?s <urn:p> ?o } }",
                "SELECT ?s WHERE { ?s <urn:n> ?n FILTER EXISTS { ?s <urn:p> ?o } }",
                "SELECT ?s (SUM(?n*2) AS ?sum) WHERE { ?s <urn:n> ?n } GROUP BY ?s HAVING(COUNT(*) > 0) ORDER BY SUM(?n)",
                "SELECT ?s (IF(?n > 1, CONCAT('n=', STR(?n)), 'small') AS ?label) WHERE { ?s <urn:n> ?n } ORDER BY ?s",
                "SELECT ?n (GROUP_CONCAT(DISTINCT STR(?n); SEPARATOR=',') AS ?values) WHERE { ?s <urn:n> ?n } GROUP BY ?n ORDER BY ?n",
                "SELECT ?s (ROUND(?n/3) AS ?rounded) (SHA256(STR(?s)) AS ?hash) WHERE { ?s <urn:n> ?n FILTER(isNumeric(?n)) } ORDER BY ?s ?rounded",
                "SELECT ?s (ENCODE_FOR_URI(STR(?s)) AS ?encoded) WHERE { ?s <urn:n> ?n FILTER(STRENDS(STR(?s), 'a')) } ORDER BY ?s",
                "SELECT ?s WHERE { VALUES ?s { <urn:a> <urn:c> } ?s <urn:n> ?n }",
                "SELECT ?s ?x WHERE { ?s <urn:n> ?n BIND(?n AS ?x) }",
                "PREFIX xsd: <http://www.w3.org/2001/XMLSchema#> SELECT ?s WHERE { ?s <urn:n> ?n FILTER(!'bad'^^xsd:integer) }",
                "PREFIX xsd: <http://www.w3.org/2001/XMLSchema#> SELECT ?s (IF(' true '^^xsd:boolean,1,0) AS ?truth) WHERE { ?s <urn:n> ?n }",
                "SELECT ?s (UCASE('Straße'@de) AS ?upper) (LCASE('ΟΣ'@el) AS ?lower) WHERE { ?s <urn:n> ?n } ORDER BY ?s ?n",
                "SELECT ?s (LANGMATCHES('en'@en,'en') AS ?invalid) (LANGMATCHES(LANG('text'@en-GB),'en') AS ?valid) WHERE { ?s <urn:n> ?n } ORDER BY ?s ?n",
                "PREFIX xsd: <http://www.w3.org/2001/XMLSchema#> SELECT ?s ('16777217'^^xsd:float+0e0 AS ?rounded) (IF('1e-50'^^xsd:float,true,false) AS ?truth) WHERE { ?s <urn:n> ?n } ORDER BY ?s ?n",
                "PREFIX xsd: <http://www.w3.org/2001/XMLSchema#> SELECT ?s (xsd:double('\u00A01') AS ?bad) WHERE { ?s <urn:n> ?n }",
                "PREFIX xsd: <http://www.w3.org/2001/XMLSchema#> SELECT ?s (xsd:string(?n) AS ?text) (xsd:integer(?n/2) AS ?half) WHERE { ?s <urn:n> ?n } ORDER BY ?s ?half",
                "SELECT (IRI(STR(?s)) AS ?iri) WHERE { ?s <urn:n> ?n } ORDER BY ?iri",
                "SELECT ?s ?o WHERE { ?s <urn:p>+ ?o } ORDER BY ?s ?o",
                "SELECT ?o WHERE { <urn:a> <urn:p>* ?o } ORDER BY ?o",
                "SELECT ?s ?o WHERE { ?s !<urn:n> ?o } ORDER BY ?s ?o",
                "SELECT ?g ?s ?o WHERE { GRAPH ?g { ?s <urn:p>* ?o } } ORDER BY ?g ?s ?o",
                "SELECT ?s (COUNT(*) AS ?count) (SUM(?n) AS ?sum) WHERE { ?s <urn:n> ?n } GROUP BY ?s ORDER BY ?s",
                "SELECT (COUNT(DISTINCT ?n) AS ?count) (AVG(?n) AS ?avg) WHERE { ?s <urn:n> ?n }",
                "SELECT ?s WHERE { { SELECT ?s WHERE { ?s <urn:n> ?n } ORDER BY ?s LIMIT 2 } ?s <urn:p> ?o }",
                "SELECT ?g ?s ?n WHERE { GRAPH ?g { ?s <urn:n> ?n } }",
                "SELECT ?s WHERE { ?s <urn:self> ?s }",
                "SELECT ?n WHERE { ?s <urn:n> ?n } ORDER BY ?s",
                "SELECT (COUNT(*) AS ?count) WHERE { ?s <urn:n> ?n }",
                "SELECT ?s (COUNT(?n) AS ?count) WHERE { ?s <urn:n> ?n } GROUP BY ?s HAVING (COUNT(?n) > 1) ORDER BY ?s",
                "SELECT ?s ?o WHERE { << ?s <urn:p> ?o >> <urn:quoted> ?n }",
                "ASK { ?s <urn:p> ?o . ?o <urn:n> 4 }",
                "ASK { ?s <urn:p> ?o } LIMIT 0",
                "ASK { ?s <urn:n> ?n } GROUP BY ?s HAVING (COUNT(*)>1)",
                "ASK { ?s <urn:n> ?n } ORDER BY ?s OFFSET 100",
                "CONSTRUCT { ?s <urn:grouped> true } WHERE { ?s <urn:n> ?n } GROUP BY ?s HAVING (COUNT(*)>1)",
                "CONSTRUCT { ?s <urn:copy> ?n } WHERE { ?s <urn:n> ?n }",
                "CONSTRUCT WHERE { ?s <urn:n> ?n }",
                "CONSTRUCT FROM <urn:g> WHERE { ?s <urn:n> ?n }",
                "SELECT ?o WHERE { <urn:a> <urn:p>/<urn:p> ?o }",
                "DESCRIBE ?s WHERE { ?s <urn:n> 2 }",
                "DESCRIBE <urn:a>",
                "DESCRIBE * { ?s <urn:n> 2 }"
            ];
            foreach (string statement in queries)
            {
                var request = new PartitionQueryRequest(candidate.Generation, candidate.Profile, "sparql", statement);
                var baseline = await control.QueryAsync(request, default);
                var actual = await candidate.QueryAsync(request, default);
                Assert.AreEqual(Canonical(baseline.Value, statement.Contains("ORDER BY")), Canonical(actual.Value, statement.Contains("ORDER BY")), statement);
                Assert.AreEqual(EntityForge.Db.Rdf.RdfSparql.SupportsBindingFragments(statement), actual.Statistics!.Strategy is "rdf-fragment-bindings" or "rdf-binding-frontier", statement);
            }
        });
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Compact_semantic_operators_preserve_projection_order_evidence_scopes_and_paging(bool commands)
    {
        await WithProfileFragments(DatabaseProfileId.Semantic, async (control, candidate, _, _, http) =>
        {
            var store = new HttpSemanticEntityClient(http, candidate.Generation).EntityStore<Item>();
            for (int i = 0; i < 18; i++)
            {
                var capture = store.CaptureSave(new(i.ToString(), "value-" + (i % 4)), SemanticEntityWriteOperation.Create());
                await candidate.DeliverAsync(capture.OperationId, capture.Export());
            }
            var manifest = store.Manifests[0];
            string property = manifest.ProfileBindings.Single(p => p.MemberIdentity == manifest.Members.Single(m => m.ClrName == "Name").Identity).StoredBinding;
            var query = new PortableEntityQueryRequest(manifest.ProfileTypeBinding,
                new(manifest.ProfileTypeBinding, Fields: [new(property, "name")]), ConceptMatchMode: "direct");
            PortableEntityQueryRequest[] requests = [query,
                query with { IncludeEvidence = true },
                query with { RootConcept = query.RootConcept.Replace("urn:", "URN:", StringComparison.Ordinal) },
                query with { OrderBy = [new(property, "descending")] },
                query with { OrderBy = [new("urn:missing", MissingValues: "first"), new(property)], IncludeEvidence = true },
                query with { Filter = new("propertycomparison", property, "equal", new(JsonSerializer.SerializeToElement("value-2"))) },
                query with { AssertionPolicy = new(AllowedProviders: ["urn:absent"]) },
                query with { TemporalScopes = [new("validTime", DateTimeOffset.UtcNow.AddYears(-1), DateTimeOffset.UtcNow.AddYears(1))] },
                query with { First = 5 },
                query with { ConceptMatchMode = "entailed" }];
            foreach (var selected in requests)
            {
                var request = new PartitionQueryRequest(candidate.Generation, candidate.Profile, "entity", EntityQuery: selected);
                var baseline = await control.QueryAsync(request, default);
                var actual = await candidate.QueryAsync(request, default);
                Assert.AreEqual(Canonical(baseline.Value, true), Canonical(actual.Value, true));
                if (selected.RootConcept != query.RootConcept)
                {
                    Assert.AreEqual(18, actual.Value.GetProperty("entities").GetArrayLength(), "Equivalent URI spelling must retain the selected concept.");
                }

                Assert.AreEqual(selected.ConceptMatchMode == "direct", actual.Statistics!.Strategy == "semantic-projection-fragments");
                if (selected.First is not null)
                {
                    int count = actual.Value.GetProperty("entities").GetArrayLength();
                    while (actual.NextContinuation is { } after)
                    {
                        actual = await candidate.QueryAsync(request with
                        {
                            EntityQuery = selected with
                            {
                                After = after
                            }
                        }, default);
                        count += actual.Value.GetProperty("entities").GetArrayLength();
                    }
                    Assert.AreEqual(18, count);
                }
            }
            await Assert.ThrowsAsync<Exception>(async () => await candidate.QueryAsync(
                new(candidate.Generation, candidate.Profile, "entity", EntityQuery: query with
                {
                    MaximumExpandedEntities = 1
                }), default));
            Assert.AreEqual(18, (await candidate.QueryAsync(new(candidate.Generation, candidate.Profile, "entity", EntityQuery: query), default))
                .Value.GetProperty("entities").GetArrayLength(), "A rejected projection must drain its owner jobs.");
        }, commands: commands);
    }

    // Ignore fresh query IDs and evidence enumeration order, but preserve entity
    // order when it is part of the query and always preserve duplicate row counts.
    private static string Canonical(JsonElement value, bool ordered = false)
    {
        object? Normalize(JsonElement item, string? name = null) => item.ValueKind switch
        {
            JsonValueKind.Object => item.EnumerateObject().Where(p => p.Name is not ("queryId" or "snapshotVersion" or "nextContinuation"))
                .OrderBy(p => p.Name, StringComparer.Ordinal).ToDictionary(p => p.Name, p => Normalize(p.Value, p.Name)),
            JsonValueKind.Array => ordered && name is "entities" or "bindings"
                ? item.EnumerateArray().Select(v => JsonSerializer.Serialize(Normalize(v))).ToArray()
                : item.EnumerateArray().Select(v => JsonSerializer.Serialize(Normalize(v))).Order(StringComparer.Ordinal).ToArray(),
            _ => JsonSerializer.Deserialize<object>(item.GetRawText())
        };
        return JsonSerializer.Serialize(Normalize(value));
    }

    private async Task WithProfileFragments(DatabaseProfileId profile,
        Func<PartitionApplicationClient, PartitionApplicationClient, HttpPartitionEntityClient, HttpPartitionEntityClient, HttpClient, Task> action,
        bool bothCompact = false, bool commands = false, EntityForge.Db.Rdf.RdfSparqlHttpResources? rdfHttpResources = null,
        EntityForge.Db.Gql.GqlProcedureCatalog? gqlProcedures = null)
        => await WithProfileFragments(profile, (left, right, publicLeft, publicRight, http, _, _) =>
            action(left, right, publicLeft, publicRight, http), bothCompact, commands, rdfHttpResources: rdfHttpResources, gqlProcedures: gqlProcedures);

    private async Task WithProfileFragments(DatabaseProfileId profile,
        Func<PartitionApplicationClient, PartitionApplicationClient, HttpPartitionEntityClient, HttpPartitionEntityClient, HttpClient,
            PartitionOwnedNode[], IPartitionTransactionAuthority, Task> action,
        bool bothCompact = false, bool commands = false, bool timestamps = false, bool localTraversalControl = false,
        EntityForge.Db.Rdf.RdfSparqlHttpResources? rdfHttpResources = null, EntityForge.Db.Gql.GqlProcedureCatalog? gqlProcedures = null,
        bool allRemoteParticipants = false)
    {
        string? endpoint = Environment.GetEnvironmentVariable("ENTITYFORGE_TEST_ETCD_ENDPOINT");
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            Assert.Inconclusive("Set ENTITYFORGE_TEST_ETCD_ENDPOINT.");
            return;
        }
        using var etcd = new EtcdClient(endpoint, configureChannelOptions: o => o.Credentials = ChannelCredentials.Insecure);
        string prefix = "/entityforge/profile-fragments/" + Guid.NewGuid().ToString("N") + "/";
        using var placements = new GrpcEtcdShardPlacementAuthority([new EtcdClient(endpoint, configureChannelOptions: o => o.Credentials = ChannelCredentials.Insecure)], new([new(endpoint)], prefix));
        try
        {
            var routing = new StableHashDistributedShardRouter(new(new(1), [new("a"), new("b")])).Binding;
            var generation = new DistributedDatabaseGeneration(Guid.NewGuid());
            var a = await placements.AcquireAsync(new("a"), new("a"), new("https://localhost:22001"), generation, routing.MapVersion, TimeSpan.FromMinutes(30));
            var b = await placements.AcquireAsync(new("b"), new("b"), new("https://localhost:22002"), generation, routing.MapVersion, TimeSpan.FromMinutes(30));
            await using var authority = FilePartitionTransactionAuthority.Open(Path.Combine(_root, "authority"));
            var clock = ReadClock;
            await using var first = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "a"), profile, a, routing, placements, Options, transactionAuthority: authority, timestampClock: clock);
            await using var second = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "b"), profile, b, routing, placements, Options, transactionAuthority: authority, timestampClock: clock);
            using var certificate = PartitionCertificate();
            await using var service = new PartitionParticipantHttpService(second);
            await using var remoteHost = await StartPartitionHostAsync(service, certificate);
            await using var firstService = new PartitionParticipantHttpService(first);
            await using var firstHost = bothCompact || allRemoteParticipants ? await StartPartitionHostAsync(firstService, certificate) : null;
            using var http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, observed, _, _) => observed?.Thumbprint == certificate.Thumbprint });
            IPartitionTransactionParticipant[] owners = [allRemoteParticipants
                ? new HttpPartitionTransactionParticipant(http, PartitionAddress(firstHost!), PartitionToken, first.Domain, authority.Identity) { TypedQueryProtocol = true }
                : first, new HttpPartitionTransactionParticipant(http, PartitionAddress(remoteHost), PartitionToken, second.Domain, authority.Identity) { TypedQueryProtocol = true }];
            IPartitionTransactionParticipant[] otherOwners = firstHost is null ? owners :
                [new HttpPartitionTransactionParticipant(http, PartitionAddress(firstHost), PartitionToken, first.Domain, authority.Identity) { TypedQueryProtocol = true }, second];
            await using var home = await PartitionQueryHomeAuthority.OpenAsync(placements, clock);
            await using var otherHome = await PartitionQueryHomeAuthority.OpenAsync(placements, clock);
            var control = new PartitionApplicationClient(authority, routing, otherOwners, clock) { GqlProcedures = gqlProcedures, OwnerLocalTraversal = !localTraversalControl, IndexDirectedRoots = localTraversalControl, CompactProfileOperators = bothCompact, QueryHome = otherHome, CompleteOwnerCommands = bothCompact && commands };
            var candidate = new PartitionApplicationClient(authority, routing, owners, clock) { GqlProcedures = gqlProcedures, IndexDirectedRoots = Environment.GetEnvironmentVariable("ENTITYFORGE_TEST_ROOT_INDEX") != "0", QueryHome = home, CompleteOwnerCommands = commands, RdfHttpResources = rdfHttpResources };
            await using var controlService = new PartitionApplicationHttpService(control);
            await using var candidateService = new PartitionApplicationHttpService(candidate);
            async Task<WebApplication> Host(PartitionApplicationHttpService application)
            {
                var builder = WebApplication.CreateBuilder();
                builder.Logging.ClearProviders();
                if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ENTITYFORGE_SEMANTIC_SCOPE_OUTPUT")))
                {
                    builder.Logging.AddConsole().SetMinimumLevel(LogLevel.Error);
                }

                builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, l => l.UseHttps(certificate)));
                var host = builder.Build();
                application.Map(host, new(PartitionToken));
                await host.StartAsync();
                return host;
            }
            await using var controlHost = await Host(controlService);
            await using var candidateHost = await Host(candidateService);
            try
            {
                await action(control, candidate,
                new(http, PartitionAddress(controlHost), PartitionToken, generation.Value, profile),
                new(http, PartitionAddress(candidateHost), PartitionToken, generation.Value, profile), http, [first, second], authority);
            }
            finally
            {
                await controlHost.StopAsync();
                await candidateHost.StopAsync();
                await controlService.DisposeAsync();
                await candidateService.DisposeAsync();
                Assert.AreEqual(0L, first.RetainedQueryWorkspaceBytes);
                Assert.AreEqual(0L, second.RetainedQueryWorkspaceBytes);
                Assert.AreEqual(0, first.ActiveQueryJobCount);
                Assert.AreEqual(0, second.ActiveQueryJobCount);
                Assert.AreEqual(0, first.UnreleasedReadCutCount);
                Assert.AreEqual(0, second.UnreleasedReadCutCount);
                Assert.AreEqual(0, first.Kernel.TimestampProtectionCount);
                Assert.AreEqual(0, second.Kernel.TimestampProtectionCount);
                await remoteHost.StopAsync();
                if (firstHost is not null)
                {
                    await firstHost.StopAsync();
                }
            }
        }
        finally
        {
            await etcd.DeleteAsync(new DeleteRangeRequest { Key = ByteString.CopyFromUtf8(prefix), RangeEnd = ByteString.CopyFromUtf8(prefix[..^1] + "0") });
        }
    }
}
