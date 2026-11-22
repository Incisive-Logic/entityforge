using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("WALK", false)]
    [DataRow("TRAIL", false)]
    [DataRow("SIMPLE", false)]
    [DataRow("ACYCLIC", false)]
    [DataRow("WALK", true)]
    [DataRow("TRAIL", true)]
    [DataRow("SIMPLE", true)]
    [DataRow("ACYCLIC", true)]
    public async Task Gql_standard_path_bags_match_an_independent_finite_enumeration(string mode, bool bothDirections)
    {
        // Five distinct physical edges: a self-loop, parallel edges and a cycle.
        // Vertex 1 is deliberately not Terminal: endpoint constraints must not
        // accidentally constrain the interior of a quantified relationship.
        (int From, int To)[] edges = [(0, 0), (0, 1), (0, 1), (1, 2), (2, 0)];
        var oracle = new List<(int Start, int End, int Length)>();
        for (int start = 0; start < 3; start++)
        {
            Enumerate([start], []);
        }
        void Enumerate(List<int> vertices, List<int> traversed)
        {
            bool allowed = mode switch
            {
                "WALK" => true,
                "TRAIL" => traversed.Distinct().Count() == traversed.Count,
                "ACYCLIC" => vertices.Distinct().Count() == vertices.Count,
                "SIMPLE" => vertices.Distinct().Count() == vertices.Count ||
                    vertices[0] == vertices[^1] && vertices.Take(vertices.Count - 1).Distinct().Count() == vertices.Count - 1,
                _ => throw new AssertFailedException("Unknown test path mode.")
            };
            if (allowed && vertices[^1] != 1)
            {
                oracle.Add((vertices[0], vertices[^1], traversed.Count));
            }
            if (traversed.Count == 4)
            {
                return;
            }
            for (int id = 0; id < edges.Length; id++)
            {
                var edge = edges[id];
                if (edge.From == vertices[^1])
                {
                    Enumerate([.. vertices, edge.To], [.. traversed, id]);
                }
                if (bothDirections && edge.To == vertices[^1] && edge.From != edge.To)
                {
                    Enumerate([.. vertices, edge.From], [.. traversed, id]);
                }
            }
        }
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        var nodes = Enumerable.Range(0, 3).Select(index => $"(v{index}:Vertex{(index == 1 ? "" : "&Terminal")} {{key:{index}}})");
        var relationships = edges.Select(edge => $"(v{edge.From})-[:route]->(v{edge.To})");
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT " + String.Join(",", nodes.Concat(relationships))
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode, await seed.Content.ReadAsStringAsync());
        string direction = bothDirections ? "-" : "->";
        string Pattern(int low, int high) => $"(a:Vertex)-[:route]{direction}{{{low},{high}}}(b:Terminal)";
        const string projection = " RETURN a.key AS startKey,b.key AS endKey,PATH_LENGTH(p) AS pathSize";
        static string Key((int Start, int End, int Length) row) => $"{row.Start},{row.End},{row.Length}";
        async Task<string[]> Execute(string query)
        {
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query
            });
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, query + ": " + await response.Content.ReadAsStringAsync());
            var result = await response.Content.ReadFromJsonAsync<JsonElement>();
            return result.GetProperty("matches").EnumerateArray().Select(row =>
            {
                var binding = row.GetProperty("bindings");
                return Key((binding.GetProperty("startKey").GetInt32(), binding.GetProperty("endKey").GetInt32(),
                    binding.GetProperty("pathSize").GetInt32()));
            }).ToArray();
        }
        var exactBags = new List<string>();
        for (int length = 0; length <= 4; length++)
        {
            string[] actual = await Execute($"MATCH REPEATABLE ELEMENTS p={mode} {Pattern(length, length)}" + projection);
            CollectionAssert.AreEquivalent(oracle.Where(path => path.Length == length).Select(Key).ToArray(), actual,
                $"Incorrect bag for {mode}, direction {direction}, exact length {length}.");
            exactBags.AddRange(actual);
        }
        string[] combined = await Execute($"MATCH REPEATABLE ELEMENTS p={mode} {Pattern(0, 4)}" + projection);
        CollectionAssert.AreEquivalent(oracle.Select(Key).ToArray(), combined);
        CollectionAssert.AreEquivalent(exactBags.ToArray(), combined, "A finite range must preserve the bag union of its disjoint exact lengths.");
        string[] positive = await Execute($"MATCH REPEATABLE ELEMENTS p={mode} {Pattern(1, 4)}" + projection);
        CollectionAssert.AreEquivalent(oracle.Where(path => path.Length > 0).Select(Key).ToArray(), positive);

        // Filter complete candidates before shortest selection. Compare all ties
        // as a bag; no engine's output is used to manufacture expected answers.
        string[] shortest = await Execute($"MATCH REPEATABLE ELEMENTS ALL SHORTEST (p={mode} {Pattern(0, 4)} WHERE PATH_LENGTH(p)>=2)" + projection);
        var expectedShortest = oracle.Where(path => path.Length >= 2).GroupBy(path => (path.Start, path.End))
            .SelectMany(group => group.Where(path => path.Length == group.Min(item => item.Length))).Select(Key).ToArray();
        CollectionAssert.AreEquivalent(expectedShortest, shortest);
    }
}
