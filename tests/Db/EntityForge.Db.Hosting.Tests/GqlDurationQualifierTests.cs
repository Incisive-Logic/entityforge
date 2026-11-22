using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Query.Querying;
using EntityForge.Query.Values;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    public void Gql_standard_duration_qualifiers_keep_native_frequency_estimates_separate()
    {
        var zero = new EntityForge.Values.CalendarDuration(0, 0, 0, 0);
        var native = new DatabaseValue.Duration(zero);
        var months = new DatabaseValue.Duration(zero, DatabaseDurationQualifier.YearToMonth);
        var days = new DatabaseValue.Duration(zero, DatabaseDurationQualifier.DayToSecond);
        var values = new[] { native, months, days, days };
        var statistics = GraphQueryStatistics.From(new(1), values.Select(value =>
            new GraphVertex(GraphVertexId.New(), new HashSet<string>(), new Dictionary<string, DatabaseValue> { ["span"] = value })), []);
        Assert.AreEqual(1L, statistics.EstimateVertexValue("span", native));
        Assert.AreEqual(1L, statistics.EstimateVertexValue("span", months));
        Assert.AreEqual(2L, statistics.EstimateVertexValue("span", days));
    }

    [TestMethod]
    public void Gql_standard_duration_comparison_does_not_infer_a_missing_native_qualifier()
    {
        var native = new LiteralValue("PT0S", QueryScalarKind.Duration);
        foreach (string operation in new[] { "ABS", "unary+", "unary-" })
        {
            var expression = new GraphScalarExpression(null, null, null, operation, [new(null, null, native)]);
            Assert.ThrowsExactly<ArgumentException>(() => GraphScalarEvaluator.Evaluate(expression, (_, _) => UnboundValue.Instance));
        }
        foreach (string family in new[] { "year-to-month", "day-to-second" })
        {
            var qualified = new LiteralValue("PT0S", QueryScalarKind.Duration, new Uri("urn:entityforge:duration:" + family));
            Assert.AreNotEqual(GraphScalarEvaluator.DistinctKey(native), GraphScalarEvaluator.DistinctKey(qualified));
            foreach (var pair in new[] { (native, qualified), (qualified, native) })
            {
                var comparison = new GraphScalarExpression(null, null, null, "=",
                    [new(null, null, pair.Item1), new(null, null, pair.Item2)]);
                Assert.ThrowsExactly<ArgumentException>(() => GraphScalarEvaluator.Evaluate(comparison, (_, _) => UnboundValue.Instance));
            }
        }
    }

    [TestMethod]
    public async Task Gql_standard_duration_qualifiers_preserve_zero_identity_through_replay_and_restart()
    {
        _directory = NewDirectory();
        var write = new
        {
            statement = "INSERT (n:QualifiedSpan {monthZero:DURATION('P0M'),dayZero:DURATION('PT0S')," +
                "months:[DURATION('P0M'),DURATION('P1M')],days:[DURATION('PT0S'),DURATION('P1D')]}) " +
                "SET n.monthDifference=DURATION_BETWEEN(DATE('2024-01-01'),DATE('2024-01-01')) YEAR TO MONTH," +
                "n.dayDifference=DURATION_BETWEEN(DATE('2024-01-01'),DATE('2024-01-01')) " +
                "RETURN n.monthZero IS TYPED DURATION(YEAR TO MONTH) AND n.dayZero IS TYPED DURATION(DAY TO SECOND) AS answer",
            transactionId = Guid.NewGuid()
        };
        string? receipt = null;
        for (int restart = 0; restart < 2; restart++)
        {
            await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
                .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
            await host.StartAsync();
            using var client = Client(host);
            using var update = await client.PostAsJsonAsync("/v1/graph/gql/update", write);
            Assert.AreEqual(HttpStatusCode.OK, update.StatusCode, await update.Content.ReadAsStringAsync());
            receipt ??= await update.Content.ReadAsStringAsync();
            Assert.AreEqual(receipt, await update.Content.ReadAsStringAsync());
            Assert.IsTrue((await update.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result").GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetBoolean());
            foreach (string query in new[]
            {
                "MATCH (n:QualifiedSpan) RETURN COUNT(*)=1 AS answer",
                "MATCH (n:QualifiedSpan) RETURN n.monthZero IS TYPED DURATION(YEAR TO MONTH) NOT NULL AND n.monthZero IS NOT TYPED DURATION(DAY TO SECOND) AS answer",
                "MATCH (n:QualifiedSpan) RETURN n.dayZero IS TYPED DURATION(DAY TO SECOND) NOT NULL AND n.dayZero IS NOT TYPED DURATION(YEAR TO MONTH) AS answer",
                "MATCH (n:QualifiedSpan) RETURN n.months IS TYPED LIST<DURATION(YEAR TO MONTH) NOT NULL> AND n.days IS TYPED LIST<DURATION(DAY TO SECOND) NOT NULL> AS answer",
                "MATCH (n:QualifiedSpan) RETURN n.monthDifference IS TYPED DURATION(YEAR TO MONTH) AND n.dayDifference IS TYPED DURATION(DAY TO SECOND) AS answer",
                "MATCH (n:QualifiedSpan) RETURN CAST(n.monthZero AS DURATION(YEAR TO MONTH)) IS TYPED DURATION(YEAR TO MONTH) AS answer",
                "MATCH (n:QualifiedSpan) LET magnitude=ABS(n.monthZero),negative=-n.monthZero RETURN magnitude IS TYPED DURATION(YEAR TO MONTH) AND negative IS TYPED DURATION(YEAR TO MONTH) AS answer",
                "MATCH (n:QualifiedSpan) LET difference=n.monthZero-n.monthZero,scaled=n.monthZero*0 RETURN difference IS TYPED DURATION(YEAR TO MONTH) AND scaled IS TYPED DURATION(YEAR TO MONTH) AS answer",
                "MATCH (n:QualifiedSpan) FOR span IN [n.monthZero,n.dayZero] RETURN COUNT(DISTINCT span)=2 AS answer"
            })
            {
                using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
                {
                    query
                });
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, query + ": " + await response.Content.ReadAsStringAsync());
                var rows = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
                Assert.AreEqual(1, rows.GetArrayLength(), query);
                Assert.IsTrue(rows[0].GetProperty("bindings").GetProperty("answer").GetBoolean(), query);
            }
            using var invalidCast = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = "MATCH (n:QualifiedSpan) RETURN CAST(n.monthZero AS DURATION(DAY TO SECOND)) AS answer"
            });
            Assert.AreEqual(HttpStatusCode.BadRequest, invalidCast.StatusCode, await invalidCast.Content.ReadAsStringAsync());
            await host.StopAsync();
        }
    }
}
