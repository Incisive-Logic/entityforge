using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using EntityForge.Db.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class ObservabilityTests
{
    [TestMethod]
    [DataRow("missing", 503)]
    [DataRow("unreachable", 502)]
    [DataRow("timeout", 504)]
    [DataRow("malformed", 502)]
    public async Task Backend_failures_are_distinct_and_do_not_leak_backend_details(string mode, int expected)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        using var backendClient = new HttpClient(new FailureBackend(mode));
        var options = new EntityForgeObservabilityOptions("test-db", mode == "missing" ? null : new Uri("http://private-backend/"));
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(new EntityForgeObservability(options, backendClient));
        builder.Services.AddSingleton<IEntityForgeDbAuthorizationPolicy, AllowEntityForgeDbAuthorizationPolicy>();
        await using var app = builder.Build();
        EntityForgeObservability.Map(app);
        await app.StartAsync();
        using var client = new HttpClient
        {
            BaseAddress = new Uri(app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single())
        };
        using var response = await client.GetAsync("/v1/observability/history");
        Assert.AreEqual(expected, (int)response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        Assert.IsFalse(body.Contains("private-backend"));
        Assert.AreEqual("no-store", response.Headers.CacheControl!.ToString());
    }

    private sealed class FailureBackend(string mode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => mode switch
        {
            "unreachable" => throw new HttpRequestException("private-backend connection refused"),
            "timeout" => throw new TaskCanceledException("private-backend timeout"),
            _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") })
        };
    }

    private static readonly EntityForgeObservabilityOptions Options = new("test-db", new("http://metrics/"), new("http://traces/"));

    private sealed class Backend(string json) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    [TestMethod]
    public async Task History_scopes_every_series_and_keeps_missing_samples_as_gaps()
    {
        using var handler = new Backend("""{"status":"success","data":{"result":[{"values":[[1700000000,"2.5"],[1700000005,"NaN"]]}]}}""");
        using var client = new HttpClient(handler);
        var report = await new EntityForgeObservability(Options, client).History(60, default);
        Assert.AreEqual(4, report.Series.Length);
        Assert.AreEqual(2.5, report.Series[0].Points[0].Value);
        Assert.IsNull(report.Series[0].Points[1].Value);
        Assert.IsTrue(handler.Requests.All(r => Uri.UnescapeDataString(r.Query).Contains("service_instance_id=\"test-db\"")));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => new EntityForgeObservability(Options, client).History(1441, default));
    }

    [TestMethod]
    public async Task History_rejects_partial_backend_results()
    {
        using var handler = new Backend("""{"status":"success","warnings":["partial response"],"data":{"result":[]}}""");
        using var client = new HttpClient(handler);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new EntityForgeObservability(Options, client).History(15, default));
    }

    [TestMethod]
    public async Task Search_is_scoped_and_does_not_return_arbitrary_root_names()
    {
        using var handler = new Backend("""{"traces":[{"traceID":"01234567890123456789012345678901","rootTraceName":"private statement","durationMs":12,"startTimeUnixNano":"1700000000000000000"}]}""");
        using var client = new HttpClient(handler);
        var report = await new EntityForgeObservability(Options, client).Search(15, default);
        Assert.AreEqual(1, report.Traces.Length);
        StringAssert.Contains(Uri.UnescapeDataString(handler.Requests.Single().Query), "resource.service.instance.id = \"test-db\"");
        StringAssert.Contains(Uri.UnescapeDataString(handler.Requests.Single().Query), "span.entityforge.query = true");
        Assert.IsFalse(JsonSerializer.Serialize(report).Contains("private statement"));
    }

    [TestMethod]
    public async Task Search_accepts_Tempo_zero_duration_and_unpadded_trace_ids()
    {
        using var handler = new Backend("""{"traces":[{"traceID":"580707f531e26a3d3838a830c137f","startTimeUnixNano":"1700000000000000000"}]}""");
        using var client = new HttpClient(handler);
        var trace = (await new EntityForgeObservability(Options, client).Search(15, default)).Traces.Single();
        Assert.AreEqual("000580707f531e26a3d3838a830c137f", trace.Id);
        Assert.AreEqual(0d, trace.DurationMs);
        using var detailHandler = new Backend(Trace("test-db"));
        using var detailClient = new HttpClient(detailHandler);
        var detail = await new EntityForgeObservability(Options, detailClient).Detail(trace.Id, default);
        Assert.AreEqual(trace.Id, detail.Id);
    }

    private static string Trace(string instance) => $$$"""
        {"batches":[{"resource":{"attributes":[{"key":"service.instance.id","value":{"stringValue":"{{{instance}}}"}},{"key":"service.name","value":{"stringValue":"EntityForge.Db.Server"}}]},"scopeSpans":[{"spans":[{"spanId":"1234","name":"database.query","startTimeUnixNano":"1700000000000000000","endTimeUnixNano":"1700000000012000000","attributes":[{"key":"entityforge.query","value":{"boolValue":true}},{"key":"db.statement","value":{"stringValue":"secret"}},{"key":"http.route","value":{"stringValue":"/graphql"}}]}]}]}]}
        """;

    [TestMethod]
    public async Task Detail_enforces_ownership_and_excludes_sensitive_attributes()
    {
        using var handler = new Backend(Trace("test-db"));
        using var client = new HttpClient(handler);
        var backend = new EntityForgeObservability(Options, client);
        var detail = await backend.Detail(new string('a', 32), default);
        Assert.AreEqual(12, detail.Spans[0].DurationMs, 0.01);
        Assert.AreEqual("/graphql", detail.Spans[0].Route);
        Assert.IsFalse(JsonSerializer.Serialize(detail).Contains("secret"));
        using var foreignHandler = new Backend(Trace("other-db"));
        using var foreign = new HttpClient(foreignHandler);
        await Assert.ThrowsExactlyAsync<ObservabilityTraceNotFoundException>(() => new EntityForgeObservability(Options, foreign).Detail(new string('a', 32), default));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => backend.Detail("../bad", default));
    }

    [TestMethod]
    public async Task Query_activity_marks_graphql_errors_without_recording_payload()
    {
        Activity? captured = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == EntityForgeQueryTracing.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => captured = activity
        };
        ActivitySource.AddActivityListener(listener);
        var context = new DefaultHttpContext();
        context.Request.Path = "/graphql";
        context.Request.Headers.Authorization = "Bearer secret";
        var middleware = new EntityForgeQueryTracing(c =>
        {
            c.Items["entityforge.graphql.errors"] = true;
            return Task.CompletedTask;
        });
        await middleware.InvokeAsync(context, Options);
        Assert.IsNotNull(captured);
        Assert.AreEqual(ActivityStatusCode.Error, captured.Status);
        Assert.AreEqual("graphql-error", captured.GetTagItem("entityforge.outcome"));
        Assert.IsFalse(captured.TagObjects.Any(t => t.Value?.ToString()?.Contains("secret") == true));
    }

    [TestMethod]
    public void Scope_identifiers_cannot_inject_backend_queries()
    {
        Assert.IsTrue(EntityForgeObservability.ValidInstance("db-01.test"));
        Assert.IsFalse(EntityForgeObservability.ValidInstance("db\"} or true"));
    }

    [TestMethod]
    public async Task Observability_routes_require_administrator_credentials()
    {
        bool reached = false;
        var security = new EntityForgeDbSecurityMiddleware(_ =>
        {
            reached = true;
            return Task.CompletedTask;
        },
            new EntityForgeDbBearerSecurityOptions(new string('a', 32), readerToken: new string('r', 32), requireHttps: false),
            new LoggingEntityForgeDbSecurityAuditSink(NullLogger<LoggingEntityForgeDbSecurityAuditSink>.Instance));
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        foreach (string view in new[] { "configuration", "history", "traces", "trace" })
        {
            var context = new DefaultHttpContext { RequestServices = services };
            context.Request.Path = "/v1/observability/" + view;
            context.Request.Method = "GET";
            context.Request.Headers.Authorization = "Bearer " + new string('r', 32);
            context.Response.Body = new MemoryStream();
            await security.InvokeAsync(context);
            Assert.IsFalse(reached);
            Assert.AreEqual(401, context.Response.StatusCode);
        }
        var admin = new DefaultHttpContext { RequestServices = services };
        admin.Request.Path = "/v1/observability/history";
        admin.Request.Method = "GET";
        admin.Request.Headers.Authorization = "Bearer " + new string('a', 32);
        await security.InvokeAsync(admin);
        Assert.IsTrue(reached);
    }
}
