using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    public async Task Gql_delimited_mutation_parameters_preserve_data_keys_and_replay()
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        const string value = "'); MATCH (n) DELETE n; ('";
        var request = new
        {
            statement = "INSERT (n:QuotedParameter {name:$`display name`,age:$1,note:$`a//b`}) RETURN n.name AS answer",
            parameters = new Dictionary<string, object> { ["display name"] = value, ["1"] = 7, ["a//b"] = "literal $1" },
            transactionId = Guid.NewGuid()
        };
        string? receipt = null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/update", request);
            string body = await response.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, body);
            using var result = JsonDocument.Parse(body);
            Assert.AreEqual(value, result.RootElement.GetProperty("result").GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetString());
            if (receipt is not null)
            {
                Assert.AreEqual(receipt, body);
            }
            receipt = body;
        }
        using var rejected = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (:QuotedParameter {name:$\"display name\"})",
            parameters = new Dictionary<string, object> { ["Display name"] = "wrong case" }
        });
        Assert.AreEqual(HttpStatusCode.BadRequest, rejected.StatusCode);
        using var stored = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (n:QuotedParameter) RETURN n.age AS age,n.note AS note"
        });
        Assert.AreEqual(HttpStatusCode.OK, stored.StatusCode);
        var rows = (await stored.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
        Assert.AreEqual(1, rows.GetArrayLength());
        Assert.AreEqual(7, rows[0].GetProperty("bindings").GetProperty("age").GetInt32());
        Assert.AreEqual("literal $1", rows[0].GetProperty("bindings").GetProperty("note").GetString());
    }

    [TestMethod]
    [DataRow("TRUE", "true")]
    [DataRow("FALSE", "false")]
    [DataRow("TrUe", "true")]
    [DataRow("FaLsE", "false")]
    [DataRow("UNKNOWN", "null")]
    [DataRow("UnKnOwN", "null")]
    [DataRow("NULL", "null")]
    public async Task Gql_standard_insert_truth_literals_are_case_insensitive(string literal, string expected)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var inserted = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (a:TruthLiteral {flag:" + literal + "})-[e:truthEdge {flag:" + literal + "}]->() RETURN a.flag AS nodeFlag,e.flag AS edgeFlag"
        });
        Assert.AreEqual(HttpStatusCode.OK, inserted.StatusCode, await inserted.Content.ReadAsStringAsync());
        var bindings = (await inserted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result").GetProperty("matches")[0].GetProperty("bindings");
        Assert.AreEqual(expected, bindings.GetProperty("nodeFlag").GetRawText());
        Assert.AreEqual(expected, bindings.GetProperty("edgeFlag").GetRawText());
        using var duplicate = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (:TruthLiteral {flag:UNKNOWN,flag:TRUE})"
        });
        Assert.AreEqual(HttpStatusCode.BadRequest, duplicate.StatusCode);
        using var query = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (n:TruthLiteral) RETURN COUNT(*) AS answer"
        });
        Assert.AreEqual(HttpStatusCode.OK, query.StatusCode);
        Assert.AreEqual(1, (await query.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetInt32());
        await host.StopAsync();
    }

    [TestMethod]
    public async Task Gql_mutation_parameters_preserve_values_and_reject_missing_values_atomically()
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        const string name = "'); MATCH (n) DELETE n; ('";
        using var inserted = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (n:Person { id: $id, name: $name, age: $age })",
            parameters = new
            {
                id = Guid.NewGuid().ToString(),
                name,
                age = 18
            }
        });
        Assert.AreEqual(HttpStatusCode.OK, inserted.StatusCode, await inserted.Content.ReadAsStringAsync());
        using var selected = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (n:Person) WHERE n.name = $name RETURN n",
            parameters = new
            {
                name
            }
        });
        Assert.AreEqual(1, (await selected.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches").GetArrayLength());
        using var rejected = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "MATCH (n:Person) SET n.age = $age, n.name = $missing",
            parameters = new
            {
                age = 99
            }
        });
        Assert.AreEqual(HttpStatusCode.BadRequest, rejected.StatusCode);
        using var unchanged = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (n:Person) WHERE n.age = 18 RETURN n"
        });
        Assert.AreEqual(1, (await unchanged.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches").GetArrayLength());
        foreach (object? invalid in new object?[] { new object?[] { 20, null }, new { value = 20 } })
        {
            using var invalidRequest = await client.PostAsJsonAsync("/v1/graph/gql/update", new
            {
                statement = "MATCH (n:Person) SET n.age = $age",
                parameters = new
                {
                    age = invalid
                }
            });
            Assert.AreEqual(HttpStatusCode.BadRequest, invalidRequest.StatusCode);
        }
        var excessive = Enumerable.Range(0, 33).ToDictionary(index => "p" + index, index => index);
        using var excessiveRequest = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "MATCH (n:Person) SET n.age = $p0",
            parameters = excessive
        });
        Assert.AreEqual(HttpStatusCode.BadRequest, excessiveRequest.StatusCode);
        using var stillUnchanged = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (n:Person) WHERE n.age = 18 RETURN n"
        });
        Assert.AreEqual(1, (await stillUnchanged.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches").GetArrayLength());
        using var updated = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "MATCH (n:Person) WHERE n.name = $name SET n.age = $age",
            parameters = new
            {
                name,
                age = 19
            }
        });
        Assert.AreEqual(HttpStatusCode.OK, updated.StatusCode, await updated.Content.ReadAsStringAsync());
        using var deleted = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "MATCH (n:Person) WHERE n.age = $age DELETE n",
            parameters = new
            {
                age = 19
            }
        });
        Assert.AreEqual(HttpStatusCode.OK, deleted.StatusCode);
        using var empty = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (n:Person) RETURN n"
        });
        Assert.AreEqual(0, (await empty.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches").GetArrayLength());
    }

    [TestMethod]
    public async Task Gql_null_parameters_are_values_and_set_removes_properties()
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var inserted = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = $"INSERT (n:Person {{ id: '{Guid.NewGuid()}', age: 18 }})"
        });
        Assert.AreEqual(HttpStatusCode.OK, inserted.StatusCode, await inserted.Content.ReadAsStringAsync());
        var parameters = new Dictionary<string, object?> { ["value"] = null };
        foreach (var (expression, expected) in new[]
        {
            ("$value", "null"), ("$value IS NULL", "true"),
            ("COALESCE($value, n.age)", "18"), ("n.age = $value", "null"),
            ("'$value'", "\"$value\"")
        })
        {
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = $"MATCH (n:Person) RETURN {expression} AS answer",
                parameters
            });
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            var result = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.AreEqual(expected, result.GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetRawText());
        }
        using var missing = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "MATCH (n:Person) SET n.age = $value, n.other = $missing",
            parameters
        });
        Assert.AreEqual(HttpStatusCode.BadRequest, missing.StatusCode);
        using var unchanged = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (n:Person) WHERE n.age=18 RETURN n"
        });
        Assert.AreEqual(1, (await unchanged.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches").GetArrayLength());
        using var removed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "MATCH (n:Person) SET n.age = $value",
            parameters
        });
        Assert.AreEqual(HttpStatusCode.OK, removed.StatusCode, await removed.Content.ReadAsStringAsync());
        using var selected = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (n:Person) WHERE $value IS NULL AND n.age IS NULL RETURN n",
            parameters
        });
        Assert.AreEqual(HttpStatusCode.OK, selected.StatusCode, await selected.Content.ReadAsStringAsync());
        Assert.AreEqual(1, (await selected.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches").GetArrayLength());
    }

    [TestMethod]
    public async Task Gql_insert_null_properties_are_absent_and_duplicate_properties_are_rejected_atomically()
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        var parameters = new Dictionary<string, object?> { ["value"] = null };
        using var inserted = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = $"INSERT (a:Person {{id:'{Guid.NewGuid()}', age:$value}}), " +
                $"(b:Person {{id:'{Guid.NewGuid()}', age:NULL}}), " +
                $"(a)-[r:knows {{id:'{Guid.NewGuid()}', weight:$value}}]->(b)",
            parameters
        });
        Assert.AreEqual(HttpStatusCode.OK, inserted.StatusCode, await inserted.Content.ReadAsStringAsync());
        using var selected = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (a:Person)-[r:knows]->(b:Person) WHERE a.age IS NULL AND b.age IS NULL AND r.weight IS NULL RETURN a"
        });
        Assert.AreEqual(HttpStatusCode.OK, selected.StatusCode, await selected.Content.ReadAsStringAsync());
        Assert.AreEqual(1, (await selected.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches").GetArrayLength());
        foreach (string properties in new[] { "id:NULL, id:1", $"id:'{Guid.NewGuid()}', age:NULL, age:1" })
        {
            using var rejected = await client.PostAsJsonAsync("/v1/graph/gql/update", new
            {
                statement = $"INSERT (a:Rejected {{id:'{Guid.NewGuid()}'}}), (b:Rejected {{{properties}}})",
                parameters
            });
            Assert.AreEqual(HttpStatusCode.BadRequest, rejected.StatusCode, await rejected.Content.ReadAsStringAsync());
        }
        using var unchanged = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (n:Rejected) RETURN n"
        });
        Assert.AreEqual(0, (await unchanged.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches").GetArrayLength());
    }
}
