using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow(false, false, "TRAIL")]
    [DataRow(true, false, "TRAIL")]
    [DataRow(false, true, "TRAIL")]
    [DataRow(false, false, "SIMPLE")]
    [DataRow(true, false, "ACYCLIC")]
    [DataRow(false, true, "SIMPLE")]
    public async Task Gql_anonymous_quantified_trail_hides_generated_bindings(bool imported, bool optional, string mode)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (:HiddenStart)-[:link]->()"
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode, await seed.Content.ReadAsStringAsync());
        using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = (imported ? "MATCH (origin:HiddenStart) " : "") + "LET __subpathElement0=7 " +
                (optional ? "OPTIONAL " : "") + "MATCH REPEATABLE ELEMENTS " +
                (imported ? "(origin)" : "(:HiddenStart)") +
                "-[:link]->(" + mode + " ()<-[:link]-{1}(:HiddenStart)" + (optional ? " WHERE FALSE" : "") +
                ") RETURN * NEXT RETURN *"
        });
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var rows = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
        Assert.AreEqual(1, rows.GetArrayLength());
        var bindings = rows[0].GetProperty("bindings");
        CollectionAssert.AreEquivalent(imported ? new[] { "origin", "__subpathElement0" } : new[] { "__subpathElement0" },
            bindings.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.AreEqual(7, bindings.GetProperty("__subpathElement0").GetInt32());
        await host.StopAsync();
    }
}
