using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("MATCH p=(a:Person) -> (s)(() -> () |+| () -> () -> ())+ (t) RETURN PATH_LENGTH(p) AS answer", "[2]")]
    [DataRow("MATCH p=(s)(() -> () |+| () -> () -> ())+ (t) -> (b) RETURN PATH_LENGTH(p) AS answer", "[2]")]
    [DataRow("MATCH p=(a) -> (s)(() -> () |+| () -> () -> ())* (t) -> (b) RETURN PATH_LENGTH(p) AS answer", "[2]")]
    [DataRow("MATCH p=(a) -> (s)(() -> () |+| () -> () -> ()){0} (t) RETURN s=t AS answer", "[true,true]")]
    [DataRow("OPTIONAL MATCH (a:Missing) -> (s)(() -> () |+| () -> () -> ())+ (t) RETURN t AS answer", "[null]")]
    [DataRow("MATCH (a:Person {name:'A'}) MATCH (a) -> (s)(() -> () |+| () -> () -> ())+ (t) RETURN t.name AS answer", "[\"C\"]")]
    [DataRow("MATCH p=(a:Person WHERE a.name=s.name) -> (s)(() -> () |+| () -> () -> ())* (t) RETURN PATH_LENGTH(p) AS answer", "[]")]
    [DataRow("MATCH p=(a WHERE a.name='A' AND t.name='C') -> (s)(() -> () |+| () -> () -> ())+ (t) RETURN PATH_LENGTH(p) AS answer", "[2]")]
    [DataRow("MATCH p=(s)(() -> () |+| () -> () -> ())+ (t)-[e WHERE s.name='A']->(b) RETURN PATH_LENGTH(p) AS answer", "[2]")]
    [DataRow("MATCH p=({name:s.name}) -> (s)(() -> () |+| () -> () -> ())* (t) RETURN PATH_LENGTH(p) AS answer", "[]")]
    [DataRow("MATCH p=({name:s.name,name:s.name}) -> (s)(() -> () |+| () -> () -> ())* (t) RETURN PATH_LENGTH(p) AS answer", "ERROR")]
    [DataRow("MATCH p=(a:Person {name:'A'}) -> (s)(() <- () |+| () <- () <- ())+ (t) RETURN PATH_LENGTH(p) AS answer", "[]")]
    [DataRow("MATCH REPEATABLE ELEMENTS p=SIMPLE (a:Person {name:'A'}) -> (s)(() <- () |+| () <- () <- ())+ (t) RETURN PATH_LENGTH(p) AS answer", "[2]")]
    public Task Gql_different_shape_bag_exterior_preserves_fixed_edges_and_boundaries(string query, string expected) =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls(query, expected);

    [TestMethod]
    [DataRow("", "*", "2,3,4,4")]
    [DataRow("ALL SHORTEST ", "*", "2,3,4,4")]
    [DataRow("ANY SHORTEST ", "*", "2,3,4")]
    [DataRow("", "+", "3,4,4")]
    [DataRow("", "{2}", "4")]
    public async Task Gql_different_shape_bag_exterior_counts_only_repeated_section(string selector, string quantifier, string expected)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (:Person {name:'A'})-[:step]->(:Person {name:'B'})-[:step]->(:Person {name:'C'})-[:step]->(:Person {name:'D'})-[:step]->(:Person {name:'E'})"
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode, await seed.Content.ReadAsStringAsync());
        using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH p=" + selector + "(a:Person {name:'A'})-[first:step]->(s)(()-[:step]->() |+| ()-[:step]->()-[:step]->())" +
                quantifier + "(t)-[last:step]->(b) RETURN PATH_LENGTH(p) AS answer"
        });
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        CollectionAssert.AreEquivalent(expected.Split(',').Select(int.Parse).ToArray(),
            result.GetProperty("matches").EnumerateArray().Select(row => row.GetProperty("bindings").GetProperty("answer").GetInt32()).ToArray());
    }

    [TestMethod]
    public Task Gql_different_shape_bag_exterior_preserves_update_scope_rollback_and_replay() =>
        Gql_cyclic_product_predicates_preserve_update_scope_rollback_and_replay(
            "MATCH p=(a:Person {name:'A'})-[e:knows]->(c)(() -> () |+| () -> () -> ())+ (b)", "a,b,c,e,p");
}
