using System.Net;
using System.Text.Json;
using EntityForge.Db.AppModel.Connections;
using EntityForge.Db.Transport.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EntityForge.Db.Studio.Core.Tests;

[TestClass]
public sealed class ConnectionInspectionTests
{
    private static readonly Guid Database = Guid.Parse("b61ad831-5433-47e2-bf8b-e5451cd0eae4");
    private static readonly Guid Operation = Guid.Parse("a61ad831-5433-47e2-bf8b-e5451cd0eae4");
    private static readonly EntityForgeConnectionProfile Profile = new("test", "Test", new("https://example.test/"));

    [TestMethod]
    public async Task AccessIsLoadedAndFailureMakesCachedPermissionsUnknown()
    {
        var host = new Host();
        using var session = new EntityForgeConnectionSession(host);
        await session.ConnectAsync(Profile, "token");
        Assert.AreEqual(false, session.CanAdminister);
        host.AccessStatus = HttpStatusCode.Forbidden;
        await session.RefreshAccessAsync();
        Assert.IsNull(session.CanAdminister);
        Assert.IsNotNull(session.Access!.Report);
        Assert.IsNotNull(session.Access.Error);
        session.Disconnect();
        Assert.IsNull(session.Access);
    }

    [TestMethod]
    [DataRow("NotFound", 200)]
    [DataRow("PlanBound", 200)]
    [DataRow("Aborted", 200)]
    [DataRow("Expired", 410)]
    [DataRow("Committed", 200)]
    [DataRow("CommittedPendingReadability", 200)]
    public async Task OutcomeStatesRemainDistinct(string state, int code)
    {
        var host = new Host { State = state, Status = (HttpStatusCode)code };
        using var session = new EntityForgeConnectionSession(host);
        await session.ConnectAsync(Profile, "token");
        await session.LookupOperationAsync(Operation, Database);
        Assert.IsNull(session.Operation!.Error);
        Assert.AreEqual(state, session.Operation.Report!.State);
        Assert.IsFalse(string.IsNullOrWhiteSpace(OperationOutcome.Explanation(state)));
    }

    [TestMethod]
    [DataRow(401)]
    [DataRow(403)]
    [DataRow(404)]
    [DataRow(409)]
    [DataRow(503)]
    public async Task HttpFailuresDoNotInventOperationOutcomes(int status)
    {
        var host = new Host { Status = (HttpStatusCode)status };
        using var session = new EntityForgeConnectionSession(host);
        await session.ConnectAsync(Profile, "token");
        await session.LookupOperationAsync(Operation, Database);
        Assert.IsNull(session.Operation!.Report);
        Assert.IsNotNull(session.Operation.Error);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CancelAndDisconnectRejectLateReplies(bool disconnect)
    {
        var host = new Host { Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var session = new EntityForgeConnectionSession(host);
        await session.ConnectAsync(Profile, "token");
        var pending = session.LookupOperationAsync(Operation, Database);
        if (disconnect)
        {
            session.Disconnect();
        }
        else
        {
            session.CancelOperationLookup();
        }

        host.Pending.SetResult(host.OperationResponse());
        await pending;
        Assert.IsFalse(session.IsLookingUpOperation);
        Assert.IsNull(session.Operation?.Report);
        if (disconnect)
        {
            Assert.IsNull(session.Operation);
        }
        else
        {
            Assert.IsNotNull(session.Operation!.Error);
        }
    }

    [TestMethod]
    public async Task MismatchedResponseAndUnsupportedHostAreRejected()
    {
        var host = new Host { WrongIdentity = true };
        using var session = new EntityForgeConnectionSession(host);
        await session.ConnectAsync(Profile, "token");
        await session.LookupOperationAsync(Operation, Database);
        Assert.IsNull(session.Operation!.Report);
        Assert.IsNotNull(session.Operation.Error);
        host.Supported = false;
        await session.ConnectAsync(Profile, "token");
        Assert.IsFalse(session.SupportsOperationLookup);
        Assert.IsNull(session.Access!.Report);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => session.LookupOperationAsync(Operation, Database));
    }

    private sealed class Host : HttpMessageHandler
    {
        public bool Supported = true, WrongIdentity;
        public string State = "NotFound";
        public HttpStatusCode Status = HttpStatusCode.OK, AccessStatus = HttpStatusCode.OK;
        public TaskCompletionSource<HttpResponseMessage>? Pending;

        public HttpResponseMessage OperationResponse()
        {
            string receipt = State.StartsWith("Committed") ? $$"""{"DatabaseIncarnation":"{{Database}}","TransactionId":"{{Operation}}","Version":{"Value":42} }""" : "null";
            return Json(Status, $$"""{"version":1,"databaseIncarnation":"{{Database}}","profile":"Rdf","operationId":"{{(WrongIdentity ? Guid.NewGuid() : Operation)}}","state":"{{State}}","receipt":{{receipt}}} """);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path == "/health/live" || path == "/health/ready")
            {
                return Task.FromResult(Json(HttpStatusCode.OK, "{}"));
            }

            if (path == "/v1/capabilities")
            {
                return Task.FromResult(Json(HttpStatusCode.OK, $$"""{"product":"EntityForgeDB","version":"1","profile":"rdf","databaseIncarnation":"{{Database}}","surfaces":{{(Supported ? "[{\"identifier\":\"effective-access\",\"maturity\":\"Stable\",\"features\":[\"current-credential\"]},{\"identifier\":\"application-operations\",\"maturity\":\"Stable\",\"features\":[\"resolve\"]}]" : "[]")}}} """));
            }

            if (path == "/v1/security/access")
            {
                return Task.FromResult(Json(AccessStatus, $$"""{"version":1,"authentication":"bearer","role":"Read","canRead":true,"canWrite":false,"canAdminister":false,"isHttps":true,"databaseIncarnation":"{{Database}}"} """));
            }

            StringAssert.Contains(request.RequestUri.Query, "expectedProfile=Rdf");
            return Pending?.Task ?? Task.FromResult(OperationResponse());
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string body) => new(code) { Content = new StringContent(body) };
}
