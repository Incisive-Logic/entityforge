using EntityForge.Db.Gql;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Runtime.CompilerServices;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using EntityForge.Query.Values;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Gql_string_codepoint_sorted_scan_preserves_order_across_spilled_runs(bool descending)
    {
        const int count = 6000;
        async IAsyncEnumerable<GqlScanInputPage> Read([EnumeratorCancellation] CancellationToken cancellation)
        {
            for (int offset = 0; offset < count; offset += 16)
            {
                cancellation.ThrowIfCancellationRequested();
                var vertices = Enumerable.Range(offset, Math.Min(16, count - offset)).Select(index =>
                    new GraphVertex(GraphVertexId.New(), new HashSet<string>(), new Dictionary<string, DatabaseValue>
                    {
                        ["textValue"] = new DatabaseValue.String(index % 2 == 0 ? "\U00010000" : "\uE000")
                    })).ToArray();
                yield return new(new(42), vertices, vertices.Length, offset + vertices.Length == count);
                await Task.Yield();
            }
        }
        int rows = 0, completed = 0;
        await foreach (var page in GraphGqlScan.ExecuteAsync(
            new("MATCH (n) RETURN n.textValue AS textValue ORDER BY textValue " + (descending ? "DESC" : "ASC")),
            Read, new(PageSize: 16, MaximumSortTemporaryBytes: 4 * 1024 * 1024)))
        {
            foreach (var row in page.Rows)
            {
                bool lower = rows++ < count / 2;
                Assert.AreEqual(lower != descending ? "\uE000" : "\U00010000", ((LiteralValue)row.Bindings["textValue"]).LexicalValue);
            }
            completed += page.Complete ? 1 : 0;
        }
        Assert.AreEqual(count, rows);
        Assert.AreEqual(1, completed);
    }

    [TestMethod]
    [DataRow("\uE000", "\U00010000")]
    [DataRow("\uFFFF", "\U00010000")]
    [DataRow("a", "a ")]
    [DataRow("A", "a")]
    [DataRow("e\u0301", "é")]
    [DataRow("\U00010000", "\U00010001")]
    [DataRow("", "a")]
    public async Task Gql_string_codepoint_order_agrees_across_predicates_sort_and_aggregates(string first, string second)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        async Task<JsonElement> Query(string query)
        {
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query,
                parameters = new
                {
                    first,
                    second
                }
            });
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, query + ": " + await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
        }
        var predicates = (await Query("RETURN $first<$second AS lt,$second>$first AS gt,$first<>$second AS ne"))[0].GetProperty("bindings");
        foreach (var result in predicates.EnumerateObject())
        {
            Assert.IsTrue(result.Value.GetBoolean(), result.Name);
        }
        var rows = await Query("FOR textValue IN [$second,$first,$second] RETURN DISTINCT textValue ORDER BY textValue");
        Assert.AreEqual(2, rows.GetArrayLength());
        Assert.AreEqual(first, rows[0].GetProperty("bindings").GetProperty("textValue").GetString());
        Assert.AreEqual(second, rows[1].GetProperty("bindings").GetProperty("textValue").GetString());
        var aggregate = (await Query("FOR textValue IN [$second,$first] RETURN MIN(textValue) AS lo,MAX(textValue) AS hi"))[0].GetProperty("bindings");
        Assert.AreEqual(first, aggregate.GetProperty("lo").GetString());
        Assert.AreEqual(second, aggregate.GetProperty("hi").GetString());
        var lists = await Query("FOR textValue IN [[$second],[$first]] RETURN textValue ORDER BY textValue");
        Assert.AreEqual(first, lists[0].GetProperty("bindings").GetProperty("textValue")[0].GetString());
        using var inserted = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (:StringOrder {textValue:$first}),(:StringOrder {textValue:$second})",
            parameters = new
            {
                first,
                second
            }
        });
        Assert.AreEqual(HttpStatusCode.OK, inserted.StatusCode, await inserted.Content.ReadAsStringAsync());
        var filtered = await Query("MATCH (n:StringOrder) WHERE n.textValue<$second RETURN n.textValue AS v ORDER BY v");
        Assert.AreEqual(1, filtered.GetArrayLength());
        Assert.AreEqual(first, filtered[0].GetProperty("bindings").GetProperty("v").GetString());
        var stored = await Query("MATCH (n:StringOrder) RETURN n.textValue AS v ORDER BY n.textValue");
        Assert.AreEqual(first, stored[0].GetProperty("bindings").GetProperty("v").GetString());
    }
}

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("\uE000", "\U00010000")]
    [DataRow("\uFFFF", "\U00010000")]
    [DataRow("a", "a ")]
    [DataRow("A", "a")]
    [DataRow("e\u0301", "é")]
    [DataRow("\U00010000", "\U00010001")]
    [DataRow("", "a")]
    public async Task Sparql_string_codepoint_order_agrees_across_predicates_sort_and_aggregates(string first, string second)
    {
        await using var database = await OpenAsync(CreateDirectory());
        var predicate = RdfSparql.Execute(database, $"SELECT ('{first}'<'{second}' AS ?lt) ('{second}'>'{first}' AS ?gt) ('{first}'!='{second}' AS ?ne) {{}}").Single();
        foreach (var textValue in predicate.Values)
        {
            Assert.AreEqual("true", ((RdfLiteral)textValue).LexicalForm);
        }
        string values = $"VALUES ?textValue {{ '{second}' '{first}' '{second}' }}";
        var rows = RdfSparql.Execute(database, $"SELECT DISTINCT ?textValue {{ {values} }} ORDER BY ?textValue").ToArray();
        Assert.AreEqual(2, rows.Length);
        Assert.AreEqual(first, ((RdfLiteral)rows[0]["textValue"]).LexicalForm);
        Assert.AreEqual(second, ((RdfLiteral)rows[1]["textValue"]).LexicalForm);
        var aggregate = RdfSparql.Execute(database, $"SELECT (MIN(?textValue) AS ?lo) (MAX(?textValue) AS ?hi) {{ {values} }}").Single();
        Assert.AreEqual(first, ((RdfLiteral)aggregate["lo"]).LexicalForm);
        Assert.AreEqual(second, ((RdfLiteral)aggregate["hi"]).LexicalForm);
    }
}
