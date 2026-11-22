using System.Net;
using System.Text;
using System.Text.Json;
using EntityForge.Db.AppModel.Connections;
using EntityForge.Db.AppModel.Queries;
using EntityForge.Db.Client;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EntityForge.Db.Studio.Core.Tests;

[TestClass]
public sealed class ConnectionMonitoringTests
{
    [TestMethod]
    public async Task Published_catalogue_becomes_available_without_reconnecting_and_unchanged_checks_keep_executors()
    {
        var host = new Host();
        using var connection = new EntityForgeConnectionSession(host);
        await connection.ConnectAsync(Profile, "session-token");
        var originalClient = connection.NativeQueryExecutor;
        var originalExecutor = connection.QueryExecutor;
        var unchanged = await connection.CheckForUpdatesAsync();
        Assert.AreEqual(new EntityForgeConnectionUpdate(), unchanged);
        Assert.AreSame(originalExecutor, connection.QueryExecutor);
        host.Version = 2;
        var update = await connection.CheckForUpdatesAsync();
        Assert.IsTrue(update.CatalogChanged);
        Assert.IsFalse(update.SessionInvalidated);
        Assert.AreEqual("Company", connection.SemanticQueryMetadata!.Concepts.Single().DisplayName);
        Assert.AreNotSame(originalExecutor, connection.QueryExecutor);
        Assert.AreSame(originalClient, connection.NativeQueryExecutor);
        Assert.AreEqual("session-token", host.Token);
        var refreshed = connection.QueryExecutor;
        Assert.AreEqual(new EntityForgeConnectionUpdate(), await connection.CheckForUpdatesAsync());
        Assert.AreSame(refreshed, connection.QueryExecutor);
    }

    [TestMethod]
    public async Task Unavailable_connection_recovers_and_keeps_builder_text_and_variables()
    {
        var host = new Host { Version = 2 };
        using var connection = new EntityForgeConnectionSession(host);
        await connection.ConnectAsync(Profile, "session-token");
        using var builder = new EntityQuerySession();
        using var native = new NativeQuerySession();
        builder.Bind(connection.QueryExecutor);
        native.Bind(connection.NativeQueryExecutor);
        var draft = new SemanticQueryDocument(new("urn:Company", "Company"), [], []);
        builder.EditDraft(draft);
        native.Draft = "query($first: Int!) { companies(first: $first) { name } }";
        native.Variables = "{\"first\":7}";
        var text = native.Draft;
        host.Offline = true;
        var lost = await connection.CheckForUpdatesAsync();
        Assert.IsTrue(lost.SessionInvalidated);
        Assert.IsFalse(connection.Availability!.Available);
        builder.RefreshBinding(connection.QueryExecutor, lost.SessionInvalidated);
        native.RefreshBinding(connection.NativeQueryExecutor, lost.SessionInvalidated);
        Assert.IsFalse((await connection.CheckForUpdatesAsync()).SessionInvalidated);
        host.Offline = false;
        var recovered = await connection.CheckForUpdatesAsync();
        Assert.IsTrue(recovered.SessionInvalidated);
        Assert.IsTrue(connection.Availability!.Available);
        Assert.IsNull(connection.Error);
        builder.RefreshBinding(connection.QueryExecutor, recovered.SessionInvalidated);
        native.RefreshBinding(connection.NativeQueryExecutor, recovered.SessionInvalidated);
        Assert.AreSame(draft, builder.Draft);
        Assert.AreEqual(text, native.Draft);
        Assert.AreEqual("{\"first\":7}", native.Variables);
        Assert.IsTrue(builder.CanExecute);
        Assert.IsTrue(native.CanExecute);
        Assert.AreEqual("session-token", host.Token);
    }

    [TestMethod]
    public async Task Initially_unready_host_gains_executors_when_it_becomes_ready()
    {
        var host = new Host { Ready = false, Version = 2 };
        using var connection = new EntityForgeConnectionSession(host);
        await connection.ConnectAsync(Profile);
        Assert.IsNull(connection.QueryExecutor);
        host.Ready = true;
        Assert.IsTrue((await connection.CheckForUpdatesAsync()).SessionInvalidated);
        Assert.IsTrue(connection.Current!.Ready);
        Assert.IsNotNull(connection.QueryExecutor);
        Assert.IsNotNull(connection.NativeQueryExecutor);
    }

    [TestMethod]
    public async Task Changed_database_identity_invalidates_results_even_when_catalogue_version_is_unchanged()
    {
        var host = new Host { Version = 2 };
        using var connection = new EntityForgeConnectionSession(host);
        await connection.ConnectAsync(Profile);
        var previous = connection.QueryExecutor;
        host.Incarnation = Guid.NewGuid();
        var update = await connection.CheckForUpdatesAsync();
        Assert.IsTrue(update.SessionInvalidated);
        Assert.AreEqual(host.Incarnation, connection.Current!.Capabilities.DatabaseIncarnation);
        Assert.AreNotSame(previous, connection.QueryExecutor);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Late_catalogue_check_cannot_change_disconnected_or_replaced_session(bool replace)
    {
        var host = new Host();
        using var connection = new EntityForgeConnectionSession(host);
        await connection.ConnectAsync(Profile);
        host.DelayCatalog = true;
        var pending = connection.CheckForUpdatesAsync().AsTask();
        await host.Started.Task;
        connection.Disconnect();
        host.DelayCatalog = false;
        if (replace)
        {
            await connection.ConnectAsync(Profile with
            {
                Id = "replacement"
            });
        }

        var executor = connection.QueryExecutor;
        host.Response.SetResult(Catalog(2));
        Assert.AreEqual(new EntityForgeConnectionUpdate(), await pending);
        Assert.AreEqual(replace ? "replacement" : null, connection.Current?.Profile.Id);
        Assert.AreSame(executor, connection.QueryExecutor);
        Assert.AreEqual(replace ? 1L : null, connection.SemanticCatalog?.VocabularyVersion);
    }

    [TestMethod]
    public async Task Cancelled_monitor_does_not_mark_a_healthy_host_unavailable()
    {
        var host = new Host();
        using var connection = new EntityForgeConnectionSession(host);
        await connection.ConnectAsync(Profile);
        host.DelayCatalog = true;
        using var cancellation = new CancellationTokenSource();
        var pending = connection.CheckForUpdatesAsync(cancellation.Token).AsTask();
        await host.Started.Task;
        cancellation.Cancel();
        host.Response.SetResult(Catalog(2));
        Assert.AreEqual(new EntityForgeConnectionUpdate(), await pending);
        Assert.IsTrue(connection.Availability!.Available);
        Assert.AreEqual(1L, connection.SemanticCatalog!.VocabularyVersion);
    }

    [TestMethod]
    public async Task Revoked_access_reports_actionable_status_and_recovers_after_access_returns()
    {
        var host = new Host();
        using var connection = new EntityForgeConnectionSession(host);
        await connection.ConnectAsync(Profile, "session-token");
        host.Denied = true;
        await connection.CheckForUpdatesAsync();
        Assert.IsFalse(connection.Availability!.Available);
        StringAssert.Contains(connection.Error!, "Reconnect with a token");
        host.Denied = false;
        await connection.CheckForUpdatesAsync();
        Assert.IsTrue(connection.Availability!.Available);
        Assert.IsNull(connection.Error);
    }

    private static readonly EntityForgeConnectionProfile Profile = new("demo", "Demo", new("https://example.test/"));

    private static HttpResponseMessage Json(HttpStatusCode status, string value) => new(status)
    {
        Content = new StringContent(value, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage Catalog(int version) => Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
    {
        vocabularyVersion = version,
        vocabularies = Array.Empty<object>(),
        identitySchemes = Array.Empty<object>(),
        concepts = version == 1 ? Array.Empty<object>() : [new
        {
            identifier = "urn:Company", displayName = "Company", properties = Array.Empty<object>(),
            relationships = Array.Empty<object>(), broaderConcepts = Array.Empty<string>(),
            narrowerConcepts = Array.Empty<string>(), equivalentConcepts = Array.Empty<string>()
        }
        ]
    }));

    private sealed class Host : HttpMessageHandler
    {
        public int Version = 1;
        public bool Ready = true, Offline, Denied, DelayCatalog;
        public Guid Incarnation = Guid.NewGuid();
        public string? Token;

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<HttpResponseMessage> Response { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Token = request.Headers.Authorization?.Parameter;
            if (Offline)
            {
                throw new HttpRequestException("Offline");
            }

            if (Denied)
            {
                return Task.FromResult(Json(HttpStatusCode.Unauthorized, "{}"));
            }

            if (request.RequestUri!.AbsolutePath == "/v1/semantic/catalog")
            {
                if (DelayCatalog)
                {
                    Started.TrySetResult();
                    return Response.Task;
                }
                return Task.FromResult(Catalog(Version));
            }
            return Task.FromResult(request.RequestUri.AbsolutePath switch
            {
                "/health/ready" => Json(Ready ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable, "{}"),
                "/v1/capabilities" => Json(HttpStatusCode.OK, $$"""{"product":"EntityForgeDB","version":"0.9","profile":"semantic","databaseIncarnation":"{{Incarnation}}","surfaces":[{"identifier":"semantic-catalog","maturity":"Stable","features":[]},{"identifier":"entity-projection","maturity":"Stable","features":[]},{"identifier":"graphql-projection","maturity":"Stable","features":[]}]}"""),
                _ => Json(HttpStatusCode.OK, "{}")
            });
        }
    }
}
