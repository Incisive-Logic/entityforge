using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("café")]
    [DataRow("变量")]
    [DataRow("Δοκιμή")]
    [DataRow("𐐀CASE")]
    [DataRow("e\u0301")]
    [DataRow("‿CASE")]
    [DataRow("RETURN变量")]
    [DataRow("SUMΔ")]
    [DataRow("GRAPH𐐀")]
    [DataRow("ALL𐐀")]
    [DataRow("DISTINCT𐐀")]
    [DataRow("℘")]
    [DataRow("a·b")]
    [DataRow("\u1C89name")]
    [DataRow("\U000105C0name")]
    [DataRow("\U00011380name")]
    [DataRow("\U00013460name")]
    [DataRow("name\U0001E5F1")]
    [DataRow("name\U000113B8")]
    public async Task Gql_standard_Unicode_identifiers_preserve_bindings_labels_and_parameters(string variable)
    {
        _directory = NewDirectory();
        string label = "Label_" + variable, edge = "edge_" + variable, other = "other_" + variable;
        string property = "property_" + variable, parameter = "input_" + variable, alias = "result_" + variable;
        var seed = new
        {
            statement = $"INSERT ({variable}:{label} {{{property}:1}})-[{edge}:UnicodeLink {{{property}:2}}]->({other}:UnicodeTarget)",
            transactionId = Guid.NewGuid()
        };
        string? receipt = null;
        for (int restart = 0; restart < 2; restart++)
        {
            await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
                .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
            await host.StartAsync();
            using var client = Client(host);
            client.Timeout = TimeSpan.FromSeconds(15);
            using var seeded = await client.PostAsJsonAsync("/v1/graph/gql/update", seed);
            string body = await seeded.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, seeded.StatusCode, body);
            receipt ??= body;
            Assert.AreEqual(receipt, body);
            foreach (string query in new[]
            {
                $"MATCH ({variable}:{label})-[{edge}:UnicodeLink]->({other}) WHERE {variable}.{property}=${parameter} RETURN {variable}.{property}+{edge}.{property} AS {alias}",
                $"FOR {variable} IN [1,2] RETURN SUM({variable}) AS {alias}",
                $"LET {variable}=3 RETURN {variable} UNION LET {variable}=3 RETURN {variable}",
                $"MATCH ({variable}:{label}) MATCH ANY ({variable})-[{edge}:UnicodeLink WHERE {edge}.{property}>0]->({other}) RETURN {variable}.{property}+{edge}.{property} AS {alias}",
                $"RETURN 1 AS {variable} NEXT RETURN {variable}+2 AS {alias}",
                $"LET {variable}=1 CALL ({variable}) {{ RETURN {variable}+2 AS {alias} }} RETURN {alias}"
            })
            {
                using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
                {
                    query,
                    parameters = new Dictionary<string, int> { [parameter] = 1 }
                });
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, query + ": " + await response.Content.ReadAsStringAsync());
                var rows = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
                Assert.AreEqual(1, rows.GetArrayLength(), query);
                Assert.AreEqual(3, rows[0].GetProperty("bindings").GetProperty(query.Contains("UNION", StringComparison.Ordinal) ? variable : alias).GetInt32(), query);
            }
            await host.StopAsync();
        }
    }

    [TestMethod]
    [DataRow(129)]
    [DataRow(257)]
    [DataRow(1024)]
    public async Task Gql_standard_long_regular_identifiers_preserve_names_through_restart(int length)
    {
        _directory = NewDirectory();
        string variable = "n" + new string('a', length - 1);
        string label = "L" + new string('b', length - 1);
        string property = "p" + new string('c', length - 1);
        string parameter = "v" + new string('d', length - 1);
        string aliasPrefix = "r" + new string('e', length - 2);
        string firstAlias = aliasPrefix + "a", secondAlias = aliasPrefix + "b";
        Guid operation = Guid.NewGuid();
        string? receipt = null;
        for (int restart = 0; restart < 2; restart++)
        {
            await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
                .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
            await host.StartAsync();
            using var client = Client(host);
            using var inserted = await client.PostAsJsonAsync("/v1/graph/gql/update", new
            {
                statement = $"INSERT ({variable}:{label} {{{property}:1}})",
                transactionId = operation
            });
            Assert.AreEqual(HttpStatusCode.OK, inserted.StatusCode, await inserted.Content.ReadAsStringAsync());
            receipt ??= await inserted.Content.ReadAsStringAsync();
            Assert.AreEqual(receipt, await inserted.Content.ReadAsStringAsync());
            using var result = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = $"MATCH ({variable}:{label}) WHERE {variable}.{property}=${parameter} RETURN {variable}.{property} AS {firstAlias},{variable}.{property}+1 AS {secondAlias}",
                parameters = new Dictionary<string, int> { [parameter] = 1 }
            });
            Assert.AreEqual(HttpStatusCode.OK, result.StatusCode, await result.Content.ReadAsStringAsync());
            var rows = (await result.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
            Assert.AreEqual(1, rows.GetArrayLength());
            var bindings = rows[0].GetProperty("bindings");
            Assert.AreEqual(2, bindings.EnumerateObject().Count());
            Assert.AreEqual(1, bindings.GetProperty(firstAlias).GetInt32());
            Assert.AreEqual(2, bindings.GetProperty(secondAlias).GetInt32());
            await host.StopAsync();
        }
    }
}
