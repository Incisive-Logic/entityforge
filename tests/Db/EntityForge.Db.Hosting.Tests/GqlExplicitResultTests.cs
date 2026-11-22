using EntityForge.Db.Gql;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    public async Task Gql_selected_standard_results_match_explicit_expectations()
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        string label = "StandardResults_" + Guid.NewGuid().ToString("N");
        string seed = $"INSERT (a:{label} {{age:20,name:'Alice'}}),(b:{label} {{age:10,name:'Bob'}}),(c:{label} {{name:'Carol'}})," +
            "(a)-[:knows {weight:2}]->(b),(a)-[:knows {weight:1}]->(c)";
        using var seeded = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = seed
        });
        Assert.AreEqual(HttpStatusCode.OK, seeded.StatusCode, await seeded.Content.ReadAsStringAsync());
        foreach (var (query, expected, columns) in new[]
        {
            ($"MATCH (n:{label}) RETURN n.name AS answer ORDER BY answer", "[[\"Alice\"],[\"Bob\"],[\"Carol\"]]", new[] { "answer" }),
            ($"MATCH (n:{label}) WHERE n.age>=10 RETURN n.name AS answer ORDER BY answer", "[[\"Alice\"],[\"Bob\"]]", new[] { "answer" }),
            ($"MATCH (n:{label}) RETURN COUNT(*) AS total,SUM(n.age) AS amount", "[[3,30]]", new[] { "total", "amount" }),
            ($"MATCH (n:{label}) WHERE n.age<0 RETURN COUNT(*) AS answer", "[[0]]", new[] { "answer" }),
            ($"MATCH (n:{label} {{name:'Alice'}}) OPTIONAL MATCH (n)-[:missing]->(m) RETURN m.name AS answer", "[[null]]", new[] { "answer" }),
            ($"MATCH (n:{label})-[e:knows]->() RETURN e.weight AS answer ORDER BY answer", "[[1],[2]]", new[] { "answer" }),
            ($"MATCH (n:{label}) RETURN n.name AS person,EXISTS {{ MATCH (n)-[:knows]->() }} AS answer ORDER BY person", "[[\"Alice\",true],[\"Bob\",false],[\"Carol\",false]]", new[] { "person", "answer" }),
            ($"MATCH (n:{label} {{name:'Alice'}}) CALL (n) {{ RETURN n.age+1 AS nextAge }} RETURN nextAge AS answer", "[[21]]", new[] { "answer" }),
            ("RETURN (TRUE OR FALSE) XOR TRUE AS answer", "[[false]]", new[] { "answer" }),
            ("RETURN 1 AS answer UNION ALL RETURN 1 AS answer", "[[1],[1]]", new[] { "answer" }),
            ("RETURN NULL IS NULL AS answer", "[[true]]", new[] { "answer" })
        })
        {
            // Expected rows are independent of the database implementation.
            // These selected cases do not establish complete GQL conformance.
            GraphGqlStandardSyntax.Validate(query);
            using var wanted = JsonDocument.Parse(expected);
            string[] expectedRows = wanted.RootElement.EnumerateArray()
                .Select(row => JsonSerializer.Serialize(row.EnumerateArray().Select(ScalarKey))).ToArray();
            using var actual = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query
            });
            Assert.AreEqual(HttpStatusCode.OK, actual.StatusCode, await actual.Content.ReadAsStringAsync());
            string[] actualRows = (await actual.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches").EnumerateArray()
                .Select(row => JsonSerializer.Serialize(columns.Select(column => ScalarKey(row.GetProperty("bindings").GetProperty(column))))).ToArray();
            CollectionAssert.AreEqual(expectedRows, actualRows, query);
        }
        await host.StopAsync();
    }

    private static string ScalarKey(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => NumericJsonKey(value.GetRawText()),
        JsonValueKind.String => "string:" + value.GetString(),
        JsonValueKind.Array => JsonSerializer.Serialize(value.EnumerateArray().Select(ScalarKey)),
        _ => value.GetRawText()
    };

    private static string NumericJsonKey(string lexical)
    {
        if (Double.TryParse(lexical, NumberStyles.Float, CultureInfo.InvariantCulture, out double floating) &&
            Double.IsFinite(floating) && floating != 0 && (Math.Abs(floating) < 1e-28 || Math.Abs(floating) > (double)Decimal.MaxValue))
        {
            // Java and .NET can emit different shortest strings for the same
            // subnormal binary64 value (4.9e-324 and 5e-324, for example).
            return "number:binary64:" + BitConverter.DoubleToInt64Bits(floating).ToString(CultureInfo.InvariantCulture);
        }
        bool negative = lexical.StartsWith('-');
        if (negative)
        {
            lexical = lexical[1..];
        }
        int marker = lexical.IndexOfAny(['e', 'E']);
        int exponent = marker < 0 ? 0 : Int32.Parse(lexical[(marker + 1)..], CultureInfo.InvariantCulture);
        if (marker >= 0)
        {
            lexical = lexical[..marker];
        }
        int point = lexical.IndexOf('.');
        exponent -= point < 0 ? 0 : lexical.Length - point - 1;
        string digits = lexical.Replace(".", "", StringComparison.Ordinal).TrimStart('0');
        if (digits.Length == 0)
        {
            return "number:0";
        }
        string normalized = digits.TrimEnd('0');
        exponent += digits.Length - normalized.Length;
        return "number:" + (negative ? "-" : "") + normalized + "E" + exponent.ToString(CultureInfo.InvariantCulture);
    }
}
