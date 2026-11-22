using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("WALK", "WALK", 2)]
    [DataRow("TRAIL", "WALK", 2)]
    [DataRow("ACYCLIC", "WALK", 2)]
    [DataRow("SIMPLE", "WALK", 2)]
    [DataRow("WALK", "TRAIL", 2)]
    [DataRow("SIMPLE", "TRAIL", 2)]
    [DataRow("TRAIL", "SIMPLE", 2)]
    [DataRow("TRAIL", "ACYCLIC", 2)]
    [DataRow("ACYCLIC", "WALK", 0)]
    [DataRow("WALK", "WALK", 2, true)]
    [DataRow("TRAIL", "WALK", 2, true)]
    [DataRow("ACYCLIC", "WALK", 2, true)]
    [DataRow("SIMPLE", "WALK", 2, true)]
    [DataRow("ACYCLIC", "WALK", 0, true)]
    [DataRow("ACYCLIC", "TRAIL", -1)]
    [DataRow("SIMPLE", "TRAIL", -1, true)]
    [DataRow("ACYCLIC", "TRAIL", -1, false, true)]
    [DataRow("SIMPLE", "TRAIL", -1, false, true)]
    public async Task Gql_repeated_subpath_modes_preserve_iteration_local_uniqueness(string localMode, string outerMode, int repetitions, bool anonymous = false, bool shortest = false)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        (int From, int To)[] arcs = [(0, 0), (0, 1), (1, 0), (1, 2), (2, 0), (0, 1)];
        string insert = String.Join(',', Enumerable.Range(0, 3).Select(index => $"(n{index}:RepeatMode {{idx:{index}}})")
            .Concat(arcs.Select((arc, index) => $"(n{arc.From})-[:link {{ordinal:{index}}}]->(n{arc.To})")));
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT " + insert
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode, await seed.Content.ReadAsStringAsync());
        string quantifier = repetitions < 0 ? "1," : repetitions.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string body = anonymous ? "(:RepeatMode)-[:link]->()-[:link]->()" : "(a:RepeatMode)-[e:link]->(b)-[f:link]->(c)";
        string pattern = $"MATCH REPEATABLE ELEMENTS p={(shortest ? "ALL SHORTEST " : "")}{outerMode} " +
            $"({localMode} {body}{(shortest ? " WHERE b.idx<>1" : "")}){{{quantifier}}}";
        string query = pattern + " " +
            "RETURN VALUE { FOR edge IN ELEMENTS(p) FILTER edge IS TYPED EDGE RETURN COLLECT_LIST(edge.ordinal) AS ordinals } AS answer";
        using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query
        });
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        var actual = result.GetProperty("matches").EnumerateArray().Select(row =>
            String.Join(',', row.GetProperty("bindings").GetProperty("answer").EnumerateArray().Select(value => value.GetInt32()))).ToArray();
        var expected = new List<(int Start, int End, string Route, int Length)>();
        for (int start = 0; start < 3; start++)
        {
            Enumerate([start], []);
        }
        var selected = shortest ? expected.GroupBy(path => (path.Start, path.End))
            .SelectMany(group => group.Where(path => path.Length == group.Min(item => item.Length))) : expected;
        CollectionAssert.AreEquivalent(selected.Select(path => path.Route).ToArray(), actual);
        if (anonymous)
        {
            using var visible = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = pattern + " RETURN *"
            });
            Assert.AreEqual(HttpStatusCode.OK, visible.StatusCode, await visible.Content.ReadAsStringAsync());
            foreach (var row in (await visible.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches").EnumerateArray())
            {
                CollectionAssert.AreEquivalent(new[] { "p" }, row.GetProperty("bindings").EnumerateObject().Select(property => property.Name).ToArray());
            }
        }

        void Enumerate(int[] vertices, int[] edges)
        {
            int maximumEdges = repetitions < 0 ? arcs.Length : repetitions * 2;
            if (edges.Length == maximumEdges || repetitions < 0 && edges.Length > 0 && edges.Length % 2 == 0)
            {
                if (Accepts(outerMode, vertices, edges) && Enumerable.Range(0, edges.Length / 2).All(iteration =>
                    Accepts(localMode, vertices[(iteration * 2)..(iteration * 2 + 3)], edges[(iteration * 2)..(iteration * 2 + 2)]) &&
                    (!shortest || vertices[iteration * 2 + 1] != 1)))
                {
                    expected.Add((vertices[0], vertices[^1], String.Join(',', edges), edges.Length));
                }
                if (edges.Length == maximumEdges)
                {
                    return;
                }
            }
            for (int edge = 0; edge < arcs.Length; edge++)
            {
                if (arcs[edge].From == vertices[^1])
                {
                    Enumerate([.. vertices, arcs[edge].To], [.. edges, edge]);
                }
            }
        }

        static bool Accepts(string mode, int[] vertices, int[] edges) => mode switch
        {
            "WALK" => true,
            "TRAIL" => edges.Distinct().Count() == edges.Length,
            "SIMPLE" when vertices[0] == vertices[^1] => vertices[..^1].Distinct().Count() == vertices.Length - 1,
            _ => vertices.Distinct().Count() == vertices.Length
        };
    }
}
