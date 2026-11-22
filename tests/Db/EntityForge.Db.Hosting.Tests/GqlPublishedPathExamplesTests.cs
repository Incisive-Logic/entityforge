using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    // Published graph and expected paths: Deutsch et al., Graph Pattern Matching in
    // GQL and SQL/PGQ, section 5.1, https://arxiv.org/abs/2112.06217.
    // Use explicit match modes: the paper predates ISO's implementation-defined default.
    [TestMethod]
    [DataRow("TRAIL", "*", false, "t5,t2;t6,t8,t1,t2;t5,t7,t8,t1,t2")]
    // The unrestricted WALK form is outside the current release profile. The
    // finite bound contains the published shortest path without changing its answer.
    [DataRow("ANY SHORTEST WALK", "{0,8}", false, "t5,t2")]
    [DataRow("ANY SHORTEST TRAIL", "*", false, "t5,t2")]
    [DataRow("ALL SHORTEST TRAIL", "*", true, "t5,t2,t3,t4,t6,t8,t1;t6,t8,t1,t2,t3,t4,t5")]
    [DataRow("ALL SHORTEST TRAIL", "{0,8}", true, "t5,t2,t3,t4,t6,t8,t1;t6,t8,t1,t2,t3,t4,t5")]
    public async Task Gql_published_path_examples_preserve_exact_edge_sequences(
        string mode, string quantifier, bool via, string expected)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (a1:Account {owner:'Scott'}),(a2:Account {owner:'Aretha'})," +
                "(a3:Account {owner:'Mike'}),(a4:Account {owner:'Charles'})," +
                "(a5:Account {owner:'Natalia'}),(a6:Account {owner:'Dave'})," +
                "(a1)-[:Transfer {tag:'t1'}]->(a3),(a3)-[:Transfer {tag:'t2'}]->(a2)," +
                "(a2)-[:Transfer {tag:'t3'}]->(a4),(a4)-[:Transfer {tag:'t4'}]->(a6)," +
                "(a6)-[:Transfer {tag:'t5'}]->(a3),(a6)-[:Transfer {tag:'t6'}]->(a5)," +
                "(a3)-[:Transfer {tag:'t7'}]->(a5),(a5)-[:Transfer {tag:'t8'}]->(a1)"
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode, await seed.Content.ReadAsStringAsync());
        string query = "MATCH REPEATABLE ELEMENTS p=" + mode +
            " (a:Account {owner:'Dave'})-[:Transfer]->" + quantifier + "(b:Account {owner:'Aretha'})" +
            (via ? "-[:Transfer]->" + quantifier + "(c:Account {owner:'Mike'})" : "") +
            " RETURN VALUE { FOR e IN ELEMENTS(p) FILTER e IS TYPED EDGE RETURN COLLECT_LIST(e.tag) AS tags } AS answer";
        using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query
        });
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, query + ": " + await response.Content.ReadAsStringAsync());
        var rows = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
        CollectionAssert.AreEquivalent(expected.Split(';'), rows.EnumerateArray().Select(row =>
            String.Join(",", row.GetProperty("bindings").GetProperty("answer").EnumerateArray().Select(item => item.GetString()))).ToArray(), query);
        await host.StopAsync();
    }
}
