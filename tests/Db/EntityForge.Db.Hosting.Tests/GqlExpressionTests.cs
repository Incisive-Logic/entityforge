using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("n.name STARTS WITH 'Al'", "ERROR")]
    [DataRow("n.name ENDS WITH 'ice'", "ERROR")]
    [DataRow("n.name CONTAINS 'lic'", "ERROR")]
    [DataRow("n.name CONTAINS 'LIC'", "ERROR")]
    [DataRow("n.name CONTAINS ''", "ERROR")]
    [DataRow("NOT n.name STARTS WITH 'Bo' AND n.age=20", "ERROR")]
    [DataRow("n.name STARTS WITH 'A'||'l'", "ERROR")]
    [DataRow("1 CONTAINS '1'", "ERROR")]
    [DataRow("'a' ENDS WITH NULL", "ERROR")]
    [DataRow("'a😀b' CONTAINS '😀'", "ERROR")]
    [DataRow("REPLACE('a😀a','a','b')", "ERROR")]
    [DataRow("REPLACE('abc','','x')", "ERROR")]
    [DataRow("REPLACE('😀','','x')", "ERROR")]
    [DataRow("REPLACE('','','x')", "ERROR")]
    [DataRow("REPLACE('aaa','aa','x')", "ERROR")]
    [DataRow("REPLACE('ab','a','')", "ERROR")]
    [DataRow("REPLACE(NULL,'a','b')", "ERROR")]
    [DataRow("NORMALIZE('é',NFD)", "é")]
    [DataRow("NORMALIZE('①',NFKC)", "1")]
    [DataRow("NORMALIZE('ﬁ',NFKD)", "fi")]
    [DataRow("'é' IS NFD NORMALIZED", "True")]
    [DataRow("'é' IS NOT NFD NORMALIZED", "True")]
    [DataRow("1 IS NORMALIZED", "ERROR")]
    [DataRow("NULL IS NOT NFKC NORMALIZED", "")]
    [DataRow("1 IS TYPED INTEGER", "True")]
    [DataRow("[1,NULL] IS :: LIST<INTEGER>", "True")]
    [DataRow("[1,NULL] IS :: LIST<INTEGER NOT NULL>", "False")]
    [DataRow("[] IS :: LIST<NOTHING>", "True")]
    [DataRow("[NULL] IS :: LIST<NOTHING>", "False")]
    [DataRow("NULL IS :: LIST<INTEGER> NOT NULL", "False")]
    [DataRow("[[1],[],NULL] IS :: LIST<LIST<INTEGER NOT NULL>>", "True")]
    [DataRow("[[1],NULL] IS :: LIST<LIST<INTEGER> NOT NULL>", "False")]
    [DataRow("[1,'a'] IS :: LIST<ANY NOT NULL>", "True")]
    [DataRow("[1,'a'] IS NOT :: LIST<INTEGER>", "True")]
    [DataRow("{a:1} IS :: MAP NOT NULL", "ERROR")]
    [DataRow("n IS :: NODE NOT NULL", "True")]
    [DataRow("n IS :: MAP", "ERROR")]
    [DataRow("n IS :: VERTEX", "True")]
    [DataRow("[n] IS :: LIST<NODE>", "True")]
    [DataRow("[1,'a'] IS :: LIST<INTEGER | STRING>", "True")]
    [DataRow("[1,true] IS :: LIST<INTEGER | STRING>", "False")]
    [DataRow("NULL IS :: INTEGER NOT NULL | STRING NOT NULL", "False")]
    [DataRow("NULL IS :: INTEGER | STRING", "True")]
    [DataRow("[1,'a'] IS :: LIST<INTEGER | STRING> NOT NULL", "True")]
    [DataRow("size([x IN [1,'a'] WHERE x IS :: INTEGER | x])", "ERROR")]
    [DataRow("TRIM(BOTH 'x' FROM 'xxhelloxx')", "hello")]
    [DataRow("TRIM(LEADING 'x' FROM 'xxhelloxx')", "helloxx")]
    [DataRow("TRIM(TRAILING 'x' FROM 'xxhelloxx')", "xxhello")]
    [DataRow("TRIM('😀' FROM '😀a😀')", "a")]
    [DataRow("TRIM(LEADING FROM ' a ')", "a ")]
    [DataRow("TRIM(FROM ' a ')", "a")]
    [DataRow("TRIM(NULL FROM 'a')", "")]
    [DataRow("BTRIM('xxyyhelloxyxy','xy')", "hello")]
    [DataRow("LTRIM('xxyyhelloxyxy','xy')", "helloxyxy")]
    [DataRow("RTRIM('xxyyhelloxyxy','xy')", "xxyyhello")]
    [DataRow("BTRIM('abc','')", "abc")]
    [DataRow("LTRIM('\\tx\\t')", "\tx\t")]
    [DataRow("RTRIM('\\tx\\t')", "\tx\t")]
    [DataRow("BTRIM('\\tx\\t')", "\tx\t")]
    [DataRow("BTRIM('\\u00a0x\\u00a0')", "\u00a0x\u00a0")]
    [DataRow("1 IS :: STRING", "False")]
    [DataRow("NULL IS TYPED INTEGER", "True")]
    [DataRow("NULL IS TYPED INTEGER NOT NULL", "False")]
    [DataRow("NULL IS NOT TYPED INTEGER NOT NULL", "True")]
    [DataRow("true :: BOOL", "ERROR")]
    [DataRow("NULL IS TYPED NULL", "True")]
    [DataRow("1 IS TYPED NULL", "False")]
    [DataRow("NULL IS TYPED NOTHING", "False")]
    [DataRow("n.name IS TYPED VARCHAR NOT NULL", "True")]
    [DataRow("1+2*3", "7")]
    [DataRow("(1+2)*3", "9")]
    [DataRow("5/2", "2")]
    [DataRow("5/2.0", "2.5")]
    [DataRow("-5/2", "-2")]
    [DataRow("-(-2+3)", "-1")]
    [DataRow("n.age/2+3", "13")]
    [DataRow("1e2+1", "101")]
    [DataRow("UPPER(LOWER(n.name))", "ALICE")]
    [DataRow("UPPER('Straße')", "STRASSE")]
    [DataRow("UPPER('ﬃ')", "FFI")]
    [DataRow("LOWER('İ')", "i\u0307")]
    [DataRow("LOWER('ΟΣ')", "ος")]
    [DataRow("LOWER('ΟΣΑ')", "οσα")]
    [DataRow("LOWER('Σ')", "σ")]
    [DataRow("UPPER('𐐨')", "𐐀")]
    [DataRow("LOWER('𐐀')", "𐐨")]
    [DataRow("COALESCE(n.missing, n.name)", "Alice")]
    [DataRow("COALESCE(n.name, 1/0)", "Alice")]
    [DataRow("COALESCE(NULLIF(1,1.0), 3)", "3")]
    [DataRow("NULLIF('1', 1)", "1")]
    [DataRow("CHAR_LENGTH('a😀b')", "3")]
    [DataRow("CHARACTER_LENGTH('a😀b')", "3")]
    [DataRow("LEFT('a😀b', 2)", "a😀")]
    [DataRow("RIGHT('a😀b', 2)", "😀b")]
    [DataRow("LEFT('a😀b', 2147483648)", "a😀b")]
    [DataRow("RIGHT('a😀b', 9223372036854775807)", "a😀b")]
    [DataRow("LEFT('', 9223372036854775807)", "")]
    [DataRow("RIGHT('a😀b', 0)", "")]
    [DataRow("SUBSTRING('a😀bc', 1, 2)", "ERROR")]
    [DataRow("TRIM('  a  ')", "a")]
    [DataRow("LTRIM('  a  ')", "a  ")]
    [DataRow("RTRIM('  a  ')", "  a")]
    [DataRow("LOWER('A') || UPPER('b')", "aB")]
    [DataRow("ABS(-2)", "2")]
    [DataRow("CEILING(-2.5)", "-2")]
    [DataRow("FLOOR(-2.5)", "-3")]
    [DataRow("SQRT(9)", "3")]
    [DataRow("POWER(2,3)", "8")]
    [DataRow("MOD(7,3)", "1")]
    [DataRow("7%3", "ERROR")]
    [DataRow("LN(1)", "0")]
    [DataRow("LOG10(100)", "2")]
    [DataRow("SIN(0)+COS(0)", "1")]
    [DataRow("CASE WHEN n.age >= 18 THEN 'adult' ELSE 'child' END", "adult")]
    [DataRow("CASE n.age WHEN 20 THEN 'twenty' ELSE 'other' END", "twenty")]
    [DataRow("CASE WHEN NULL THEN 1 WHEN true THEN 2 ELSE 1/0 END", "2")]
    [DataRow("COALESCE(CASE WHEN false THEN 1 END, 3)", "3")]
    [DataRow("CASE WHEN true OR false AND false THEN 1 ELSE 0 END", "1")]
    [DataRow("CASE WHEN NOT (1>2) AND 2=2 THEN 1 ELSE 0 END", "1")]
    [DataRow("CASE WHEN NULL AND false THEN 1 ELSE 0 END", "0")]
    [DataRow("CASE WHEN NULL OR true THEN 1 ELSE 0 END", "1")]
    [DataRow("CASE WHEN n.missing IS NULL THEN 1 ELSE 0 END", "1")]
    [DataRow("CASE WHEN n.name IS NOT NULL THEN 1 ELSE 0 END", "1")]
    [DataRow("NORMALIZE('é')", "é")]
    [DataRow("CASE WHEN 'é' IS NOT NORMALIZED THEN 1 ELSE 0 END", "1")]
    [DataRow("REVERSE('a😀b')", "ERROR")]
    public async Task Gql_standard_expression_results_and_rejected_vendor_forms(string expression, string expected)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = $"INSERT (n:Person {{ id: '{Guid.NewGuid()}', name: 'Alice', age: 20 }})"
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode);
        using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = $"MATCH (n:Person) RETURN {expression} AS answer"
        });
        if (expected == "ERROR")
        {
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, await response.Content.ReadAsStringAsync());
            return;
        }
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual(expected, result.GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").ToString());
    }

    [TestMethod]
    public async Task Gql_computed_order_handles_nulls_and_rejects_invalid_keys()
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        foreach (string properties in new[] { "age: 20", "age: 10", "name: 'missing'" })
        {
            using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
            {
                statement = $"INSERT (n:Person {{ id: '{Guid.NewGuid()}', {properties} }})"
            });
            Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode);
        }
        foreach (var (direction, expected) in new[] { ("ASC", "11"), ("DESC", "") })
        {
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = $"MATCH (n:Person) RETURN n.age+1 AS answer ORDER BY answer {direction} LIMIT 1"
            });
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            var result = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.AreEqual(expected, result.GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").ToString());
        }
        foreach (string query in new[]
        {
            "MATCH (n:Person) RETURN n ORDER BY n.age DESC LIMIT 1",
            "MATCH (n:Person) WHERE true RETURN n ORDER BY n.age DESC LIMIT 1",
            "MATCH (n:Person) RETURN n ORDER BY n.age DESC, n.name ASC LIMIT 1"
        })
        {
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query
            });
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            var result = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.AreEqual("missing", result.GetProperty("nodes")[0].GetProperty("properties")
                .GetProperty("urn:entityforge:graph:property:name").GetString());
        }
        using (var mixed = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (n:Person) RETURN COALESCE(n.age, 'missing') AS answer ORDER BY answer LIMIT 1"
        }))
        {
            Assert.AreEqual(HttpStatusCode.OK, mixed.StatusCode, await mixed.Content.ReadAsStringAsync());
            var result = await mixed.Content.ReadFromJsonAsync<JsonElement>();
            Assert.AreEqual("missing", result.GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetString());
        }
        foreach (string key in new[] { "n.age/0" })
        {
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = $"MATCH (n:Person) RETURN {key} AS answer ORDER BY answer LIMIT 1"
            });
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, await response.Content.ReadAsStringAsync());
        }
    }

    [TestMethod]
    public async Task Gql_standard_remove_properties_is_atomic_and_supports_vertices_and_edges()
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = $"INSERT (a:Person {{id:'{Guid.NewGuid()}', age:20}}), (b:Person {{id:'{Guid.NewGuid()}'}}), (a)-[r:knows {{id:'{Guid.NewGuid()}', weight:2}}]->(b)"
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode);
        using var invalid = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "MATCH (a:Person) REMOVE a.age, invalid"
        });
        Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var unchanged = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            includeSupportingGraph = true,
            query = "MATCH (a:Person) WHERE a.age = 20 RETURN a"
        });
        Assert.AreEqual(1, (await unchanged.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches").GetArrayLength());
        using var removed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "MATCH (a:Person)-[r:knows]->(b) REMOVE a.age, r.weight"
        });
        Assert.AreEqual(HttpStatusCode.OK, removed.StatusCode, await removed.Content.ReadAsStringAsync());
        using var checkedResult = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            includeSupportingGraph = true,
            query = "MATCH (a:Person)-[r:knows]->(b) WHERE a.age IS NULL RETURN a,r,b"
        });
        Assert.AreEqual(HttpStatusCode.OK, checkedResult.StatusCode, await checkedResult.Content.ReadAsStringAsync());
        var result = await checkedResult.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual(1, result.GetProperty("matches").GetArrayLength());
        Assert.IsFalse(result.GetProperty("edges")[0].GetProperty("properties").TryGetProperty("urn:entityforge:graph:property:weight", out _));
        using var initialized = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "MATCH (a:Person)-[r:knows]->(b) SET a.leftAmount = 1, a.rightAmount = 2, r.weight = 3"
        });
        Assert.AreEqual(HttpStatusCode.OK, initialized.StatusCode);
        using var initializedRead = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            includeSupportingGraph = true,
            query = "MATCH (a:Person)-[r:knows]->(b) RETURN a,r,b"
        });
        var initializedBody = await initializedRead.Content.ReadFromJsonAsync<JsonElement>();
        Assert.IsTrue(initializedBody.GetProperty("edges")[0].GetProperty("properties").TryGetProperty("urn:entityforge:graph:property:weight", out var initialWeight), initializedBody.ToString());
        Assert.AreEqual(3, initialWeight.GetInt32());
        using var swapped = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "MATCH (a:Person)-[r:knows]->(b) SET a.leftAmount = a.rightAmount, a.rightAmount = a.leftAmount, r.weight = r.weight + 1"
        });
        Assert.AreEqual(HttpStatusCode.OK, swapped.StatusCode, await swapped.Content.ReadAsStringAsync());
        using var invalidExpression = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "MATCH (a:Person) SET a.leftAmount = 999, a.rightAmount = 1/0"
        });
        Assert.AreEqual(HttpStatusCode.BadRequest, invalidExpression.StatusCode);
        using var verified = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            includeSupportingGraph = true,
            query = "MATCH (a:Person)-[r:knows]->(b) WHERE a.leftAmount = 2 AND a.rightAmount = 1 RETURN a,r,b"
        });
        var verifiedBody = await verified.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual(1, verifiedBody.GetProperty("matches").GetArrayLength());
        Assert.IsTrue(verifiedBody.GetProperty("edges")[0].GetProperty("properties").TryGetProperty("urn:entityforge:graph:property:weight", out var finalWeight), verifiedBody.ToString());
        Assert.AreEqual(4, finalWeight.GetInt32());
        using var nullAssignment = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "MATCH (a:Person) SET a.leftAmount = NULL"
        });
        Assert.AreEqual(HttpStatusCode.OK, nullAssignment.StatusCode);
        foreach (string expression in new[] { "TRIM(BOTH 'xy' FROM 'a')", "TRIM('' FROM 'a')", "REPLACE('a',1,'x')", "TRIM(BOTH 1 FROM 'a')", "NORMALIZE('a',NFX)", "n.name STARTS 'A'", "REPLACE('" + new string('a', 2000) + "','a','" + new string('b', 1000) + "')", "1/0", "9223372036854775807+1", "ABS('1')", "1 || 2", "LEFT('abc', -1)", "LEFT('abc', 1.5)", "BOGUS(1)", "COALESCE()", "CASE WHEN 1 THEN 2 END", "UPPER('a','b')", new string('(', 20) + "1" + new string(')', 20) })
        {
            using var rejected = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                includeSupportingGraph = true,
                query = $"MATCH (n:Person) RETURN {expression} AS answer"
            });
            Assert.IsTrue(rejected.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity,
                expression + ": " + await rejected.Content.ReadAsStringAsync());
        }
    }
}
