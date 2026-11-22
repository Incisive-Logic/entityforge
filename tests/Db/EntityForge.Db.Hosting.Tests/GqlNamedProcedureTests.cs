using EntityForge.Db.Gql;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    public void Gql_catalog_names_respect_procedure_and_parameter_contexts()
    {
        foreach (string name in new[] { "tools.RECORD", "tools..increment", ".increment", "tools/increment", "tools.$increment", "tools.increment ", "increment() CALL other", "$procedure" })
        {
            Assert.ThrowsExactly<ArgumentException>(() => new GqlReadProcedure(name, [], "RETURN 1 AS answer"));
            Assert.ThrowsExactly<ArgumentException>(() => new GqlWriteProcedure(name, [], "INSERT (:Probe) RETURN 1 AS answer"));
        }
        Assert.ThrowsExactly<ArgumentException>(() => new GqlReadProcedure("tools.increment", ["tools.input"], "RETURN 1 AS answer"));
        Assert.ThrowsExactly<ArgumentException>(() => new GqlWriteProcedure("tools.increment", ["tools.input"], "INSERT (:Probe) RETURN 1 AS answer"));
        Assert.ThrowsExactly<ArgumentException>(() => new GqlProcedureCatalog([
            new("tools.increment", [], "RETURN 1 AS answer"),
            new("\"tools\".`increment`", [], "RETURN 2 AS answer")]));
        Assert.ThrowsExactly<ArgumentException>(() => new GqlProcedureCatalog([
            new("`recursive name`", [], "CALL \"recursive name\"() RETURN answer")]));
    }

    internal static GqlProcedureCatalog TestReadProcedures() => new([
        new("personAge", ["requestedName"], "CALL lookupPerson(requestedName) YIELD person,age RETURN age AS answer,person"),
        new("quotedPerson", ["requestedName"], "CALL lookupPerson(requestedName) YIELD person,age RETURN person AS `person node`,age AS `display age`"),
        new("increment", ["inputNumber"], "RETURN inputNumber+1 AS answer"),
        new("tools.increment", ["inputNumber"], "RETURN inputNumber+1 AS answer"),
        new("other.increment", ["inputNumber"], "RETURN inputNumber+10 AS answer"),
        new("tools.twice", ["inputNumber"], "CALL tools.increment(inputNumber) YIELD answer AS firstAnswer CALL tools.increment(firstAnswer) RETURN answer"),
        new("`tools.increment`", ["inputNumber"], "RETURN inputNumber+20 AS answer"),
        new("\"special tools\".\"RECORD\"", ["inputNumber"], "RETURN inputNumber+30 AS answer"),
        new("`quote``name`", [], "RETURN 41 AS answer"),
        new("\"quote\"\"name\"", [], "RETURN 42 AS answer"),
        new("\"quoted dependency\"", ["inputNumber"], "CALL `special tools`.`RECORD`(inputNumber) RETURN answer"),
        new("`工具😀`", [], "RETURN 43 AS answer"),
        new("café", ["变量"], "RETURN 变量+1 AS résultat"),
        new("lookupPerson", ["requestedName"], "MATCH (person:ProcedurePerson {name:requestedName}) RETURN person,person.age AS age"),
        new("constantAnswer", [], "/* RETURN * in a comment is not a wildcard projection. */ RETURN 42 AS answer"),
        new("unpack", ["items"], "FOR item IN items RETURN item AS answer")]);

    [TestMethod]
    public async Task Gql_standard_named_read_procedures_preserve_scopes_aliases_and_update_replay()
    {
        _directory = NewDirectory();
        var catalog = TestReadProcedures();
        var seed = new
        {
            statement = "INSERT (:ProcedurePerson {name:'Ada',age:37})",
            transactionId = Guid.NewGuid()
        };
        var update = new
        {
            statement = "CALL quotedPerson('Ada') YIELD `person node` AS person SET person.checked=TRUE RETURN person.age AS answer",
            transactionId = Guid.NewGuid()
        };
        string? receipt = null;
        for (int restart = 0; restart < 2; restart++)
        {
            await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
                .UseGqlProcedures(catalog).ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
            await host.StartAsync();
            using var client = Client(host);
            using var seeded = await client.PostAsJsonAsync("/v1/graph/gql/update", seed);
            Assert.AreEqual(HttpStatusCode.OK, seeded.StatusCode, await seeded.Content.ReadAsStringAsync());
            foreach (var (query, expected) in new[]
            {
                ("CALL increment(4) RETURN answer", 5),
                ("CALL tools.increment(4) RETURN answer", 5),
                ("CALL \"tools\".`increment`(4) RETURN answer", 5),
                ("CALL \"tools.increment\"(4) RETURN answer", 24),
                ("CALL `special tools`.\"RECORD\"(4) RETURN answer", 34),
                ("CALL `quoted dependency`(4) RETURN answer", 34),
                ("CALL \"quote`name\"() RETURN answer", 41),
                ("CALL `quote\"name`() RETURN answer", 42),
                ("CALL \"\\u0074ools\".increment(4) RETURN answer", 5),
                ("CALL \"工具😀\"() RETURN answer", 43),
                ("CALL tools . increment(4) YIELD answer AS result RETURN result AS answer", 5),
                ("CALL other.increment(4) RETURN answer", 14),
                ("CALL tools.twice(4) RETURN answer", 6),
                ("CALL increment(4) YIELD \"answer\" AS result RETURN result AS answer", 5),
                ("CALL increment(4) YIELD `answer` AS result RETURN result AS answer", 5),
                ("CALL café(4) YIELD résultat AS answer RETURN answer", 5),
                ("CALL constantAnswer() RETURN answer", 42),
                ("FOR inputNumber IN [1,2,3] CALL increment(inputNumber) RETURN SUM(answer) AS answer", 9),
                ("LET answer=100 CALL increment(4) YIELD answer AS other RETURN answer+other AS answer", 105),
                ("LET inputNumber=10 CALL increment(inputNumber+1) RETURN answer", 12),
                ("CALL lookupPerson('Ada') YIELD age AS answer RETURN answer", 37),
                ("CALL personAge('Ada') RETURN answer", 37),
                ("CALL quotedPerson('Ada') YIELD `display age` AS answer RETURN answer", 37),
                ("CALL quotedPerson('Ada') YIELD \"display age\" AS answer RETURN answer", 37),
                ("LET requestedName='Ada',answer=100 CALL personAge(requestedName) YIELD answer AS years RETURN answer+years AS answer", 137),
                ("CALL lookupPerson('Missing') RETURN COUNT(*) AS answer", 0),
                ("OPTIONAL CALL lookupPerson('Missing') RETURN CASE WHEN COUNT(*)=1 AND COUNT(person)=0 AND COUNT(age)=0 THEN 1 ELSE 0 END AS answer", 1),
                ("CALL unpack([1,2,3]) RETURN SUM(answer) AS answer", 6),
                ("CALL increment(VALUE { CALL increment(2) RETURN answer }) RETURN answer", 4),
                ("RETURN 4 AS inputNumber NEXT CALL increment(inputNumber) RETURN answer", 5),
                ("CALL increment(1) RETURN answer UNION ALL CALL increment(2) RETURN answer NEXT RETURN SUM(answer) AS answer", 5),
                ("LET __procedure0_0=8 CALL increment(__procedure0_0) RETURN answer", 9)
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
            foreach (string query in new[] { "CALL absent() RETURN *", "CALL Tools.increment(4) RETURN *", "CALL missing.increment(4) RETURN *", "CALL increment() RETURN *", "CALL increment(1,2) RETURN *", "CALL increment(1) YIELD missing RETURN missing", "LET answer=1 CALL increment(2) RETURN answer" })
            {
                using var rejected = await client.PostAsJsonAsync("/v1/graph/gql/query", new
                {
                    query
                });
                Assert.AreEqual(HttpStatusCode.BadRequest, rejected.StatusCode, query + ": " + await rejected.Content.ReadAsStringAsync());
            }
            using var visible = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = "CALL increment(4) RETURN *"
            });
            Assert.AreEqual(HttpStatusCode.OK, visible.StatusCode, await visible.Content.ReadAsStringAsync());
            CollectionAssert.AreEqual(new[] { "answer" }, (await visible.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").EnumerateObject().Select(item => item.Name).ToArray());
            using var parameterized = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = "CALL increment($inputNumber) RETURN answer",
                parameters = new
                {
                    inputNumber = 9
                }
            });
            Assert.AreEqual(HttpStatusCode.OK, parameterized.StatusCode, await parameterized.Content.ReadAsStringAsync());
            Assert.AreEqual(10, (await parameterized.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetInt32());
            using var explain = await client.PostAsJsonAsync("/v1/graph/gql/explain", new
            {
                query = new
                {
                    query = "CALL increment(2) RETURN answer"
                }
            });
            Assert.AreEqual(HttpStatusCode.OK, explain.StatusCode, await explain.Content.ReadAsStringAsync());
            using var changed = await client.PostAsJsonAsync("/v1/graph/gql/update", update);
            Assert.AreEqual(HttpStatusCode.OK, changed.StatusCode, await changed.Content.ReadAsStringAsync());
            receipt ??= await changed.Content.ReadAsStringAsync();
            Assert.AreEqual(receipt, await changed.Content.ReadAsStringAsync());
            Assert.AreEqual(37, (await changed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result").GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetInt32());
            await host.StopAsync();
        }
    }

    [TestMethod]
    public async Task Gql_named_procedure_yield_aliases_constrain_selective_path_candidates()
    {
        var catalog = new GqlProcedureCatalog([
            new("pickNode", ["requested"], "MATCH (candidate:CallSelected {name:requested}) RETURN candidate AS `node result`"),
            new("pickEdge", ["requestedWeight"], "MATCH ()-[candidate:callEdge]->() WHERE candidate.weight=requestedWeight RETURN candidate AS `edge result`")]);
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .UseGqlProcedures(catalog).ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (origin:CallStart),(good:CallSelected {name:'good'}),(bad:CallSelected {name:'bad'})," +
                "(origin)-[:callEdge {weight:2}]->(good),(origin)-[:callEdge {weight:1}]->(bad)"
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode, await seed.Content.ReadAsStringAsync());
        foreach (var (call, expected) in new[]
        {
            ("CALL pickNode('good') YIELD `node result` AS m", 1),
            ("CALL pickNode('bad') YIELD `node result` AS m", 0),
            ("CALL pickEdge(2) YIELD `edge result` AS e", 1),
            ("CALL pickEdge(1) YIELD `edge result` AS e", 0)
        })
        {
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = call + " MATCH ANY (a:CallStart)-[e:callEdge WHERE e.weight>1]->(m) RETURN COUNT(*) AS answer"
            });
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, call + ": " + await response.Content.ReadAsStringAsync());
            var rows = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
            Assert.AreEqual(1, rows.GetArrayLength());
            Assert.AreEqual(expected, rows[0].GetProperty("bindings").GetProperty("answer").GetInt32(), call);
        }
        await host.StopAsync();
    }

    [TestMethod]
    [DataRow('"')]
    [DataRow('`')]
    public async Task Gql_named_procedure_delimited_result_names_are_exact(char delimiter)
    {
        foreach (string name in new[] { "display name", "RETURN", "a\"`😀" })
        {
            string quoted = delimiter + name.Replace(delimiter.ToString(), new string(delimiter, 2), StringComparison.Ordinal) + delimiter;
            var catalog = new GqlProcedureCatalog([
                new("original", ["inputNumber"], "RETURN inputNumber+1 AS " + quoted),
                new("derived", ["inputNumber"], "CALL original(inputNumber) YIELD " + quoted + " AS result RETURN result AS " + quoted),
                new("emptyResult", [], "FILTER FALSE RETURN 1 AS " + quoted)]);
            _directory = NewDirectory();
            await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
                .UseGqlProcedures(catalog).ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
            await host.StartAsync();
            using var client = Client(host);
            foreach (string query in new[]
            {
                "CALL original(4) RETURN *",
                "CALL original(4) YIELD " + quoted + " RETURN *",
                "CALL derived(4) RETURN *",
                "CALL derived(4) YIELD " + quoted + " AS answer RETURN answer AS " + quoted,
                "CALL original(4) RETURN * NEXT YIELD " + quoted + " AS answer RETURN answer AS " + quoted
            })
            {
                using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
                {
                    query
                });
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, query + ": " + await response.Content.ReadAsStringAsync());
                var rows = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
                Assert.AreEqual(1, rows.GetArrayLength());
                var bindings = rows[0].GetProperty("bindings");
                CollectionAssert.AreEqual(new[] { name }, bindings.EnumerateObject().Select(item => item.Name).ToArray());
                Assert.AreEqual(5, bindings.GetProperty(name).GetInt32());
            }
            using var optional = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = "OPTIONAL CALL emptyResult() RETURN *"
            });
            Assert.AreEqual(HttpStatusCode.OK, optional.StatusCode, await optional.Content.ReadAsStringAsync());
            var optionalRows = (await optional.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
            Assert.AreEqual(1, optionalRows.GetArrayLength());
            Assert.AreEqual(JsonValueKind.Null, optionalRows[0].GetProperty("bindings").GetProperty(name).ValueKind);
            foreach (string query in new[]
            {
                "CALL original(4) YIELD `missing column` AS answer RETURN answer",
                "CALL original(4) YIELD " + quoted + "," + quoted + " RETURN *",
                "LET answer=1 CALL original(4) YIELD " + quoted + " AS answer RETURN answer"
            })
            {
                using var rejected = await client.PostAsJsonAsync("/v1/graph/gql/query", new
                {
                    query
                });
                Assert.AreEqual(HttpStatusCode.BadRequest, rejected.StatusCode, query);
            }
        }
    }

    [TestMethod]
    [DataRow("INSERT (:Forbidden) RETURN 1 AS answer")]
    [DataRow("CALL constantAnswer() RETURN answer")]
    [DataRow("RETURN outside AS answer")]
    [DataRow("LET answer=1 RETURN *")]
    public void Gql_standard_procedure_catalog_rejects_unsafe_or_unscoped_bodies(string query)
        => Assert.ThrowsExactly<ArgumentException>(() => new GqlProcedureCatalog([new("invalidBody", [], query)]));

    [TestMethod]
    public void Gql_standard_procedure_catalog_rejects_recursive_dependencies()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new GqlProcedureCatalog([
            new("firstProcedure", [], "CALL secondProcedure() RETURN answer"),
            new("secondProcedure", [], "CALL firstProcedure() RETURN answer")]));
        Assert.ThrowsExactly<ArgumentException>(() => new GqlProcedureCatalog([
            new("selfProcedure", [], "CALL selfProcedure() RETURN answer")]));
    }
}
