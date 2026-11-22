using System.Net;
using EntityForge.Db.AppModel.Connections;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EntityForge.Db.Studio.Core.Tests;

[TestClass]
public sealed class NativeConnectionAuthenticationTests
{
    private static readonly EntityForgeConnectionProfile Profile = new("native", "Native", new("https://example.test/"));

    [TestMethod]
    [DataRow("rdf", null)]
    [DataRow("rdf", "invalid")]
    [DataRow("property-graph", null)]
    [DataRow("property-graph", "invalid")]
    public async Task Rejected_credentials_do_not_publish_a_connected_session_and_can_be_corrected(string profile, string? token)
    {
        using var host = new Host(profile);
        using var session = new EntityForgeConnectionSession(host);
        var failure = await Assert.ThrowsExceptionAsync<EntityForgeConnectionException>(
            () => session.ConnectAsync(Profile, token).AsTask());
        StringAssert.Contains(failure.Message, "token");
        Assert.IsNull(session.Current);
        Assert.IsNull(session.Access);
        Assert.AreEqual(EntityForgeConnectionState.Failed, session.State);
        await session.ConnectAsync(Profile, "reader");
        Assert.AreEqual(EntityForgeConnectionState.Connected, session.State);
        Assert.AreEqual(true, session.Access!.Report!.CanRead);
        Assert.AreEqual(false, session.CanAdminister);
    }

    [TestMethod]
    [DataRow(403)]
    [DataRow(404)]
    [DataRow(503)]
    public async Task Unavailable_access_discovery_does_not_misclassify_credentials(int status)
    {
        using var host = new Host("rdf") { AccessStatus = (HttpStatusCode)status };
        using var session = new EntityForgeConnectionSession(host);
        await session.ConnectAsync(Profile, "reader");
        Assert.IsNotNull(session.Current);
        Assert.IsNull(session.CanAdminister);
        Assert.IsNotNull(session.Access!.Error);
    }

    [TestMethod]
    [DataRow("rdf")]
    [DataRow("property-graph")]
    public async Task Revoked_native_credentials_invalidate_availability_and_recover(string profile)
    {
        using var host = new Host(profile);
        using var session = new EntityForgeConnectionSession(host);
        await session.ConnectAsync(Profile, "reader");
        host.AccessStatus = HttpStatusCode.Unauthorized;
        var update = await session.CheckForUpdatesAsync();
        Assert.IsTrue(update.SessionInvalidated);
        Assert.IsFalse(session.Availability!.Available);
        StringAssert.Contains(session.Error!, "token");
        Assert.IsNotNull(session.Access!.Error);
        Assert.IsNull(session.CanAdminister);
        host.AccessStatus = HttpStatusCode.OK;
        Assert.IsTrue((await session.CheckForUpdatesAsync()).SessionInvalidated);
        Assert.IsTrue(session.Availability!.Available);
        Assert.IsNull(session.Error);
        Assert.IsNull(session.Access!.Error);
    }

    [TestMethod]
    public async Task Access_discovery_finishes_before_connection_publication()
    {
        using var host = new Host("rdf") { Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var session = new EntityForgeConnectionSession(host);
        var connecting = session.ConnectAsync(Profile, "reader").AsTask();
        await host.Started.Task;
        try
        {
            Assert.IsNull(session.Current);
            Assert.AreEqual(EntityForgeConnectionState.Connecting, session.State);
        }
        finally
        {
            host.Pending.TrySetResult(host.AccessResponse());
        }
        await connecting;
        Assert.IsNotNull(session.Current);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Late_access_discovery_cannot_publish_a_disconnected_or_replaced_session(bool replace)
    {
        using var host = new Host("rdf") { Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var session = new EntityForgeConnectionSession(host);
        var connecting = session.ConnectAsync(Profile, "reader").AsTask();
        await host.Started.Task;
        var pending = host.Pending;
        host.Pending = null;
        session.Disconnect();
        if (replace)
        {
            await session.ConnectAsync(Profile with
            {
                Id = "replacement"
            }, "reader");
        }

        pending.SetResult(host.AccessResponse());
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => connecting);
        Assert.AreEqual(replace ? "replacement" : null, session.Current?.Profile.Id);
        Assert.AreEqual(replace ? EntityForgeConnectionState.Connected : EntityForgeConnectionState.Disconnected, session.State);
    }

    private sealed class Host(string profile) : HttpMessageHandler
    {
        internal HttpStatusCode AccessStatus = HttpStatusCode.OK;
        internal TaskCompletionSource<HttpResponseMessage>? Pending;
        internal TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private static readonly Guid Database = Guid.Parse("0be0a273-f910-48ae-a82c-de97cf8f7dca");

        internal HttpResponseMessage AccessResponse() => Json(AccessStatus,
            $$"""{"version":1,"authentication":"bearer","role":"Read","canRead":true,"canWrite":false,"canAdminister":false,"isHttps":true,"databaseIncarnation":"{{Database}}"}""");

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/v1/capabilities")
            {
                return Task.FromResult(Json(HttpStatusCode.OK,
                    $$"""{"product":"EntityForgeDB","version":"1","profile":"{{profile}}","databaseIncarnation":"{{Database}}","surfaces":[{"identifier":"effective-access","maturity":"Stable","features":["current-credential"]}]}"""));
            }

            if (request.RequestUri.AbsolutePath == "/v1/security/access")
            {
                Started.TrySetResult();
                if (request.Headers.Authorization?.Parameter != "reader")
                {
                    return Task.FromResult(Json(HttpStatusCode.Unauthorized, "{}"));
                }

                return Pending?.Task ?? Task.FromResult(AccessResponse());
            }
            return Task.FromResult(Json(HttpStatusCode.OK, "{}"));
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body) };
    }
}
