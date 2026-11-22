using System.Net;
using System.Text.Json;
using EntityForge.Db.AppModel.Connections;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EntityForge.Db.Studio.Core.Tests;

[TestClass]
public sealed class AdministrationTests
{
    private static readonly Guid Database = Guid.Parse("3f72501e-2e20-4cd6-b05f-9f7e5d3363f4");
    private static readonly EntityForgeConnectionProfile Profile = new("local", "Fixture", new("https://example.test/"));

    [TestMethod]
    public async Task ReviewDoesNotMutateAndSubmissionIsBoundToReviewedTarget()
    {
        var host = new Host();
        using var session = new EntityForgeConnectionSession(host);
        await session.ConnectAsync(Profile, "credential");
        var plan = await session.PrepareAdministrationAsync("backup", "weekly");
        Assert.AreEqual(0, host.Posts);
        StringAssert.Contains(plan.ExpectedTarget, $"weekly-{plan.Id:N}.efbackup");
        await session.RunAdministrationAsync(plan);
        Assert.AreEqual(1, host.Posts);
        Assert.AreEqual("Succeeded", session.AdministrationReceipt!.State);
        Assert.AreEqual(plan.ExpectedTarget, host.Body!.RootElement.GetProperty("expectedTarget").GetString());
        Assert.IsFalse(host.Body.RootElement.GetRawText().Contains("credential"));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => session.RunAdministrationAsync(plan));
        await session.ResolveAdministrationAsync(plan.Id);
        Assert.AreEqual(1, host.Posts);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task StoppingOrDisconnectingRetainsIdentityAndDiscardsLateSuccess(bool disconnect)
    {
        var host = new Host { Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var session = new EntityForgeConnectionSession(host);
        await session.ConnectAsync(Profile, "credential");
        var plan = await session.PrepareAdministrationAsync("checkpoint", null);
        var running = session.RunAdministrationAsync(plan);
        Assert.IsTrue(session.AdministrationBusy);
        if (disconnect)
        {
            session.Disconnect();
        }
        else
        {
            session.StopAdministrationWait();
        }

        host.Pending.SetResult(host.Receipt(plan.Id));
        await running;
        Assert.IsFalse(session.AdministrationBusy);
        Assert.IsNull(session.AdministrationReceipt);
        Assert.AreEqual(plan.Id, session.LastAdministrationPlan!.Id);
        Assert.IsNotNull(session.AdministrationError);
    }

    [TestMethod]
    [DataRow(404)]
    [DataRow(403)]
    [DataRow(503)]
    public async Task LookupFailureNeverEstablishesSuccessOrRetriesMutation(int status)
    {
        var host = new Host { LookupStatus = (HttpStatusCode)status };
        using var session = new EntityForgeConnectionSession(host);
        await session.ConnectAsync(Profile, "credential");
        await session.ResolveAdministrationAsync(Guid.NewGuid());
        Assert.AreEqual(0, host.Posts);
        Assert.IsNull(session.AdministrationReceipt);
        Assert.IsNotNull(session.AdministrationError);
    }

    [TestMethod]
    public async Task MismatchedReceiptIsRejectedAndReaderCannotSubmit()
    {
        var host = new Host { WrongIdentity = true };
        using var session = new EntityForgeConnectionSession(host);
        await session.ConnectAsync(Profile, "credential");
        var plan = await session.PrepareAdministrationAsync("checkpoint", null);
        await session.RunAdministrationAsync(plan);
        Assert.IsNull(session.AdministrationReceipt);
        Assert.IsNotNull(session.AdministrationError);
        host.Allowed = false;
        await session.RefreshAccessAsync();
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => session.PrepareAdministrationAsync("checkpoint", null));
        Assert.AreEqual(1, host.Posts);
    }

    [TestMethod]
    public async Task InterruptedResponseCanBeResolvedWithoutResubmission()
    {
        var host = new Host { Interrupt = true };
        using var session = new EntityForgeConnectionSession(host);
        await session.ConnectAsync(Profile, "credential");
        var plan = await session.PrepareAdministrationAsync("checkpoint", null);
        await session.RunAdministrationAsync(plan);
        Assert.IsNull(session.AdministrationReceipt);
        Assert.IsNotNull(session.AdministrationError);
        await session.ResolveAdministrationAsync(plan.Id);
        Assert.AreEqual("Succeeded", session.AdministrationReceipt!.State);
        Assert.AreEqual(1, host.Posts);
    }

    [TestMethod]
    public async Task OptimizationRequiresItsOwnCapabilityAndCannotUseRdfAdministrationSupport()
    {
        var host = new Host();
        using var session = new EntityForgeConnectionSession(host);
        await session.ConnectAsync(Profile, "credential");
        Assert.IsTrue(session.SupportsAdministrationJobs);
        Assert.IsFalse(session.SupportsOptimizationJobs);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => session.PrepareAdministrationAsync("optimize", null));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => session.PrepareAdministrationAsync("reset-observations", null));
        Assert.AreEqual(0, host.Posts);
    }

    private sealed class Host : HttpMessageHandler
    {
        public string ObservabilityJson = "{}";
        public HttpStatusCode ObservabilityStatus = HttpStatusCode.OK;
        public TaskCompletionSource<HttpResponseMessage>? ObservabilityPending;
        public HttpStatusCode TelemetryStatus = HttpStatusCode.OK;
        public TaskCompletionSource<HttpResponseMessage>? TelemetryPending;
        public int Posts; public bool WrongIdentity, Interrupt, Allowed = true;
        public HttpStatusCode LookupStatus = HttpStatusCode.OK;
        public JsonDocument? Body;
        public TaskCompletionSource<HttpResponseMessage>? Pending;

        public HttpResponseMessage Receipt(Guid id) => Json(HttpStatusCode.OK, $$"""{"version":1,"id":"{{(WrongIdentity ? Guid.NewGuid() : id)}}","databaseIncarnation":"{{Database}}","operation":"{{Body?.RootElement.GetProperty("operation").GetString() ?? "checkpoint"}}","target":{{(Body?.RootElement.GetProperty("expectedTarget").GetRawText() ?? "\"/db\"")}},"state":"Succeeded","updatedAt":"2026-10-31T12:00:00Z","result":{"version":1} }""");

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.StartsWith("/health/"))
            {
                return Json(HttpStatusCode.OK, "{}");
            }

            if (path == "/v1/capabilities")
            {
                return Json(HttpStatusCode.OK, $$"""{"product":"EntityForgeDB","version":"1","profile":"rdf","hostKind":"standalone","databaseIncarnation":"{{Database}}","surfaces":[{"identifier":"administration","maturity":"Stable","features":["administration-jobs-v1","live-metrics-v1","observability-v1"]},{"identifier":"effective-access","maturity":"Stable","features":["current-credential"]}]}""");
            }

            if (path == "/v1/security/access")
            {
                return Json(HttpStatusCode.OK, $$"""{"version":1,"authentication":"bearer","role":"Administration","canRead":true,"canWrite":true,"canAdminister":{{(Allowed ? "true" : "false")}},"isHttps":true,"databaseIncarnation":"{{Database}}"}""");
            }

            if (path == "/v1/administration/target")
            {
                return Json(HttpStatusCode.OK, $$"""{"version":1,"databaseIncarnation":"{{Database}}","profile":"Rdf","dataDirectory":"/db","backupDirectory":"/backups"}""");
            }

            if (path.StartsWith("/v1/observability/"))
            {
                return ObservabilityPending is not null ? await ObservabilityPending.Task : Json(ObservabilityStatus, ObservabilityJson);
            }

            if (path == "/v1/telemetry")
            {
                return TelemetryPending is not null ? await TelemetryPending.Task : Json(TelemetryStatus, TelemetryJson);
            }

            if (request.Method == HttpMethod.Post)
            {
                Posts++;
                Body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                if (Interrupt)
                {
                    throw new HttpRequestException("Response lost");
                }

                if (Pending is not null)
                {
                    return await Pending.Task;
                }
            }
            else if (LookupStatus != HttpStatusCode.OK)
            {
                return Json(LookupStatus, "{}");
            }

            return Receipt(Guid.Parse(path.Split('/')[^1]));
        }
    }

    private const string TelemetryJson = """{"version":1,"processSession":"3f72501e-2e20-4cd6-b05f-9f7e5d3363f4","scope":"process","startedAt":"2026-10-31T10:00:00Z","capturedAt":"2026-10-31T10:05:00Z","maximumMetrics":128,"droppedMeasurements":0,"metrics":[{"meter":"EntityForge.Db","name":"commits","kind":"Counter","samples":2,"sum":5,"minimum":2,"maximum":3,"last":3,"lastObservedAt":"2026-10-31T10:04:00Z"}]}""";

    [TestMethod]
    public async Task TelemetryFailureRetainsStaleReportAndDisconnectClearsIt()
    {
        var host = new Host();
        using var session = new EntityForgeConnectionSession(host);
        await session.ConnectAsync(Profile, "credential");
        await session.RefreshTelemetryAsync();
        Assert.IsNull(session.Telemetry!.Error);
        Assert.AreEqual(5d, session.Telemetry.Report!.Metrics.Single().Sum);
        var report = session.Telemetry.Report;
        host.TelemetryStatus = HttpStatusCode.ServiceUnavailable;
        await session.RefreshTelemetryAsync();
        Assert.AreSame(report, session.Telemetry.Report);
        Assert.IsNotNull(session.Telemetry.Error);
        session.Disconnect();
        Assert.IsNull(session.Telemetry);
    }

    [TestMethod]
    public async Task TelemetryDiscardsLateResponseAfterDisconnect()
    {
        var host = new Host { TelemetryPending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var session = new EntityForgeConnectionSession(host);
        await session.ConnectAsync(Profile, "credential");
        var pending = session.RefreshTelemetryAsync();
        session.Disconnect();
        host.TelemetryPending.SetResult(Json(HttpStatusCode.OK, TelemetryJson));
        await pending;
        Assert.IsNull(session.Telemetry);
    }

    [TestMethod]
    public async Task ObservabilityUsesGeneratedSerializationAndRejectsInvalidReports()
    {
        var host = new Host { ObservabilityJson = """{"history":true,"profiler":true,"instance":"test-db"}""" };
        using var session = new EntityForgeConnectionSession(host);
        await session.ConnectAsync(Profile, "credential");
        Assert.IsTrue((await session.ReadObservabilityConfigurationAsync()).History);
        host.ObservabilityJson = """{"start":"2026-10-31T10:00:00Z","end":"2026-10-31T10:05:00Z","stepSeconds":5,"series":[]}""";
        Assert.AreEqual(0, (await session.ReadHistoryAsync(15)).Series.Length);
        host.ObservabilityJson = """{"spans":null}""";
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => session.ReadTraceAsync(new string('a', 32)));
    }

    [TestMethod]
    public async Task ObservabilityDiscardsResponsesAfterDisconnect()
    {
        var host = new Host { ObservabilityPending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var session = new EntityForgeConnectionSession(host);
        await session.ConnectAsync(Profile, "credential");
        var pending = session.ReadObservabilityConfigurationAsync();
        session.Disconnect();
        host.ObservabilityPending.SetResult(Json(HttpStatusCode.OK, """{"history":true,"profiler":true,"instance":"test-db"}"""));
        try
        {
            await pending;
            Assert.Fail("Late response must be discarded.");
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body) };

    [TestMethod]
    [DataRow(HttpStatusCode.ServiceUnavailable, "not configured")]
    [DataRow(HttpStatusCode.GatewayTimeout, "timed out")]
    [DataRow(HttpStatusCode.BadGateway, "unreachable")]
    public async Task ObservabilityFailuresGiveActionableSafeMessages(HttpStatusCode status, string expected)
    {
        var host = new Host { ObservabilityStatus = status, ObservabilityJson = "private backend details" };
        using var session = new EntityForgeConnectionSession(host);
        await session.ConnectAsync(Profile, "credential");
        var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => session.ReadHistoryAsync(15));
        StringAssert.Contains(failure.Message, expected);
        Assert.IsFalse(failure.Message.Contains("private backend details"));
        host.ObservabilityStatus = HttpStatusCode.OK;
        host.ObservabilityJson = """{"start":"2026-10-31T10:00:00Z","end":"2026-10-31T10:05:00Z","stepSeconds":5,"series":[]}""";
        Assert.AreEqual(0, (await session.ReadHistoryAsync(15)).Series.Length);
    }
}
