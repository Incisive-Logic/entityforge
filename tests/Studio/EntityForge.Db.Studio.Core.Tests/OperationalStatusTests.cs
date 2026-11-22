using System.Net;
using EntityForge.Db.AppModel.Connections;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EntityForge.Db.Studio.Core.Tests;

[TestClass]
public sealed class OperationalStatusTests
{
    private static readonly EntityForgeConnectionProfile Profile = new("test", "Test", new("https://example.test/"));
    private const string Report = """
        {"ready":false,"maintenanceFailure":"IOException","profile":"rdf","version":42,"walBytes":1024,
         "databaseIncarnation":"b61ad831-5433-47e2-bf8b-e5451cd0eae4",
         "changeJournal":{"retainedBytes":512,"retentionPressureBytes":0,"segmentCount":2,"materializationDebt":3}}
        """;

    [TestMethod]
    public async Task Not_ready_host_remains_diagnosable_and_outage_retains_stale_report_until_recovery()
    {
        var host = new Host();
        using var session = new EntityForgeConnectionSession(host);
        await session.ConnectAsync(Profile, "test-session-token");
        await session.RefreshOperationalStatusAsync();
        var first = session.OperationalStatus!;
        Assert.IsTrue(first.Reachable);
        Assert.IsFalse(first.Report!.Ready);
        Assert.AreEqual("IOException", first.Report.MaintenanceFailure);
        Assert.AreEqual(3L, first.Report.ChangeJournal!.MaterializationDebt);
        host.Offline = true;
        await session.RefreshOperationalStatusAsync();
        Assert.IsFalse(session.OperationalStatus!.Reachable);
        Assert.IsNotNull(session.OperationalStatus.Error);
        Assert.AreSame(first.Report, session.OperationalStatus.Report);
        Assert.AreEqual(first.ReceivedAt, session.OperationalStatus.ReceivedAt);
        host.Offline = false;
        await session.RefreshOperationalStatusAsync();
        Assert.IsNull(session.OperationalStatus!.Error);
        session.Disconnect();
        Assert.IsNull(session.OperationalStatus);
    }

    [TestMethod]
    [DataRow(401, "Authentication")]
    [DataRow(403, "permission")]
    [DataRow(404, "endpoint")]
    public async Task Access_and_endpoint_failures_are_distinguished(int code, string message)
    {
        var host = new Host { StatusCode = (HttpStatusCode)code };
        using var session = new EntityForgeConnectionSession(host);
        await session.ConnectAsync(Profile, "test-session-token");
        await session.RefreshOperationalStatusAsync();
        StringAssert.Contains(session.OperationalStatus!.Error!, message);
        Assert.IsNull(session.OperationalStatus.Report);
        Assert.IsTrue(session.OperationalStatus.Reachable);
    }

    [TestMethod]
    public async Task Unsupported_host_is_not_queried_for_standalone_status()
    {
        var host = new Host { Supported = false };
        using var session = new EntityForgeConnectionSession(host);
        await session.ConnectAsync(Profile, "test-session-token");
        await session.RefreshOperationalStatusAsync();
        Assert.AreEqual(0, host.StatusCalls);
        StringAssert.Contains(session.OperationalStatus!.Error!, "does not advertise");
    }

    [TestMethod]
    public async Task Late_reply_after_disconnect_cannot_repopulate_status()
    {
        var host = new Host { Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var session = new EntityForgeConnectionSession(host);
        await session.ConnectAsync(Profile, "test-session-token");
        var pending = session.RefreshOperationalStatusAsync();
        await host.Started.Task;
        await session.RefreshOperationalStatusAsync();
        Assert.AreEqual(1, host.StatusCalls, "Refreshes must not overlap.");
        session.Disconnect();
        host.Pending.SetResult(Json(HttpStatusCode.OK, Report));
        await pending;
        Assert.IsNull(session.OperationalStatus);
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string text) => new(code) { Content = new StringContent(text) };

    private sealed class Host : HttpMessageHandler
    {
        public bool Offline, Supported = true;
        public HttpStatusCode StatusCode = HttpStatusCode.OK;
        public int StatusCalls;
        public TaskCompletionSource<HttpResponseMessage>? Pending;
        public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.AreEqual("test-session-token", request.Headers.Authorization?.Parameter);
            if (Offline)
            {
                throw new HttpRequestException("Offline");
            }

            switch (request.RequestUri!.AbsolutePath)
            {
                case "/health/live":
                    return Task.FromResult(Json(HttpStatusCode.OK, "{}"));
                case "/health/ready":
                    return Task.FromResult(Json(HttpStatusCode.ServiceUnavailable, "{\"message\":\"Starting\"}"));
                case "/v1/capabilities":
                    return Task.FromResult(Json(HttpStatusCode.OK,
                    "{\"product\":\"EntityForgeDB\",\"version\":\"1\",\"profile\":\"rdf\",\"surfaces\":" +
                    (Supported ? "[{\"identifier\":\"administration\",\"maturity\":\"Stable\",\"features\":[\"status\"]}]" : "[]") + "}"));
                case "/v1/status":
                    StatusCalls++;
                    Started.TrySetResult();
                    return Pending?.Task ?? Task.FromResult(Json(StatusCode, Report));
                default:
                    throw new AssertFailedException(request.RequestUri.AbsolutePath);
            }
        }
    }
}
