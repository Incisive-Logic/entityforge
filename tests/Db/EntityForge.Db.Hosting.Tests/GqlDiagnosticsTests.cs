using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("ANY $take", "{1,2}", 1, 2)]
    [DataRow("SHORTEST $take", "{1,2}", 1, 2)]
    [DataRow("SHORTEST $take GROUPS", "{1,2}", 2, 3)]
    [DataRow("ANY $take", "+", 1, 2)]
    [DataRow("SHORTEST $take", "+", 1, 2)]
    [DataRow("SHORTEST $take GROUPS", "+", 2, 3)]
    public async Task Gql_repeated_path_count_parameters_control_selection_and_update_replay(string selector, string quantifier, int one, int two)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (a:CountStart)-[:CountEdge]->(b:CountEnd),(a)-[:CountEdge]->(c)-[:CountEdge]->(b),(a)-[:CountEdge]->(b)"
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode, await seed.Content.ReadAsStringAsync());
        string pattern = "MATCH " + selector + " (a:CountStart)((x)-[e:CountEdge]->(y))" + quantifier + "(b:CountEnd) ";
        foreach (var (take, expected) in new[] { (0, 0), (1, one), (2, two) })
        {
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = pattern + "RETURN COUNT(*)+$offset AS total",
                parameters = new
                {
                    take,
                    offset = 10
                }
            });
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            Assert.AreEqual(expected + 10, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("total").GetInt32());
        }
        var update = new
        {
            statement = pattern + "INSERT (:CountSelection) RETURN COUNT(*) AS total",
            parameters = new
            {
                take = 2
            },
            transactionId = Guid.NewGuid()
        };
        string? receipt = null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/update", update);
            string body = await response.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, body);
            using var result = JsonDocument.Parse(body);
            Assert.AreEqual(two, result.RootElement.GetProperty("result").GetProperty("matches")[0].GetProperty("bindings").GetProperty("total").GetInt32());
            if (receipt is not null)
            {
                Assert.AreEqual(receipt, body);
            }
            receipt = body;
        }
        using var stored = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (:CountSelection) RETURN COUNT(*) AS total"
        });
        Assert.AreEqual(HttpStatusCode.OK, stored.StatusCode);
        Assert.AreEqual(two, (await stored.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("total").GetInt32());
    }

    [TestMethod]
    [DataRow("ANY $take (a)-[e]->(b)")]
    [DataRow("SHORTEST $take (a)-[e]->(b)")]
    [DataRow("SHORTEST $take GROUPS (a)-[e]->(b)")]
    [DataRow("ANY /* count */ $take (a)-[e]->(b)")]
    [DataRow("ANY $\"take\" (a)-[e]->(b)")]
    [DataRow("SHORTEST $`take` GROUPS (a)-[e]->(b)")]
    [DataRow("ANY $take (a)((x)-[e]->(y)){1,2}(b)")]
    [DataRow("SHORTEST $take GROUPS (a)((x)-[e]->(y)){1,2}(b)")]
    public async Task Gql_negative_path_count_parameters_report_data_exception(string pattern)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        string source = "MATCH " + pattern + " RETURN COUNT(*) AS total";
        foreach (string route in new[] { "query", "explain", "update" })
        {
            object request = route switch
            {
                "explain" => new { query = new { query = source, parameters = new { take = -1 } }, analyze = true },
                "update" => new { statement = "INSERT (:InvalidPathCount) " + source, parameters = new { take = -1 } },
                _ => new { query = source, parameters = new { take = -1 } }
            };
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/" + route, request);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, await response.Content.ReadAsStringAsync());
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.IsTrue(problem.TryGetProperty("gqlStatus", out var status), problem.GetRawText());
            Assert.AreEqual("22G0F", status.GetString(), route);
        }
        foreach (string literal in new[] { "0", "-0", "1" })
        {
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = source,
                parameters = new
                {
                    take = JsonSerializer.Deserialize<JsonElement>(literal)
                }
            });
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        }
        using var stored = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (:InvalidPathCount) RETURN COUNT(*) AS total"
        });
        Assert.AreEqual(HttpStatusCode.OK, stored.StatusCode);
        Assert.AreEqual(0, (await stored.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("total").GetInt32());
        using var ordinary = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "RETURN 'ANY $take SHORTEST $take' AS text, $take AS amount",
            parameters = new
            {
                take = -1
            }
        });
        Assert.AreEqual(HttpStatusCode.OK, ordinary.StatusCode, await ordinary.Content.ReadAsStringAsync());
        Assert.AreEqual(-1, (await ordinary.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("amount").GetInt32());
    }

    [TestMethod]
    public async Task Gql_negative_limit_parameters_report_data_exception_without_mutation()
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        foreach (string source in new[] { "RETURN 1 AS answer LIMIT $take", "RETURN 1 AS answer LIMIT $\"take\"", "RETURN 1 AS answer LIMIT $`take`", "FOR x IN [1,2] RETURN COUNT(*) AS answer LIMIT $take", "CALL () { RETURN 1 AS answer LIMIT $take } RETURN answer" })
        {
            foreach (string route in new[] { "query", "explain", "update" })
            {
                object request = route switch
                {
                    "explain" => new { query = new { query = source, parameters = new { take = -1 } }, analyze = true },
                    "update" => new { statement = "INSERT (:InvalidPage) " + source, parameters = new { take = -1 } },
                    _ => new { query = source, parameters = new { take = -1 } }
                };
                using var response = await client.PostAsJsonAsync("/v1/graph/gql/" + route, request);
                Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, await response.Content.ReadAsStringAsync());
                var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
                Assert.IsTrue(problem.TryGetProperty("gqlStatus", out var status), route + ": " + problem.GetRawText());
                Assert.AreEqual("22G02", status.GetString(), source);
            }
        }
        using var stored = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (:InvalidPage) RETURN COUNT(*) AS total"
        });
        Assert.AreEqual(HttpStatusCode.OK, stored.StatusCode);
        Assert.AreEqual(0, (await stored.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("total").GetInt32());
        foreach (string literal in new[] { "0", "-0", "1" })
        {
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = "RETURN 1 AS answer LIMIT $take",
                parameters = new
                {
                    take = JsonSerializer.Deserialize<JsonElement>(literal)
                }
            });
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            Assert.AreEqual(literal == "1" ? 1 : 0, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches").GetArrayLength());
        }
        using var ordinary = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "RETURN 'LIMIT $take' AS text, $take AS amount",
            parameters = new
            {
                take = -1
            }
        });
        Assert.AreEqual(HttpStatusCode.OK, ordinary.StatusCode, await ordinary.Content.ReadAsStringAsync());
        Assert.AreEqual(-1, (await ordinary.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("amount").GetInt32());
        await host.StopAsync();
    }

    [TestMethod]
    public async Task Gql_domain_errors_report_corrigendum_conditions_and_abort_updates()
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        foreach (var (expression, status) in new[]
        {
            ("ACOS(2)", "2202M"), ("ACOS(-1.0001)", "2202M"),
            ("ASIN(1.0001)", "2202M"), ("ASIN(-2)", "2202M"),
            ("ASIN(1.00000000000000000001)", "2202M"),
            ("ACOS(-1.00000000000000000001)", "2202M"),
            ("LOG(1,8)", "2201Z"), ("LOG(0,8)", "2201Z"),
            ("LOG(-2,8)", "2201Z"), ("LOG(2,0)", "2201Z"), ("LOG(2,-1)", "2201Z"),
            ("LN(0)", "2201E"), ("LN(-1)", "2201E"),
            ("LEFT('abc',-1)", "22011"), ("RIGHT('😀',-9223372036854775808)", "22011"),
            ("1/0", "22012"), ("1.0M/0.0M", "22012"),
            ("MOD(1,0)", "22012"), ("MOD(1.0M,0.0M)", "22012"),
            ("DURATION('P1D')/0", "22012"), ("DURATION('P1M')/0.0M", "22012"),
            ("9223372036854775807+1", "22003"), ("-9223372036854775808-1", "22003"),
            ("9223372036854775807*2", "22003"), ("-(-9223372036854775808)", "22003"),
            ("ABS(-9223372036854775808)", "22003"),
            ("100000000000000000000M*100000000000000000000M", "22003"),
            ("TRIM('' FROM 'text')", "22027"), ("TRIM('ab' FROM 'text')", "22027"),
            ("TRIM(LEADING '😀😀' FROM '😀text')", "22027"),
            ("DATE({`year`:2020,unexpected:1})", "22G05"),
            ("DATE({`year`:2020,`month`:1,week:1})", "22G05"),
            ("DATE({`year`:2020,`day`:1})", "22G05"),
            ("LOCAL_TIME({`hour`:12,`second`:1})", "22G05"),
            ("LOCAL_TIME({`minute`:1})", "22G05"),
            ("ZONED_TIME({`hour`:12,`second`:1,timezone:'Z'})", "22G05"),
            ("LOCAL_DATETIME({`year`:2020,`hour`:12})", "22G05"),
            ("ZONED_DATETIME({`year`:2020,`hour`:12,timezone:'Z'})", "22G05"),
            ("LOCAL_DATETIME({`datetime`:LOCAL_DATETIME('2020-01-01T12:00:00'),`date`:DATE('2020-01-01')})", "22G05"),
            ("DURATION({days:1,unexpected:1})", "22G07"),
            ("DURATION({`years`:1,`unexpected field`:1})", "22G07"),
            ("LOCAL_TIME({`hour`:12,unexpected:1})", "22G05"),
            ("LOCAL_DATETIME({`year`:2020,`month`:1,`day`:1,`hour`:12,unexpected:1})", "22G05"),
            ("ZONED_TIME({`hour`:12,timezone:'Z',unexpected:1})", "22G05"),
            ("ZONED_DATETIME({`year`:2020,`month`:1,`day`:1,`hour`:12,timezone:'Z',unexpected:1})", "22G05"),
            ("TRIM([1,2],-1)", "2202E"), ("TRIM([1,2],3)", "2202E"), ("TRIM([],1)", "2202E")
        })
        {
            foreach (bool update in new[] { false, true })
            {
                object request = update
                    ? new
                    {
                        statement = "INSERT (:DomainProbe) LET invalid=" + expression + " RETURN invalid"
                    }
                    : new
                    {
                        query = "RETURN " + expression + " AS answer"
                    };
                using var response = await client.PostAsJsonAsync("/v1/graph/gql/" + (update ? "update" : "query"), request);
                Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, expression + ": " + await response.Content.ReadAsStringAsync());
                var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
                Assert.IsTrue(problem.TryGetProperty("gqlStatus", out var condition), expression + ": " + problem.GetRawText());
                Assert.AreEqual(status, condition.GetString(), expression);
            }
        }
        using var unchanged = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (:DomainProbe) RETURN COUNT(*) AS total"
        });
        Assert.AreEqual(HttpStatusCode.OK, unchanged.StatusCode);
        Assert.AreEqual(0, (await unchanged.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("total").GetInt32());
        await host.StopAsync();
    }

    [TestMethod]
    public async Task Gql_invalid_syntax_reports_standard_condition_without_misclassifying_resource_limits()
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        foreach (string source in new[]
        {
            "CREATE (:Person)", "RETURN @ AS answer", "RETURN 1<2<3 AS answer",
            "RETURN 1", "RETURN TRUE", "RETURN NULL", "RETURN $x", "RETURN SESSION_USER",
            "RETURN COUNT(*)", "RETURN 1+2", "LET n=1 RETURN (n)",
            "LET n=RECORD {amount:1} RETURN n.amount",
            "RETURN VALUE { RETURN 1 } AS answer", "CALL () { RETURN 1 } RETURN *",
            "INSERT (:RejectedAlias) RETURN 1", "RETURN 1 AS n NEXT RETURN n+1",
            "RETURN 1 AS answer UNION ALL RETURN 1 AS answer UNION RETURN 2 AS answer",
            "INSERT (:RejectedUnion) RETURN 1 AS answer NEXT RETURN answer UNION ALL RETURN answer UNION RETURN answer",
            "RETURN __GQL_PATH_JOIN('WALK',TRUE,NULL,NULL) AS answer",
            "RETURN __GQL_PATHS_DISTINCT(NULL,NULL) AS answer"
        })
        {
            foreach (string route in new[] { "query", "update", "explain" })
            {
                object request = route switch
                {
                    "update" => new { statement = source },
                    "explain" => new { query = new { query = source }, analyze = false },
                    _ => new { query = source }
                };
                using var response = await client.PostAsJsonAsync("/v1/graph/gql/" + route, request);
                Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, await response.Content.ReadAsStringAsync());
                var error = await response.Content.ReadFromJsonAsync<JsonElement>();
                Assert.AreEqual("42001", error.GetProperty("gqlStatus").GetString(), route + ": " + source);
                Assert.AreEqual("request_invalid", error.GetProperty("code").GetString());
            }
        }
        using var unchanged = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (:RejectedAlias) RETURN COUNT(*) AS total"
        });
        Assert.AreEqual(HttpStatusCode.OK, unchanged.StatusCode);
        Assert.AreEqual(0, (await unchanged.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("total").GetInt32());
        using var limited = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "RETURN 1 AS answer" + new string(' ', 16_384)
        });
        Assert.AreEqual(HttpStatusCode.BadRequest, limited.StatusCode);
        Assert.IsFalse((await limited.Content.ReadFromJsonAsync<JsonElement>()).TryGetProperty("gqlStatus", out _));
        await host.StopAsync();
    }
}
