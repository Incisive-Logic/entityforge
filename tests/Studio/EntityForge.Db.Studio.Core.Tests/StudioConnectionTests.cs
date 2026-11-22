using System.Net;
using System.Text;
using EntityForge.Db.AppModel.Connections;
using EntityForge.Db.Client;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EntityForge.Db.Studio.Core.Tests;

[TestClass]
public sealed class StudioConnectionTests
{
    [TestMethod]
    public async Task Unavailable_host_reports_failure_and_allows_retry()
    {
        bool reachable = false;
        using var session = new EntityForgeConnectionSession(new StubHandler(request =>
        {
            if (!reachable)
            {
                throw new HttpRequestException("Connection refused",
                new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused));
            }

            return Json(HttpStatusCode.OK, request.RequestUri!.AbsolutePath == "/v1/capabilities"
                ? """{"product":"EntityForgeDB","version":"0.9","profile":"rdf","surfaces":[]}""" : "{}");
        }));
        var profile = new EntityForgeConnectionProfile("local", "Local", new Uri("http://127.0.0.1:5080"));
        var failure = await Assert.ThrowsExceptionAsync<EntityForgeConnectionException>(() => session.ConnectAsync(profile).AsTask());
        StringAssert.Contains(failure.Message, "running and reachable");
        Assert.AreEqual(EntityForgeConnectionState.Failed, session.State);
        Assert.AreEqual(failure.Message, session.Error);
        Assert.IsNull(session.Current);
        Assert.IsNull(session.QueryExecutor);
        Assert.IsNull(session.NativeQueryExecutor);
        reachable = true;
        await session.ConnectAsync(profile);
        Assert.AreEqual(EntityForgeConnectionState.Connected, session.State);
        Assert.IsNull(session.Error);
    }

    [TestMethod]
    public async Task Refresh_uses_session_credentials_and_disables_queries_when_readiness_changes()
    {
        bool ready = true;
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/health/live" => Json(HttpStatusCode.OK, "{}"),
            "/health/ready" => Json(ready ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable, "{}"),
            "/v1/capabilities" => Json(HttpStatusCode.OK, SemanticCapabilities),
            _ => Json(HttpStatusCode.OK, """{"vocabularyVersion":1,"vocabularies":[],"identitySchemes":[],"concepts":[]}"""),
        });
        using var session = new EntityForgeConnectionSession(handler);
        await session.ConnectAsync(new("one", "One", new("https://one.test")), "secret");
        Assert.IsNotNull(session.QueryExecutor);
        ready = false;
        var refreshed = await session.RefreshAsync();
        Assert.AreEqual("Bearer secret", handler.Authorization);
        Assert.IsFalse(refreshed.Ready);
        Assert.IsNull(session.QueryExecutor);
        Assert.IsFalse(session.SupportsOptimization);
    }

    [TestMethod]
    public async Task Gateway_discovery_preserves_identity_without_requesting_unadvertised_catalog()
    {
        var incarnation = Guid.NewGuid();
        using var session = new EntityForgeConnectionSession(new StubHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/health/live" => Json(HttpStatusCode.OK, "{}"),
            "/health/ready" => Json(HttpStatusCode.ServiceUnavailable, """{"message":"Database readiness unavailable"}"""),
            "/v1/capabilities" => Json(HttpStatusCode.OK, $$"""{"product":"EntityForgeDB","version":"0.9","profile":"semantic","hostKind":"application-gateway","databaseIncarnation":"{{incarnation}}","surfaces":[]}"""),
            _ => throw new AssertFailedException("Unadvertised route requested"),
        }));
        var snapshot = await session.ConnectAsync(new("gateway", "Gateway", new("https://gateway.test")));
        Assert.AreEqual(incarnation, snapshot.Capabilities.DatabaseIncarnation);
        Assert.AreEqual("application-gateway", snapshot.Capabilities.HostKind);
        Assert.IsFalse(snapshot.Ready);
        Assert.IsNull(session.QueryExecutor);
    }

    [TestMethod]
    public async Task Connect_discovers_identity_profile_and_readiness()
    {
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/health/live" => Json(HttpStatusCode.OK, "{\"status\":\"live\"}"),
            "/health/ready" => Json(HttpStatusCode.OK, "{\"ready\":true,\"message\":\"Ready\"}"),
            "/v1/capabilities" => Json(HttpStatusCode.OK,
                SemanticCapabilities),
            "/v1/semantic/catalog" => Json(HttpStatusCode.OK,
                """{"vocabularyVersion":1,"vocabularies":[],"identitySchemes":[],"concepts":[]}"""),
            _ => Json(HttpStatusCode.NotFound, "{}"),
        });
        using var session = new EntityForgeConnectionSession(handler);

        EntityForgeConnectionSnapshot result = await session.ConnectAsync(
            new EntityForgeConnectionProfile("local", "Local", new Uri("http://localhost:5080")), "secret");

        Assert.IsTrue(result.Ready);
        Assert.AreEqual(EntityForgeDatabaseProfile.Semantic, result.Capabilities.Profile);
        Assert.AreEqual("Bearer secret", handler.Authorization);
        Assert.AreEqual(new Uri("http://localhost:5080/"), result.Profile.Endpoint);
        Assert.AreEqual(EntityForgeConnectionState.Connected, session.State);
        Assert.IsNotNull(session.SemanticCatalog);
        Assert.IsNotNull(session.SemanticQueryMetadata);
        Assert.IsNotNull(session.QueryExecutor);
        Assert.AreEqual(2, result.Capabilities.Surfaces.Count);
        Assert.IsTrue(session.SupportsOptimization);
    }

    private const string SemanticCapabilities = """{"product":"EntityForgeDB","version":"0.9","profile":"semantic","surfaces":[{"identifier":"semantic-catalog","maturity":"Stable","features":["property-storage-optimization"]},{"identifier":"entity-projection","maturity":"Stable","features":["entity-document-query"]}]}""";

    [TestMethod]
    [DataRow(false, true)]
    [DataRow(true, false)]
    public async Task Not_ready_or_unadvertised_hosts_do_not_enable_semantic_actions(bool ready, bool advertise)
    {
        int catalogRequests = 0;
        using var session = new EntityForgeConnectionSession(new StubHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/health/live" => Json(HttpStatusCode.OK, "{}"),
            "/health/ready" => Json(ready ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable, "{}"),
            "/v1/capabilities" => Json(HttpStatusCode.OK, advertise ? SemanticCapabilities : """{"product":"EntityForgeDB","version":"0.9","profile":"semantic","surfaces":[]}"""),
            _ => Catalog(),
        }));
        HttpResponseMessage Catalog()
        {
            catalogRequests++;
            return Json(HttpStatusCode.Forbidden, "{}");
        }
        var result = await session.ConnectAsync(new("test", "Test", new Uri("https://example.test")));
        Assert.AreEqual(ready, result.Ready);
        Assert.AreEqual(0, catalogRequests);
        Assert.IsNull(session.QueryExecutor);
        Assert.IsFalse(session.SupportsOptimization);
    }

    [TestMethod]
    public async Task Temporary_catalog_failure_preserves_connection_diagnostics()
    {
        using var session = new EntityForgeConnectionSession(new StubHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/health/live" or "/health/ready" => Json(HttpStatusCode.OK, "{}"),
            "/v1/capabilities" => Json(HttpStatusCode.OK, SemanticCapabilities),
            _ => Json(HttpStatusCode.ServiceUnavailable, "{}"),
        }));
        await session.ConnectAsync(new("test", "Test", new Uri("https://example.test")));
        Assert.AreEqual(EntityForgeConnectionState.Connected, session.State);
        Assert.IsNotNull(session.CatalogStatus);
        Assert.IsNull(session.QueryExecutor);
    }

    [DataTestMethod]
    [DataRow(401, false, "Enter the database token")]
    [DataRow(401, true, "did not accept this token")]
    [DataRow(403, true, "does not have access")]
    public async Task Catalog_authentication_failure_explains_how_to_restore_access(int status, bool suppliedToken, string expected)
    {
        using var session = new EntityForgeConnectionSession(new StubHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/health/live" or "/health/ready" => Json(HttpStatusCode.OK, "{}"),
            "/v1/capabilities" => Json(HttpStatusCode.OK, SemanticCapabilities),
            _ => Json((HttpStatusCode)status, "{}"),
        }));
        var error = await Assert.ThrowsExceptionAsync<EntityForgeConnectionException>(async () =>
            await session.ConnectAsync(new("test", "Test", new Uri("https://example.test")), suppliedToken ? "invalid-test-token" : null));
        StringAssert.Contains(error.Message, expected);
        StringAssert.Contains(session.Error!, expected);
        Assert.IsNull(session.Current);
        Assert.IsNull(session.SemanticCatalog);
        Assert.IsNull(session.SemanticQueryMetadata);
        Assert.IsNull(session.NativeQueryExecutor);
        Assert.IsNull(session.QueryExecutor);
        Assert.AreEqual(EntityForgeConnectionState.Failed, session.State);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Delayed_discovery_cannot_reconnect_after_disconnect_or_overwrite_new_connection(bool replace)
    {
        var handler = new DelayedHandler();
        using var session = new EntityForgeConnectionSession(handler);
        Task old = session.ConnectAsync(new("old", "Old", new Uri("https://old.test"))).AsTask();
        await handler.Started.Task;
        session.Disconnect();
        if (replace)
        {
            await session.ConnectAsync(new("new", "New", new Uri("https://new.test")));
        }

        handler.Response.SetResult(Json(HttpStatusCode.OK, "{}"));
        try
        {
            await old;
            Assert.Fail("Obsolete connection must be cancelled.");
        }
        catch (OperationCanceledException)
        {
        }
        Assert.AreEqual(replace ? EntityForgeConnectionState.Connected : EntityForgeConnectionState.Disconnected, session.State);
        Assert.AreEqual(replace ? "new" : null, session.Current?.Profile.Id);
    }

    [TestMethod]
    public async Task Corrected_token_can_connect_after_authentication_rejection()
    {
        using var session = new EntityForgeConnectionSession(new StubHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/health/live" or "/health/ready" => Json(HttpStatusCode.OK, "{}"),
            "/v1/capabilities" => Json(HttpStatusCode.OK, SemanticCapabilities),
            _ when request.Headers.Authorization?.Parameter == "accepted" => Json(HttpStatusCode.OK,
                """{"vocabularyVersion":1,"vocabularies":[],"identitySchemes":[],"concepts":[]}"""),
            _ => Json(HttpStatusCode.Unauthorized, "{}")
        }));
        var profile = new EntityForgeConnectionProfile("test", "Test", new Uri("https://example.test"));
        await Assert.ThrowsExceptionAsync<EntityForgeConnectionException>(async () => await session.ConnectAsync(profile, "rejected"));
        Assert.IsNull(session.Current);
        await session.ConnectAsync(profile, "accepted");
        Assert.AreEqual(EntityForgeConnectionState.Connected, session.State);
        Assert.IsNull(session.Error);
        Assert.IsNotNull(session.QueryExecutor);
    }

    private sealed class DelayedHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<HttpResponseMessage> Response { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host == "old.test")
            {
                Started.TrySetResult();
                return Response.Task;
            }
            return Task.FromResult(Json(HttpStatusCode.OK, request.RequestUri.AbsolutePath == "/v1/capabilities"
                ? """{"product":"EntityForgeDB","version":"0.9","profile":"rdf","surfaces":[]}""" : "{}"));
        }
    }

    [TestMethod]
    public async Task Profile_store_never_contains_credentials()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"entityforge-studio-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "connections.json");
        try
        {
            var store = new JsonEntityForgeConnectionProfileStore(path);
            await store.SaveAsync([new("local", "Local", new Uri("https://localhost:5080"))]);
            string persisted = await File.ReadAllTextAsync(path);

            StringAssert.Contains(persisted, "https://localhost:5080/");
            Assert.IsFalse(persisted.Contains("token", StringComparison.OrdinalIgnoreCase));
            Assert.AreEqual("Local", (await store.LoadAsync()).Single().Name);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    [TestMethod]
    public async Task Availability_checks_recover_without_replacing_query_session()
    {
        bool available = true;
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/health/ready" when !available => throw new HttpRequestException("Offline"),
            "/v1/capabilities" => Json(HttpStatusCode.OK, SemanticCapabilities),
            "/v1/semantic/catalog" => Json(HttpStatusCode.OK,
                """{"vocabularyVersion":1,"vocabularies":[],"identitySchemes":[],"concepts":[]}"""),
            _ => Json(HttpStatusCode.OK, "{}")
        });
        using var session = new EntityForgeConnectionSession(handler);
        var snapshot = await session.ConnectAsync(new("test", "Test", new Uri("https://example.test")), "secret");
        var executor = session.QueryExecutor;
        available = false;
        await session.CheckAvailabilityAsync();
        Assert.IsFalse(session.Availability!.Available);
        available = true;
        await session.CheckAvailabilityAsync();
        Assert.IsTrue(session.Availability!.Available);
        Assert.AreSame(snapshot, session.Current);
        Assert.AreSame(executor, session.QueryExecutor);
        Assert.AreEqual("Bearer secret", handler.Authorization);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Late_availability_check_cannot_publish_after_disconnect_or_replacement(bool replace)
    {
        var handler = new AvailabilityHandler();
        using var session = new EntityForgeConnectionSession(handler);
        await session.ConnectAsync(new("old", "Old", new Uri("https://example.test")));
        handler.Delay = true;
        var check = session.CheckAvailabilityAsync().AsTask();
        await handler.Started.Task;
        session.Disconnect();
        handler.Delay = false;
        if (replace)
        {
            await session.ConnectAsync(new("new", "New", new Uri("https://example.test")));
        }

        var observation = session.Availability;
        handler.Response.SetResult(Json(HttpStatusCode.ServiceUnavailable, "{}"));
        await check;
        Assert.AreSame(observation, session.Availability);
        Assert.AreEqual(replace ? "new" : null, session.Current?.Profile.Id);
    }

    private sealed class AvailabilityHandler : HttpMessageHandler
    {
        public bool Delay
        {
            get; set;
        }

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<HttpResponseMessage> Response { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Delay)
            {
                Started.TrySetResult();
                return Response.Task;
            }
            return Task.FromResult(Json(HttpStatusCode.OK, request.RequestUri!.AbsolutePath == "/v1/capabilities"
                ? """{"product":"EntityForgeDB","version":"0.9","profile":"rdf","surfaces":[]}""" : "{}"));
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string value) => new(status)
    {
        Content = new StringContent(value, Encoding.UTF8, "application/json"),
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public string? Authorization
        {
            get; private set;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Authorization = request.Headers.Authorization?.ToString() ?? Authorization;
            return Task.FromResult(response(request));
        }
    }
}
