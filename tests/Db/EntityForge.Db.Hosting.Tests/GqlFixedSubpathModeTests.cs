using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("WALK", "WALK")]
    [DataRow("TRAIL", "WALK")]
    [DataRow("ACYCLIC", "WALK")]
    [DataRow("SIMPLE", "WALK")]
    [DataRow("WALK", "TRAIL")]
    [DataRow("TRAIL", "SIMPLE")]
    [DataRow("SIMPLE", "TRAIL")]
    [DataRow("WALK", "ACYCLIC")]
    [DataRow("WALK", "WALK", true)]
    [DataRow("TRAIL", "WALK", true)]
    [DataRow("ACYCLIC", "WALK", true)]
    [DataRow("SIMPLE", "WALK", true)]
    [DataRow("WALK", "TRAIL", true)]
    [DataRow("TRAIL", "SIMPLE", true)]
    [DataRow("SIMPLE", "TRAIL", true)]
    [DataRow("WALK", "ACYCLIC", true)]
    public async Task Fixed_subpath_modes_match_finite_path_enumeration(string innerMode, string outerMode, bool nested = false)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        var nodes = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToArray();
        (int From, int To)[] arcs = [(0, 0), (0, 1), (1, 0), (1, 2), (2, 0), (0, 1)];
        var edges = arcs.Select(_ => Guid.NewGuid()).ToArray();
        string insert = String.Join(',', nodes.Select((id, index) => $"(n{index}:ModeNode {{id:'{id}',idx:{index}}})")
            .Concat(arcs.Select((arc, index) => $"(n{arc.From})-[:link {{id:'{edges[index]}',ordinal:{index}}}]->(n{arc.To})")));
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT " + insert
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode, await seed.Content.ReadAsStringAsync());
        string query = $"MATCH REPEATABLE ELEMENTS p={outerMode} (a:ModeNode WHERE a.idx=0)-[edgeZero:link]->" +
            $"({innerMode} (b)-[edgeOne:link]->(c)-[edgeTwo:link]->(d)) " +
            "RETURN edgeZero.ordinal AS o,edgeOne.ordinal AS f,edgeTwo.ordinal AS s";
        if (nested)
        {
            query = query.Replace("(c)-[edgeTwo:link]->(d))", "(ACYCLIC (c)-[edgeTwo:link]->(d) WHERE d.idx<>1) WHERE b.idx<>2)");
        }
        using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query
        });
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        var actual = result.GetProperty("matches").EnumerateArray().Select(row =>
        {
            var bindings = row.GetProperty("bindings");
            return String.Join('/', new[] { "o", "f", "s" }.Select(name => bindings.GetProperty(name).GetInt32()));
        }).ToArray();
        var expected = new List<string>();
        for (int first = 0; first < arcs.Length; first++)
        {
            for (int second = 0; second < arcs.Length; second++)
            {
                for (int third = 0; third < arcs.Length; third++)
                {
                    if (arcs[first].From != 0 || arcs[first].To != arcs[second].From || arcs[second].To != arcs[third].From)
                    {
                        continue;
                    }
                    int[] vertices = [0, arcs[first].To, arcs[second].To, arcs[third].To];
                    int[] route = [first, second, third];
                    if (Accepts(innerMode, vertices[1..], route[1..]) && Accepts(outerMode, vertices, route) &&
                        (!nested || vertices[2] != vertices[3] && vertices[3] != 1 && vertices[1] != 2))
                    {
                        expected.Add(String.Join('/', route));
                    }
                }
            }
        }
        CollectionAssert.AreEquivalent(expected.ToArray(), actual);

        static bool Accepts(string mode, int[] vertices, int[] route)
        {
            if (mode == "WALK")
            {
                return true;
            }
            if (mode == "TRAIL")
            {
                return route.Distinct().Count() == route.Length;
            }
            for (int first = 0; first < vertices.Length; first++)
            {
                for (int second = first + 1; second < vertices.Length; second++)
                {
                    if (vertices[first] == vertices[second] &&
                        !(mode == "SIMPLE" && first == 0 && second == vertices.Length - 1))
                    {
                        return false;
                    }
                }
            }
            return true;
        }
    }
}
