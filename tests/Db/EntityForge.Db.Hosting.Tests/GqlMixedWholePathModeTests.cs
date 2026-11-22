using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("SIMPLE", "TRAIL", true, false)]
    [DataRow("TRAIL", "SIMPLE", true, false)]
    [DataRow("SIMPLE", "TRAIL", false, false)]
    [DataRow("TRAIL", "SIMPLE", false, false)]
    [DataRow("SIMPLE", "TRAIL", true, true)]
    [DataRow("TRAIL", "SIMPLE", false, true)]
    [DataRow("SIMPLE", "TRAIL", true, false, true)]
    [DataRow("TRAIL", "SIMPLE", false, true, true)]
    [DataRow("SIMPLE", "TRAIL", false, false, false, true)]
    [DataRow("TRAIL", "SIMPLE", false, false, false, true)]
    [DataRow("SIMPLE", "TRAIL", false, true, false, true)]
    [DataRow("TRAIL", "SIMPLE", false, true, false, true)]
    public async Task Gql_mixed_whole_path_modes_enforce_both_identity_constraints(
        string outer, string inner, bool innerName, bool unbounded, bool closedShortest = false, bool unnamed = false)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        (int From, int To)[] arcs = [(0, 1), (1, 2), (2, 0), (0, 1)];
        string insert = String.Join(',', Enumerable.Range(0, 3).Select(index =>
            $"(n{index}:MixedMode {{id:'{Guid.NewGuid()}',idx:{index}}})").Concat(arcs.Select((edge, index) =>
            $"(n{edge.From})-[:link {{id:'{Guid.NewGuid()}',ordinal:{index}}}]->(n{edge.To})")));
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT " + insert
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode, await seed.Content.ReadAsStringAsync());
        string quantifier = unbounded ? "*" : "{0,4}";
        string pattern = "(a:MixedMode WHERE a.idx=0)-[edges:link]-" + quantifier +
            (closedShortest ? "(b WHERE b.idx=0)" : "(b)") +
            (closedShortest ? " WHERE PATH_LENGTH(p)>0" : "");
        string path = innerName ? outer + " (p=" + inner + " " + pattern + ")" : "p=" + outer + " (" + inner + " " + pattern + ")";
        if (unnamed)
        {
            path = outer + " (" + inner + " " + pattern + ")";
        }
        if (closedShortest)
        {
            path = innerName ? "ALL SHORTEST " + path : path.Replace("p=", "p=ALL SHORTEST ", StringComparison.Ordinal);
        }
        string match = "MATCH REPEATABLE ELEMENTS " + path;
        string query = match + " RETURN VALUE { FOR edge IN " + (unnamed ? "edges" : "ELEMENTS(p)") +
            " FILTER edge IS TYPED EDGE RETURN COLLECT_LIST(edge.ordinal) AS route } AS route";
        using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query
        });
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        var actual = result.GetProperty("matches").EnumerateArray().Select(row => String.Join(',',
            row.GetProperty("bindings").GetProperty("route").EnumerateArray().Select(value => value.GetInt32()))).ToArray();
        var expected = new List<string>();
        void Visit(List<int> vertices, List<int> edges)
        {
            bool closed = vertices.Count > 1 && vertices[0] == vertices[^1];
            var distinctNodes = closed ? vertices.Take(vertices.Count - 1) : vertices;
            if (distinctNodes.Distinct().Count() != vertices.Count - (closed ? 1 : 0) || edges.Distinct().Count() != edges.Count)
            {
                return;
            }
            if (!closedShortest || closed)
            {
                expected.Add(String.Join(',', edges));
            }
            if (closed || edges.Count == 4)
            {
                return;
            }
            for (int index = 0; index < arcs.Length; index++)
            {
                var edge = arcs[index];
                int current = vertices[^1];
                if (edge.From == current || edge.To == current)
                {
                    Visit([.. vertices, edge.From == current ? edge.To : edge.From], [.. edges, index]);
                }
            }
        }
        Visit([0], []);
        if (closedShortest)
        {
            int shortest = expected.Min(route => route.Split(',').Length);
            expected = expected.Where(route => route.Split(',').Length == shortest).ToList();
        }
        CollectionAssert.AreEquivalent(expected.ToArray(), actual);
        if (unnamed)
        {
            using var wildcard = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = match + " RETURN *"
            });
            Assert.AreEqual(HttpStatusCode.OK, wildcard.StatusCode, await wildcard.Content.ReadAsStringAsync());
            var wildcardResult = await wildcard.Content.ReadFromJsonAsync<JsonElement>();
            foreach (var row in wildcardResult.GetProperty("matches").EnumerateArray())
            {
                CollectionAssert.AreEquivalent(new[] { "a", "b", "edges" },
                    row.GetProperty("bindings").EnumerateObject().Select(property => property.Name).ToArray());
            }
        }
    }
}
