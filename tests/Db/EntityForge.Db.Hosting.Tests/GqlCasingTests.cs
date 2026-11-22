using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EntityForge.Db.Graph;
using EntityForge.Query.Querying;
using EntityForge.Query.Values;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    public void Gql_case_conversion_bounds_input_and_expanded_output()
    {
        QueryValue Upper(string value) => GraphScalarEvaluator.Evaluate(
            new GraphScalarExpression(null, null, null, "UPPER",
                [new GraphScalarExpression(null, null, new LiteralValue(value))]),
            (_, _) => UnboundValue.Instance);
        Assert.ThrowsExactly<ArgumentException>(() => Upper(new string('a', 1_048_577)));
        Assert.ThrowsExactly<ArgumentException>(() => Upper(new string('\uFB03', 350_000)));
    }

    [TestMethod]
    public async Task Gql_case_conversion_persists_full_mappings_and_replays_after_restart()
    {
        _directory = NewDirectory();
        Guid operation = Guid.NewGuid();
        string? captured = null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
                .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
            await host.StartAsync();
            using var client = Client(host);
            using var update = await client.PostAsJsonAsync("/v1/graph/gql/update", new
            {
                statement = "INSERT (n:UnicodeCase {original:'Straße',foldedUpper:UPPER('Straße'),foldedLower:LOWER('ΟΣ')}) RETURN n.foldedUpper AS upperText,n.foldedLower AS lowerText",
                transactionId = operation
            });
            Assert.AreEqual(HttpStatusCode.OK, update.StatusCode, await update.Content.ReadAsStringAsync());
            var result = (await update.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result");
            captured ??= result.GetRawText();
            Assert.AreEqual(captured, result.GetRawText());
            var row = result.GetProperty("matches")[0].GetProperty("bindings");
            Assert.AreEqual("STRASSE", row.GetProperty("upperText").GetString());
            Assert.AreEqual("ος", row.GetProperty("lowerText").GetString());
            using var query = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = "MATCH (n:UnicodeCase) RETURN n.original AS original,n.foldedUpper AS upperText,n.foldedLower AS lowerText"
            });
            Assert.AreEqual(HttpStatusCode.OK, query.StatusCode);
            var matches = (await query.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
            Assert.AreEqual(1, matches.GetArrayLength());
            Assert.AreEqual("Straße", matches[0].GetProperty("bindings").GetProperty("original").GetString());
            Assert.AreEqual("STRASSE", matches[0].GetProperty("bindings").GetProperty("upperText").GetString());
            Assert.AreEqual("ος", matches[0].GetProperty("bindings").GetProperty("lowerText").GetString());
            await host.StopAsync();
        }
    }
}
