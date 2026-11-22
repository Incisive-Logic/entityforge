using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EntityForge.Db.Hosting;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    public async Task Live_telemetry_aggregates_real_instruments_without_tags_and_bounds_cardinality()
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .UseBearerTokenSecurity(new(AdministratorToken, readerToken: ReaderToken, requireHttps: false))
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var admin = Client(host, AdministratorToken);
        using var reader = Client(host, ReaderToken);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await reader.GetAsync("/v1/telemetry")).StatusCode);
        using var meter = new Meter("EntityForge.Db.Tests.LiveTelemetry");
        var counter = meter.CreateCounter<long>("fixture.commits");
        var histogram = meter.CreateHistogram<double>("fixture.duration", "ms");
        counter.Add(3, new KeyValuePair<string, object?>("secret", "sensitive-tag-value"));
        counter.Add(2);
        histogram.Record(10);
        histogram.Record(30);
        using var response = await admin.GetAsync("/v1/telemetry");
        response.EnsureSuccessStatusCode();
        Assert.IsTrue(response.Headers.CacheControl!.NoStore);
        string text = await response.Content.ReadAsStringAsync();
        Assert.IsFalse(text.Contains("sensitive-tag-value"));
        using var json = JsonDocument.Parse(text);
        Assert.AreEqual("process", json.RootElement.GetProperty("scope").GetString());
        var metrics = json.RootElement.GetProperty("metrics").EnumerateArray().ToArray();
        var count = metrics.Single(m => m.GetProperty("name").GetString() == "fixture.commits");
        Assert.AreEqual(5d, count.GetProperty("sum").GetDouble());
        var duration = metrics.Single(m => m.GetProperty("name").GetString() == "fixture.duration");
        Assert.AreEqual(2L, duration.GetProperty("samples").GetInt64());
        Assert.AreEqual(40d, duration.GetProperty("sum").GetDouble());
        Assert.AreEqual(10d, duration.GetProperty("minimum").GetDouble());
        Assert.AreEqual(30d, duration.GetProperty("maximum").GetDouble());
        for (int i = 0; i < 140; i++)
        {
            meter.CreateCounter<long>("bounded." + i).Add(1);
        }

        var bounded = await admin.GetFromJsonAsync<JsonElement>("/v1/telemetry");
        Assert.IsTrue(bounded.GetProperty("metrics").GetArrayLength() <= 128);
        Assert.IsTrue(bounded.GetProperty("droppedMeasurements").GetInt64() > 0);
        Assert.AreEqual(json.RootElement.GetProperty("processSession").GetGuid(), bounded.GetProperty("processSession").GetGuid());
    }
}
