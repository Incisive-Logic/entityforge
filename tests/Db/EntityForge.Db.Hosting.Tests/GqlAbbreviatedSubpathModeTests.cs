using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("WALK", 3)]
    [DataRow("TRAIL", 4)]
    [DataRow("ACYCLIC", 4)]
    [DataRow("SIMPLE", 3)]
    public async Task Abbreviated_subpath_modes_filter_candidates_before_shortest_selection(string mode, int expectedLength)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (s:Origin),(x:Transit),(a0:Entry&Target),(a1:Entry)," +
                "(b0:Pivot),(b1:Pivot),(s)-[:Prefix]->(a0),(s)-[:Prefix]->(x),(x)-[:Prefix]->(a1)," +
                "(a0)-[:Hop]->(b0),(a1)-[:Hop]->(b1),(a0)-[:Hop]->(b1)"
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode, await seed.Content.ReadAsStringAsync());
        string pattern = "REPEATABLE ELEMENTS p=ANY SHORTEST (s:Origin)-[:Prefix]->{1,2}(" + mode +
            " (a:Entry)->(b:Pivot)<-(c:Target)) RETURN PATH_LENGTH(p) AS length";
        foreach (bool optional in new[] { false, true })
        {
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = (optional ? "OPTIONAL MATCH " : "MATCH ") + pattern
            });
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            var rows = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
            Assert.AreEqual(1, rows.GetArrayLength());
            Assert.AreEqual(expectedLength, rows[0].GetProperty("bindings").GetProperty("length").GetInt32());
        }
        using var filtered = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH " + pattern.Replace(" RETURN ", " WHERE PATH_LENGTH(p)=4 RETURN ", StringComparison.Ordinal)
        });
        Assert.AreEqual(HttpStatusCode.OK, filtered.StatusCode, await filtered.Content.ReadAsStringAsync());
        var filteredRows = (await filtered.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
        Assert.AreEqual(expectedLength == 4 ? 1 : 0, filteredRows.GetArrayLength(),
            "The outer WHERE must not replace a selected shorter path with a longer candidate.");
    }

    [TestMethod]
    [DataRow("WALK", 1)]
    [DataRow("TRAIL", 0)]
    [DataRow("ACYCLIC", 0)]
    [DataRow("SIMPLE", 1)]
    [DataRow("WALK", 1, "-", "-")]
    [DataRow("TRAIL", 0, "-", "-")]
    [DataRow("ACYCLIC", 0, "-", "-")]
    [DataRow("SIMPLE", 1, "-", "-")]
    [DataRow("WALK", 1, "<->", "<->")]
    [DataRow("TRAIL", 0, "<->", "<->")]
    [DataRow("ACYCLIC", 0, "<->", "<->")]
    [DataRow("SIMPLE", 1, "<->", "<->")]
    [DataRow("WALK", 1, "~>", "<~")]
    [DataRow("TRAIL", 0, "~>", "<~")]
    [DataRow("ACYCLIC", 0, "~>", "<~")]
    [DataRow("SIMPLE", 1, "~>", "<~")]
    [DataRow("TRAIL", -1, "~", "~")]
    public async Task Abbreviated_subpath_edges_enforce_local_modes_without_exposing_generated_bindings(
        string mode, int expected, string outward = "->", string inward = "<-")
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (s:Entrance)-[:Prefix]->(a:ModeStart)-[:Hop]->(b:ModeEnd)"
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode, await seed.Content.ReadAsStringAsync());
        string pattern = "REPEATABLE ELEMENTS (s:Entrance)-[:Prefix]->(" + mode +
            " (a:ModeStart)" + outward + "(b:ModeEnd)" + inward + "(c:ModeStart))";
        foreach (bool optional in new[] { false, true })
        {
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = (optional ? "OPTIONAL MATCH " : "MATCH ") + pattern + " RETURN *"
            });
            if (expected < 0)
            {
                // Exclusively undirected stored edges remain outside this
                // directed store's contract; OPTIONAL must not hide rejection.
                Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
                continue;
            }
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            var rows = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
            Assert.AreEqual(optional ? 1 : expected, rows.GetArrayLength());
            foreach (var row in rows.EnumerateArray())
            {
                var bindings = row.GetProperty("bindings");
                CollectionAssert.AreEquivalent(new[] { "s", "a", "b", "c" },
                    bindings.EnumerateObject().Select(property => property.Name).ToArray());
                if (expected == 0)
                {
                    Assert.IsTrue(bindings.EnumerateObject().All(property => property.Value.ValueKind == JsonValueKind.Null));
                }
            }
        }
    }
}
