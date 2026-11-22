using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    public async Task Gql_standard_temporal_values_remain_typed_through_writes_and_restart()
    {
        _directory = NewDirectory();
        for (int restart = 0; restart < 2; restart++)
        {
            await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
                .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
            await host.StartAsync();
            using var client = Client(host);
            if (restart == 0)
            {
                foreach (string statement in new[]
                {
                    "INSERT (n:CalendarSample {eventDate:DATE '2020-02-29',dates:[DATE '2019-01-01',DATE '2021-01-01'],span:DURATION 'P1M',daySpan:DURATION 'P1D',spans:[DURATION 'P1D',DURATION '-PT0.5S']})",
                    "MATCH (n:CalendarSample) SET n.nextDate=n.eventDate+n.span",
                    "INSERT (:SpanStart)-[:spans {daySpan:DURATION 'P1D'}]->(:SpanEnd)",
                    "INSERT (:TemporalSample {wallTime:LOCAL_TIME('12:30:45.123456789'),wallTimes:[LOCAL_TIME('00:00:00'),LOCAL_TIME('23:59:59.999999999')]})",
                    "MATCH (n:TemporalSample) SET n.zonedStamp=ZONED_DATETIME('2020-02-29T12:30:45.123456789+01:00'),n.zonedStamps=[ZONED_DATETIME('2019-01-01T00:00:00Z'),ZONED_DATETIME('2021-01-01T00:00:00Z')]",
                    "MATCH (n:TemporalSample) SET n.zonedTime=ZONED_TIME('12:30:45.123456789+01:00'),n.zonedTimes=[ZONED_TIME('00:00:00Z'),ZONED_TIME('12:00:00+01:00')]",
                    "MATCH (n:TemporalSample) SET n.wallStamp=LOCAL_DATETIME('2020-02-29T23:59:59.999999999'),n.wallStamps=[LOCAL_DATETIME('2019-01-01T00:00:00'),LOCAL_DATETIME('2021-01-01T00:00:00')]",
                    "INSERT (:CalendarKey {eventDate:DATE '2020-02-29',hits:1})",
                    "MATCH (n:CalendarKey {eventDate:DATE '2020-02-29'}) SET n.hits=n.hits+1"
                })
                {
                    using var written = await client.PostAsJsonAsync("/v1/graph/gql/update", new
                    {
                        statement
                    });
                    Assert.AreEqual(HttpStatusCode.OK, written.StatusCode, statement + ": " + await written.Content.ReadAsStringAsync());
                }
            }
            foreach (string query in new[]
            {
                "MATCH (n:CalendarSample {eventDate:DATE '2020-02-29'}) RETURN COUNT(n)=1 AS answer",
                "MATCH (n:CalendarSample {span:DURATION 'P1M'}) RETURN COUNT(n)=1 AS answer",
                "MATCH (n:CalendarSample {daySpan:DURATION 'PT24H'}) RETURN COUNT(n)=1 AS answer",
                "MATCH (n:CalendarSample) WHERE n.daySpan=DURATION 'PT24H' RETURN COUNT(n)=1 AS answer",
                "MATCH (n:CalendarSample) RETURN n.daySpan=DURATION 'PT24H' AS answer",
                "MATCH (:SpanStart)-[:spans {daySpan:DURATION 'PT24H'}]->{1,2}(:SpanEnd) RETURN COUNT(*)=1 AS answer",
                "MATCH (n:CalendarSample) RETURN n.eventDate IS TYPED DATE AS answer",
                "MATCH (n:CalendarSample) RETURN n.span=DURATION 'P1M' AS answer",
                "MATCH (n:CalendarSample) RETURN n.nextDate=DATE '2020-03-29' AS answer",
                "MATCH (n:CalendarSample) FOR d IN n.dates RETURN MIN(d)=DATE '2019-01-01' AND COUNT(*)=2 AS answer",
                "MATCH (n:CalendarSample) FOR d IN n.spans RETURN MIN(d)=DURATION '-PT0.5S' AND COUNT(*)=2 AS answer",
                "MATCH (n:CalendarKey) RETURN SUM(n.hits)=2 AS answer",
                "MATCH (n:TemporalSample {wallTime:LOCAL_TIME('12:30:45.123456789')}) RETURN COUNT(n)=1 AS answer",
                "MATCH (n:TemporalSample {wallStamp:LOCAL_DATETIME('2020-02-29T23:59:59.999999999')}) RETURN COUNT(n)=1 AS answer",
                "MATCH (n:TemporalSample {zonedTime:ZONED_TIME('12:30:45.123456789+01:00')}) RETURN COUNT(n)=1 AS answer",
                "MATCH (n:TemporalSample {zonedStamp:ZONED_DATETIME('2020-02-29T12:30:45.123456789+01:00')}) RETURN COUNT(n)=1 AS answer",
                "MATCH (n:TemporalSample) RETURN n.wallTime IS TYPED LOCAL TIME AS answer",
                "MATCH (n:TemporalSample) RETURN n.wallStamp IS TYPED LOCAL DATETIME AS answer",
                "MATCH (n:TemporalSample) RETURN n.zonedStamp IS TYPED ZONED DATETIME AS answer",
                "MATCH (n:TemporalSample) RETURN n.zonedTime IS TYPED ZONED TIME AS answer",
                "MATCH (n:TemporalSample) RETURN n.wallStamp+DURATION 'PT0.000000001S'=LOCAL_DATETIME('2020-03-01T00:00:00') AS answer",
                "MATCH (n:TemporalSample) FOR t IN n.wallTimes RETURN MAX(t)=LOCAL_TIME('23:59:59.999999999') AND COUNT(*)=2 AS answer",
                "MATCH (n:TemporalSample) FOR t IN n.wallStamps RETURN MIN(t)=LOCAL_DATETIME('2019-01-01T00:00:00') AND COUNT(*)=2 AS answer",
                "MATCH (n:TemporalSample) FOR t IN n.zonedStamps RETURN MIN(t)=ZONED_DATETIME('2019-01-01T00:00:00Z') AND COUNT(*)=2 AS answer",
                "MATCH (n:TemporalSample) FOR t IN n.zonedTimes RETURN MIN(t)=ZONED_TIME('00:00:00Z') AND COUNT(*)=2 AS answer"
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
            await host.StopAsync();
        }
    }
}
