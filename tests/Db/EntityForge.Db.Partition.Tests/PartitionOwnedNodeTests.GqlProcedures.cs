using EntityForge.Db.Gql;
using EntityForge.Db.Distributed.Hosting;
using EntityForge.Db.Distributed.Sharding;
using EntityForge.Db.Hosting;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [TestMethod]
    public async Task Gql_standard_named_procedures_capture_bodies_before_distributed_execution_and_replay()
    {
        var catalog = new GqlProcedureCatalog([
            new("personAge", ["requestedName"], "CALL lookupPerson(requestedName) YIELD person,age RETURN age AS answer,person"),
            new("quotedPerson", ["requestedName"], "CALL lookupPerson(requestedName) YIELD person,age RETURN person AS `person node`,age AS `display age`"),
            new("increment", ["inputNumber"], "RETURN inputNumber+1 AS answer"),
            new("tools.increment", ["inputNumber"], "RETURN inputNumber+1 AS answer"),
            new("`tools.increment`", ["inputNumber"], "RETURN inputNumber+20 AS answer"),
            new("\"quoted dependency\"", ["inputNumber"], "CALL `tools`.\"increment\"(inputNumber) RETURN answer"),
            new("whoAmI", [], "RETURN SESSION_USER AS who"),
            new("lookupPerson", ["requestedName"], "MATCH (person:ProcedurePerson {name:requestedName}) RETURN person,person.age AS age")],
            [new("identity.recordIdentity", [], "CALL whoAmI() INSERT (:SessionRecord {owner:who}) RETURN who")]);
        await WithProfileFragments(DatabaseProfileId.PropertyGraph, async (control, candidate, _, remote, _) =>
        {
            foreach (string sessionQuery in new[] { "RETURN SESSION_USER AS who", "CALL whoAmI() RETURN who" })
            {
                var localRequest = new PartitionQueryRequest(candidate.Generation, candidate.Profile, "gql", sessionQuery);
                var anonymous = await candidate.QueryAsync(localRequest, default);
                Assert.AreEqual("anonymous", anonymous.Value.GetProperty("matches")[0].GetProperty("bindings").GetProperty("who").GetString());
                var authenticated = await remote.QueryGqlAsync(sessionQuery);
                Assert.AreEqual("shared-bearer", authenticated.Value.GetProperty("matches")[0].GetProperty("bindings").GetProperty("who").GetString());
                var anonymousAgain = await candidate.QueryAsync(localRequest, default);
                Assert.AreEqual("anonymous", anonymousAgain.Value.GetProperty("matches")[0].GetProperty("bindings").GetProperty("who").GetString());
                Assert.IsNotNull(await remote.ExplainGqlAsync(sessionQuery));
                int streamed = 0;
                int completionDiagnostics = 0;
                bool completed = false;
                await foreach (var frame in remote.StreamGqlAsync(sessionQuery))
                {
                    if (frame.Kind == "item" && frame.Section == "/matches")
                    {
                        Assert.AreEqual("shared-bearer", frame.Value.GetProperty("bindings").GetProperty("who").GetString());
                        streamed++;
                    }
                    completed |= frame.Kind == "complete";
                    if (frame.Kind == "item" && frame.Section == "/diagnostics" && frame.Value.GetProperty("code").GetString() == "00000")
                    {
                        completionDiagnostics++;
                    }
                }
                Assert.AreEqual(1, streamed);
                Assert.IsTrue(completed);
                Assert.AreEqual(1, completionDiagnostics);
            }
            Guid identityOperation = EntityOperationIds.New();
            const string identityUpdate = "CALL identity.recordIdentity() RETURN who";
            await Assert.ThrowsExactlyAsync<HttpRequestException>(() => remote.QueryGqlAsync(identityUpdate).AsTask());
            var identityResult = await remote.ExecuteGqlUpdateWithResultAsync(identityOperation, identityUpdate);
            Assert.AreEqual("shared-bearer", identityResult.Result!.Value.GetProperty("matches")[0].GetProperty("bindings").GetProperty("who").GetString());
            var identityReplay = await remote.ExecuteGqlUpdateWithResultAsync(identityOperation, identityUpdate);
            Assert.AreEqual(identityResult.Result.Value.GetRawText(), identityReplay.Result?.GetRawText());
            var storedIdentity = await remote.QueryGqlAsync("MATCH (n:SessionRecord) RETURN n.owner AS who");
            Assert.AreEqual(1, storedIdentity.Value.GetProperty("matches").GetArrayLength());
            Assert.AreEqual("shared-bearer", storedIdentity.Value.GetProperty("matches")[0].GetProperty("bindings").GetProperty("who").GetString());
            var invalid = new PartitionQueryRequest(candidate.Generation, candidate.Profile, "gql", "CREATE (:Person)");
            var queryError = await Assert.ThrowsExactlyAsync<ArgumentException>(() => candidate.QueryAsync(invalid, default).AsTask());
            Assert.AreEqual("42001", queryError.Data["EntityForge.GqlStatus"]);
            var explainError = Assert.ThrowsExactly<ArgumentException>(() => candidate.ExplainQuery(invalid));
            Assert.AreEqual("42001", explainError.Data["EntityForge.GqlStatus"]);
            var updateError = await Assert.ThrowsExactlyAsync<HttpRequestException>(() => remote.ExecuteGqlUpdateWithResultAsync(
                EntityOperationIds.New(), "CREATE (:Person)").AsTask());
            Assert.AreEqual(System.Net.HttpStatusCode.BadRequest, updateError.StatusCode);
            StringAssert.Contains(updateError.Message, "\"gqlStatus\":\"42001\"");
            Assert.AreEqual("42001", updateError.Data["EntityForge.GqlStatus"]);
            var remoteReadError = await Assert.ThrowsExactlyAsync<HttpRequestException>(() => remote.QueryGqlAsync("CREATE (:Person)").AsTask());
            Assert.AreEqual(System.Net.HttpStatusCode.BadRequest, remoteReadError.StatusCode);
            Assert.AreEqual("42001", remoteReadError.Data["EntityForge.GqlStatus"]);
            var remoteExplainError = await Assert.ThrowsExactlyAsync<HttpRequestException>(() => remote.ExplainGqlAsync("CREATE (:Person)").AsTask());
            Assert.AreEqual("42001", remoteExplainError.Data["EntityForge.GqlStatus"]);
            var remoteStreamError = await Assert.ThrowsExactlyAsync<HttpRequestException>(async () =>
            {
                await foreach (var frame in remote.StreamGqlAsync("CREATE (:Person)"))
                {
                    Assert.Fail("Invalid syntax must not emit a result frame.");
                }
            });
            Assert.AreEqual("42001", remoteStreamError.Data["EntityForge.GqlStatus"]);
            const string mixedUnion = "RETURN 1 AS answer UNION ALL RETURN 1 AS answer UNION RETURN 2 AS answer";
            var mixedQuery = await Assert.ThrowsExactlyAsync<HttpRequestException>(() => remote.QueryGqlAsync(mixedUnion).AsTask());
            Assert.AreEqual("42001", mixedQuery.Data["EntityForge.GqlStatus"]);
            var mixedUpdate = await Assert.ThrowsExactlyAsync<HttpRequestException>(() => remote.ExecuteGqlUpdateWithResultAsync(
                EntityOperationIds.New(), "INSERT (:RejectedDomain) RETURN 1 AS marker NEXT " + mixedUnion).AsTask());
            Assert.AreEqual("42001", mixedUpdate.Data["EntityForge.GqlStatus"]);
            foreach (string missingAlias in new[] { "RETURN 1", "RETURN SESSION_USER", "RETURN VALUE { RETURN 1 } AS answer" })
            {
                var aliasRead = await Assert.ThrowsExactlyAsync<HttpRequestException>(() => remote.QueryGqlAsync(missingAlias).AsTask());
                Assert.AreEqual("42001", aliasRead.Data["EntityForge.GqlStatus"]);
                var aliasUpdate = await Assert.ThrowsExactlyAsync<HttpRequestException>(() => remote.ExecuteGqlUpdateWithResultAsync(
                    EntityOperationIds.New(), "INSERT (:RejectedDomain) " + missingAlias).AsTask());
                Assert.AreEqual("42001", aliasUpdate.Data["EntityForge.GqlStatus"]);
            }
            foreach (var (expression, status) in new[] { ("ACOS(2)", "2202M"), ("LOG(1,8)", "2201Z"), ("LN(0)", "2201E"), ("LEFT('abc',-1)", "22011"), ("1/0", "22012"), ("9223372036854775807+1", "22003"), ("TRIM([1],2)", "2202E"), ("TRIM('ab' FROM 'text')", "22027"), ("DATE({`year`:2020,unexpected:1})", "22G05"), ("DURATION({days:1,unexpected:1})", "22G07") })
            {
                var numericRead = await Assert.ThrowsExactlyAsync<HttpRequestException>(() => remote.QueryGqlAsync("RETURN " + expression + " AS answer").AsTask());
                Assert.AreEqual(status, numericRead.Data["EntityForge.GqlStatus"]);
                var numericUpdate = await Assert.ThrowsExactlyAsync<HttpRequestException>(() => remote.ExecuteGqlUpdateWithResultAsync(
                    EntityOperationIds.New(), "INSERT (:RejectedDomain) LET bad=" + expression + " RETURN bad").AsTask());
                Assert.AreEqual(status, numericUpdate.Data["EntityForge.GqlStatus"]);
            }
            var rejected = await remote.QueryGqlAsync("MATCH (:RejectedDomain) RETURN COUNT(*) AS total");
            Assert.AreEqual(0, rejected.Value.GetProperty("matches")[0].GetProperty("bindings").GetProperty("total").GetInt32());
            await remote.ExecuteGqlUpdateWithResultAsync(EntityOperationIds.New(), "INSERT (person:ProcedurePerson {name:'Ada',age:37})-[:procedureLink]->(:ProcedureTarget)-[:procedureTail]->(:ProcedureEnd)-[:procedureBack]->(person)");
            var connectedDelete = await Assert.ThrowsExactlyAsync<HttpRequestException>(() => remote.ExecuteGqlUpdateWithResultAsync(
                EntityOperationIds.New(), "MATCH (n:ProcedurePerson) SET n.age=99 NODETACH DELETE n").AsTask());
            Assert.AreEqual("G1001", connectedDelete.Data["EntityForge.GqlStatus"]);
            foreach (string following in new[] { "RETURN n.age AS answer", "SET n.age=1 RETURN 1 AS answer" })
            {
                var deletedReference = await Assert.ThrowsExactlyAsync<HttpRequestException>(() => remote.ExecuteGqlUpdateWithResultAsync(
                    EntityOperationIds.New(), "MATCH (n:ProcedurePerson) DETACH DELETE n " + following).AsTask());
                Assert.AreEqual("22G11", deletedReference.Data["EntityForge.GqlStatus"]);
            }
            foreach (var (completionQuery, completionCode) in new[]
            {
                ("RETURN 1 AS answer", "00000"),
                ("RETURN 1 AS answer LIMIT 0", "02000"),
                ("RETURN 1 AS answer NEXT FINISH", "00001"),
                ("LET stamp=CURRENT_TIMESTAMP FINISH", "00001"),
                ("USE CURRENT_GRAPH { RETURN 1 AS answer }", "00000")
            })
            {
                var completionResult = await remote.QueryGqlAsync(completionQuery);
                Assert.AreEqual(1, completionResult.Value.GetProperty("diagnostics").EnumerateArray()
                    .Count(item => item.GetProperty("code").GetString() == completionCode));
            }
            foreach (string diagnosticQuery in new[]
            {
                "MATCH (n:ProcedurePerson) RETURN COUNT(n.missing) AS total",
                "CALL { FOR x IN [NULL,1] RETURN SUM(x) AS total } RETURN total",
                "RETURN VALUE { FOR x IN [NULL,1] RETURN SUM(x) AS total } AS total"
            })
            {
                var diagnosticResult = await remote.QueryGqlAsync(diagnosticQuery);
                Assert.AreEqual(1, diagnosticResult.Value.GetProperty("diagnostics").EnumerateArray()
                    .Count(item => item.GetProperty("code").GetString() == "01G11"));
            }
            foreach (var (query, expected) in new[]
            {
                ("MATCH (origin:ProcedurePerson)((x)-[e:procedureLink]->(y) WHERE VALUE { RETURN x.age AS amount UNION RETURN x.age AS amount NEXT RETURN amount }=37)+(destination) RETURN COUNT(*) AS answer", 1),
                ("MATCH (origin:ProcedurePerson)((x)-[e:procedureLink]->(y) WHERE VALUE { CALL (x) { RETURN x.age AS age } RETURN age }=37)+(destination) RETURN COUNT(*) AS answer", 1),
                ("MATCH (origin:ProcedurePerson)((x)-[e:procedureLink]->(y) WHERE VALUE { RETURN x NEXT RETURN * NEXT YIELD \"x\" AS selectedNode RETURN selectedNode.age AS \"selectedNode.age\" }=37)+(destination) RETURN COUNT(*) AS answer", 1),
                ("MATCH (origin:ProcedurePerson)((x)-[e:procedureLink]->(y) WHERE VALUE { FOR item IN [x,y] RETURN item.age AS x ORDER BY x DESC NULLS LAST LIMIT 1 }=37)+(destination) RETURN COUNT(*) AS answer", 1),
                ("MATCH (origin:ProcedurePerson)((x)-[e:procedureLink]->(y) WHERE EXISTS { FILTER x.age=37 RETURN y })+(destination) RETURN COUNT(*) AS answer", 1),
                ("MATCH (origin:ProcedurePerson)((x)-[e:procedureLink]->(y) WHERE VALUE { FILTER FALSE RETURN COUNT(*) AS total }=0)+(destination) RETURN COUNT(*) AS answer", 1),
                ("MATCH (origin:ProcedurePerson)((x)-[e:procedureLink]->(y) WHERE VALUE { LET age=x.age RETURN age }=37)+(destination) RETURN COUNT(*) AS answer", 1),
                ("MATCH (origin:ProcedurePerson)((x)-[e:procedureLink]->(y) WHERE VALUE { LET age=VALUE { RETURN x.age AS \"x.age\" } RETURN age }=37)+(destination) RETURN COUNT(*) AS answer", 1),
                ("MATCH (origin:ProcedurePerson)((x)-[e:procedureLink]->(y) WHERE VALUE { FOR item IN [x,y] FILTER item.age=37 RETURN COUNT(*) AS total }=1)+(destination) RETURN COUNT(*) AS answer", 1),
                ("MATCH (origin:ProcedurePerson)((x)-[e:procedureLink]->(y) WHERE VALUE { MATCH (x)-[:procedureLink]->(other) RETURN COUNT(*) AS total }=1)+(destination) RETURN COUNT(*) AS answer", 1),
                ("MATCH (origin:ProcedurePerson)((x)-[e:procedureLink]->(y) WHERE VALUE { FILTER FALSE LET age=x.age RETURN COUNT(*) AS total }=0)+(destination) RETURN COUNT(*) AS answer", 1),
                ("RETURN 1+2 AS \"1+2\" UNION ALL RETURN 1+2 AS \"1+2\" NEXT YIELD `1+2` AS amount RETURN SUM(amount) AS answer", 6),
                ("RETURN 1+2 AS \"1+2\" UNION RETURN 1+2 AS \"1+2\" NEXT YIELD `1+2` AS amount RETURN amount AS answer", 3),
                ("MATCH (c:ProcedureTarget) MATCH (:ProcedurePerson)((x)-[:procedureLink]->(y)){1}(b WHERE EXISTS { MATCH (c:ProcedurePerson) }),(:ProcedurePerson) RETURN COUNT(*) AS answer", 0),
                ("MATCH (c:ProcedureTarget) MATCH (:ProcedurePerson)((x)-[:procedureLink]->(y)){1}(b WHERE NOT EXISTS { MATCH (c:ProcedurePerson) }),(:ProcedurePerson) RETURN COUNT(*) AS answer", 1),
                ("MATCH (c:ProcedureTarget) MATCH (:ProcedurePerson)((x WHERE EXISTS { MATCH (c:ProcedurePerson) })-[:procedureLink]->(y)){1}(b)((u)-[:procedureTail]->(v)){0}(z) RETURN COUNT(*) AS answer", 0),
                ("USE CURRENT_GRAPH { RETURN 6 AS valueNumber NEXT RETURN valueNumber+1 AS answer }", 7),
                ("MATCH p=TRAIL (a:ProcedurePerson)((x)-[rs:procedureLink]->(y)-[ss:procedureTail]->(z)-[ts:procedureBack]->(w))+(a) RETURN PATH_LENGTH(p) AS answer", 3),
                ("MATCH ANY SHORTEST (p=ACYCLIC (:ProcedurePerson)-[:procedureLink]->(:ProcedureTarget) WHERE PATH_LENGTH(p)>0) RETURN PATH_LENGTH(p) AS answer", 1),
                ("MATCH ANY SHORTEST ((a:ProcedurePerson)-[edges:procedureLink|procedureTail|procedureBack]->+(b:ProcedureEnd) WHERE CARDINALITY(edges)>1) RETURN CARDINALITY(edges) AS answer", 2),
                ("MATCH REPEATABLE ELEMENTS (p=ACYCLIC (:ProcedurePerson)-[:procedureLink|procedureTail|procedureBack]->{1,3}(:ProcedurePerson)) RETURN COUNT(*) AS answer", 0),
                ("MATCH (a:ProcedurePerson) MATCH p=(a)((x)-[rs:procedureLink]->(y)-[ss:procedureTail]->(z)){0}(a) RETURN PATH_LENGTH(p) AS answer", 0),
                ("MATCH p=(:ProcedurePerson)((x)-[rs:procedureLink]->(y)-[ss:procedureTail]->(z)){1,20}(:ProcedureEnd) RETURN PATH_LENGTH(p) AS answer", 2),
                ("MATCH p=(:ProcedurePerson)((x)-[rs:procedureLink]->(y)-[ss:procedureTail]->(z)){0}(:ProcedurePerson) RETURN PATH_LENGTH(p)+SIZE(rs) AS answer", 0),
                ("MATCH p=(:ProcedurePerson)-[rs:procedureLink]->{0}(:ProcedurePerson) RETURN SIZE(rs) AS answer", 0),
                ("MATCH REPEATABLE ELEMENTS p=ANY (n:ProcedurePerson WHERE EXISTS { MATCH (n) WHERE n.age=37 })-[:procedureLink]->(),(:ProcedurePerson) RETURN n.age AS answer", 37),
                ("MATCH REPEATABLE ELEMENTS p=ANY (n:ProcedurePerson WHERE VALUE { LET age=n.age FILTER age=37 RETURN age }=37)-[:procedureLink]->(),(:ProcedurePerson) RETURN n.age AS answer", 37),
                ("MATCH REPEATABLE ELEMENTS p=ANY (n:ProcedurePerson WHERE VALUE { LET age=n.age FILTER age=99 RETURN age }=99)-[:procedureLink]->(),(:ProcedurePerson) RETURN COUNT(*) AS answer", 0),
                ("MATCH REPEATABLE ELEMENTS p=ANY (n:ProcedurePerson WHERE EXISTS { MATCH (n) WHERE n.age=99 })-[:procedureLink]->(),(:ProcedurePerson) RETURN COUNT(*) AS answer", 0),
                ("MATCH (a:ProcedurePerson)-[e:procedureLink]->(b) RETURN PATH_LENGTH(PATH[a,e,b]) AS answer", 1),
                ("MATCH (a:ProcedurePerson)-[e:procedureLink]->(b) WHERE PATH_LENGTH(PATH[b,e,a])=1 RETURN COUNT(*) AS answer", 1),
                ("MATCH (a:ProcedurePerson)-[e:procedureLink]->(b) RETURN PATH_LENGTH(PATH[a,e,b,e,a]) AS answer", 2),
                ("MATCH (a:ProcedurePerson) RETURN PATH_LENGTH(PATH[a]) AS answer", 0),
                ("MATCH (:ProcedurePerson)-[:procedureLink]->(segment=()-[:procedureTail]->()) RETURN PATH_LENGTH(segment) AS answer", 1),
                ("MATCH whole=(:ProcedurePerson)-[:procedureLink]->(segment=()-[:procedureTail]->()) RETURN PATH_LENGTH(whole)+PATH_LENGTH(segment) AS answer", 3),
                ("MATCH ANY SHORTEST (:ProcedurePerson)-[:procedureLink]->(segment=ACYCLIC ()-[:procedureTail]->()) RETURN PATH_LENGTH(segment) AS answer", 1),
                ("MATCH (:ProcedurePerson)-[:procedureLink]->(outer=(inner=()-[:procedureTail]->())) RETURN CASE WHEN outer=inner THEN PATH_LENGTH(outer) ELSE 0 END AS answer", 1),
                ("MATCH ANY SHORTEST (:ProcedurePerson)-[:procedureLink]->(outer=TRAIL (inner=ACYCLIC ()-[:procedureTail]->())) RETURN PATH_LENGTH(outer)+PATH_LENGTH(inner) AS answer", 2),
                ("MATCH (:ProcedurePerson)-[:procedureLink]->(segment=()-[:procedureTail]->()),(:ProcedurePerson)-[:procedureLink]->(tail=()) RETURN COUNT(*) AS answer", 0),
                ("MATCH REPEATABLE ELEMENTS (:ProcedurePerson)-[:procedureLink]->(segment=()-[:procedureTail]->()),(:ProcedurePerson)-[:procedureLink]->(tail=()) RETURN PATH_LENGTH(segment)+PATH_LENGTH(tail) AS answer", 1),
                ("MATCH (:ProcedurePerson)(segments=()-[:procedureLink|procedureTail]->()){1,2}() RETURN SUM(CARDINALITY(segments)) AS answer", 3),
                ("MATCH (:ProcedurePerson)(segments=()-[:procedureLink|procedureTail]->()){1,20}() RETURN SUM(CARDINALITY(segments)) AS answer", 3),
                ("MATCH (segments=(a:ProcedurePerson)-[e:procedureLink]->(b) WHERE VALUE { CALL (segments) { RETURN PATH_LENGTH(segments) AS hops } RETURN hops }=1){1,20} RETURN CARDINALITY(segments) AS answer", 1),
                ("MATCH (:ProcedurePerson)(segments=ACYCLIC ()-[:procedureLink|procedureTail]->()){1,20}() RETURN SUM(CARDINALITY(segments)) AS answer", 3),
                ("MATCH REPEATABLE ELEMENTS (segments=TRAIL (:ProcedurePerson)-[:procedureLink]->()<-[:procedureLink]-()){1} RETURN COUNT(*) AS answer", 0),
                ("MATCH REPEATABLE ELEMENTS (segments=SIMPLE (a:ProcedurePerson)-[e:procedureLink]->(b)<-[f:procedureLink]-(c)){1} RETURN COUNT(*) AS answer", 1),
                ("MATCH (segments=(:ProcedurePerson)-[:procedureLink]->()){1},(other=(:ProcedurePerson)-[:procedureLink]->()){1} RETURN COUNT(*) AS answer", 0),
                ("MATCH REPEATABLE ELEMENTS (segments=(:ProcedurePerson)-[:procedureLink]->()){1},(other=(:ProcedurePerson)-[:procedureLink]->()){1} RETURN CARDINALITY(segments)+CARDINALITY(other) AS answer", 2),
                ("MATCH REPEATABLE ELEMENTS ANY SHORTEST (segments=(:ProcedurePerson)-[:procedureLink]->()){1},ANY SHORTEST (other=(:ProcedurePerson)-[:procedureLink]->()){1} RETURN COUNT(*) AS answer", 1),
                ("MATCH whole=(pieces=(:ProcedurePerson)-[:procedureLink]->()){1}(tails=()-[:procedureTail]->()){1} RETURN PATH_LENGTH(whole)+CARDINALITY(pieces)+CARDINALITY(tails) AS answer", 4),
                ("MATCH (pieces=(:ProcedurePerson)-[:procedureLink]->()){0}(tails=()-[:procedureLink]->()){1} RETURN CARDINALITY(pieces)+CARDINALITY(tails) AS answer", 1),
                ("MATCH (pieces=(:ProcedurePerson)-[:procedureLink]->()){1}(tails=()<-[:procedureLink]-()){1} RETURN COUNT(*) AS answer", 0),
                ("MATCH REPEATABLE ELEMENTS (pieces=(:ProcedurePerson)-[:procedureLink]->()){1}(tails=()<-[:procedureLink]-()){1} RETURN COUNT(*) AS answer", 1),
                ("MATCH ANY SHORTEST (pieces=(:ProcedurePerson)-[:procedureLink]->()){1}(tails=()-[:procedureTail]->()){1} RETURN CARDINALITY(pieces)+CARDINALITY(tails) AS answer", 2),
                ("MATCH (:ProcedurePerson)-[:procedureLink]->(outer=(inner=()->())) RETURN CASE WHEN outer=inner THEN PATH_LENGTH(outer) ELSE 0 END AS answer", 1),
                ("MATCH ANY SHORTEST (:ProcedurePerson)-[:procedureLink]->(segment=()->() WHERE VALUE { CALL (segment) { RETURN PATH_LENGTH(segment) AS hops } RETURN hops }=1) RETURN PATH_LENGTH(segment) AS answer", 1),
                ("MATCH (:ProcedurePerson)-[:procedureLink]->(outer=(inner=()->() WHERE inner=outer)) RETURN PATH_LENGTH(outer) AS answer", 1),
                ("MATCH ANY SHORTEST (:ProcedurePerson)-[:procedureLink]->(segment=()->() WHERE PATH_LENGTH(segment)=2) RETURN COUNT(*) AS answer", 0),
                ("MATCH (segments=(inner=(:ProcedurePerson)-[:procedureLink]->())){1,20} RETURN CASE WHEN segments=inner THEN CARDINALITY(inner) ELSE 0 END AS answer", 1),
                ("MATCH (segments=(:ProcedurePerson)-[:procedureLink]->(tail=()-[:procedureTail]->())){1,20} FOR p IN tail RETURN PATH_LENGTH(p) AS answer", 1),
                ("MATCH REPEATABLE ELEMENTS (segments=(inner=TRAIL (:ProcedurePerson)-[:procedureLink]->()<-[:procedureLink]-())){1} RETURN COUNT(*) AS answer", 0),
                ("MATCH (segments=(inner=(:ProcedurePerson)-[:procedureLink]->() WHERE VALUE { CALL (inner) { RETURN PATH_LENGTH(inner) AS hops } RETURN hops }=1)){1,20} RETURN CARDINALITY(inner) AS answer", 1),
                ("CALL increment(4) RETURN answer", 5),
                ("MATCH (segment=(a:ProcedurePerson)-[:procedureLink]->(b)) RETURN PATH_LENGTH(segment) AS answer", 1),
                ("MATCH ANY SHORTEST (segment=(a:ProcedurePerson)-[:procedureLink]->(b) WHERE a.age=37) RETURN PATH_LENGTH(segment) AS answer", 1),
                ("MATCH ANY SHORTEST (segment=(a:ProcedurePerson)-[:procedureLink]->(b) WHERE PATH_LENGTH(segment)=1) RETURN PATH_LENGTH(segment) AS answer", 1),
                ("OPTIONAL MATCH ANY SHORTEST (segment=(a:ProcedurePerson)-[:procedureLink]->(b) WHERE PATH_LENGTH(segment)>1) RETURN COUNT(segment) AS answer", 0),
                ("RETURN CASE WHEN UPPER('Straße')='STRASSE' AND LOWER('ΟΣ')='ος' AND LOWER('İ')='i\\u0307' THEN 1 ELSE 0 END AS answer", 1),
                ("RETURN CASE WHEN RIGHT('a😀b',9223372036854775807)='a😀b' THEN 1 ELSE 0 END AS answer", 1),
                ("FOR n IN [1,2,2] RETURN n NEXT RETURN SUM(n) AS total UNION ALL RETURN COUNT(*) AS total NEXT RETURN SUM(total) AS answer", 8),
                ("RETURN 1 AS n UNION ALL RETURN 1 AS n NEXT RETURN n AS answer UNION RETURN 2 AS answer NEXT RETURN COUNT(*) AS answer", 2),
                ("FOR n IN [] RETURN n NEXT RETURN COUNT(*) AS total UNION ALL RETURN COUNT(n) AS total NEXT RETURN COUNT(*) AS answer", 2),
                ("RETURN " + String.Join(',', Enumerable.Range(0, 32).Select(i => $"{i} AS n{i}")) +
                    " NEXT RETURN * UNION ALL RETURN * NEXT RETURN COUNT(*) AS answer", 2),
                ("RETURN " + String.Join(',', Enumerable.Range(0, 31).Select(i => $"{i} AS n{i}")) +
                    " NEXT CALL { RETURN * UNION ALL RETURN * NEXT RETURN COUNT(*) AS answer } RETURN answer", 2),
                ("CALL tools.increment(4) RETURN answer", 5),
                ("CALL \"tools\".`increment`(4) RETURN answer", 5),
                ("CALL \"tools.increment\"(4) RETURN answer", 24),
                ("CALL `quoted dependency`(4) RETURN answer", 5),
                ("CALL () { RETURN 1 AS answer UNION ALL RETURN 1 AS answer } RETURN answer UNION RETURN 2 AS answer NEXT RETURN SUM(answer) AS answer", 3),
                ("RETURN (RECORD {`answer value`:42}).`answer value` AS answer", 42),
                ("RETURN CASE WHEN DATE({`year`:2020,`month`:2,`day`:29})=DATE('2020-02-29') THEN 1 ELSE 0 END AS answer", 1),
                ("LET answer=100 CALL increment(4) YIELD answer AS other RETURN answer+other AS answer", 105),
                ("CALL lookupPerson('Ada') YIELD age AS answer RETURN answer", 37),
                ("CALL personAge('Ada') RETURN answer", 37),
                ("CALL quotedPerson('Ada') YIELD `display age` AS answer RETURN answer", 37),
                ("OPTIONAL CALL lookupPerson('Missing') RETURN CASE WHEN COUNT(*)=1 AND COUNT(person)=0 THEN 1 ELSE 0 END AS answer", 1)
            })
            {
                var request = new PartitionQueryRequest(candidate.Generation, candidate.Profile, "gql", query);
                var result = await candidate.QueryAsync(request, default);
                Assert.AreEqual(Canonical((await control.QueryAsync(request, default)).Value), Canonical(result.Value));
                Assert.AreEqual(expected, result.Value.GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetInt32(), query);
                Assert.IsNotNull(candidate.ExplainQuery(request));
            }
            Guid operation = EntityOperationIds.New();
            const string update = "CALL quotedPerson('Ada') YIELD `person node` AS person SET person.checked=TRUE RETURN person.age AS answer";
            var first = await remote.ExecuteGqlUpdateWithResultAsync(operation, update);
            var replay = await remote.ExecuteGqlUpdateWithResultAsync(operation, update);
            Assert.IsNotNull(first.Result);
            Assert.AreEqual(1, first.Result.Value.GetProperty("diagnostics").EnumerateArray()
                .Count(item => item.GetProperty("code").GetString() == "00000"));
            Assert.AreEqual(first.Result.Value.GetRawText(), replay.Result?.GetRawText());
            foreach (string omittedUpdate in new[]
            {
                "INSERT (:OmittedDiagnostic)",
                "MATCH (n:MissingOmittedDiagnostic) SET n.amount=1",
                "FOR x IN [NULL,1] RETURN SUM(x) AS amount NEXT INSERT (:OmittedDiagnostic {amount:amount})"
            })
            {
                Guid omittedOperation = EntityOperationIds.New();
                var omitted = await remote.ExecuteGqlUpdateWithResultAsync(omittedOperation, omittedUpdate);
                var omittedReplay = await remote.ExecuteGqlUpdateWithResultAsync(omittedOperation, omittedUpdate);
                Assert.IsNotNull(omitted.Result);
                Assert.AreEqual(0, omitted.Result.Value.GetProperty("matches").GetArrayLength());
                var codes = omitted.Result.Value.GetProperty("diagnostics").EnumerateArray()
                    .Select(item => item.GetProperty("code").GetString()).ToArray();
                Assert.IsTrue(codes.Contains("00001"));
                Assert.AreEqual(omittedUpdate.StartsWith("FOR", StringComparison.Ordinal), codes.Contains("01G11"));
                Assert.AreEqual(omitted.Result.Value.GetRawText(), omittedReplay.Result?.GetRawText());
            }
            var checkedResult = await candidate.QueryAsync(new(candidate.Generation, candidate.Profile, "gql",
                "CALL lookupPerson('Ada') YIELD person RETURN person.checked AS answer"), default);
            Assert.IsTrue(checkedResult.Value.GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetBoolean());
        }, gqlProcedures: catalog);
    }
}
