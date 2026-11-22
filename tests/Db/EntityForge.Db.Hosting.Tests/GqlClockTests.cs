using EntityForge.Db.Gql;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using EntityForge.Query.Results;
using EntityForge.Query.Values;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow(1, "12:30:00Z")]
    [DataRow(7, "12:30:00Z")]
    public async Task Gql_standard_statement_time_uses_utc_in_each_season(int month, string expected)
    {
        var compiler = new GraphGqlQueryCompiler(new DateTimeOffset(2024, month, 1, 12, 30, 0, TimeSpan.Zero));
        var plan = compiler.Compile(new(GraphGqlStandardSyntax.Prepare("RETURN CURRENT_TIME AS answer")));
        var answer = await plan.ExecuteAsync(_ =>
            new(new QueryResultEnvelope(Guid.NewGuid(), new GraphPatternResult(new GraphResult([], []), []))), CancellationToken.None);
        var value = (LiteralValue)((GraphPatternResult)answer.Result).Matches.Single().Bindings["answer"];
        Assert.AreEqual(expected, value.LexicalValue);
    }

    [TestMethod]
    public async Task Gql_standard_reused_clock_plan_captures_each_execution()
    {
        var plan = new GraphGqlQueryCompiler().Compile(new(GraphGqlStandardSyntax.Prepare("RETURN CURRENT_TIMESTAMP AS captured")));
        ValueTask<QueryResultEnvelope> Read(CompiledGraphGqlQuery _) =>
            new(new QueryResultEnvelope(Guid.NewGuid(), new GraphPatternResult(new GraphResult([], []), [])));
        async Task<long> Execute()
        {
            var answer = await plan.ExecuteAsync(Read, CancellationToken.None);
            var value = (LiteralValue)((GraphPatternResult)answer.Result).Matches.Single().Bindings["captured"];
            return DateTimeOffset.Parse(value.LexicalValue, System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeMilliseconds();
        }
        long first = await Execute();
        await Task.Delay(25);
        long second = await Execute();
        Assert.IsTrue(second > first);
        Assert.IsTrue(first > DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds());
    }

    [TestMethod]
    public async Task Gql_standard_statement_clocks_share_one_instant_and_update_replay_preserves_it()
    {
        _directory = NewDirectory();
        Guid transactionId = Guid.NewGuid();
        const string statement = "FOR i IN [1,2] INSERT (n:ClockSample {stamp:CURRENT_TIMESTAMP,localStamp:LOCAL_TIMESTAMP,calendarDate:CURRENT_DATE}) RETURN n.stamp AS captured,n.localStamp AS localCaptured";
        string? original = null;
        for (int restart = 0; restart < 2; restart++)
        {
            await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
                .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
            await host.StartAsync();
            using var client = Client(host);
            long before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            using var written = await client.PostAsJsonAsync("/v1/graph/gql/update", new
            {
                statement,
                transactionId
            });
            Assert.AreEqual(HttpStatusCode.OK, written.StatusCode, await written.Content.ReadAsStringAsync());
            var update = (await written.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result");
            var rows = update.GetProperty("matches");
            Assert.AreEqual(2, rows.GetArrayLength());
            var first = rows[0].GetProperty("bindings");
            Assert.AreEqual(first.GetProperty("captured").GetRawText(), rows[1].GetProperty("bindings").GetProperty("captured").GetRawText());
            Assert.AreEqual(first.GetProperty("localCaptured").GetRawText(), rows[1].GetProperty("bindings").GetProperty("localCaptured").GetRawText());
            if (restart == 0)
            {
                long stamp = DateTimeOffset.Parse(first.GetProperty("captured").GetString()!, System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeMilliseconds();
                Assert.IsTrue(stamp >= before && stamp <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                original = update.GetRawText();
            }
            else
            {
                Assert.AreEqual(original, update.GetRawText());
            }
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = "MATCH (n:ClockSample) RETURN COUNT(*)=2 AND COUNT(DISTINCT n.stamp)=1 AND COUNT(DISTINCT n.localStamp)=1 AND COUNT(DISTINCT n.calendarDate)=1 AS answer"
            });
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            Assert.IsTrue((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetBoolean());
            using var dated = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = "MATCH (n:ClockSample) RETURN n.stamp AS captured,n.localStamp AS localCaptured"
            });
            Assert.AreEqual(HttpStatusCode.OK, dated.StatusCode, await dated.Content.ReadAsStringAsync());
            foreach (var stored in (await dated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches").EnumerateArray())
            {
                Assert.AreEqual(first.GetProperty("captured").GetRawText(), stored.GetProperty("bindings").GetProperty("captured").GetRawText());
                Assert.AreEqual(first.GetProperty("localCaptured").GetRawText(), stored.GetProperty("bindings").GetProperty("localCaptured").GetRawText());
            }
        }
    }

    [TestMethod]
    public void Gql_standard_clock_binding_preserves_literals_and_uses_utc()
    {
        var instant = new DateTimeOffset(2024, 3, 1, 0, 30, 0, TimeSpan.FromHours(2));
        string source = GraphGqlStandardSyntax.Prepare("RETURN CURRENT_DATE AS \"CURRENT_DATE\",LOCAL_TIME AS \"LOCAL_TIME\",LOCAL_TIMESTAMP AS \"LOCAL_TIMESTAMP\",'date()' AS \"'date()'\" // CURRENT_TIMESTAMP");
        string bound = GraphGqlQueryCompiler.BindStatementClock(source, instant);
        StringAssert.Contains(bound, "date('2024-02-29')");
        StringAssert.Contains(bound, "localtime('22:30:00.0000000')");
        StringAssert.Contains(bound, "localdatetime('2024-02-29T22:30:00.0000000')");
        StringAssert.Contains(bound, "'date()'");
        Assert.IsFalse(bound.Contains("//", StringComparison.Ordinal));
    }
}
