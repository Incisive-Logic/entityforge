using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Gql_standard_standalone_rows_do_not_depend_on_graph_cardinality(bool populated)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        if (populated)
        {
            using var seeded = await client.PostAsJsonAsync("/v1/graph/gql/update", new
            {
                statement = $"INSERT (a:Person {{id:'{Guid.NewGuid()}'}}),(b:Person {{id:'{Guid.NewGuid()}'}})"
            });
            Assert.AreEqual(HttpStatusCode.OK, seeded.StatusCode);
        }
        foreach (var (query, expected) in new[]
        {
            ("RETURN 42 AS answer", "[42]"),
            ("LET m = {entries:[1,2,3]} FOR x IN m.entries RETURN SUM(x) AS answer", "[6]"),
            ("FOR x IN [] RETURN COUNT(*) AS answer", "[0]"),
            ("FOR x IN [3,1,2] RETURN x AS answer ORDER BY answer", "[1,2,3]"),
            ("RETURN 1 AS answer LIMIT 0", "[]"),
            ("LET x = 1 FILTER x=2 RETURN x AS answer", "[]")
        })
        {
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query
            });
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, query + ": " + await response.Content.ReadAsStringAsync());
            var result = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.AreEqual(expected, JsonSerializer.Serialize(result.GetProperty("matches").EnumerateArray()
                .Select(row => row.GetProperty("bindings").GetProperty("answer"))), query);
        }
    }

    [TestMethod]
    public async Task Gql_standard_row_stages_accept_list_parameters_without_interpreting_string_contents()
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seeded = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = $"INSERT (n:Person {{id:'{Guid.NewGuid()}'}})"
        });
        Assert.AreEqual(HttpStatusCode.OK, seeded.StatusCode);
        using var result = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (n:Person) FOR xs IN $lists FOR answer IN xs RETURN answer ORDER BY answer",
            parameters = new
            {
                lists = new[] { new[] { "WITH", "FOR x IN [1]" }, new[] { "WHERE" } }
            }
        });
        Assert.AreEqual(HttpStatusCode.OK, result.StatusCode, await result.Content.ReadAsStringAsync());
        var json = await result.Content.ReadFromJsonAsync<JsonElement>();
        CollectionAssert.AreEqual(new[] { "FOR x IN [1]", "WHERE", "WITH" }, json.GetProperty("matches").EnumerateArray()
            .Select(row => row.GetProperty("bindings").GetProperty("answer").GetString()).ToArray());
        using var maps = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (n:Person) FOR x IN $list RETURN x.name AS answer",
            parameters = new
            {
                list = new[] { new { name = "WITH $list RETURN" } }
            }
        });
        Assert.AreEqual(HttpStatusCode.OK, maps.StatusCode, await maps.Content.ReadAsStringAsync());
        var mapped = await maps.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual("WITH $list RETURN", mapped.GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetString());
        using var invalid = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (n:Person) FOR x IN $list RETURN x",
            parameters = new
            {
                list = JsonSerializer.Deserialize<JsonElement>("[[[[[[[[[1]]]]]]]]]")
            }
        });
        Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [TestMethod]
    [DataRow("MATCH (n:Person) RETURN n AS p NEXT RETURN p", "p")]
    [DataRow("MATCH (n:Person) RETURN DISTINCT n AS p NEXT RETURN p,1 AS total", "p,total")]
    [DataRow("MATCH (n:Person) RETURN DISTINCT n AS p,n AS q NEXT RETURN p,q", "p,q")]
    [DataRow("MATCH (n:Person) RETURN n AS p,n AS q NEXT RETURN p,q", "p,q")]
    [DataRow("MATCH (n:Person) RETURN n AS p NEXT RETURN p AS renamed", "renamed")]
    [DataRow("MATCH (n:Person) RETURN n AS p NEXT RETURN DISTINCT p ORDER BY p.age", "p")]
    [DataRow("MATCH (n:Person) RETURN n AS p NEXT RETURN p,COUNT(*) AS total", "p,total")]
    public async Task Gql_standard_next_entity_aliases_preserve_identity_and_supporting_graph(string query, string aliases)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seeded = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = $"INSERT (n:Person {{id:'{Guid.NewGuid()}',age:1}})"
        });
        Assert.AreEqual(HttpStatusCode.OK, seeded.StatusCode);
        using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query,
            includeSupportingGraph = true
        });
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        var row = result.GetProperty("matches")[0].GetProperty("bindings");
        CollectionAssert.AreEquivalent(aliases.Split(','), row.EnumerateObject().Select(property => property.Name).ToArray());
        string identity = result.GetProperty("nodes")[0].GetProperty("identity").GetString()!;
        foreach (string alias in aliases.Split(','))
        {
            if (alias == "total")
            {
                Assert.AreEqual(1, row.GetProperty(alias).GetInt32());
            }
            else
            {
                Assert.AreEqual(identity, row.GetProperty(alias).GetString());
            }
        }
    }

    [TestMethod]
    [DataRow("MATCH (n:Person) RETURN SUM(n.age)+1 AS answer", "[7]")]
    [DataRow("MATCH (n:Person) RETURN LABELS(n) AS answer", "ERROR")]
    [DataRow("MATCH (n:Person) WHERE n IS LABELED Person RETURN n.age AS answer ORDER BY answer", "[1,2,3]")]
    [DataRow("MATCH (n:Person) RETURN CHAR_LENGTH(ELEMENT_ID(n))>0 AS answer", "[true,true,true]")]
    [DataRow("MATCH (n:Person) RETURN LABELS(NULL) AS answer", "ERROR")]
    [DataRow("MATCH (n:Person) RETURN TYPE(n) AS answer", "ERROR")]
    [DataRow("MATCH (n:Person) RETURN [x IN [n.age,n.age+1] WHERE x>2 | x*2] AS answer ORDER BY answer", "ERROR")]
    [DataRow("MATCH (n:Person) WHERE ANY(x IN [n.age] WHERE x>1) RETURN n.age AS answer ORDER BY answer", "ERROR")]
    [DataRow("MATCH (n:Person) RETURN n ORDER BY n.age NEXT RETURN COLLECT_LIST(n) AS nodes NEXT RETURN [n IN nodes | n.age] AS answer", "ERROR")]
    [DataRow("MATCH (n:Person) RETURN SUM(n.age)/COUNT(*) AS answer", "[2]")]
    [DataRow("MATCH (n:Person) RETURN COALESCE(MAX(n.missing),42) AS answer", "[42]")]
    [DataRow("MATCH (n:Person) RETURN SIZE(COLLECT_LIST(n.age)) AS answer", "[3]")]
    [DataRow("MATCH (n:Person) RETURN SUM(n.age)+1 AS total NEXT RETURN total*2 AS answer", "[14]")]
    [DataRow("MATCH (n:Person) RETURN MOD(n.age,2) AS parity,SUM(n.age)+1 AS answer NEXT RETURN answer ORDER BY answer", "[3,5]")]
    [DataRow("MATCH (n:Person) RETURN CASE WHEN COUNT(*)>2 THEN SUM(n.age) ELSE 0 END AS answer", "[6]")]
    [DataRow("MATCH (n:Person) WHERE n.age>100 RETURN SUM(n.age)+COUNT(*) AS answer", "[null]")]
    [DataRow("MATCH (n:Person) RETURN SUM(DISTINCT n.age)+COUNT(DISTINCT n.age) AS answer", "[9]")]
    [DataRow("MATCH (n:Person) RETURN n.age+SUM(n.age) AS answer", "ERROR")]
    [DataRow("MATCH (n:Person) RETURN n ORDER BY n.age OFFSET 1 LIMIT 2 NEXT RETURN n.age AS answer", "[2,3]")]
    [DataRow("MATCH (n:Person) RETURN DISTINCT n.age AS age LIMIT 1 LIMIT 2 NEXT RETURN age AS answer", "ERROR")]
    [DataRow("MATCH (n:Person) RETURN DISTINCT n.age AS age OFFSET 1 OFFSET 1 NEXT RETURN age AS answer", "ERROR")]
    [DataRow("MATCH (n:Person) FOR x IN [1,2] RETURN SUM(x) AS answer LIMIT 0", "[]")]
    [DataRow("MATCH (n:Person) FOR x IN [n.age,n.age+10] RETURN x AS answer ORDER BY answer", "[1,2,3,11,12,13]")]
    [DataRow("MATCH (n:Person) RETURN {age:n.age,name:'person'} AS m NEXT RETURN m.age AS answer ORDER BY answer", "[1,2,3]")]
    [DataRow("MATCH (n:Person) RETURN {age:n.age} AS answer ORDER BY answer.age", "[{\"age\":1},{\"age\":2},{\"age\":3}]")]
    [DataRow("MATCH (n:Person) RETURN {nested:{age:n.age}} AS m NEXT RETURN m.nested.age AS answer ORDER BY answer", "[1,2,3]")]
    [DataRow("MATCH (n:Person) RETURN RECORD {nested:RECORD {age:n.age}} AS m NEXT RETURN m.nested.age AS answer ORDER BY answer", "[1,2,3]")]
    [DataRow("MATCH (n:Person) RETURN {nested:{age:n.age}}.nested.age AS answer ORDER BY answer", "[1,2,3]")]
    [DataRow("MATCH (n:Person) RETURN SIZE(KEYS({a:1,b:2})) AS answer", "ERROR")]
    [DataRow("MATCH (n:Person) RETURN n.age AS answer ORDER BY answer", "[1,2,3]")]
    [DataRow("MATCH (n:Person) WHERE n.age>1 RETURN n.age AS answer ORDER BY answer", "[2,3]")]
    [DataRow("MATCH (n:Person) RETURN ISEMPTY({}) AS answer", "ERROR")]
    [DataRow("MATCH (n:Person) RETURN {a:1,b:2}={b:2,a:1.0} AS answer", "[true,true,true]")]
    [DataRow("MATCH (n:Person) RETURN {a:NULL}={a:NULL} AS answer", "[null,null,null]")]
    [DataRow("MATCH (n:Person) RETURN {a:1}={a:1,b:NULL} AS answer", "[false,false,false]")]
    [DataRow("MATCH (n:Person) RETURN {x:1} AS m NEXT RETURN m.missing AS answer", "[null,null,null]")]
    [DataRow("MATCH (n:Person) RETURN {x:CASE WHEN n.age=1 THEN 1 ELSE 1.0 END} AS m NEXT RETURN COUNT(DISTINCT m) AS answer", "[1]")]
    [DataRow("MATCH (n:Person) RETURN {a:1,a:2} AS answer", "ERROR")]
    [DataRow("MATCH (n:Person) RETURN n AS p NEXT RETURN p=p AS answer", "[true,true,true]")]
    [DataRow("MATCH (n:Person) FOR x IN ARRAY[n.age,n.age+10] RETURN x AS answer ORDER BY answer", "[1,2,3,11,12,13]")]
    [DataRow("MATCH (n:Person) FOR x IN [1,2] FOR y IN [x,x+1] RETURN SUM(y) AS answer", "[24]")]
    [DataRow("MATCH (n:Person) FOR x IN [1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17,18,19,20,21,22,23,24,25,26,27,28,29,30,31,32,33,34,35,36,37,38,39,40,41,42,43,44,45,46,47,48,49,50,51,52,53,54,55,56,57,58,59,60,61,62,63,64,65,66,67,68,69,70,71,72,73,74,75,76,77,78,79,80,81,82,83,84,85,86,87,88,89,90,91,92,93,94,95,96,97,98,99,100] FOR y IN [1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17,18,19,20,21,22,23,24,25,26,27,28,29,30,31,32,33,34,35,36,37,38,39,40,41,42,43,44,45,46,47,48,49,50,51,52,53,54,55,56,57,58,59,60,61,62,63,64,65,66,67,68,69,70,71,72,73,74,75,76,77,78,79,80,81,82,83,84,85,86,87,88,89,90,91,92,93,94,95,96,97,98,99,100] RETURN x AS answer LIMIT 1", "ERROR")]
    [DataRow("MATCH (n:Person) RETURN n LIMIT 1 NEXT FOR x IN [1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17,18,19,20,21,22,23,24,25,26,27,28,29,30,31,32,33,34,35,36,37,38,39,40,41,42,43,44,45,46,47,48,49,50,51,52,53,54,55,56,57,58,59,60,61,62,63,64,65,66,67,68,69,70,71,72,73,74,75,76,77,78,79,80,81,82,83,84,85,86,87,88,89,90,91,92,93,94,95,96,97,98,99,100] FOR y IN [1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17,18,19,20,21,22,23,24,25,26,27,28,29,30,31,32,33,34,35,36,37,38,39,40,41,42,43,44,45,46,47,48,49,50,51,52,53,54,55,56,57,58,59,60,61,62,63,64,65,66,67,68,69,70,71,72,73,74,75,76,77,78,79,80,81,82,83,84,85,86,87,88,89,90,91,92,93,94,95,96,97,98,99,100] RETURN COUNT(*) AS answer", "[10000]")]
    [DataRow("MATCH (n:Person) FOR x IN [] RETURN x AS answer", "[]")]
    [DataRow("MATCH (n:Person) FOR x IN NULL RETURN x AS answer", "[]")]
    [DataRow("MATCH (n:Person) FOR x IN n.age RETURN x AS answer ORDER BY answer", "ERROR")]
    [DataRow("MATCH (n:Person) RETURN DISTINCT MOD(n.age,2) AS x NEXT RETURN SUM(x) AS answer", "[1]")]
    [DataRow("MATCH (n:Person) RETURN MOD(n.age,2) AS x,COUNT(*) AS totalCount NEXT FILTER totalCount>1 RETURN x AS answer", "[1]")]
    [DataRow("MATCH (n:Person) RETURN COLLECT_LIST(n.age) AS xs NEXT FOR x IN xs RETURN x AS answer ORDER BY answer DESC", "[3,2,1]")]
    [DataRow("MATCH (n:Person) RETURN COLLECT_LIST(n) AS nodes NEXT FOR p IN nodes RETURN p.age AS answer ORDER BY answer", "[1,2,3]")]
    [DataRow("MATCH (n:Person) RETURN n ORDER BY n.age DESC LIMIT 2 NEXT RETURN n.age AS age NEXT RETURN SUM(age) AS answer", "[5]")]
    [DataRow("MATCH (n:Person) FOR x IN [1,2] RETURN DISTINCT x NEXT RETURN x AS answer ORDER BY answer", "[1,2]")]
    [DataRow("MATCH (n:Person) RETURN DISTINCT 'text WITH word' AS x NEXT RETURN x AS answer", "[\"text WITH word\"]")]
    [DataRow("MATCH (n:Person) FOR x IN [1,2] WITH OFFSET position FOR y IN [x,x+1] RETURN SUM(y) AS answer", "[24]")]
    [DataRow("MATCH (:Person) RETURN 1 AS x NEXT RETURN SUM(x) AS answer", "[3]")]
    [DataRow("MATCH (:Person) FOR x IN [1,2] RETURN SUM(x) AS answer", "[9]")]
    [DataRow("MATCH (:Person) RETURN COUNT(*) AS totalCount NEXT RETURN totalCount AS answer", "[3]")]
    [DataRow("MATCH (boundNode:Person) RETURN boundNode.age AS answer ORDER BY answer", "[1,2,3]")]
    [DataRow("MATCH (n:Person) RETURN n.age AS resultAge ORDER BY resultAge", "[1,2,3]")]
    [DataRow("MATCH (n:Person) RETURN n.age AS age NEXT RETURN age AS answer ORDER BY answer", "[1,2,3]")]
    [DataRow("MATCH (n:Person) RETURN n.age+1 AS age NEXT FILTER age>2 RETURN age AS answer ORDER BY answer", "[3,4]")]
    [DataRow("MATCH (n:Person) WHERE n.age>1 RETURN n.age AS a NEXT RETURN a*2 AS b NEXT FILTER b<6 RETURN b AS answer", "[4]")]
    [DataRow("MATCH (n:Person) RETURN n AS p NEXT RETURN p.age AS answer ORDER BY answer DESC LIMIT 2", "[3,2]")]
    [DataRow("MATCH (n:Person) RETURN n, n.age AS age NEXT RETURN n.age+age AS answer ORDER BY answer", "[2,4,6]")]
    [DataRow("MATCH (n:Person) RETURN * NEXT RETURN n.age AS answer ORDER BY answer", "[1,2,3]")]
    [DataRow("MATCH (n:Person) RETURN n.age AS age NEXT RETURN SUM(age) AS answer", "[6]")]
    [DataRow("MATCH (n:Person) RETURN 1 AS age NEXT RETURN DISTINCT age AS answer", "[1]")]
    [DataRow("MATCH (n:Person) RETURN n.age AS answer NEXT RETURN answer ORDER BY answer OFFSET 1 LIMIT 1", "[2]")]
    [DataRow("MATCH (n:Person) RETURN 'WITH RETURN WHERE' AS text NEXT RETURN CHAR_LENGTH(text) AS answer LIMIT 1", "[17]")]
    [DataRow("MATCH (n:Person) RETURN [n.age,10] AS xs NEXT RETURN xs[0] AS answer ORDER BY answer", "ERROR")]
    [DataRow("MATCH (n:Person) RETURN n.age AS a NEXT RETURN a AS answer ORDER BY answer LIMIT 0", "[]")]
    [DataRow("MATCH (n:Person) RETURN n.age AS age NEXT RETURN n.age AS answer", "ERROR")]
    [DataRow("MATCH (n:Person) RETURN n.age AS a,a+1 AS b NEXT RETURN b AS answer", "ERROR")]
    [DataRow("MATCH (n:Person) RETURN n AS a,n AS a NEXT RETURN a.age AS answer", "ERROR")]
    [DataRow("MATCH (n:Person) RETURN n AS p NEXT RETURN p,p", "ERROR")]
    [DataRow("MATCH (n:Person) RETURN n AS p NEXT RETURN p AS x,p AS x", "ERROR")]
    [DataRow("MATCH (n:Person) RETURN COUNT(*) AS a NEXT RETURN a AS answer", "[3]")]
    [DataRow("MATCH (n:Person) RETURN DISTINCT n.age AS a NEXT RETURN a AS answer ORDER BY answer", "[1,2,3]")]
    [DataRow("MATCH (n:Person) RETURN n ORDER BY n.age LIMIT 1 NEXT RETURN n.age AS answer", "[1]")]
    [DataRow("MATCH (n:Person) OPTIONAL MATCH (n)-[e]->(m) RETURN n NEXT RETURN n.age AS answer ORDER BY answer", "[1,2,3]")]
    public async Task Gql_standard_projection_scope_and_extension_rejection(string query, string expected)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seeded = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT " + String.Join(",", Enumerable.Range(1, 3).Select(age => $"(n{age}:Person {{id:'{Guid.NewGuid()}',age:{age}}})"))
        });
        Assert.AreEqual(HttpStatusCode.OK, seeded.StatusCode, await seeded.Content.ReadAsStringAsync());
        using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query
        });
        if (expected == "ERROR")
        {
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, await response.Content.ReadAsStringAsync());
            return;
        }
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        using var wanted = JsonDocument.Parse(expected);
        CollectionAssert.AreEqual(wanted.RootElement.EnumerateArray().Select(ScalarKey).ToArray(),
            json.GetProperty("matches").EnumerateArray().Select(row => ScalarKey(row.GetProperty("bindings").EnumerateObject().Single().Value)).ToArray());
    }
}
