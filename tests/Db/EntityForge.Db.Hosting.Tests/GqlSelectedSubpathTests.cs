using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("MATCH", "", "", 2)]
    [DataRow("OPTIONAL MATCH", "WHERE 1=0", "", 1)]
    [DataRow("MATCH", "", " NEXT RETURN *", 2)]
    [DataRow("MATCH", "WHERE EXISTS { MATCH localPath=()-[:route]->() }", "", 2)]
    [DataRow("MATCH", "", "", 2, true)]
    public async Task Gql_anonymous_subpath_modes_keep_zero_column_bags_and_outer_scope(
        string match, string predicate, string next, int count, bool imported = false)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (a:AnonStart),(b),(c),(d)," +
                "(a)-[:entry]->(b),(a)-[:entry]->(b),(b)-[:route]->(c),(c)-[:route]->(d)"
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode, await seed.Content.ReadAsStringAsync());
        using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = (imported ? "MATCH (origin:AnonStart) " : "") + "LET __subpathElement0=7 " + match +
                (imported ? " (origin)" : " (:AnonStart)") +
                "-[:entry]->(TRAIL ()-[:route]->()-[:route]->() " + predicate + ") RETURN *" + next
        });
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var rows = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
        Assert.AreEqual(count, rows.GetArrayLength());
        foreach (var row in rows.EnumerateArray())
        {
            var bindings = row.GetProperty("bindings");
            CollectionAssert.AreEquivalent(imported ? new[] { "origin", "__subpathElement0" } : new[] { "__subpathElement0" },
                bindings.EnumerateObject().Select(property => property.Name).ToArray());
            Assert.AreEqual(7, bindings.GetProperty("__subpathElement0").GetInt32());
        }
    }

    [TestMethod]
    [DataRow("TRAIL", "ANY SHORTEST", "", "", false, "[3]")]
    [DataRow("ACYCLIC", "ALL SHORTEST", "", "", false, "[3,3]")]
    [DataRow("SIMPLE", "SHORTEST 2 GROUPS", "", "", false, "[3,3,4]")]
    [DataRow("TRAIL", "SHORTEST 2", "", "", false, "[3,3]")]
    [DataRow("WALK", "ALL SHORTEST", "WHERE b.idx=0", "", false, "[2]")]
    [DataRow("TRAIL", "ALL SHORTEST", "WHERE b.idx=wanted", "", false, "[3,3]")]
    [DataRow("TRAIL", "ANY SHORTEST", "", "WHERE PATH_LENGTH(p)<3", false, "[]")]
    [DataRow("TRAIL", "ANY SHORTEST", "WHERE b.idx=99", "", true, "[null]")]
    [DataRow("TRAIL", "ANY SHORTEST", "", "", false, "[3]", true)]
    [DataRow("ACYCLIC", "ALL SHORTEST", "", "", false, "[3,3]", true)]
    [DataRow("SIMPLE", "SHORTEST 2 GROUPS", "", "", false, "[3,3,4]", true)]
    [DataRow("TRAIL", "ANY SHORTEST", "WHERE 1=0", "", true, "[null]", true)]
    [DataRow("TRAIL", "ANY SHORTEST", "", "", false, "[3]", false, true)]
    [DataRow("SIMPLE", "SHORTEST 2 GROUPS", "", "", false, "[3,3,4]", false, true)]
    [DataRow("TRAIL", "ANY SHORTEST", "", "WHERE PATH_LENGTH(p)<3", false, "[]", true, true)]
    [DataRow("TRAIL", "ALL SHORTEST", "", "", false, "[3,3]", true, true)]
    [DataRow("TRAIL", "ANY SHORTEST", "WHERE 1=0", "", true, "[null]", true, true)]
    public async Task Gql_fixed_subpath_prefilters_choose_eligible_complete_paths(string mode, string selector,
        string condition, string postfilter, bool optional, string expected, bool anonymous = false, bool nested = false)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (s:SelectNode {idx:0}),(b:SelectNode {idx:1}),(c:SelectNode {idx:2}),(u:SelectNode {idx:3})," +
                "(s)-[:route]->(s),(b)-[:route]->(c),(c)-[:route]->(s)," +
                "(s)-[:entry]->(b),(s)-[:entry]->(b),(s)-[:entry]->(u),(u)-[:entry]->(b)"
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode, await seed.Content.ReadAsStringAsync());
        // The two-hop candidate repeats the self-loop. Two eligible three-hop
        // routes use distinct parallel entry edges; one eligible route has four hops.
        string match = "LET wanted=1 " + (optional ? "OPTIONAL " : "") + "MATCH REPEATABLE ELEMENTS p=" + selector +
            " (a:SelectNode WHERE a.idx=0)-[:entry]->{0,2}(" + mode +
            " (b)-[edgeOne:route]->(c)-[edgeTwo:route]->(d:SelectNode WHERE d.idx=0) " + condition + ") " + postfilter;
        if (nested)
        {
            match = match.Replace("(c)-[edgeTwo:route]->(d:SelectNode WHERE d.idx=0)",
                "(ACYCLIC (c)-[edgeTwo:route]->(d:SelectNode WHERE d.idx=0))");
        }
        if (anonymous)
        {
            match = match.Replace("(b)", "()").Replace("(c)", "()").Replace("[edgeOne:", "[:").Replace("[edgeTwo:", "[:");
        }
        using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = match + " RETURN PATH_LENGTH(p) AS answer ORDER BY answer"
        });
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var rows = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
        Assert.AreEqual(expected, JsonSerializer.Serialize(rows.EnumerateArray().Select(row => row.GetProperty("bindings").GetProperty("answer"))));
        using var star = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = match + " RETURN *"
        });
        Assert.AreEqual(HttpStatusCode.OK, star.StatusCode, await star.Content.ReadAsStringAsync());
        var starRows = (await star.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
        Assert.AreEqual(rows.GetArrayLength(), starRows.GetArrayLength());
        foreach (var row in starRows.EnumerateArray())
        {
            CollectionAssert.AreEquivalent(anonymous ? new[] { "wanted", "a", "d", "p" } :
                new[] { "wanted", "a", "b", "c", "d", "edgeOne", "edgeTwo", "p" },
                row.GetProperty("bindings").EnumerateObject().Select(property => property.Name).ToArray());
        }
    }
}
