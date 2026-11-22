using EntityForge.Db.Query.Adapters;
using EntityForge.Db.Gql;
using EntityForge.Db.Semantic.Durability;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EntityForge.Db.Semantic;
using EntityForge.Db.Client;
using EntityForge.Db.Transport.Contracts;
using EntityForge.Db.Hosting;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Graph;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using EntityForge.Db.Storage.Local;
using EntityForge.Semantics.Protobuf;
using EntityForge.Query.Projection;
using EntityForge.Query.Querying;
using EntityForge.Query.Values;
using EntityForge.Semantics;
using EntityForge.Values;
using System.Text;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System.Threading.Channels;
using System.Diagnostics;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed partial class EntityForgeDbHostTests
{
    private const int ParserResilienceSamples = 256;
    private const int MaximumSupportedGqlPathHops = GraphPatternLimits.MaximumSpecifiedHops;
    private static readonly TimeSpan ParserResilienceBudget = TimeSpan.FromSeconds(5);
    private const string AdministratorToken = "entityforge-test-administrator-token-0001";
    private const string WriterToken = "entityforge-test-writer-token-0000000001";
    private const string ReaderToken = "entityforge-test-reader-token-0000000001";
    private static readonly string ExpectedProductVersion = ((System.Reflection.AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(typeof(EntityForgeDbHostTests).Assembly, typeof(System.Reflection.AssemblyInformationalVersionAttribute))!).InformationalVersion.Split('+')[0];
    private static readonly TimeSpan MaintenanceTestInterval = TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan MaintenanceTestTimeout = TimeSpan.FromSeconds(5);
    private string? _directory;
    private readonly TemporaryTestDirectories _testDirectories = new("entityforge-host");
    private readonly SemanticChangeSessionTransportCodec _codec = new();

    [TestCleanup]
    public void Cleanup() => _testDirectories.Cleanup();

    [TestMethod]
    public void Fluent_builder_is_single_use_and_validates_owned_storage_configuration()
    {
        _directory = NewDirectory();
        EntityForgeDbHostBuilder builder = EntityForgeDbServer.CreateBuilder()
            .UseLocalStorage(_directory)
            .ConfigureStorage(options => options.MaximumRequestBodyBytes = 1024 * 1024)
            .ConfigureServices(services => services.AddSingleton<Marker>());
        EntityForgeDbHost host = builder.Build();
        try
        {
            Assert.IsNotNull(host.Application.Services.GetService<Marker>());
            Assert.ThrowsException<InvalidOperationException>(() => builder.Build());
        }
        finally
        {
            host.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    [TestMethod]
    public async Task Host_serves_studio_as_an_anonymous_same_origin_browser_client()
    {
        _directory = NewDirectory();
        string assets = Path.Combine(_directory, "studio-assets");
        Directory.CreateDirectory(assets);
        await File.WriteAllTextAsync(Path.Combine(assets, "index.html"), "<html>EntityForge DB Studio</html>");
        await File.WriteAllTextAsync(Path.Combine(assets, "app.js"), "// studio");
        byte[] runtimeData = [0x00, 0x01, 0x02, 0xff];
        await File.WriteAllBytesAsync(Path.Combine(assets, "runtime.bundle.dat"), runtimeData);

        await using EntityForgeDbHost host = EntityForgeDbServer.CreateBuilder()
            .UseLocalStorage(Path.Combine(_directory, "database"))
            .UseStudio(assets)
            .UseBearerTokenSecurity(new EntityForgeDbBearerSecurityOptions(AdministratorToken))
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0"))
            .Build();
        await host.StartAsync();
        using HttpClient client = Client(host);

        using HttpResponseMessage studio = await client.GetAsync("/studio/");
        Assert.AreEqual("no-cache", studio.Headers.CacheControl?.ToString());
        Assert.AreEqual("<html>EntityForge DB Studio</html>", await studio.Content.ReadAsStringAsync());
        Assert.AreEqual("// studio", await client.GetStringAsync("/studio/app.js"));
        CollectionAssert.AreEqual(runtimeData, await client.GetByteArrayAsync("/studio/runtime.bundle.dat"));
        Assert.AreEqual("<html>EntityForge DB Studio</html>",
            await client.GetStringAsync("/studio/catalog/concepts"));
        Assert.AreEqual(HttpStatusCode.UpgradeRequired, (await client.GetAsync("/v1/status")).StatusCode);
    }

    [TestMethod]
    public void Administration_configuration_rejects_unbounded_limits()
    {
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            new EntityForgeDbAdministrationOptions(0));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            new EntityForgeDbAdministrationOptions(jobTimeout: TimeSpan.Zero));
        _directory = NewDirectory();
        Assert.ThrowsException<ArgumentException>(() => EntityForgeDbServer.CreateBuilder()
            .UseLocalStorage(_directory)
            .ConfigureStorage(options => options.PageFileName = options.CommitLogFileName)
            .Build());
    }

    [TestMethod]
    public void Bearer_security_rejects_weak_duplicate_and_unbounded_configuration()
    {
        Assert.ThrowsException<ArgumentException>(() => new EntityForgeDbBearerSecurityOptions("too-short"));
        Assert.ThrowsException<ArgumentException>(() =>
            new EntityForgeDbBearerSecurityOptions(AdministratorToken, AdministratorToken));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            new EntityForgeDbBearerSecurityOptions(AdministratorToken, maximumConcurrentRequests: 0));
        Assert.IsFalse(new EntityForgeDbBearerSecurityOptions(AdministratorToken).ToString()!
            .Contains(AdministratorToken, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Administrative_endpoints_honor_the_application_authorization_policy()
    {
        _directory = NewDirectory();
        await using EntityForgeDbHost host = EntityForgeDbServer.CreateBuilder()
            .UseLocalStorage(_directory)
            .ConfigureServices(services => services.AddSingleton<IEntityForgeDbAuthorizationPolicy,
                DenyAdministrationPolicy>())
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0"))
            .Build();
        await host.StartAsync();
        using HttpClient client = Client(host);

        HttpResponseMessage ready = await client.GetAsync("/health/ready");
        HttpResponseMessage status = await client.GetAsync("/v1/status");
        HttpResponseMessage checkpoint = await client.PostAsync("/v1/checkpoints", null);

        Assert.AreEqual(HttpStatusCode.OK, ready.StatusCode);
        EntityForgeDbReadiness? readiness = await ready.Content.ReadFromJsonAsync<EntityForgeDbReadiness>();
        Assert.IsTrue(readiness!.Ready);
        CollectionAssert.AreEqual(Array.Empty<string>(), readiness.Reasons.ToArray());
        Assert.AreEqual(HttpStatusCode.Forbidden, status.StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, checkpoint.StatusCode);
    }

    [TestMethod]
    public async Task Administration_is_denied_by_default()
    {
        _directory = NewDirectory();
        await using EntityForgeDbHost host = EntityForgeDbServer.CreateBuilder()
            .UseLocalStorage(_directory)
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0"))
            .Build();
        await host.StartAsync();
        using HttpClient client = Client(host);

        Assert.AreEqual(HttpStatusCode.Forbidden, (await client.GetAsync("/v1/status")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await client.PostAsync("/v1/checkpoints", null)).StatusCode);
    }

    [TestMethod]
    public async Task Semantic_property_optimization_is_an_administration_only_bounded_surface()
    {
        _directory = NewDirectory();
        await using EntityForgeDbHost host = EntityForgeDbServer.CreateBuilder()
            .UseLocalStorage(_directory)
            .UseBearerTokenSecurity(new(AdministratorToken, readerToken: ReaderToken, requireHttps: false))
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0"))
            .Build();
        await host.StartAsync();
        using HttpClient reader = Client(host, ReaderToken);
        using HttpClient administrator = Client(host, AdministratorToken);
        const string path = "/v1/semantic/property-storage/optimization";

        Assert.AreEqual(HttpStatusCode.Unauthorized, (await reader.GetAsync(path)).StatusCode);
        SemanticPropertyOptimizationReport report = (await administrator
            .GetFromJsonAsync<SemanticPropertyOptimizationReport>(path))!;
        Assert.AreEqual(0, report.Observations.Count);
        SemanticPropertyOptimizationResult result = (await (await administrator.PostAsync(
            path + "?maximumChanges=1", null)).Content
            .ReadFromJsonAsync<SemanticPropertyOptimizationResult>())!;
        Assert.AreEqual(0, result.AppliedPlacements.Count);
        Assert.AreEqual(HttpStatusCode.NoContent, (await administrator.DeleteAsync(
            path + "/observations")).StatusCode);
        EntityForgeHostCapabilitiesTransport capabilities = (await administrator
            .GetFromJsonAsync<EntityForgeHostCapabilitiesTransport>("/v1/capabilities"))!;
        CollectionAssert.Contains(capabilities.Surfaces.Single(surface =>
            surface.Identifier == EntityForgeSurfaceIds.SemanticCatalog).Features.ToArray(),
            "property-storage-optimization");
    }

    [TestMethod]
    public async Task Bearer_security_enforces_roles_without_protecting_health_discovery()
    {
        _directory = NewDirectory();
        var audit = new CapturingSecurityAuditSink();
        await using EntityForgeDbHost host = EntityForgeDbServer.CreateBuilder()
            .UseLocalStorage(_directory).UsePropertyGraphProfile()
            .UseBearerTokenSecurity(new(AdministratorToken, WriterToken, ReaderToken, requireHttps: false,
                maximumConcurrentRequests: 4))
            .ConfigureServices(services => services.AddSingleton<IEntityForgeDbSecurityAuditSink>(audit))
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0"))
            .Build();
        await host.StartAsync();
        using HttpClient anonymous = Client(host);
        using HttpClient reader = Client(host, ReaderToken);
        using HttpClient writer = Client(host, WriterToken);
        using HttpClient administrator = Client(host, AdministratorToken);
        var vertex = new GraphVertexWriteRequest(Guid.NewGuid(), ["Company"]);

        Assert.AreEqual(HttpStatusCode.OK, (await anonymous.GetAsync("/health/ready")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync($"/v1/graph/vertices/{vertex.Id}")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized,
            (await reader.PostAsJsonAsync("/v1/graph/vertices", vertex)).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK,
            (await writer.PostAsJsonAsync("/v1/graph/vertices", vertex)).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK,
            (await reader.GetAsync($"/v1/graph/vertices/{vertex.Id}")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await writer.GetAsync("/v1/status")).StatusCode);
        HttpResponseMessage status = await administrator.GetAsync("/v1/status");
        Assert.AreEqual(HttpStatusCode.OK, status.StatusCode);
        Assert.AreEqual("no-store", status.Headers.CacheControl!.ToString());
        Assert.IsTrue(audit.Events.Any(item => item.Outcome == "denied"));
        Assert.IsTrue(audit.Events.Any(item => item.RequiredAccess is EntityForgeDbAccessKind.Administration &&
                                               item.Outcome == "allowed"));
        Assert.IsFalse(audit.Events.Any(item => item.Resource.Contains(vertex.Id.ToString(),
            StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task Bearer_security_rejects_protected_plain_http_when_tls_is_required()
    {
        _directory = NewDirectory();
        await using EntityForgeDbHost host = EntityForgeDbServer.CreateBuilder()
            .UseLocalStorage(_directory)
            .UseBearerTokenSecurity(new(AdministratorToken))
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0"))
            .Build();
        await host.StartAsync();
        using HttpClient client = Client(host, AdministratorToken);

        Assert.AreEqual((HttpStatusCode)426, (await client.GetAsync("/v1/status")).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
    }

    [TestMethod]
    public async Task RDF_POST_queries_require_reader_access_while_document_pages_require_writer_access()
    {
        _directory = NewDirectory();
        await using EntityForgeDbHost host = EntityForgeDbServer.CreateBuilder()
            .UseLocalStorage(_directory).UseRdfProfile()
            .UseBearerTokenSecurity(new(AdministratorToken, WriterToken, ReaderToken, requireHttps: false))
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using HttpClient reader = Client(host, ReaderToken);
        using HttpClient writer = Client(host, WriterToken);
        var query = new RdfQuadQueryRequest(new());
        Assert.AreEqual(HttpStatusCode.OK,
            (await reader.PostAsJsonAsync("/v1/rdf/quads", query)).StatusCode);
        const string shapes = """
            @prefix sh: <http://www.w3.org/ns/shacl#> .
            @prefix ex: <https://example.test/> .
            ex:Shape a sh:NodeShape ; sh:targetNode ex:subject ;
                sh:property [ sh:path ex:required ; sh:minCount 1 ] .
            """;
        Assert.AreEqual(HttpStatusCode.OK, (await reader.PostAsync("/v1/rdf/shacl/validate",
            new StringContent(shapes, Encoding.UTF8, "text/turtle"))).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK,
            (await reader.GetAsync("/v1/rdf/entailment/rdfs?maximumResults=10")).StatusCode);
        var quad = new RdfQuad(new RdfIri("https://example.test/s"),
            new RdfIri("https://example.test/p"), new RdfLiteral("value"));
        var page = new RdfDocumentPageRequest(Guid.NewGuid(), 0, [quad]);
        Assert.AreEqual(HttpStatusCode.Unauthorized,
            (await reader.PostAsJsonAsync("/v1/rdf/document-pages", page)).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK,
            (await writer.PostAsJsonAsync("/v1/rdf/document-pages", page)).StatusCode);
        var transaction = new RdfMutationBatchRequest(Guid.NewGuid(), [new("assert", Wire(quad))]);
        Assert.AreEqual(HttpStatusCode.Unauthorized,
            (await reader.PostAsJsonAsync("/v1/rdf/transactions", transaction)).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK,
            (await writer.PostAsJsonAsync("/v1/rdf/transactions", transaction)).StatusCode);
        var update = new RdfSparqlUpdateRequest(Guid.NewGuid(),
            "INSERT DATA { <https://example.test/u> <https://example.test/p> \"value\" }");
        Assert.AreEqual(HttpStatusCode.Unauthorized,
            (await reader.PostAsJsonAsync("/v1/rdf/sparql/update", update)).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK,
            (await writer.PostAsJsonAsync("/v1/rdf/sparql/update", update)).StatusCode);
    }

    [TestMethod]
    public async Task RDF_host_atomically_asserts_retracts_and_idempotently_replays_transactions()
    {
        _directory = NewDirectory();
        await using EntityForgeDbHost host = EntityForgeDbServer.CreateBuilder()
            .UseLocalStorage(_directory).UseRdfProfile()
            .ConfigureServices(services => services.AddSingleton<IEntityForgeDbAuthorizationPolicy,
                AllowEntityForgeDbAuthorizationPolicy>())
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using HttpClient client = Client(host);
        var subject = new RdfIri("https://example.test/alice");
        var name = new RdfIri("https://schema.org/name");
        var oldQuad = new RdfQuad(subject, name, new RdfLiteral("Old name"));
        var newQuad = new RdfQuad(subject, name, new RdfLiteral("Alice"));
        Guid transactionId = Guid.NewGuid();
        var request = new RdfMutationBatchRequest(transactionId,
            [new("assert", Wire(newQuad)), new("retract", Wire(oldQuad))]);

        RdfMutationBatchResponse first = (await (await client.PostAsJsonAsync(
            "/v1/rdf/transactions", request)).Content.ReadFromJsonAsync<RdfMutationBatchResponse>())!;
        RdfMutationBatchResponse replay = (await (await client.PostAsJsonAsync(
            "/v1/rdf/transactions", request)).Content.ReadFromJsonAsync<RdfMutationBatchResponse>())!;
        Assert.AreEqual(first.Version, replay.Version);
        Assert.AreEqual(2, first.MutationCount);
        RdfQueryPage page = (await (await client.PostAsJsonAsync("/v1/rdf/quads",
            new RdfQuadQueryRequest(new(Subject: subject, Predicate: name))))
            .Content.ReadFromJsonAsync<RdfQueryPage>())!;
        CollectionAssert.AreEquivalent(new[] { newQuad }, page.Quads.ToArray());

        HttpResponseMessage changedReplay = await client.PostAsJsonAsync("/v1/rdf/transactions",
            new RdfMutationBatchRequest(transactionId, [new("retract", Wire(newQuad))]));
        Assert.AreEqual(HttpStatusCode.Conflict, changedReplay.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/v1/rdf/transactions",
            new RdfMutationBatchRequest(Guid.Empty, [new("assert", Wire(newQuad))]))).StatusCode);
        string orderIndependentJson = $$"""
            {
              "mutations": [{
                "quad": {
                  "object": { "lexicalForm": "JSON order", "kind": "literal" },
                  "predicate": { "value": "https://schema.org/name", "kind": "iri" },
                  "subject": { "value": "https://example.test/order-independent", "kind": "iri" }
                },
                "kind": "assert"
              }],
              "transactionId": "{{Guid.NewGuid()}}"
            }
            """;
        HttpResponseMessage orderIndependent = await client.PostAsync("/v1/rdf/transactions",
            new StringContent(orderIndependentJson, Encoding.UTF8, "application/json"));
        Assert.AreEqual(HttpStatusCode.OK, orderIndependent.StatusCode,
            await orderIndependent.Content.ReadAsStringAsync());
        string openApi = await client.GetStringAsync("/openapi/v1.json");
        Assert.IsTrue(openApi.Contains("/v1/rdf/transactions", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Named_backup_is_bounded_to_the_configured_directory()
    {
        _directory = NewDirectory();
        string backups = Path.Combine(_directory, "owned-backups");
        await using EntityForgeDbHost host = EntityForgeDbServer.CreateBuilder()
            .UseLocalStorage(_directory)
            .UseAdministration(new EntityForgeDbAdministrationOptions(), backups)
            .ConfigureServices(services => services.AddSingleton<IEntityForgeDbAuthorizationPolicy,
                AllowEntityForgeDbAuthorizationPolicy>())
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0"))
            .Build();
        await host.StartAsync();
        using HttpClient client = Client(host);

        HttpResponseMessage invalid = await client.PostAsJsonAsync("/v1/backups",
            new EntityForgeDbBackupRequest("../escape"));
        HttpResponseMessage created = await client.PostAsJsonAsync("/v1/backups",
            new EntityForgeDbBackupRequest("backup-001"));

        Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, created.StatusCode, await created.Content.ReadAsStringAsync());
        Assert.IsTrue(File.Exists(Path.Combine(backups, "backup-001")));
    }

    [TestMethod]
    public async Task Administrative_job_admission_rejects_excess_concurrency()
    {
        using var coordinator = new EntityForgeDbAdministrativeJobCoordinator(
            new EntityForgeDbAdministrationOptions(1, TimeSpan.FromSeconds(5)));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ValueTask<int> first = coordinator.ExecuteAsync(async cancellation =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(cancellation);
            return 1;
        }, CancellationToken.None);
        await entered.Task;

        await Assert.ThrowsExceptionAsync<EntityForgeDbAdministrativeJobRejectedException>(async () =>
            await coordinator.ExecuteAsync(_ => ValueTask.FromResult(2), CancellationToken.None));
        release.SetResult();
        Assert.AreEqual(1, await first);
    }

    [TestMethod]
    public async Task Administrative_job_timeout_is_reported_distinctly()
    {
        using var coordinator = new EntityForgeDbAdministrativeJobCoordinator(
            new EntityForgeDbAdministrationOptions(1, TimeSpan.FromMilliseconds(10)));
        await Assert.ThrowsExceptionAsync<EntityForgeDbAdministrativeJobTimeoutException>(async () =>
            await coordinator.ExecuteAsync(async cancellation =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
                return 1;
            }, CancellationToken.None));
    }

    [TestMethod]
    public async Task Http_ingest_recovers_from_same_volume_after_host_restart()
    {
        _directory = NewDirectory();
        SemanticSessionEnvelope session = Session();
        await using (EntityForgeDbHost first = BuildHost())
        {
            await first.StartAsync();
            using HttpClient client = Client(first);
            HttpResponseMessage ready = await client.GetAsync("/health/ready");
            Assert.AreEqual(HttpStatusCode.OK, ready.StatusCode);
            HttpResponseMessage applied = await client.PostAsJsonAsync("/v1/semantic/sessions", Transport(session));
            Assert.AreEqual(HttpStatusCode.OK, applied.StatusCode, await applied.Content.ReadAsStringAsync());
        }

        await using (EntityForgeDbHost recovered = BuildHost())
        {
            await recovered.StartAsync();
            using HttpClient client = Client(recovered);
            HttpResponseMessage replay = await client.PostAsJsonAsync("/v1/semantic/sessions", Transport(session));
            SessionCommitResult? result = await replay.Content.ReadFromJsonAsync<SessionCommitResult>();
            Assert.AreEqual(SessionCommitOutcome.IdempotentReplay, result!.Outcome);
            var runtime = recovered.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
            var resolution = await runtime.Database.Operations().ResolveAsync(session.Start.SessionId);
            Assert.AreEqual(EntityForge.Db.Mapping.EntityOperationState.Committed, resolution.State);
            Assert.AreEqual(result.Version, resolution.Receipt!.Version);
            Assert.AreEqual(EntityForge.Db.Mapping.EntityOperationState.Committed,
                (await runtime.Database.Operations().AbortAsync(session.Start.SessionId)).State);
            var query = new PortableEntityQueryRequest(Company().Identifier.AbsoluteUri,
                new PortableEntityProjectionRequest(Company().Identifier.AbsoluteUri,
                    Fields: [new PortableEntityFieldRequest(Industry().Identifier.AbsoluteUri, "industry")]),
                Identities: ["https://example.test/Company|https://example.test/companyNumber|01234567"],
                IncludeEvidence: true);
            EntityProjectionTransportResponse? recoveredResult = await (await client.PostAsJsonAsync(
                "/v1/semantic/entities/query", query)).Content.ReadFromJsonAsync<EntityProjectionTransportResponse>();
            Assert.AreEqual(1, recoveredResult!.Entities.Count);
            Assert.AreEqual(1, recoveredResult.Evidence.Count);
        }
    }

    [TestMethod]
    public async Task Http_bulk_load_applies_an_initial_semantic_session()
    {
        _directory = NewDirectory();
        await using EntityForgeDbHost host = BuildHost();
        await host.StartAsync();
        using HttpClient client = Client(host);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/v1/semantic/sessions/bulk", Transport(Session()));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        SessionCommitResult? result = await response.Content.ReadFromJsonAsync<SessionCommitResult>();
        Assert.AreEqual(SessionCommitOutcome.Applied, result!.Outcome);
    }

    private static string RevisionTarget(EntityForgeDbHost host, bool hasQuery = false) =>
        (hasQuery ? "&" : "?") + "databaseIncarnation=" + host.Application.Services.GetRequiredService<EntityForgeDbRuntime>().Database.Operations().DatabaseIncarnation.ToString("D") + "&expectedProfile=Semantic";

    [TestMethod]
    public async Task Http_contribution_revision_is_hidden_until_publish_and_reports_current_revision()
    {
        _directory = NewDirectory();
        await using EntityForgeDbHost host = BuildHost();
        await host.StartAsync();
        using HttpClient client = Client(host);
        SemanticSessionEnvelope draft = SourceSnapshot(new Uri("https://source.example/revision"),
            "companies", "revision-1", new SemanticFieldChange(Industry(),
                SemanticFieldChangeKind.Set, new StringSemanticValue("Technology")));
        var close = new SemanticChangeSessionClose(draft.Close.SessionId, draft.Close.PageCount,
            draft.Close.ChangeCount, draft.Close.ContentHash, draft.Close.ClosedAt,
            _codec.ComputeOperationHash(draft.Start, draft.Pages));
        var revision = new SemanticSessionEnvelope(draft.Start, draft.Pages, close);

        HttpResponseMessage begin = await client.PostAsJsonAsync(
            "/v1/semantic/contribution-revisions/begin" + RevisionTarget(host),
            new SemanticContributionRevisionBeginRequest(
                Convert.ToBase64String(_codec.Serialize(revision.Start)), null));
        Assert.AreEqual(HttpStatusCode.OK, begin.StatusCode, await begin.Content.ReadAsStringAsync());
        HttpResponseMessage part = await client.PostAsJsonAsync(
            "/v1/semantic/contribution-revisions/parts" + RevisionTarget(host),
            new SemanticContributionRevisionPartRequest(
                Convert.ToBase64String(_codec.Serialize(revision.Pages.Single()))));
        Assert.AreEqual(HttpStatusCode.OK, part.StatusCode, await part.Content.ReadAsStringAsync());
        SemanticContributionRevisionStatusResponse? status = await (await client.GetAsync(
                $"/v1/semantic/contribution-revisions/{revision.Start.SessionId:D}" + RevisionTarget(host)))
            .Content.ReadFromJsonAsync<SemanticContributionRevisionStatusResponse>();
        Assert.AreEqual(SemanticContributionRevisionState.Building, status!.Revision!.State);

        string lookup = "/v1/semantic/contribution-revisions/published?sourceSystem=" +
            Uri.EscapeDataString(revision.Start.Source.SourceSystem.AbsoluteUri) + "&sourcePartition=" +
            Uri.EscapeDataString(revision.Start.Source.SourcePartition.AbsoluteUri) + RevisionTarget(host, true);
        SemanticContributionRevisionLookupResponse? before = await (await client.GetAsync(lookup)).Content
            .ReadFromJsonAsync<SemanticContributionRevisionLookupResponse>();
        Assert.IsNull(before!.Revision);

        await client.PostAsJsonAsync("/v1/semantic/contribution-revisions/seal" + RevisionTarget(host),
            new SemanticContributionRevisionSealRequest(
                Convert.ToBase64String(_codec.Serialize(revision.Close))));
        HttpResponseMessage publish = await client.PostAsJsonAsync(
            $"/v1/semantic/contribution-revisions/{revision.Start.SessionId:D}/publish" + RevisionTarget(host), new
            {
            });
        Assert.AreEqual(HttpStatusCode.OK, publish.StatusCode, await publish.Content.ReadAsStringAsync());
        SemanticContributionRevisionLookupResponse? after = await (await client.GetAsync(lookup)).Content
            .ReadFromJsonAsync<SemanticContributionRevisionLookupResponse>();
        Assert.AreEqual(revision.Start.SessionId, after!.Revision!.GenerationId);
        status = await (await client.GetAsync(
                $"/v1/semantic/contribution-revisions/{revision.Start.SessionId:D}" + RevisionTarget(host)))
            .Content.ReadFromJsonAsync<SemanticContributionRevisionStatusResponse>();
        Assert.AreEqual(SemanticContributionRevisionState.Published, status!.Revision!.State);
    }

    [TestMethod]
    public async Task Http_exact_ordinary_replay_remains_available_after_revision_enrollment()
    {
        _directory = NewDirectory();
        await using EntityForgeDbHost host = BuildHost();
        await host.StartAsync();
        using HttpClient client = Client(host);
        SemanticSessionEnvelope ordinary = Session();
        HttpResponseMessage appliedResponse = await client.PostAsJsonAsync(
            "/v1/semantic/sessions", Transport(ordinary));
        SessionCommitResult applied = (await appliedResponse.Content
            .ReadFromJsonAsync<SessionCommitResult>())!;
        Guid revisionId = Guid.NewGuid();
        var revisionStart = new SemanticChangeSessionStart(revisionId,
            SemanticChangeSessionKind.SourceSnapshot, ordinary.Start.Source,
            Guid.NewGuid(), DateTimeOffset.UtcNow);
        HttpResponseMessage begin = await client.PostAsJsonAsync(
            "/v1/semantic/contribution-revisions/begin" + RevisionTarget(host),
            new SemanticContributionRevisionBeginRequest(
                Convert.ToBase64String(_codec.Serialize(revisionStart)), null));
        Assert.AreEqual(HttpStatusCode.OK, begin.StatusCode, await begin.Content.ReadAsStringAsync());

        HttpResponseMessage replayResponse = await client.PostAsJsonAsync(
            "/v1/semantic/sessions", Transport(ordinary));
        SessionCommitResult replay = (await replayResponse.Content
            .ReadFromJsonAsync<SessionCommitResult>())!;

        Assert.AreEqual(HttpStatusCode.OK, replayResponse.StatusCode);
        Assert.AreEqual(SessionCommitOutcome.IdempotentReplay, replay.Outcome);
        Assert.AreEqual(applied.TransactionId, replay.TransactionId);
        Assert.AreEqual(applied.Version, replay.Version);
    }

    [TestMethod]
    public async Task Http_semantic_session_reports_source_progress_conflict_as_conflict()
    {
        _directory = NewDirectory();
        await using EntityForgeDbHost host = BuildHost();
        await host.StartAsync();
        using HttpClient client = Client(host);

        SemanticSessionEnvelope first = WithProgressPrecondition(
            Session(), SemanticSourceProgressPrecondition.Initial);
        HttpResponseMessage applied = await client.PostAsJsonAsync(
            "/v1/semantic/sessions", Transport(first));
        Assert.AreEqual(HttpStatusCode.OK, applied.StatusCode, await applied.Content.ReadAsStringAsync());

        SemanticSessionEnvelope stale = WithProgressPrecondition(
            Session(), SemanticSourceProgressPrecondition.Initial);
        HttpResponseMessage conflict = await client.PostAsJsonAsync(
            "/v1/semantic/sessions", Transport(stale));
        string problem = await conflict.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.Conflict, conflict.StatusCode, problem);
        StringAssert.Contains(problem, "source_progress_conflict");
    }

    [TestMethod]
    public async Task Hosted_source_replacement_emits_retractions_and_preserves_other_source_after_restart()
    {
        _directory = NewDirectory();
        Uri authoritative = new("https://source.example/authoritative-companies");
        Uri enrichment = new("https://source.example/company-enrichment");
        string? continuation;

        await using (EntityForgeDbHost first = BuildHost())
        {
            await first.StartAsync();
            using HttpClient client = Client(first);
            await ApplyAsync(client, SourceSnapshot(authoritative, "authoritative", "1",
                new SemanticFieldChange(Industry(), SemanticFieldChangeKind.Set,
                    new StringSemanticValue("Technology"))));
            await ApplyAsync(client, SourceSnapshot(enrichment, "enrichment", "1",
                new SemanticFieldChange(Name(), SemanticFieldChangeKind.Set,
                    new StringSemanticValue("Acuity Demo Consulting Ltd"))));
            await ApplyAsync(client, SourceSnapshot(authoritative, "authoritative", "2",
                new SemanticFieldChange(Industry(), SemanticFieldChangeKind.Set,
                    new StringSemanticValue("Consulting"))));

            SemanticChangePageResponse? firstPage = await client.GetFromJsonAsync<SemanticChangePageResponse>(
                "/v1/semantic/changes?maximumCount=2");
            Assert.AreEqual(2, firstPage!.Changes.Count);
            Assert.IsTrue(firstPage.HasMore);
            continuation = firstPage.NextCursor;
        }

        await using (EntityForgeDbHost recovered = BuildHost())
        {
            await recovered.StartAsync();
            using HttpClient client = Client(recovered);
            SemanticChangePageResponse? page = await client.GetFromJsonAsync<SemanticChangePageResponse>(
                $"/v1/semantic/changes?cursor={Uri.EscapeDataString(continuation!)}&maximumCount=10");
            SemanticChangeEffectResponse[] effects = page!.Changes.Single().Effects.ToArray();
            Assert.IsTrue(effects.Any(effect => effect.Kind == "fieldAssertionRetracted" &&
                effect.Term == Industry().Identifier.AbsoluteUri && effect.Value?.GetString() == "Technology"));
            Assert.IsTrue(effects.Any(effect => effect.Kind == "fieldAssertionAdded" &&
                effect.Term == Industry().Identifier.AbsoluteUri && effect.Value?.GetString() == "Consulting"));

            var query = new PortableEntityQueryRequest(Company().Identifier.AbsoluteUri,
                new PortableEntityProjectionRequest(Company().Identifier.AbsoluteUri, Fields:
                [
                    new PortableEntityFieldRequest(Industry().Identifier.AbsoluteUri, "industry"),
                    new PortableEntityFieldRequest(Name().Identifier.AbsoluteUri, "name")
                ]),
                Identities: ["https://example.test/Company|https://example.test/companyNumber|01234567"],
                IncludeEvidence: true);
            EntityProjectionTransportResponse? result = await (await client.PostAsJsonAsync(
                "/v1/semantic/entities/query", query)).Content.ReadFromJsonAsync<EntityProjectionTransportResponse>();
            Assert.AreEqual("Consulting", result!.Entities.Single().Fields["industry"].Single().LexicalValue);
            Assert.AreEqual("Acuity Demo Consulting Ltd",
                result.Entities.Single().Fields["name"].Single().LexicalValue);
            Assert.IsTrue(result.Evidence.Any(item => item.Provider == enrichment.AbsoluteUri));
        }
    }

    [TestMethod]
    public async Task Hosted_change_feed_rejects_invalid_cursors_and_incomplete_routing_filters()
    {
        _directory = NewDirectory();
        await using EntityForgeDbHost host = BuildHost();
        await host.StartAsync();
        using HttpClient client = Client(host);

        HttpResponseMessage invalidCursor = await client.GetAsync("/v1/semantic/changes?cursor=not-a-cursor");
        HttpResponseMessage incompleteFilter = await client.GetAsync(
            "/v1/semantic/changes?routingKind=semantic.entity");

        Assert.AreEqual(HttpStatusCode.BadRequest, invalidCursor.StatusCode);
        StringAssert.Contains(await invalidCursor.Content.ReadAsStringAsync(), "change_cursor_invalid");
        Assert.AreEqual(HttpStatusCode.BadRequest, incompleteFilter.StatusCode);
        StringAssert.Contains(await incompleteFilter.Content.ReadAsStringAsync(), "request_invalid");
    }

    [TestMethod]
    public async Task Capabilities_state_semantic_profile_and_explicitly_exclude_native_graph_profile()
    {
        _directory = NewDirectory();
        await using EntityForgeDbHost host = BuildHost();
        await host.StartAsync();
        using HttpClient client = Client(host);

        EntityForgeHostCapabilitiesTransport? capabilities =
            await client.GetFromJsonAsync<EntityForgeHostCapabilitiesTransport>("/v1/capabilities");

        Assert.AreEqual(ExpectedProductVersion, capabilities!.Version);
        Assert.AreEqual("semantic", capabilities.Profile);
        EntityForgeSurfaceCapability native = capabilities.Surfaces.Single(surface =>
            surface.Identifier == EntityForgeSurfaceIds.SemanticNative);
        CollectionAssert.Contains(native.Features.ToArray(), "explain-analyze");
        CollectionAssert.Contains(native.Features.ToArray(), "continuation");
        CollectionAssert.Contains(native.Features.ToArray(), "typed-scalar-filter");
        CollectionAssert.Contains(native.Features.ToArray(), "as-of-version");
        CollectionAssert.Contains(native.Features.ToArray(), "temporal-query-scopes");
        Assert.IsTrue(capabilities.Surfaces.Any(surface => surface.Identifier == EntityForgeSurfaceIds.ChangeFeed));
        Assert.IsTrue(capabilities.Surfaces.Any(surface => surface.Identifier == EntityForgeSurfaceIds.SnapshotLeases));
        Assert.IsTrue(capabilities.Surfaces.Any(surface => surface.Identifier == EntityForgeSurfaceIds.GraphQlProjection));
        Assert.IsFalse(capabilities.Surfaces.Any(surface => surface.Identifier == EntityForgeSurfaceIds.GraphNative));
        string openApi = await client.GetStringAsync("/openapi/v1.json");
        StringAssert.Contains(openApi, "3.0.3");
        StringAssert.Contains(openApi, "/v1/semantic/entities/query");
        StringAssert.Contains(openApi, "/v1/semantic/entities/explain");
        StringAssert.Contains(openApi, "/v1/semantic/changes");
        StringAssert.Contains(openApi, "/v1/semantic/snapshot-leases");
        StringAssert.Contains(openApi, "/graphql");
    }

    [TestMethod]
    public async Task Explain_endpoint_reports_the_portable_query_plan_and_analyzed_counts()
    {
        _directory = NewDirectory();
        await using EntityForgeDbHost host = BuildHost();
        await host.StartAsync();
        EntityForgeDbRuntime runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
        await runtime.Database.ConfigurePropertyStorageAsync(Industry(),
            SemanticPropertyStorageKind.IndexedJson);
        await runtime.Database.ApplyAsync(Session());
        using HttpClient client = Client(host);

        var query = new PortableEntityQueryRequest(Company().Identifier.AbsoluteUri,
            new PortableEntityProjectionRequest(Company().Identifier.AbsoluteUri,
                Fields: [new PortableEntityFieldRequest(Industry().Identifier.AbsoluteUri, "industry")]),
            Filter: new PortableEntityFilterRequest("propertyComparison", Industry().Identifier.AbsoluteUri, "equal",
                new PortableQueryLiteralRequest(JsonSerializer.SerializeToElement("Technology"))));
        HttpResponseMessage response = await client.PostAsJsonAsync("/v1/semantic/entities/explain",
            new PortableEntityQueryExplainRequest(query, Analyze: true));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        SemanticEntityQueryExplanation? explanation = await response.Content
            .ReadFromJsonAsync<SemanticEntityQueryExplanation>();
        Assert.IsNotNull(explanation);
        CollectionAssert.Contains(explanation.CandidateAccessPaths.ToArray(),
            "append-merge.semantic-field-equality");
        Assert.AreEqual(1, explanation.ActualCandidates);
        Assert.AreEqual(1, explanation.ActualRoots);
        Assert.IsNotNull(explanation.ExecutionDuration);
    }

    [TestMethod]
    public async Task Fluent_and_serialized_forms_compile_to_the_same_neutral_query_shape()
    {
        _directory = NewDirectory();
        await using EntityForgeDbHost host = BuildHost();
        await host.StartAsync();
        EntityForgeDbRuntime runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
        var map = new SemanticEntityMap<string>(Company(),
            [new EntityFieldProjection(Industry(), "industry")],
            entity => entity.Required<string>("industry"));
        EntityQueryDefinition fluent = runtime.Database.Entities(map)
            .Where(Industry(), EntityQueryComparisonOperator.Equal, "Technology")
            .OrderBy(Industry(), EntityQueryOrderDirection.Descending)
            .Take(7)
            .WithAssertionPolicy(new SemanticAssertionPolicy(
                allowedProviders: [new Uri("https://source.example/companies")]))
            .IncludeEvidence()
            .ToQueryDefinition();
        var serialized = new PortableEntityQueryRequest(Company().Identifier.AbsoluteUri,
            new PortableEntityProjectionRequest(Company().Identifier.AbsoluteUri,
                Fields: [new PortableEntityFieldRequest(Industry().Identifier.AbsoluteUri, "industry")]),
            Filter: new PortableEntityFilterRequest("propertyComparison", Industry().Identifier.AbsoluteUri,
                "equal", new PortableQueryLiteralRequest(JsonSerializer.SerializeToElement("Technology"))),
            OrderBy: [new PortableEntityOrderingRequest(Industry().Identifier.AbsoluteUri, "descending")],
            First: 7,
            IncludeEvidence: true,
            AssertionPolicy: new(AllowedProviders: ["https://source.example/companies"]));
        CompiledPortableEntityQuery compiled = new PortableEntityQueryRequestCompiler().Compile(serialized);

        Assert.AreEqual(fluent.RootConcept, compiled.Query.RootConcept);
        Assert.AreEqual(fluent.Projection.Concept, compiled.Query.Projection.Concept);
        CollectionAssert.AreEqual(fluent.Projection.Fields.ToArray(),
            compiled.Query.Projection.Fields.ToArray());
        Assert.AreEqual(fluent.Filter, compiled.Query.Filter);
        CollectionAssert.AreEqual(fluent.Orderings.ToArray(), compiled.Query.Orderings.ToArray());
        Assert.AreEqual(fluent.Page, compiled.Query.Page);
        Assert.AreEqual(fluent.AssertionPolicy!.AllowedProviders.Single(),
            compiled.Query.AssertionPolicy!.AllowedProviders.Single());
        Assert.IsTrue(compiled.ProjectionOptions.IncludeEvidence);
    }

    [TestMethod]
    public void Portable_and_graphql_temporal_scopes_compile_to_the_same_neutral_dimensions()
    {
        DateTimeOffset from = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        DateTimeOffset to = DateTimeOffset.Parse("2026-02-01T00:00:00Z");
        var portable = new PortableEntityQueryRequest(Company().Identifier.AbsoluteUri,
            new PortableEntityProjectionRequest(Company().Identifier.AbsoluteUri),
            TemporalScopes:
            [
                new("observationTime", from, to),
                new("validTime", from, to)
            ]);
        CompiledPortableEntityQuery compiled = new PortableEntityQueryRequestCompiler().Compile(portable);
        const string graphQlSource = """
            {
              entities(
                concept: "https://example.test/Company"
                observationTime: { fromInclusive: "2026-01-01T00:00:00Z", toExclusive: "2026-02-01T00:00:00Z" }
                validTime: { fromInclusive: "2026-01-01T00:00:00Z", toExclusive: "2026-02-01T00:00:00Z" }
              ) { nodes { identity } }
            }
            """;
        CompiledGraphQlEntityQuery graphQl = new GraphQlEntityQueryCompiler(null).Compile(graphQlSource, null);
        CompiledPortableEntityQuery graphQlCompiled = new PortableEntityQueryRequestCompiler().Compile(graphQl.Request);

        CollectionAssert.AreEqual(compiled.Query.Scopes.ToArray(), graphQlCompiled.Query.Scopes.ToArray());
        Assert.AreEqual(EntityQueryTemporalDimension.ObservationTime,
            ((TemporalEntityQueryScope)compiled.Query.Scopes[0]).Dimension);
        Assert.AreEqual(EntityQueryTemporalDimension.ValidTime,
            ((TemporalEntityQueryScope)compiled.Query.Scopes[1]).Dimension);
    }

    [TestMethod]
    public void Portable_numeric_literals_reject_string_values_during_request_compilation()
    {
        var request = new PortableEntityQueryRequest(Company().Identifier.AbsoluteUri,
            new PortableEntityProjectionRequest(Company().Identifier.AbsoluteUri),
            Filter: new PortableEntityFilterRequest("propertyComparison", Industry().Identifier.AbsoluteUri,
                "greaterThan", new PortableQueryLiteralRequest(JsonSerializer.SerializeToElement(String.Empty),
                    "decimal")));

        ArgumentException exception = Assert.ThrowsException<ArgumentException>(() =>
            new PortableEntityQueryRequestCompiler().Compile(request));

        StringAssert.Contains(exception.Message, "filter.value");
        StringAssert.Contains(exception.Message, "incompatible JSON value");
    }

    [TestMethod]
    public async Task GraphQl_adapter_projects_aliases_evidence_and_snapshot_page_information()
    {
        _directory = NewDirectory();
        await using EntityForgeDbHost host = BuildHost();
        await host.StartAsync();
        EntityForgeDbRuntime runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
        await runtime.Database.ApplyAsync(Session());
        using HttpClient client = Client(host);
        const string query = """
            query CompanySearch($concept: URI!, $industry: ScalarValue!) {
              companies: entities(
                concept: $concept
                first: 10
                includeEvidence: true
                where: { property: "https://example.test/industry", eq: $industry }
              ) {
                nodes {
                  identity
                  industry: field(property: "https://example.test/industry") { values { value evidence } }
                }
                pageInfo { endCursor hasNextPage snapshotVersion }
                evidence { identifier provider }
              }
            }
            """;
        var request = new EntityForgeGraphQlRequest(query, new Dictionary<string, JsonElement>
        {
            ["concept"] = JsonSerializer.SerializeToElement(Company().Identifier.AbsoluteUri),
            ["industry"] = JsonSerializer.SerializeToElement("Technology")
        }, "CompanySearch");

        HttpResponseMessage response = await client.PostAsJsonAsync("/graphql", request);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement root = document.RootElement.GetProperty("data").GetProperty("companies");
        JsonElement entity = root.GetProperty("nodes")[0];
        Assert.AreEqual("https://example.test/Company|https://example.test/companyNumber|01234567",
            entity.GetProperty("identity").GetString());
        Assert.AreEqual("Technology", entity.GetProperty("industry").GetProperty("values")[0]
            .GetProperty("value").GetString());
        Assert.AreEqual(1, root.GetProperty("evidence").GetArrayLength());
        Assert.IsTrue(root.GetProperty("pageInfo").GetProperty("snapshotVersion").GetInt64() > 0);
    }

    [TestMethod]
    public async Task Semantic_assertion_policy_filters_provenance_across_HTTP_GraphQL_and_explain()
    {
        _directory = NewDirectory();
        await using EntityForgeDbHost host = BuildHost();
        await host.StartAsync();
        using HttpClient client = Client(host);
        Uri approved = new("https://source.example/approved");
        Uri unapproved = new("https://source.example/unapproved");
        await ApplyAsync(client, SourceSnapshot(approved, "companies", "1",
            new SemanticFieldChange(Industry(), SemanticFieldChangeKind.Set,
                new StringSemanticValue("Technology"))));
        await ApplyAsync(client, SourceSnapshot(unapproved, "companies", "1",
            new SemanticFieldChange(Name(), SemanticFieldChangeKind.Set,
                new StringSemanticValue("Untrusted name"))));
        var request = new PortableEntityQueryRequest(Company().Identifier.AbsoluteUri,
            new PortableEntityProjectionRequest(Company().Identifier.AbsoluteUri,
                Fields:
                [
                    new(Industry().Identifier.AbsoluteUri, "industry"),
                    new(Name().Identifier.AbsoluteUri, "name")
                ]),
            IncludeEvidence: true,
            AssertionPolicy: new(AllowedProviders: [approved.AbsoluteUri]));

        HttpResponseMessage response = await client.PostAsJsonAsync("/v1/semantic/entities/query", request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        EntityProjectionTransportResponse result = (await response.Content
            .ReadFromJsonAsync<EntityProjectionTransportResponse>())!;
        Assert.AreEqual("Technology", result.Entities.Single().Fields["industry"].Single().LexicalValue);
        Assert.IsFalse(result.Entities.Single().Fields.ContainsKey("name"));
        Assert.IsTrue(result.Evidence.All(item => item.Provider == approved.AbsoluteUri));

        foreach (PortableSemanticAssertionPolicyRequest restrictivePolicy in new[]
        {
            new PortableSemanticAssertionPolicyRequest(ExcludedProviders:
                [approved.AbsoluteUri, unapproved.AbsoluteUri]),
            new PortableSemanticAssertionPolicyRequest(AllowedSourceVocabularies:
                ["https://vocabulary.example/not-used"]),
            new PortableSemanticAssertionPolicyRequest(AllowedTargetVocabularies:
                ["https://vocabulary.example/not-used"]),
            new PortableSemanticAssertionPolicyRequest(RequireValidityInterval: true),
            new PortableSemanticAssertionPolicyRequest(RequireResponsibleAgent: true)
        })
        {
            EntityProjectionTransportResponse restricted = (await (await client.PostAsJsonAsync(
                "/v1/semantic/entities/query", request with
                {
                    AssertionPolicy = restrictivePolicy
                }))
                .Content.ReadFromJsonAsync<EntityProjectionTransportResponse>())!;
            Assert.AreEqual(0, restricted.Entities.Count);
        }

        HttpResponseMessage graphQl = await client.PostAsJsonAsync("/graphql", new EntityForgeGraphQlRequest($$"""
            query {
              entities(
                concept: "{{Company().Identifier.AbsoluteUri}}"
                assertions: { allowedProviders: ["{{approved.AbsoluteUri}}"] }
              ) {
                nodes {
                  identity
                  industry: field(property: "{{Industry().Identifier.AbsoluteUri}}") { values { value } }
                  name: field(property: "{{Name().Identifier.AbsoluteUri}}") { values { value } }
                }
              }
            }
            """));
        Assert.AreEqual(HttpStatusCode.OK, graphQl.StatusCode, await graphQl.Content.ReadAsStringAsync());
        using JsonDocument graphQlDocument = JsonDocument.Parse(await graphQl.Content.ReadAsStringAsync());
        JsonElement graphQlNode = graphQlDocument.RootElement.GetProperty("data").GetProperty("entities")
            .GetProperty("nodes")[0];
        Assert.AreEqual("Technology", graphQlNode.GetProperty("industry").GetProperty("values")[0]
            .GetProperty("value").GetString());
        Assert.AreEqual(0, graphQlNode.GetProperty("name").GetProperty("values").GetArrayLength());

        SemanticEntityQueryExplanation explanation = (await (await client.PostAsJsonAsync(
            "/v1/semantic/entities/explain", new PortableEntityQueryExplainRequest(request)))
            .Content.ReadFromJsonAsync<SemanticEntityQueryExplanation>())!;
        Assert.IsTrue(explanation.AppliedScopes.Any(item =>
            item.Contains("assertions.allowedProviders", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Semantic_predicates_support_ordered_comparisons_boolean_composition_and_existence()
    {
        _directory = NewDirectory();
        await using EntityForgeDbHost host = BuildHost();
        await host.StartAsync();
        using HttpClient client = Client(host);
        var headcount = new SemanticTerm(new Uri("https://example.test/headcount"), SemanticTermRole.Property);
        await ApplyAsync(client, Session("COMPANY000001", headcount, new IntegerSemanticValue(10)));
        await ApplyAsync(client, Session("COMPANY000002", headcount, new IntegerSemanticValue(20)));
        await ApplyAsync(client, Session("COMPANY000003", headcount, new IntegerSemanticValue(30)));
        await ApplyAsync(client, Session("COMPANY000004", Name(), new StringSemanticValue("No count")));
        PortableEntityProjectionRequest projection = new(Company().Identifier.AbsoluteUri,
            Fields: [new(headcount.Identifier.AbsoluteUri, "headcount")]);
        PortableQueryLiteralRequest Integer(long value) => new(JsonSerializer.SerializeToElement(value), "integer");
        async Task<EntityProjectionTransportResponse> Query(PortableEntityFilterRequest filter) =>
            (await (await client.PostAsJsonAsync("/v1/semantic/entities/query", new PortableEntityQueryRequest(
                Company().Identifier.AbsoluteUri, projection, filter)))
                .Content.ReadFromJsonAsync<EntityProjectionTransportResponse>())!;

        EntityProjectionTransportResponse greater = await Query(new("propertyComparison",
            headcount.Identifier.AbsoluteUri, "greaterThan", Integer(15)));
        Assert.AreEqual(2, greater.Entities.Count);

        EntityProjectionTransportResponse disjunction = await Query(new("or", Operands:
        [
            new("propertyComparison", headcount.Identifier.AbsoluteUri, "equal", Integer(10)),
            new("propertyComparison", headcount.Identifier.AbsoluteUri, "equal", Integer(30))
        ]));
        Assert.AreEqual(2, disjunction.Entities.Count);

        EntityProjectionTransportResponse missing = await Query(new("not", Operand:
            new("propertyExists", Property: headcount.Identifier.AbsoluteUri)));
        Assert.AreEqual("https://example.test/Company|https://example.test/companyNumber|COMPANY000004",
            missing.Entities.Single().Identity);

        HttpResponseMessage graphQl = await client.PostAsJsonAsync("/graphql", new EntityForgeGraphQlRequest($$"""
            query {
              entities(
                concept: "{{Company().Identifier.AbsoluteUri}}"
                where: { or: [
                  { property: "{{headcount.Identifier.AbsoluteUri}}", lt: 15, kind: "integer" },
                  { not: { property: "{{headcount.Identifier.AbsoluteUri}}", exists: true } }
                ] }
              ) {
                nodes { identity count: field(property: "{{headcount.Identifier.AbsoluteUri}}") { values { value } } }
              }
            }
            """));
        Assert.AreEqual(HttpStatusCode.OK, graphQl.StatusCode, await graphQl.Content.ReadAsStringAsync());
        using JsonDocument document = JsonDocument.Parse(await graphQl.Content.ReadAsStringAsync());
        JsonElement nodes = document.RootElement.GetProperty("data").GetProperty("entities").GetProperty("nodes");
        Assert.AreEqual(2, nodes.GetArrayLength());
        Assert.IsTrue(nodes.EnumerateArray().Any(node =>
            node.GetProperty("identity").GetString()!.EndsWith("COMPANY000004", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Semantic_aggregation_groups_typed_values_and_preserves_evidence_and_snapshot()
    {
        _directory = NewDirectory();
        await using EntityForgeDbHost host = BuildHost();
        await host.StartAsync();
        using HttpClient client = Client(host);
        var headcount = new SemanticTerm(new Uri("https://example.test/headcount"), SemanticTermRole.Property);
        await ApplyAsync(client, Session("COMPANY000001",
            new SemanticFieldChange(Industry(), SemanticFieldChangeKind.Set, new StringSemanticValue("Technology")),
            new SemanticFieldChange(headcount, SemanticFieldChangeKind.Set, new IntegerSemanticValue(10))));
        await ApplyAsync(client, Session("COMPANY000002",
            new SemanticFieldChange(Industry(), SemanticFieldChangeKind.Set, new StringSemanticValue("Technology")),
            new SemanticFieldChange(headcount, SemanticFieldChangeKind.Set, new IntegerSemanticValue(20))));
        await ApplyAsync(client, Session("COMPANY000003",
            new SemanticFieldChange(Industry(), SemanticFieldChangeKind.Set, new StringSemanticValue("Finance")),
            new SemanticFieldChange(headcount, SemanticFieldChangeKind.Set, new IntegerSemanticValue(5))));
        var request = new PortableSemanticAggregationRequest(
            new PortableEntityQueryRequest(Company().Identifier.AbsoluteUri,
                new PortableEntityProjectionRequest(Company().Identifier.AbsoluteUri), IncludeEvidence: true),
            [new(Industry().Identifier.AbsoluteUri, "industry")],
            [
                new("countEntities", "companies"),
                new("sum", "totalHeadcount", headcount.Identifier.AbsoluteUri),
                new("average", "averageHeadcount", headcount.Identifier.AbsoluteUri),
                new("minimum", "minimumHeadcount", headcount.Identifier.AbsoluteUri),
                new("maximum", "maximumHeadcount", headcount.Identifier.AbsoluteUri)
            ]);

        HttpResponseMessage response = await client.PostAsJsonAsync("/v1/semantic/aggregations/query", request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        PortableSemanticAggregationResponse result = (await response.Content
            .ReadFromJsonAsync<PortableSemanticAggregationResponse>())!;
        Assert.AreEqual(3, result.InputEntityCount);
        Assert.AreEqual(2, result.Rows.Count);
        PortableSemanticAggregationRowResponse technology = result.Rows.Single(row =>
            row.Values["industry"].Value.GetString() == "Technology");
        Assert.AreEqual(2, technology.Values["companies"].Value.GetInt64());
        Assert.AreEqual(30m, technology.Values["totalHeadcount"].Value.GetDecimal());
        Assert.AreEqual(15m, technology.Values["averageHeadcount"].Value.GetDecimal());
        Assert.AreEqual(10, technology.Values["minimumHeadcount"].Value.GetInt64());
        Assert.AreEqual(20, technology.Values["maximumHeadcount"].Value.GetInt64());
        Assert.IsTrue(technology.Evidence.Count > 0);
        Assert.IsTrue(result.Evidence.Count > 0);

        SemanticAggregationExplanation explanation = (await (await client.PostAsJsonAsync(
            "/v1/semantic/aggregations/explain", new PortableSemanticAggregationExplainRequest(request, true)))
            .Content.ReadFromJsonAsync<SemanticAggregationExplanation>())!;
        Assert.AreEqual(3, explanation.ActualInputEntities);
        Assert.AreEqual(2, explanation.ActualGroups);

        EntityForgeDbRuntime runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
        var map = new SemanticEntityMap<string>(Company(),
            [new EntityFieldProjection(Industry(), "industry")], entity => entity.Required<string>("industry"));
        SemanticAggregationResult embedded = await runtime.Database.Entities(map).AggregateAsync(
            [new(Industry(), "industry")], [new(SemanticAggregationFunction.CountEntities, "companies")]);
        Assert.AreEqual(result.SnapshotVersion, embedded.SnapshotVersion.Value);
        Assert.AreEqual(2, embedded.Rows.Count);

        HttpResponseMessage graphQl = await client.PostAsJsonAsync("/graphql", new EntityForgeGraphQlRequest($$"""
            query {
              aggregateEntities(
                concept: "{{Company().Identifier.AbsoluteUri}}"
                dimensions: [{ property: "{{Industry().Identifier.AbsoluteUri}}", name: "industry" }]
                measures: [
                  { function: "countEntities", name: "companies" },
                  { function: "sum", name: "totalHeadcount", property: "{{headcount.Identifier.AbsoluteUri}}" }
                ]
              ) { rows { values evidence } inputEntityCount snapshotVersion }
            }
            """));
        Assert.AreEqual(HttpStatusCode.OK, graphQl.StatusCode, await graphQl.Content.ReadAsStringAsync());
        using JsonDocument graphQlDocument = JsonDocument.Parse(await graphQl.Content.ReadAsStringAsync());
        JsonElement aggregate = graphQlDocument.RootElement.GetProperty("data").GetProperty("aggregateEntities");
        Assert.AreEqual(3, aggregate.GetProperty("inputEntityCount").GetInt64());
        Assert.AreEqual(2, aggregate.GetProperty("rows").GetArrayLength());
    }

    [TestMethod]
    public async Task GraphQl_adapter_reports_unsupported_capabilities_in_graphql_error_shape()
    {
        _directory = NewDirectory();
        await using EntityForgeDbHost host = BuildHost();
        await host.StartAsync();
        using HttpClient client = Client(host);
        var request = new EntityForgeGraphQlRequest("mutation { entities(concept: \"https://example.test/Company\") { nodes { identity } } }");

        HttpResponseMessage response = await client.PostAsJsonAsync("/graphql", request);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        string json = await response.Content.ReadAsStringAsync();
        StringAssert.Contains(json, "errors");
    }

    [TestMethod]
    public void GraphQl_adapter_compiles_nested_relationships_to_the_recursive_neutral_query()
    {
        const string query = """
            {
              entities(
                concept: "https://example.test/Company"
                first: 5
                after: "continuation-token"
                orderBy: { property: "https://example.test/name", direction: descending }
              ) {
                nodes {
                  people: relationship(
                    property: "https://example.test/employs"
                    targetConcept: "https://example.test/Person"
                  ) { nodes {
                    identity
                    name: field(property: "https://example.test/name") { values { value } }
                    employer: relationship(
                      property: "https://example.test/employs"
                      targetConcept: "https://example.test/Company"
                      direction: incoming
                    ) { nodes { identity } }
                  } }
                }
              }
            }
            """;

        CompiledGraphQlEntityQuery graphQl = new GraphQlEntityQueryCompiler(null).Compile(query, null);
        CompiledPortableEntityQuery compiled = new PortableEntityQueryRequestCompiler().Compile(graphQl.Request);

        Assert.AreEqual(5, compiled.Query.Page!.First);
        Assert.AreEqual("continuation-token", compiled.Query.Page!.After);
        Assert.AreEqual(EntityQueryOrderDirection.Descending, compiled.Query.Orderings.Single().Direction);
        EntityRelationshipProjection relationship = compiled.Query.Projection.Relationships.Single();
        Assert.AreEqual("people", relationship.Name);
        Assert.AreEqual("https://example.test/Person", relationship.Target.Concept.Identifier.AbsoluteUri);
        Assert.AreEqual("name", relationship.Target.Fields.Single().Name);
        EntityRelationshipProjection nested = relationship.Target.Relationships.Single();
        Assert.AreEqual("employer", nested.Name);
        Assert.AreEqual(EntityRelationshipDirection.Incoming, nested.Direction);
        Assert.AreEqual("https://example.test/Company", nested.Target.Concept.Identifier.AbsoluteUri);
    }

    [TestMethod]
    public async Task Query_invalidation_is_resumable_and_ignores_unreferenced_terms()
    {
        _directory = NewDirectory();
        await using EntityForgeDbHost host = BuildHost();
        await host.StartAsync();
        EntityForgeDbRuntime runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
        await runtime.Database.ApplyAsync(Session());
        using HttpClient client = Client(host);
        var query = new PortableEntityQueryRequest(Company().Identifier.AbsoluteUri,
            new PortableEntityProjectionRequest(Company().Identifier.AbsoluteUri,
                Fields: [new PortableEntityFieldRequest(Industry().Identifier.AbsoluteUri, "industry")]));
        SemanticEntityQueryExplanation plan = await runtime.Database.ExplainEntitiesAsync(
            new PortableEntityQueryRequestCompiler().Compile(query).Query);

        await runtime.Database.ApplyAsync(SourceSnapshot(new("https://source.example/names"), "names", "1",
            new SemanticFieldChange(Name(), SemanticFieldChangeKind.Set, new StringSemanticValue("Unrelated"))));
        var firstRequest = new SemanticEntityQueryInvalidationRequest(query, plan.SnapshotVersion.Value);
        SemanticEntityQueryInvalidationResponse? unchanged = await (await client.PostAsJsonAsync(
            "/v1/semantic/entities/invalidation", firstRequest)).Content
            .ReadFromJsonAsync<SemanticEntityQueryInvalidationResponse>();
        Assert.IsFalse(unchanged!.IsInvalidated);
        Assert.IsNotNull(unchanged.NextCursor);

        await runtime.Database.ApplyAsync(Session("88888888", Industry(), new StringSemanticValue("Finance")));
        SemanticEntityQueryInvalidationResponse? changed = await (await client.PostAsJsonAsync(
            "/v1/semantic/entities/invalidation", firstRequest with
            {
                Cursor = unchanged.NextCursor
            })).Content
            .ReadFromJsonAsync<SemanticEntityQueryInvalidationResponse>();

        Assert.IsTrue(changed!.IsInvalidated);
        Assert.IsTrue(changed.InvalidatingVersion > plan.SnapshotVersion.Value);
        CollectionAssert.Contains(changed.RelevantEffects.ToArray(), "FieldAssertionAdded");
    }

    [TestMethod]
    public async Task Query_invalidation_subscription_streams_progress_then_stops_at_relevant_change()
    {
        _directory = NewDirectory();
        await using EntityForgeDbHost host = BuildHost();
        await host.StartAsync();
        EntityForgeDbRuntime runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
        await runtime.Database.ApplyAsync(Session());
        var query = new PortableEntityQueryRequest(Company().Identifier.AbsoluteUri,
            new PortableEntityProjectionRequest(Company().Identifier.AbsoluteUri,
                Fields: [new PortableEntityFieldRequest(Industry().Identifier.AbsoluteUri, "industry")]));
        EntityQueryDefinition definition = new PortableEntityQueryRequestCompiler().Compile(query).Query;
        SemanticEntityQueryExplanation plan = await runtime.Database.ExplainEntitiesAsync(definition);
        await using IAsyncEnumerator<SemanticEntityQuerySubscriptionEvent> subscription = runtime.Database
            .SubscribeToEntityQueryInvalidationAsync(definition, plan.SnapshotVersion)
            .GetAsyncEnumerator();

        Assert.IsTrue(await subscription.MoveNextAsync());
        Assert.AreEqual(SemanticEntityQuerySubscriptionEventKind.Progress, subscription.Current.Kind);

        Task<bool> unrelated = subscription.MoveNextAsync().AsTask();
        await runtime.Database.ApplyAsync(SourceSnapshot(new("https://source.example/names"), "names", "1",
            new SemanticFieldChange(Name(), SemanticFieldChangeKind.Set, new StringSemanticValue("Unrelated"))));
        Assert.IsTrue(await unrelated.WaitAsync(MaintenanceTestTimeout));
        Assert.AreEqual(SemanticEntityQuerySubscriptionEventKind.Progress, subscription.Current.Kind);

        Task<bool> relevant = subscription.MoveNextAsync().AsTask();
        await runtime.Database.ApplyAsync(Session("88888888", Industry(), new StringSemanticValue("Finance")));
        Assert.IsTrue(await relevant.WaitAsync(MaintenanceTestTimeout));
        Assert.AreEqual(SemanticEntityQuerySubscriptionEventKind.Invalidated, subscription.Current.Kind);
        CollectionAssert.Contains(subscription.Current.RelevantEffects.ToArray(),
            SemanticCommittedEffectKind.FieldAssertionAdded);
        Assert.IsFalse(await subscription.MoveNextAsync());
    }

    [TestMethod]
    public async Task Duplex_ready_subscription_session_acknowledges_replaces_and_cancels_logical_subscription()
    {
        _directory = NewDirectory();
        await using EntityForgeDbHost host = BuildHost();
        await host.StartAsync();
        EntityForgeDbRuntime runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
        await runtime.Database.ApplyAsync(Session());
        EntityQueryDefinition query = new PortableEntityQueryRequestCompiler().Compile(
            new(Company().Identifier.AbsoluteUri,
                new PortableEntityProjectionRequest(Company().Identifier.AbsoluteUri,
                    Fields: [new PortableEntityFieldRequest(Industry().Identifier.AbsoluteUri, "industry")])))
            .Query;
        SemanticEntityQueryExplanation plan = await runtime.Database.ExplainEntitiesAsync(query);
        var commands = Channel.CreateUnbounded<SemanticEntityQuerySubscriptionCommand>();
        Guid id = Guid.NewGuid();
        await using IAsyncEnumerator<SemanticEntityQuerySubscriptionSessionEvent> events = runtime.Database
            .RunEntityQuerySubscriptionSessionAsync(commands.Reader.ReadAllAsync())
            .GetAsyncEnumerator();

        await commands.Writer.WriteAsync(new StartSemanticEntityQuerySubscription(id, query,
            plan.SnapshotVersion));
        Assert.IsTrue(await events.MoveNextAsync().AsTask().WaitAsync(MaintenanceTestTimeout));
        Assert.AreEqual(SemanticEntityQuerySubscriptionSessionEventKind.Started, events.Current.Kind);
        Assert.IsTrue(await events.MoveNextAsync().AsTask().WaitAsync(MaintenanceTestTimeout));
        Assert.AreEqual(SemanticEntityQuerySubscriptionSessionEventKind.Progress, events.Current.Kind);
        DatabaseChangeCursor delivered = events.Current.Cursor!.Value;

        await commands.Writer.WriteAsync(new AcknowledgeSemanticEntityQuerySubscription(id, delivered));
        Assert.IsTrue(await events.MoveNextAsync().AsTask().WaitAsync(MaintenanceTestTimeout));
        Assert.AreEqual(SemanticEntityQuerySubscriptionSessionEventKind.Acknowledged, events.Current.Kind);

        await commands.Writer.WriteAsync(new RefreshSemanticEntityQuerySubscription(id, plan.SnapshotVersion));
        Assert.IsTrue(await events.MoveNextAsync().AsTask().WaitAsync(MaintenanceTestTimeout));
        Assert.AreEqual(SemanticEntityQuerySubscriptionSessionEventKind.Started, events.Current.Kind);
        Assert.AreEqual(delivered, events.Current.Cursor);

        await commands.Writer.WriteAsync(new ReplaceSemanticEntityQuerySubscription(id, query,
            plan.SnapshotVersion, delivered));
        Assert.IsTrue(await events.MoveNextAsync().AsTask().WaitAsync(MaintenanceTestTimeout));
        Assert.AreEqual(SemanticEntityQuerySubscriptionSessionEventKind.Started, events.Current.Kind);

        await commands.Writer.WriteAsync(new CancelSemanticEntityQuerySubscription(id));
        Assert.IsTrue(await events.MoveNextAsync().AsTask().WaitAsync(MaintenanceTestTimeout));
        Assert.AreEqual(SemanticEntityQuerySubscriptionSessionEventKind.Cancelled, events.Current.Kind);
        commands.Writer.Complete();
        Assert.IsFalse(await events.MoveNextAsync().AsTask().WaitAsync(MaintenanceTestTimeout));
    }

    [TestMethod]
    public async Task Duplex_ready_subscription_session_rejects_unknown_acknowledgement_without_failing_session()
    {
        _directory = NewDirectory();
        await using EntityForgeDbHost host = BuildHost();
        await host.StartAsync();
        EntityForgeDbRuntime runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
        var commands = Channel.CreateUnbounded<SemanticEntityQuerySubscriptionCommand>();
        await using IAsyncEnumerator<SemanticEntityQuerySubscriptionSessionEvent> events = runtime.Database
            .RunEntityQuerySubscriptionSessionAsync(commands.Reader.ReadAllAsync())
            .GetAsyncEnumerator();

        await commands.Writer.WriteAsync(new AcknowledgeSemanticEntityQuerySubscription(
            Guid.NewGuid(), new DatabaseChangeCursor("not-observed")));
        Assert.IsTrue(await events.MoveNextAsync().AsTask().WaitAsync(MaintenanceTestTimeout));
        Assert.AreEqual(SemanticEntityQuerySubscriptionSessionEventKind.Rejected, events.Current.Kind);
        Assert.AreEqual("subscription_not_found", events.Current.ErrorCode);
        commands.Writer.Complete();
        Assert.IsFalse(await events.MoveNextAsync().AsTask().WaitAsync(MaintenanceTestTimeout));
    }

    [TestMethod]
    public async Task Duplex_ready_subscription_session_multiplexes_logical_subscriptions_and_disposal_cancels_input()
    {
        _directory = NewDirectory();
        await using EntityForgeDbHost host = BuildHost();
        await host.StartAsync();
        EntityForgeDbRuntime runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
        await runtime.Database.ApplyAsync(Session());
        EntityQueryDefinition query = new PortableEntityQueryRequestCompiler().Compile(
            new(Company().Identifier.AbsoluteUri,
                new PortableEntityProjectionRequest(Company().Identifier.AbsoluteUri))).Query;
        SemanticEntityQueryExplanation plan = await runtime.Database.ExplainEntitiesAsync(query);
        var commands = Channel.CreateUnbounded<SemanticEntityQuerySubscriptionCommand>();
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();
        IAsyncEnumerator<SemanticEntityQuerySubscriptionSessionEvent> events = runtime.Database
            .RunEntityQuerySubscriptionSessionAsync(commands.Reader.ReadAllAsync())
            .GetAsyncEnumerator();
        await commands.Writer.WriteAsync(new StartSemanticEntityQuerySubscription(first, query,
            plan.SnapshotVersion));
        await commands.Writer.WriteAsync(new StartSemanticEntityQuerySubscription(second, query,
            plan.SnapshotVersion));

        var started = new HashSet<Guid>();
        while (started.Count < 2)
        {
            Assert.IsTrue(await events.MoveNextAsync().AsTask().WaitAsync(MaintenanceTestTimeout));
            if (events.Current.Kind is SemanticEntityQuerySubscriptionSessionEventKind.Started)
            {
                started.Add(events.Current.SubscriptionId);
            }
        }
        CollectionAssert.AreEquivalent(new[] { first, second }, started.ToArray());

        await events.DisposeAsync().AsTask().WaitAsync(MaintenanceTestTimeout);
        commands.Writer.TryComplete();
    }

    [TestMethod]
    public async Task Operational_status_exposes_commit_admission_and_bounded_wal_evidence()
    {
        _directory = NewDirectory();
        await using EntityForgeDbHost host = BuildHost();
        await host.StartAsync();
        using HttpClient client = Client(host);

        EntityForgeDbOperationalStatus? status =
            await client.GetFromJsonAsync<EntityForgeDbOperationalStatus>("/v1/status");

        Assert.IsNotNull(status);
        Assert.IsTrue(status.Ready);
        Assert.IsNull(status.MaintenanceFailure);
        Assert.AreEqual("semantic", status.Profile);
        Assert.AreEqual(0, status.Version);
        Assert.IsTrue(status.WalBytes >= 0);
    }

    [TestMethod]
    public async Task Graph_profile_is_hosted_with_neutral_status_capabilities_and_checkpointing()
    {
        _directory = NewDirectory();
        await using EntityForgeDbHost host = EntityForgeDbServer.CreateBuilder()
            .UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureServices(services => services.AddSingleton<IEntityForgeDbAuthorizationPolicy,
                AllowEntityForgeDbAuthorizationPolicy>())
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using HttpClient client = Client(host);
        Guid vertex = Guid.NewGuid();
        Guid related = Guid.NewGuid();
        Guid edge = Guid.NewGuid();
        Guid skill = Guid.NewGuid();
        Guid capability = Guid.NewGuid();
        Guid transaction = Guid.NewGuid();

        var batch = new GraphBatchWriteRequest(
            transaction,
            [new(vertex, ["Company"], new Dictionary<string, JsonElement>
            {
                ["name"] = JsonSerializer.SerializeToElement("Example Limited")
            }),
                new(related, ["Person"]),
                new(skill, ["Skill"], new Dictionary<string, JsonElement>
                {
                    ["name"] = JsonSerializer.SerializeToElement("Research")
                })],
            [new(edge, related, vertex, "worksAt"),
                new(capability, related, skill, "hasSkill")]);
        HttpResponseMessage write = await client.PostAsJsonAsync("/v1/graph/batches", batch);
        HttpResponseMessage replay = await client.PostAsJsonAsync("/v1/graph/batches", batch);
        EntityForgeHostCapabilitiesTransport? capabilities = await client.GetFromJsonAsync<EntityForgeHostCapabilitiesTransport>(
            "/v1/capabilities");
        EntityForgeDbOperationalStatus? status = await client.GetFromJsonAsync<EntityForgeDbOperationalStatus>(
            "/v1/status");
        HttpResponseMessage checkpoint = await client.PostAsync("/v1/checkpoints", null);
        HttpResponseMessage graphQuery = await client.PostAsJsonAsync("/v1/graph/gql/query", new GraphGqlQueryRequest("""
            MATCH (person:Person)-[employment:worksAt]->(company:Company),
                  (person)-[capability:hasSkill]->(skill:Skill)
            OPTIONAL MATCH (person)-[management:managedBy]->(manager:Person)
            WHERE company.name = "Example Limited"
            RETURN person, capability, skill, manager ORDER BY skill.name ASC LIMIT 10
            """));
        HttpResponseMessage graphExplain = await client.PostAsJsonAsync("/v1/graph/gql/explain",
            new GraphGqlExplainRequest(new("""
                MATCH (person:Person)-[employment:worksAt]->(company:Company),
                      (person)-[capability:hasSkill]->(skill:Skill)
                OPTIONAL MATCH (person)-[management:managedBy]->(manager:Person)
                RETURN person, capability, skill, manager ORDER BY skill.name ASC LIMIT 10
                """), Analyze: true));
        HttpResponseMessage graphAggregate = await client.PostAsJsonAsync("/v1/graph/gql/query",
            new GraphGqlQueryRequest("""
                MATCH (person:Person)-[employment:worksAt]->(company:Company),
                      (person)-[capability:hasSkill]->(skill:Skill)
                RETURN skill, COUNT(DISTINCT person) AS employees
                GROUP BY skill ORDER BY employees DESC LIMIT 10
                """));
        HttpResponseMessage graphProjection = await client.PostAsJsonAsync("/v1/graph/gql/query",
            new GraphGqlQueryRequest("""
                MATCH (person:Person)-[employment:worksAt]->(company:Company)
                RETURN company.name AS employer, person.nickname AS nickname
                ORDER BY employer ASC LIMIT 10
                """));

        Assert.AreEqual(HttpStatusCode.OK, write.StatusCode, await write.Content.ReadAsStringAsync());
        Assert.AreEqual(HttpStatusCode.OK, replay.StatusCode, await replay.Content.ReadAsStringAsync());
        StringAssert.Contains(await replay.Content.ReadAsStringAsync(), "\"version\":1");
        StringAssert.Contains(await write.Content.ReadAsStringAsync(), "mutationCount");
        JsonElement writtenBatch = await write.Content.ReadFromJsonAsync<JsonElement>();
        JsonElement replayedBatch = await replay.Content.ReadFromJsonAsync<JsonElement>();
        var receiptOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        DatabaseCommitReceipt writtenReceipt = writtenBatch.GetProperty("receipt")
            .Deserialize<DatabaseCommitReceipt>(receiptOptions)!;
        DatabaseCommitReceipt replayedReceipt = replayedBatch.GetProperty("receipt")
            .Deserialize<DatabaseCommitReceipt>(receiptOptions)!;
        Assert.AreNotEqual(Guid.Empty, writtenReceipt.DatabaseIncarnation);
        Assert.AreEqual(transaction, writtenReceipt.TransactionId);
        Assert.AreEqual(writtenReceipt.DatabaseIncarnation, replayedReceipt.DatabaseIncarnation);
        Assert.AreEqual(DatabaseCommitOutcome.AppliedWithChanges, writtenReceipt.Outcome);
        Assert.AreEqual(DatabaseCommitOutcome.IdempotentReplay, replayedReceipt.Outcome);
        Assert.AreEqual("property-graph", capabilities!.Profile);
        Assert.AreEqual(EntityForgeSurfaceMaturity.Alpha, capabilities.Surfaces.Single(surface =>
            surface.Identifier == EntityForgeSurfaceIds.GraphGql).Maturity);
        GraphGqlQueryCapabilities gqlCapabilities = (await client
            .GetFromJsonAsync<GraphGqlQueryCapabilities>("/v1/graph/gql/capabilities"))!;
        Assert.AreEqual("preview", gqlCapabilities.Maturity);
        Assert.AreEqual(GraphPatternLimits.MaximumSpecifiedHops, gqlCapabilities.Limits["maximumSpecifiedHops"]);
        CollectionAssert.Contains(gqlCapabilities.Operators.ToArray(), "bounded quantified relationships and path selection");
        foreach (string quantifier in new[] { "+", "{1,}" })
        {
            using HttpResponseMessage unsupported = await client.PostAsJsonAsync("/v1/graph/gql/query",
                new GraphGqlQueryRequest($"MATCH (n)-[e]->{quantifier}(m) RETURN m"));
            Assert.AreEqual(HttpStatusCode.OK, unsupported.StatusCode, await unsupported.Content.ReadAsStringAsync());
        }
        CollectionAssert.Contains(gqlCapabilities.Unsupported.ToArray(), "complete ISO/IEC 39075 conformance");
        Assert.AreEqual("property-graph", status!.Profile);
        Assert.AreEqual(1, status.Version);
        Assert.AreEqual(HttpStatusCode.OK, checkpoint.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, graphQuery.StatusCode, await graphQuery.Content.ReadAsStringAsync());
        string graphQueryJson = await graphQuery.Content.ReadAsStringAsync();
        StringAssert.Contains(graphQueryJson, "capability");
        StringAssert.Contains(graphQueryJson, "Research");
        StringAssert.Contains(graphQueryJson, "\"manager\":null");
        StringAssert.Contains(graphQueryJson, "paths");
        Assert.IsFalse(graphQueryJson.Contains(vertex.ToString(), StringComparison.OrdinalIgnoreCase));
        StringAssert.Contains(graphQueryJson, "snapshotVersion");
        StringAssert.Contains(graphQueryJson, "evidence");
        Assert.AreEqual(HttpStatusCode.OK, graphAggregate.StatusCode,
            await graphAggregate.Content.ReadAsStringAsync());
        string aggregateJson = await graphAggregate.Content.ReadAsStringAsync();
        StringAssert.Contains(aggregateJson, "\"employees\":1");
        StringAssert.Contains(aggregateJson, "skill");
        Assert.AreEqual(HttpStatusCode.OK, graphProjection.StatusCode,
            await graphProjection.Content.ReadAsStringAsync());
        string projectionJson = await graphProjection.Content.ReadAsStringAsync();
        StringAssert.Contains(projectionJson, "\"employer\":\"Example Limited\"");
        StringAssert.Contains(projectionJson, "\"nickname\":null");
        using (var projectionDocument = JsonDocument.Parse(projectionJson))
        {
            Assert.IsFalse(projectionDocument.RootElement.TryGetProperty("nodes", out _));
            Assert.AreEqual(1L, projectionDocument.RootElement.GetProperty("snapshotVersion").GetInt64());
            var richProjection = await client.PostAsJsonAsync("/v1/graph/gql/query", new GraphGqlQueryRequest("""
                MATCH (person:Person)-[employment:worksAt]->(company:Company)
                RETURN company.name AS employer, person.nickname AS nickname
                ORDER BY employer ASC LIMIT 10
                """, IncludeSupportingGraph: true));
            Assert.AreEqual(HttpStatusCode.OK, richProjection.StatusCode);
            using var richDocument = JsonDocument.Parse(await richProjection.Content.ReadAsStringAsync());
            Assert.AreEqual(projectionDocument.RootElement.GetProperty("matches").GetRawText(),
                richDocument.RootElement.GetProperty("matches").GetRawText());
            Assert.IsTrue(richDocument.RootElement.GetProperty("nodes").GetArrayLength() > 0);
        }
        Assert.AreEqual(HttpStatusCode.OK, graphExplain.StatusCode,
            await graphExplain.Content.ReadAsStringAsync());
        string graphExplainJson = await graphExplain.Content.ReadAsStringAsync();
        StringAssert.Contains(graphExplainJson, "relationshipAccessPaths");
        StringAssert.Contains(graphExplainJson, "OPTIONAL MATCH");
        StringAssert.Contains(graphExplainJson, "\"actualMatches\":1");
        string vertexJson = await client.GetStringAsync($"/v1/graph/vertices/{vertex}");
        StringAssert.Contains(vertexJson, vertex.ToString());
        StringAssert.Contains(vertexJson, "Example Limited");
        string incomingJson = await client.GetStringAsync($"/v1/graph/vertices/{vertex}/incoming?label=worksAt");
        StringAssert.Contains(incomingJson, edge.ToString());
        Assert.AreEqual(HttpStatusCode.NotFound,
            (await client.GetAsync("/v1/semantic/catalog")).StatusCode);
        string openApi = await client.GetStringAsync("/openapi/v1.json");
        StringAssert.Contains(openApi, "/v1/graph/vertices");
        StringAssert.Contains(openApi, "/v1/graph/gql/query");
        StringAssert.Contains(openApi, "/v1/graph/batches");
        StringAssert.Contains(openApi, "/v1/graph/gql/explain");
        StringAssert.Contains(openApi, "/v1/graph/gql/capabilities");
        Assert.IsFalse(openApi.Contains("/v1/semantic/entities/query", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Iso_gql_compiles_incoming_fixed_path_and_intermediate_predicates_to_neutral_pattern()
    {
        CompiledGraphGqlQuery compiled = new GraphGqlQueryCompiler().Compile(new("""
            MATCH (company:Company)<-[employment:worksAt]-(person:Person)-[membership:memberOf]->(team:Team)
            WHERE company.name = "Acuity AND Partners" AND person.active = true
            RETURN GRAPH LIMIT 20
            """, SnapshotVersion: 42));

        Assert.AreEqual(42L, compiled.SnapshotVersion);
        Assert.AreEqual(20, compiled.Query.MaximumMatches);
        Assert.AreEqual(GraphPatternDirection.Incoming, compiled.Query.Relationships[0].Direction);
        Assert.AreEqual(GraphPatternDirection.Outgoing, compiled.Query.Relationships[1].Direction);
        Assert.AreEqual("name", compiled.Query.Start.Predicates.Single().Property);
        Assert.AreEqual("Acuity AND Partners",
            ((LiteralValue)compiled.Query.Start.Predicates.Single().Value).LexicalValue);
        Assert.AreEqual("active", compiled.Query.Relationships[0].Target.Predicates.Single().Property);
        Assert.AreEqual("team", compiled.Query.Relationships[1].Target.Binding);
    }

    [TestMethod]
    public void Alpha_GQL_parser_rejects_malformed_and_oversized_queries_predictably()
    {
        var compiler = new GraphGqlQueryCompiler();
        string[] malformed =
        [
            String.Empty,
            "MATCH RETURN GRAPH",
            "MATCH (person)-[edge]-> RETURN GRAPH",
            "MATCH (person)-[edge]->(company) WHERE unknown.value = 1 RETURN GRAPH",
            "MATCH (person)-[edge]->{0,10001}(company) RETURN GRAPH",
            "MATCH (person)-[edge]->(company) RETURN person..name",
            "MATCH (person)-[edge]->(company) DELETE person RETURN GRAPH"
        ];
        foreach (string query in malformed)
        {
            try
            {
                _ = compiler.Compile(new(query));
                Assert.Fail($"Malformed query was accepted: {query}");
            }
            catch (ArgumentException)
            {

            }
        }
        try
        {
            _ = compiler.Compile(new(
                "MATCH " + new string('x', GraphGqlQueryCompiler.MaximumQueryCharacters) + " RETURN GRAPH"));
            Assert.Fail("An oversized GQL query was accepted.");
        }
        catch (ArgumentException)
        {

        }
    }

    [TestMethod]
    public void Alpha_GQL_parser_rejects_a_bounded_adversarial_corpus_without_pathological_runtime()
    {
        var compiler = new GraphGqlQueryCompiler();
        var stopwatch = Stopwatch.StartNew();
        for (int index = 0; index < ParserResilienceSamples; index++)
        {
            string padding = new(' ', index % 31);
            string query = (index % 4) switch
            {
                0 => $"MATCH (n){padding}RETURN n GROUP BY",
                1 => $"MATCH (n)-[r]->{{1,{MaximumSupportedGqlPathHops + 1}}}(m){padding}RETURN GRAPH",
                2 => $"MATCH (n){padding}WHERE (((n.value = {index}) RETURN n",
                _ => $"MATCH (n){padding}CREATE (m) RETURN GRAPH"
            };

            try
            {
                _ = compiler.Compile(new(query));
                Assert.Fail($"Adversarial GQL sample {index} was accepted.");
            }
            catch (ArgumentException)
            {
            }
        }
        stopwatch.Stop();

        Assert.IsTrue(stopwatch.Elapsed < ParserResilienceBudget,
            $"The bounded GQL rejection corpus took {stopwatch.Elapsed}; budget is {ParserResilienceBudget}.");
    }

    [TestMethod]
    public void Iso_gql_compiles_conjunctive_branches_from_an_existing_binding()
    {
        CompiledGraphGqlQuery compiled = new GraphGqlQueryCompiler().Compile(new("""
            MATCH (company:Company)-[employment:employs]->(person:Person),
                  (person)-[capability:hasSkill]->(skill:Skill),
                  (person)-[location:basedAt]->(office:Office)
            WHERE company.name = "Acuity" AND skill.name = "Research"
            RETURN GRAPH LIMIT 50
            """));

        Assert.AreEqual(3, compiled.Query.Relationships.Count);
        Assert.AreEqual("person", compiled.Query.Relationships[1].SourceBinding);
        Assert.AreEqual("person", compiled.Query.Relationships[2].SourceBinding);
        Assert.AreEqual("skill", compiled.Query.Relationships[1].Target.Binding);
        Assert.AreEqual("name", compiled.Query.Relationships[1].Target.Predicates.Single().Property);
    }

    [TestMethod]
    public void Iso_gql_compiles_binding_projection_ordering_and_limit_after_matching()
    {
        CompiledGraphGqlQuery compiled = new GraphGqlQueryCompiler().Compile(new("""
            MATCH (company:Company)-[employment:employs]->(person:Person)
            RETURN person, employment ORDER BY person.name DESC, company.name ASC LIMIT 7
            """));

        CollectionAssert.AreEqual(new[] { "person", "employment" },
            compiled.Query.ReturnedBindings.ToArray());
        Assert.AreEqual(2, compiled.Query.OrderBy.Count);
        Assert.AreEqual(GraphPatternSortDirection.Descending, compiled.Query.OrderBy[0].Direction);
        Assert.AreEqual("company", compiled.Query.OrderBy[1].Expression!.Binding);
        Assert.AreEqual(7, compiled.Query.MaximumMatches);
    }

    [TestMethod]
    public void Iso_gql_compiles_distinct_binding_projection_and_offset_into_neutral_query_shape()
    {
        CompiledGraphGqlQuery compiled = new GraphGqlQueryCompiler().Compile(new("""
            MATCH (company:Company)-[employment:employs]->(person:Person)
            RETURN DISTINCT company ORDER BY company.name ASC LIMIT 5 OFFSET 10
            """));

        CollectionAssert.AreEqual(new[] { "company" }, compiled.Query.ReturnedBindings.ToArray());
        Assert.IsTrue(compiled.Query.Distinct);
        Assert.AreEqual(10, compiled.Query.Offset);
        Assert.AreEqual(5, compiled.Query.MaximumMatches);
        Assert.ThrowsExactly<ArgumentException>(() => new GraphGqlQueryCompiler().Compile(new(
            "MATCH (company:Company) RETURN DISTINCT GRAPH LIMIT 5")));
    }

    [TestMethod]
    public void Iso_gql_compiles_bounded_grouped_count_projection_over_neutral_matches()
    {
        CompiledGraphGqlQuery compiled = new GraphGqlQueryCompiler().Compile(new("""
            MATCH (company:Company)-[employment:employs]->(person:Person)
            RETURN company, COUNT(DISTINCT person) AS employees
            GROUP BY company ORDER BY employees DESC LIMIT 5 OFFSET 2
            """));

        Assert.IsNotNull(compiled.Query.Aggregation);
        Assert.AreEqual("company", compiled.Query.Aggregation.GroupBy.Single().Binding);
        Assert.AreEqual("person", compiled.Query.Aggregation.Aggregates.Single().ValueBinding);
        Assert.AreEqual("employees", compiled.Query.Aggregation.Aggregates.Single().Alias);
        Assert.IsTrue(compiled.Query.Aggregation.Aggregates.Single().Distinct);
        Assert.AreEqual(GraphPatternSortDirection.Descending, compiled.Query.Aggregation.OrderBy.Single().Direction);
        Assert.AreEqual(5, compiled.Query.Aggregation.Limit);
        Assert.AreEqual(2, compiled.Query.Aggregation.Offset);
        Assert.AreEqual(GraphGqlQueryCompiler.MaximumAggregateInputRows + 1, compiled.Query.MaximumMatches);
    }

    [TestMethod]
    public async Task Iso_gql_executes_core_numeric_aggregates_over_property_expressions()
    {
        _directory = NewDirectory();
        await using EntityForgeDbHost host = EntityForgeDbServer.CreateBuilder()
            .UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using HttpClient client = Client(host);
        await client.PostAsJsonAsync("/v1/graph/batches", new GraphBatchWriteRequest(Guid.NewGuid(),
        [
            new(Guid.NewGuid(), ["Person"], new Dictionary<string, JsonElement>
            {
                ["salary"] = JsonSerializer.SerializeToElement(10)
            }),
            new(Guid.NewGuid(), ["Person"], new Dictionary<string, JsonElement>
            {
                ["salary"] = JsonSerializer.SerializeToElement(10)
            }),
            new(Guid.NewGuid(), ["Person"], new Dictionary<string, JsonElement>
            {
                ["salary"] = JsonSerializer.SerializeToElement(20)
            }),
            new(Guid.NewGuid(), ["Person"], new Dictionary<string, JsonElement>())
        ], []));

        HttpResponseMessage response = await client.PostAsJsonAsync("/v1/graph/gql/query", new GraphGqlQueryRequest(
            "MATCH (person:Person) RETURN SUM(person.salary) AS total"));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual(40D, json.RootElement.GetProperty("matches")[0].GetProperty("bindings")
            .GetProperty("total").GetDouble());

        HttpResponseMessage countResponse = await client.PostAsJsonAsync("/v1/graph/gql/query",
            new GraphGqlQueryRequest("MATCH (person:Person) RETURN COUNT(person.salary) AS salaries"));
        Assert.AreEqual(HttpStatusCode.OK, countResponse.StatusCode,
            await countResponse.Content.ReadAsStringAsync());
        using JsonDocument countJson = JsonDocument.Parse(await countResponse.Content.ReadAsStringAsync());
        Assert.AreEqual(3L, countJson.RootElement.GetProperty("matches")[0].GetProperty("bindings")
            .GetProperty("salaries").GetInt64());

        HttpResponseMessage distinctResponse = await client.PostAsJsonAsync("/v1/graph/gql/query",
            new GraphGqlQueryRequest(
                "MATCH (person:Person) RETURN COUNT(DISTINCT person.salary) AS salaries"));
        Assert.AreEqual(HttpStatusCode.OK, distinctResponse.StatusCode,
            await distinctResponse.Content.ReadAsStringAsync());
        using JsonDocument distinctJson = JsonDocument.Parse(await distinctResponse.Content.ReadAsStringAsync());
        Assert.AreEqual(2L, distinctJson.RootElement.GetProperty("matches")[0].GetProperty("bindings")
            .GetProperty("salaries").GetInt64());

        HttpResponseMessage rowsResponse = await client.PostAsJsonAsync("/v1/graph/gql/query",
            new GraphGqlQueryRequest("MATCH (person:Person) RETURN COUNT(*) AS people"));
        Assert.AreEqual(HttpStatusCode.OK, rowsResponse.StatusCode, await rowsResponse.Content.ReadAsStringAsync());
        using JsonDocument rowsJson = JsonDocument.Parse(await rowsResponse.Content.ReadAsStringAsync());
        Assert.AreEqual(4L, rowsJson.RootElement.GetProperty("matches")[0].GetProperty("bindings")
            .GetProperty("people").GetInt64());

        HttpResponseMessage bindingsResponse = await client.PostAsJsonAsync("/v1/graph/gql/query",
            new GraphGqlQueryRequest("MATCH (person:Person) RETURN COUNT(person) AS people"));
        Assert.AreEqual(HttpStatusCode.OK, bindingsResponse.StatusCode,
            await bindingsResponse.Content.ReadAsStringAsync());
        using JsonDocument bindingsJson = JsonDocument.Parse(await bindingsResponse.Content.ReadAsStringAsync());
        Assert.AreEqual(4L, bindingsJson.RootElement.GetProperty("matches")[0].GetProperty("bindings")
            .GetProperty("people").GetInt64());

        HttpResponseMessage emptyResponse = await client.PostAsJsonAsync("/v1/graph/gql/query",
            new GraphGqlQueryRequest("MATCH (company:Company) RETURN COUNT(*) AS companies"));
        Assert.AreEqual(HttpStatusCode.OK, emptyResponse.StatusCode,
            await emptyResponse.Content.ReadAsStringAsync());
        using JsonDocument emptyJson = JsonDocument.Parse(await emptyResponse.Content.ReadAsStringAsync());
        Assert.AreEqual(0L, emptyJson.RootElement.GetProperty("matches")[0].GetProperty("bindings")
            .GetProperty("companies").GetInt64());
    }

    [TestMethod]
    public async Task Iso_gql_mutations_insert_set_and_delete_in_atomic_native_transactions()
    {
        _directory = NewDirectory();
        await using EntityForgeDbHost host = EntityForgeDbServer.CreateBuilder()
            .UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using HttpClient client = Client(host);
        Guid person = Guid.NewGuid();

        HttpResponseMessage inserted = await client.PostAsJsonAsync("/v1/graph/gql/update",
            new GraphGqlMutationRequest($"INSERT (person:Person {{ id: \"{person:D}\", name: \"Alice\" }})"));
        HttpResponseMessage set = await client.PostAsJsonAsync("/v1/graph/gql/update",
            new GraphGqlMutationRequest("MATCH (person:Person) WHERE person.name = \"Alice\" SET person.age = 42"));
        using var updated = await client.PostAsJsonAsync("/v1/graph/gql/query",
            new GraphGqlQueryRequest($"MATCH (person:Person) WHERE person.id='{person:D}' RETURN person.age AS age"));
        HttpResponseMessage deleted = await client.PostAsJsonAsync("/v1/graph/gql/update",
            new GraphGqlMutationRequest("MATCH (person:Person) WHERE person.age = 42 DELETE person"));

        Assert.AreEqual(HttpStatusCode.OK, inserted.StatusCode, await inserted.Content.ReadAsStringAsync());
        Assert.AreEqual(HttpStatusCode.OK, set.StatusCode, await set.Content.ReadAsStringAsync());
        Assert.AreEqual(HttpStatusCode.OK, updated.StatusCode, await updated.Content.ReadAsStringAsync());
        Assert.AreEqual(42L, (await updated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("age").GetInt64());
        Assert.AreEqual(HttpStatusCode.OK, deleted.StatusCode, await deleted.Content.ReadAsStringAsync());
        using var remaining = await client.PostAsJsonAsync("/v1/graph/gql/query",
            new GraphGqlQueryRequest($"MATCH (person:Person) WHERE person.id='{person:D}' RETURN COUNT(*) AS total"));
        Assert.AreEqual(HttpStatusCode.OK, remaining.StatusCode, await remaining.Content.ReadAsStringAsync());
        Assert.AreEqual(0L, (await remaining.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("total").GetInt64());
    }

    [TestMethod]
    public void Iso_gql_compiles_aliased_property_projection_and_pushes_ordering_to_neutral_query()
    {
        CompiledGraphGqlQuery compiled = new GraphGqlQueryCompiler().Compile(new("""
            MATCH (person:Person)-[employment:worksAt]->(company:Company)
            RETURN person.name AS employee, company.name AS employer
            ORDER BY employer DESC LIMIT 20 OFFSET 5
            """));

        Assert.IsNotNull(compiled.Projection);
        CollectionAssert.AreEqual(new[] { "employee", "employer" },
            compiled.Projection.Terms.Select(term => term.Alias).ToArray());
        CollectionAssert.AreEqual(new[] { "person", "company" }, compiled.Query.ReturnedBindings.ToArray());
        Assert.AreEqual("company", compiled.Query.OrderBy.Single().Expression!.Binding);
        Assert.AreEqual("name", compiled.Query.OrderBy.Single().Expression!.Property);
        Assert.AreEqual(GraphPatternSortDirection.Descending, compiled.Query.OrderBy.Single().Direction);
        Assert.AreEqual(20, compiled.Query.MaximumMatches);
        Assert.AreEqual(5, compiled.Query.Offset);

        CompiledGraphGqlQuery expression = new GraphGqlQueryCompiler().Compile(new("""
            MATCH (person:Person)
            RETURN person.salary * 1.1 AS adjusted, person.firstName || " " AS display
            LIMIT 10
            """));
        Assert.AreEqual("*", expression.Projection!.Terms[0].Expression.Function);
        Assert.AreEqual("||", expression.Projection.Terms[1].Expression.Function);
        CompiledGraphGqlQuery orderedExpression = new GraphGqlQueryCompiler().Compile(new("""
            MATCH (person:Person)
            RETURN person.salary * 1.1 AS adjusted ORDER BY adjusted DESC LIMIT 10
            """));
        Assert.AreEqual("*", orderedExpression.Query.OrderBy.Single().Expression!.Function);
        Assert.AreEqual(GraphPatternSortDirection.Descending, orderedExpression.Query.OrderBy.Single().Direction);
    }

    [TestMethod]
    public void Iso_gql_numeric_comparisons_preserve_promotion_in_neutral_filters()
    {
        CompiledGraphGqlQuery compiled = new GraphGqlQueryCompiler().Compile(new("""
            MATCH (person:Person)
            WHERE person.age >= 18 AND person.age < 66 AND person.status <> "inactive"
            RETURN person LIMIT 10
            """));

        Assert.AreEqual(0, compiled.Query.Start.Predicates.Count,
            "Typed native candidate predicates must not exclude equal integer/float values.");
        var root = (GraphPatternBooleanFilter)compiled.Query.Filter!;
        var numeric = (GraphPatternBooleanFilter)root.Left;
        Assert.AreEqual(">=", ((GraphPatternExpressionFilter)numeric.Left).Expression.Function);
        Assert.AreEqual("<", ((GraphPatternExpressionFilter)numeric.Right).Expression.Function);
        Assert.AreEqual(GraphPatternComparisonOperator.NotEqual, ((GraphPatternPropertyComparison)root.Right).Operator);
    }

    [TestMethod]
    public void Iso_gql_compiles_boolean_precedence_and_grouping_into_neutral_filter_tree()
    {
        CompiledGraphGqlQuery compiled = new GraphGqlQueryCompiler().Compile(new("""
            MATCH (person:Person)
            WHERE person.active = true OR (person.age >= 18 AND NOT person.status = "inactive")
            RETURN person LIMIT 10
            """));

        Assert.IsInstanceOfType<GraphPatternBooleanFilter>(compiled.Query.Filter);
        var root = (GraphPatternBooleanFilter)compiled.Query.Filter!;
        Assert.AreEqual(GraphPatternBooleanOperator.Or, root.Operator);
        Assert.IsInstanceOfType<GraphPatternBooleanFilter>(root.Right);
        Assert.IsInstanceOfType<GraphPatternNotFilter>(((GraphPatternBooleanFilter)root.Right).Right);
        Assert.AreEqual(0, compiled.Query.Start.Predicates.Count,
            "Disjunctive filters must not be unsafely pushed into candidate selection.");
    }

    [TestMethod]
    public void Iso_gql_compiles_optional_match_as_a_clause_scoped_read_stage()
    {
        CompiledGraphGqlQuery compiled = new GraphGqlQueryCompiler().Compile(new("""
            MATCH (company:Company)-[employment:employs]->(person:Person)
            OPTIONAL MATCH (person)-[management:managedBy]->(manager:Person)
            RETURN person, manager ORDER BY manager.name ASC LIMIT 10
            """));

        var optional = compiled.Pipeline!.Stages.Single(stage => stage.Operation == "OPTIONAL MATCH");
        Assert.AreEqual("person", optional.MatchPlan!.Query.Relationships.Single().SourceBinding);
        Assert.IsTrue(compiled.RequiresCompleteGraph);
    }

    [TestMethod]
    public void Iso_gql_compiles_a_bounded_quantified_relationship()
    {
        CompiledGraphGqlQuery compiled = new GraphGqlQueryCompiler().Compile(new("""
            MATCH (employee:Person)-[line:reportsTo]->{1,4}(leader:Person)
            RETURN employee, line, leader LIMIT 10
            """));

        Assert.AreEqual(1, compiled.Pipeline!.Stages.Single(stage => stage.MatchPlan is not null).MatchPlan!.Query.Relationships.Single().Hops.Minimum);
        Assert.AreEqual(4, compiled.Pipeline!.Stages.Single(stage => stage.MatchPlan is not null).MatchPlan!.Query.Relationships.Single().Hops.Maximum);
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new GraphGqlQueryCompiler().Compile(new("""
            MATCH (employee:Person)-[line:reportsTo]->{1,10001}(leader:Person)
            RETURN leader
            """)));
        foreach (string quantifier in new[] { "+", "{2,}" })
        {
            var open = new GraphGqlQueryCompiler().Compile(new(
                $"MATCH (employee:Person)-[line:reportsTo]->{quantifier}(leader:Person) RETURN leader"));
            Assert.IsNull(open.Pipeline!.Stages.Single(stage => stage.MatchPlan is not null).MatchPlan!.Query.Relationships.Single().Hops.Maximum);
        }
    }

    [TestMethod]
    public void Iso_gql_compiles_any_shortest_for_the_complete_path()
    {
        CompiledGraphGqlQuery compiled = new GraphGqlQueryCompiler().Compile(new("""
            MATCH ANY SHORTEST (employee:Person)-[line:reportsTo]->{1,4}(leader:Person)
            RETURN line, leader LIMIT 10
            """));

        var path = compiled.Pipeline!.Stages.SelectMany(stage => stage.MatchComponents?.Select(component => component.Path) ?? [stage.PathBinding]).OfType<GqlPathBinding>().Single();
        Assert.AreEqual("SHORTEST", path.Selector);
        Assert.AreEqual(1, path.Count);
        Assert.IsFalse(path.Groups);
        var product = new GraphGqlQueryCompiler().Compile(new("""
            MATCH ANY SHORTEST (employee:Person)-[first:reportsTo]->{1,2}(leader:Person),
                  (leader)-[second:reportsTo]->{1,2}(director:Person)
            RETURN GRAPH
            """));
        var components = product.Pipeline!.Stages.SelectMany(stage => stage.MatchComponents ?? []).ToArray();
        Assert.AreEqual(2, components.Length);
        Assert.AreEqual("SHORTEST", components[0].Path!.Selector);
        Assert.IsNull(components[1].Path?.Selector);
    }

    [TestMethod]
    public void Iso_gql_compiles_all_shortest_for_the_complete_path()
    {
        CompiledGraphGqlQuery compiled = new GraphGqlQueryCompiler().Compile(new("""
            MATCH ALL SHORTEST (employee:Person)-[line:reportsTo]->{1,4}(leader:Person)
            RETURN line, leader LIMIT 10
            """));

        var path = compiled.Pipeline!.Stages.SelectMany(stage => stage.MatchComponents?.Select(component => component.Path) ?? [stage.PathBinding]).OfType<GqlPathBinding>().Single();
        Assert.AreEqual("SHORTEST", path.Selector);
        Assert.AreEqual(1, path.Count);
        Assert.IsTrue(path.Groups);
    }

    [TestMethod]
    public void Iso_gql_compiles_cycle_closure_onto_an_existing_node_binding()
    {
        CompiledGraphGqlQuery compiled = new GraphGqlQueryCompiler().Compile(new("""
            MATCH (company:Company)-[employment:employs]->(employee:Person),
                  (employee)-[management:reportsTo]->(manager:Person),
                  (manager)-[advice:advises]->(company)
            RETURN company, employee, manager, advice LIMIT 10
            """));

        Assert.AreEqual("company", compiled.Query.Relationships[2].Target.Binding);
        CollectionAssert.AreEqual(new[] { "company", "employee", "manager", "advice" },
            compiled.Query.ReturnedBindings.ToArray());
    }

    [TestMethod]
    public async Task Graph_http_returns_quantified_relationship_bindings_as_evidence_paths()
    {
        _directory = NewDirectory();
        await using EntityForgeDbHost host = EntityForgeDbServer.CreateBuilder()
            .UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using HttpClient client = Client(host);
        Guid employee = Guid.NewGuid();
        Guid manager = Guid.NewGuid();
        Guid director = Guid.NewGuid();
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();
        await client.PostAsJsonAsync("/v1/graph/batches", new GraphBatchWriteRequest(
            Guid.NewGuid(),
            [new(employee, ["Employee"]), new(manager, ["Leader"]), new(director, ["Leader"])],
            [new(first, employee, manager, "reportsTo"), new(second, manager, director, "reportsTo")]));

        HttpResponseMessage response = await client.PostAsJsonAsync("/v1/graph/gql/query", new GraphGqlQueryRequest("""
            MATCH (employee:Employee)-[line:reportsTo]->{1,2}(leader:Leader)
            RETURN line, leader LIMIT 10
            """));
        HttpResponseMessage explain = await client.PostAsJsonAsync("/v1/graph/gql/explain",
            new GraphGqlExplainRequest(new("""
                MATCH (employee:Employee)-[line:reportsTo]->{1,2}(leader:Leader)
                RETURN line, leader LIMIT 10
                """), Analyze: true));
        HttpResponseMessage shortest = await client.PostAsJsonAsync("/v1/graph/gql/query", new GraphGqlQueryRequest("""
            MATCH ANY SHORTEST (employee:Employee)-[line:reportsTo]->{1,2}(leader:Leader)
            RETURN line, leader LIMIT 10
            """));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        string json = await response.Content.ReadAsStringAsync();
        StringAssert.Contains(json, first.ToString());
        StringAssert.Contains(json, second.ToString());
        StringAssert.Contains(json, "\"line\":[");
        Assert.AreEqual(HttpStatusCode.OK, explain.StatusCode, await explain.Content.ReadAsStringAsync());
        StringAssert.Contains(await explain.Content.ReadAsStringAsync(), "\"maximumHops\":2");
        Assert.AreEqual(HttpStatusCode.OK, shortest.StatusCode, await shortest.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public async Task Graph_http_round_trips_atomic_json_bag_as_json_not_a_string()
    {
        _directory = NewDirectory();
        await using EntityForgeDbHost host = EntityForgeDbServer.CreateBuilder()
            .UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using HttpClient client = Client(host);
        Guid vertex = Guid.NewGuid();
        await client.PostAsJsonAsync("/v1/graph/vertices", new GraphVertexWriteRequest(vertex, ["Company"]));
        JsonElement address = JsonSerializer.SerializeToElement(new
        {
            line1 = "Acuity House",
            line2 = "15 Market Street",
            line3 = "London",
            line4 = "EC1A 1AA"
        });

        HttpResponseMessage write = await client.PostAsJsonAsync(
            $"/v1/graph/vertices/{vertex}/properties", new GraphPropertyWriteRequest("address", address));
        using JsonDocument response = JsonDocument.Parse(
            await client.GetStringAsync($"/v1/graph/vertices/{vertex}"));
        JsonElement stored = response.RootElement.GetProperty("properties").GetProperty("address");

        Assert.AreEqual(HttpStatusCode.OK, write.StatusCode, await write.Content.ReadAsStringAsync());
        Assert.AreEqual("json", stored.GetProperty("kind").GetString());
        Assert.AreEqual(JsonValueKind.Object, stored.GetProperty("value").ValueKind);
        Assert.AreEqual("EC1A 1AA", stored.GetProperty("value").GetProperty("line4").GetString());
    }

    [TestMethod]
    public async Task Graph_http_rejects_json_bags_outside_instance_policy_and_duplicate_members()
    {
        _directory = NewDirectory();
        await using EntityForgeDbHost host = EntityForgeDbServer.CreateBuilder()
            .UseLocalStorage(_directory).UsePropertyGraphProfile()
            .UseJsonAdmissionPolicy(new JsonValueAdmissionPolicy(maximumUtf8Bytes: 32,
                maximumDepth: 3, maximumObjectMembers: 2, maximumArrayItems: 2))
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using HttpClient client = Client(host);
        Guid vertex = Guid.NewGuid();
        await client.PostAsJsonAsync("/v1/graph/vertices", new GraphVertexWriteRequest(vertex, ["Company"]));

        HttpResponseMessage tooLarge = await client.PostAsJsonAsync(
            $"/v1/graph/vertices/{vertex}/properties", new GraphPropertyWriteRequest("bag",
                JsonSerializer.SerializeToElement(new
                {
                    value = new string('x', 64)
                })));
        using var duplicate = new StringContent(
            """{"name":"bag","value":{"a":1,"a":2}}""", Encoding.UTF8, "application/json");
        HttpResponseMessage duplicateResult = await client.PostAsync(
            $"/v1/graph/vertices/{vertex}/properties", duplicate);

        Assert.AreEqual(HttpStatusCode.BadRequest, tooLarge.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, duplicateResult.StatusCode);
        Assert.IsFalse(host.Application.Services.GetRequiredService<EntityForgeDbRuntime>()
            .GraphDatabase.GetVertex(new(vertex))!.Properties.ContainsKey("bag"));
    }

    [TestMethod]
    public async Task Neutral_portable_query_round_trips_through_http_polymorphic_contract()
    {
        _directory = NewDirectory();
        await using EntityForgeDbHost host = BuildHost();
        await host.StartAsync();
        EntityForgeDbRuntime runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
        await runtime.Database.ApplyAsync(Session());
        var request = new PortableEntityQueryRequest(Company().Identifier.AbsoluteUri,
            new PortableEntityProjectionRequest(Company().Identifier.AbsoluteUri,
                Fields: new[] { new PortableEntityFieldRequest(Industry().Identifier.AbsoluteUri, "industry") }),
            new PortableEntityFilterRequest("propertyComparison", Industry().Identifier.AbsoluteUri,
                "equal", new(JsonSerializer.SerializeToElement("Technology"))), IncludeEvidence: true);
        using HttpClient client = Client(host);

        HttpResponseMessage response = await client.PostAsJsonAsync("/v1/semantic/entities/query", request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        EntityProjectionTransportResponse? result = await response.Content
            .ReadFromJsonAsync<EntityProjectionTransportResponse>();
        Assert.AreEqual(1, result!.Entities.Count);
        Assert.AreEqual("Technology", result.Entities.Single().Fields["industry"].Single().LexicalValue);
    }

    [TestMethod]
    public async Task Hosted_query_accepts_typed_integer_literals()
    {
        _directory = NewDirectory();
        await using EntityForgeDbHost host = BuildHost();
        await host.StartAsync();
        EntityForgeDbRuntime runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
        SemanticTerm employeeCount = new(new Uri("https://example.test/employeeCount"), SemanticTermRole.Property);
        await runtime.Database.ApplyAsync(Session(employeeCount, new IntegerSemanticValue(42)));
        var request = new PortableEntityQueryRequest(Company().Identifier.AbsoluteUri,
            new PortableEntityProjectionRequest(Company().Identifier.AbsoluteUri,
                Fields: [new PortableEntityFieldRequest(employeeCount.Identifier.AbsoluteUri, "employeeCount")]),
            new PortableEntityFilterRequest("propertyComparison", employeeCount.Identifier.AbsoluteUri,
                "equal", new(JsonSerializer.SerializeToElement(42), "integer")));
        using HttpClient client = Client(host);

        HttpResponseMessage response = await client.PostAsJsonAsync("/v1/semantic/entities/query", request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        EntityProjectionTransportResponse? result = await response.Content.ReadFromJsonAsync<EntityProjectionTransportResponse>();
        Assert.AreEqual("42", result!.Entities.Single().Fields["employeeCount"].Single().LexicalValue);
    }

    [TestMethod]
    public async Task Hosted_query_maps_invalid_continuation_to_client_error()
    {
        _directory = NewDirectory();
        await using EntityForgeDbHost host = BuildHost();
        await host.StartAsync();
        var request = new PortableEntityQueryRequest(Company().Identifier.AbsoluteUri,
            new PortableEntityProjectionRequest(Company().Identifier.AbsoluteUri),
            First: 1, After: "not-a-continuation");
        using HttpClient client = Client(host);

        HttpResponseMessage response = await client.PostAsJsonAsync("/v1/semantic/entities/query", request);
        string problem = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        StringAssert.Contains(problem, "query_continuation_invalid");
    }

    [TestMethod]
    public async Task Hosted_continuation_resumes_the_same_snapshot_after_restart()
    {
        _directory = NewDirectory();
        string? continuation;
        PortableEntityQueryRequest Page(string? after = null) => new(Company().Identifier.AbsoluteUri,
            new PortableEntityProjectionRequest(Company().Identifier.AbsoluteUri,
                Fields: [new PortableEntityFieldRequest(Industry().Identifier.AbsoluteUri, "industry")]),
            OrderBy: [new PortableEntityOrderingRequest(Industry().Identifier.AbsoluteUri)],
            First: 1, IncludeEvidence: true, After: after);
        await using (EntityForgeDbHost first = BuildHost())
        {
            await first.StartAsync();
            EntityForgeDbRuntime runtime = first.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
            await runtime.Database.ApplyAsync(Session("01234567", Industry(), new StringSemanticValue("Alpha")));
            await runtime.Database.ApplyAsync(Session("01234568", Industry(), new StringSemanticValue("Beta")));
            using HttpClient client = Client(first);
            EntityProjectionTransportResponse? page = await (await client.PostAsJsonAsync("/v1/semantic/entities/query", Page()))
                .Content.ReadFromJsonAsync<EntityProjectionTransportResponse>();
            Assert.AreEqual("Alpha", page!.Entities.Single().Fields["industry"].Single().LexicalValue);
            Assert.AreEqual(1, page.Evidence.Count);
            continuation = page.NextContinuation;
            Assert.IsNotNull(continuation);
        }
        await using EntityForgeDbHost recovered = BuildHost();
        await recovered.StartAsync();
        using HttpClient recoveredClient = Client(recovered);
        HttpResponseMessage response = await recoveredClient.PostAsJsonAsync(
            "/v1/semantic/entities/query", Page(continuation));
        EntityProjectionTransportResponse? resumed = await response.Content.ReadFromJsonAsync<EntityProjectionTransportResponse>();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        Assert.AreEqual("Beta", resumed!.Entities.Single().Fields["industry"].Single().LexicalValue);
        Assert.AreEqual(1, resumed.Evidence.Count);
    }

    [TestMethod]
    public async Task Hosted_snapshot_lease_preserves_as_of_queries_and_continuations_across_restart()
    {
        _directory = NewDirectory();
        SnapshotLeaseResponse? lease;
        string? continuation;
        PortableEntityQueryRequest Page(Guid? leaseId = null, string? after = null,
            long? snapshotVersion = null) => new(Company().Identifier.AbsoluteUri,
            new PortableEntityProjectionRequest(Company().Identifier.AbsoluteUri,
                Fields: [new PortableEntityFieldRequest(Industry().Identifier.AbsoluteUri, "industry")]),
            OrderBy: [new PortableEntityOrderingRequest(Industry().Identifier.AbsoluteUri)],
            First: 1, IncludeEvidence: true, After: after, SnapshotVersion: snapshotVersion,
            SnapshotLeaseId: leaseId);

        await using (EntityForgeDbHost first = BuildHost())
        {
            await first.StartAsync();
            EntityForgeDbRuntime runtime = first.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
            await runtime.Database.ApplyAsync(Session("01234567", Industry(), new StringSemanticValue("Alpha")));
            await runtime.Database.ApplyAsync(Session("01234568", Industry(), new StringSemanticValue("Beta")));
            using HttpClient client = Client(first);
            HttpResponseMessage acquired = await client.PostAsJsonAsync("/v1/semantic/snapshot-leases",
                new SnapshotLeaseRequest(DurationSeconds: 300));
            lease = await acquired.Content.ReadFromJsonAsync<SnapshotLeaseResponse>();
            Assert.AreEqual(HttpStatusCode.OK, acquired.StatusCode, await acquired.Content.ReadAsStringAsync());

            await runtime.Database.ApplyAsync(Session("01234567", Industry(), new StringSemanticValue("Gamma")));
            var currentRequest = new PortableEntityQueryRequest(Company().Identifier.AbsoluteUri,
                new PortableEntityProjectionRequest(Company().Identifier.AbsoluteUri,
                    Fields: [new PortableEntityFieldRequest(Industry().Identifier.AbsoluteUri, "industry")]),
                Identities: ["https://example.test/Company|https://example.test/companyNumber|01234567"]);
            EntityProjectionTransportResponse? current = await (await client.PostAsJsonAsync(
                "/v1/semantic/entities/query", currentRequest)).Content
                .ReadFromJsonAsync<EntityProjectionTransportResponse>();
            Assert.AreEqual("Gamma", current!.Entities.Single().Fields["industry"].Single().LexicalValue);
            await runtime.Database.ReindexAsync();
            HttpResponseMessage historicalResponse = await client.PostAsJsonAsync(
                "/v1/semantic/entities/query", Page(lease!.LeaseId));
            Assert.AreEqual(HttpStatusCode.OK, historicalResponse.StatusCode,
                await historicalResponse.Content.ReadAsStringAsync());
            EntityProjectionTransportResponse? historical = await historicalResponse.Content
                .ReadFromJsonAsync<EntityProjectionTransportResponse>();
            Assert.AreEqual("Alpha", historical!.Entities.Single().Fields["industry"].Single().LexicalValue);
            continuation = historical.NextContinuation;
            Assert.IsNotNull(continuation);

            HttpResponseMessage renewedResponse = await client.PutAsJsonAsync(
                $"/v1/semantic/snapshot-leases/{lease.LeaseId}",
                new SnapshotLeaseRenewalRequest(lease.Revision, DurationSeconds: 300));
            SnapshotLeaseResponse? renewed = await renewedResponse.Content
                .ReadFromJsonAsync<SnapshotLeaseResponse>();
            Assert.AreEqual(lease.Revision + 1, renewed!.Revision);
            HttpResponseMessage staleRenewal = await client.PutAsJsonAsync(
                $"/v1/semantic/snapshot-leases/{lease.LeaseId}",
                new SnapshotLeaseRenewalRequest(lease.Revision, DurationSeconds: 300));
            Assert.AreEqual(HttpStatusCode.Conflict, staleRenewal.StatusCode);
            lease = renewed;
        }

        await using (EntityForgeDbHost recovered = BuildHost())
        {
            await recovered.StartAsync();
            using HttpClient client = Client(recovered);
            SnapshotLeaseResponse? recoveredLease = await client.GetFromJsonAsync<SnapshotLeaseResponse>(
                $"/v1/semantic/snapshot-leases/{lease!.LeaseId}");
            Assert.AreEqual(lease.Revision, recoveredLease!.Revision);

            HttpResponseMessage resumedResponse = await client.PostAsJsonAsync(
                "/v1/semantic/entities/query", Page(lease.LeaseId, continuation));
            EntityProjectionTransportResponse? resumed = await resumedResponse.Content
                .ReadFromJsonAsync<EntityProjectionTransportResponse>();
            Assert.AreEqual(HttpStatusCode.OK, resumedResponse.StatusCode,
                await resumedResponse.Content.ReadAsStringAsync());
            Assert.AreEqual("Beta", resumed!.Entities.Single().Fields["industry"].Single().LexicalValue);

            HttpResponseMessage mismatched = await client.PostAsJsonAsync("/v1/semantic/entities/query",
                Page(after: continuation, snapshotVersion: lease.SnapshotVersion + 1));
            Assert.AreEqual(HttpStatusCode.BadRequest, mismatched.StatusCode);

            HttpResponseMessage released = await client.DeleteAsync($"/v1/semantic/snapshot-leases/{lease.LeaseId}");
            Assert.AreEqual(HttpStatusCode.NoContent, released.StatusCode);
            HttpResponseMessage missingLease = await client.PostAsJsonAsync(
                "/v1/semantic/entities/query", Page(lease.LeaseId));
            Assert.AreEqual(HttpStatusCode.NotFound, missingLease.StatusCode);
            HttpResponseMessage unavailableSnapshot = await client.PostAsJsonAsync(
                "/v1/semantic/entities/query", Page(snapshotVersion: lease.SnapshotVersion));
            Assert.AreEqual(HttpStatusCode.Gone, unavailableSnapshot.StatusCode);
        }
    }

    [TestMethod]
    public async Task Hosted_snapshot_lease_reports_expiry_as_gone()
    {
        _directory = NewDirectory();
        await using EntityForgeDbHost host = BuildHost();
        await host.StartAsync();
        using HttpClient client = Client(host);
        SnapshotLeaseResponse? lease = await (await client.PostAsJsonAsync("/v1/semantic/snapshot-leases",
            new SnapshotLeaseRequest(DurationSeconds: 1))).Content.ReadFromJsonAsync<SnapshotLeaseResponse>();

        await Task.Delay(TimeSpan.FromMilliseconds(1_200));
        HttpResponseMessage response = await client.GetAsync($"/v1/semantic/snapshot-leases/{lease!.LeaseId}");

        Assert.AreEqual(HttpStatusCode.Gone, response.StatusCode);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "snapshot_lease_expired");
    }

    [TestMethod]
    public async Task Semantic_http_query_returns_json_bag_as_structured_value()
    {
        _directory = NewDirectory();
        await using EntityForgeDbHost host = BuildHost();
        await host.StartAsync();
        EntityForgeDbRuntime runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
        SemanticTerm address = new(new Uri("https://example.test/address"), SemanticTermRole.Property);
        await runtime.Database.ApplyAsync(Session(address,
            new JsonSemanticValue("""{"postcode":"EC1A 1AA","lines":["Acuity House","London"]}""")));
        var request = new PortableEntityQueryRequest(Company().Identifier.AbsoluteUri,
            new PortableEntityProjectionRequest(Company().Identifier.AbsoluteUri,
                Fields: [new PortableEntityFieldRequest(address.Identifier.AbsoluteUri, "address")]));
        using HttpClient client = Client(host);

        EntityProjectionTransportResponse? result = await (await client.PostAsJsonAsync("/v1/semantic/entities/query", request))
            .Content.ReadFromJsonAsync<EntityProjectionTransportResponse>();
        EntityProjectionValueTransport value = result!.Entities.Single().Fields["address"].Single();

        Assert.AreEqual("json", value.Kind);
        using JsonDocument json = JsonDocument.Parse(value.LexicalValue);
        Assert.AreEqual(JsonValueKind.Object, json.RootElement.ValueKind);
        Assert.AreEqual("EC1A 1AA", json.RootElement.GetProperty("postcode").GetString());
        CollectionAssert.AreEqual(new[] { "Acuity House", "London" },
            json.RootElement.GetProperty("lines").EnumerateArray().Select(item => item.GetString()).ToArray());
    }

    [TestMethod]
    public async Task Owned_background_maintenance_checkpoints_and_stops_with_host()
    {
        _directory = NewDirectory();
        await using EntityForgeDbHost host = EntityForgeDbServer.CreateBuilder()
            .UseLocalStorage(_directory)
            .ConfigureStorage(options => options.CheckpointInterval = MaintenanceTestInterval)
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0"))
            .Build();
        await host.StartAsync();
        EntityForgeDbRuntime runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
        await runtime.Database.ApplyAsync(Session());
        string checkpoint = Path.Combine(_directory, EntityForgeDbHostOptions.DefaultCheckpointFileName);
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(MaintenanceTestTimeout);
        while (!File.Exists(checkpoint) && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(MaintenanceTestInterval);
        }

        Assert.IsTrue(File.Exists(checkpoint));
        Assert.IsNull(runtime.LastMaintenanceFailure);
    }

    [TestMethod]
    public async Task Active_snapshot_leases_defer_background_checkpoints_without_losing_readiness()
    {
        _directory = NewDirectory();
        await using EntityForgeDbHost host = EntityForgeDbServer.CreateBuilder()
            .UseLocalStorage(_directory)
            .ConfigureStorage(options => options.CheckpointInterval = MaintenanceTestInterval)
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0"))
            .Build();
        await host.StartAsync();
        var runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
        await runtime.Database.ApplyAsync(Session());
        var lease = await runtime.Database.AcquireSnapshotLeaseAsync(TimeSpan.FromMinutes(1));
        try
        {
            using var admissionDeadline = new CancellationTokenSource(MaintenanceTestTimeout);
            async Task CheckpointAfterAdmissionAsync()
            {
                while (true)
                {
                    try
                    {
                        await runtime.CreateCheckpointAsync(admissionDeadline.Token);
                        return;
                    }
                    catch (EntityForgeDbAdministrativeJobRejectedException) when (!admissionDeadline.IsCancellationRequested)
                    {
                        // Scheduled maintenance may occupy the single job slot.
                        // Still require the retention error once this call is admitted.
                        await Task.Delay(MaintenanceTestInterval, admissionDeadline.Token);
                    }
                }
            }
            await Assert.ThrowsExceptionAsync<EntityForgeSnapshotRetentionException>(CheckpointAfterAdmissionAsync);
            // Exercise several scheduled checkpoint attempts while the lease remains active.
            await Task.Delay(MaintenanceTestInterval * 10);
            Assert.IsTrue(runtime.GetReadiness().Ready, runtime.LastMaintenanceFailure?.ToString());
            Assert.IsNull(runtime.LastMaintenanceFailure);
            Assert.AreEqual(lease.SnapshotVersion,
                (await runtime.Database.GetSnapshotLeaseAsync(lease.LeaseId)).SnapshotVersion);
        }
        finally
        {
            await runtime.Database.ReleaseSnapshotLeaseAsync(lease.LeaseId);
        }
        string checkpoint = Path.Combine(_directory, EntityForgeDbHostOptions.DefaultCheckpointFileName);
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(MaintenanceTestTimeout);
        while ((!File.Exists(checkpoint) || !runtime.IsReady) && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(MaintenanceTestInterval);
        }

        Assert.IsTrue(File.Exists(checkpoint));
        Assert.IsTrue(runtime.GetReadiness().Ready, runtime.LastMaintenanceFailure?.ToString());
    }

    [TestMethod]
    public async Task Checkpoint_backup_verifies_and_restores_into_a_fresh_database_directory()
    {
        _directory = NewDirectory();
        string backupPath = Path.Combine(_directory, "backups", "semantic.efbackup");
        string restoreDirectory = Path.Combine(_directory, "restored");
        SemanticSessionEnvelope session = Session();
        LocalDatabaseBackupInfo created;
        await using (EntityForgeDbHost source = BuildHost())
        {
            await source.StartAsync();
            EntityForgeDbRuntime runtime = source.Application.Services
                .GetRequiredService<EntityForgeDbRuntime>();
            await runtime.Database.ApplyAsync(session);
            created = await runtime.CreateBackupAsync(backupPath);
        }

        var manager = new LocalDatabaseBackupManager();
        LocalDatabaseBackupInfo verified = await manager.VerifyAsync(backupPath);
        LocalDatabaseBackupInfo restored = await manager.RestoreAsync(backupPath, restoreDirectory,
            EntityForgeDbHostOptions.DefaultCheckpointFileName,
            EntityForgeDbHostOptions.DefaultCommitLogFileName,
            EntityForgeDbHostOptions.DefaultManifestFileName,
            EntityForgeDbHostOptions.DefaultChangeJournalFileName,
            EntityForgeDbHostOptions.DefaultPageFileName, new EntityForge.Db.Storage.Pages.GroupedRedoRestoreLogInitializer());
        Assert.AreEqual(created, verified);
        Assert.AreEqual(created, restored);

        await using EntityForgeDbHost target = EntityForgeDbServer.CreateBuilder()
            .UseLocalStorage(restoreDirectory)
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0"))
            .Build();
        await target.StartAsync();
        SemanticCatalogSnapshot catalog = await target.Application.Services
            .GetRequiredService<EntityForgeDbRuntime>().Database.GetSemanticCatalogAsync();
        CollectionAssert.Contains(catalog.Terms.ToArray(), Industry());
        EntityForgeDbRuntime restoredRuntime = target.Application.Services
            .GetRequiredService<EntityForgeDbRuntime>();
        Assert.AreEqual(SessionCommitOutcome.IdempotentReplay,
            (await restoredRuntime.Database.ApplyAsync(session)).Outcome);
        Assert.AreEqual(1, (await restoredRuntime.Database.QueryAsync(
            new SemanticFieldEqualityQuery(Industry(), new StringSemanticValue("Technology")))).Count);
    }

    [TestMethod]
    public async Task RDF_release_acceptance_backup_restores_exact_quad_state_and_idempotency()
    {
        _directory = NewDirectory();
        string backupPath = Path.Combine(_directory, "backups", "rdf.efbackup");
        string restoreDirectory = Path.Combine(_directory, "restored-rdf");
        var quad = new RdfQuad(new RdfIri("https://example.test/alice"),
            new RdfIri("https://schema.org/name"), new RdfLiteral("Alice"),
            new RdfIri("https://example.test/directory"));
        var transaction = new RdfTransaction(Guid.NewGuid(), [new AssertRdfQuad(quad)]);
        LocalDatabaseBackupInfo created;
        await using (EntityForgeDbHost source = EntityForgeDbServer.CreateBuilder()
            .UseLocalStorage(_directory).UseRdfProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build())
        {
            await source.StartAsync();
            EntityForgeDbRuntime runtime = source.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
            await runtime.RdfDatabase.CommitAsync(transaction);
            created = await runtime.CreateBackupAsync(backupPath);
        }

        var manager = new LocalDatabaseBackupManager();
        Assert.AreEqual(created, await manager.VerifyAsync(backupPath));
        Assert.AreEqual(created, await manager.RestoreAsync(backupPath, restoreDirectory,
            EntityForgeDbHostOptions.DefaultCheckpointFileName,
            EntityForgeDbHostOptions.DefaultCommitLogFileName,
            EntityForgeDbHostOptions.DefaultManifestFileName,
            EntityForgeDbHostOptions.DefaultChangeJournalFileName,
            EntityForgeDbHostOptions.DefaultPageFileName, new EntityForge.Db.Storage.Pages.GroupedRedoRestoreLogInitializer()));

        await using EntityForgeDbHost target = EntityForgeDbServer.CreateBuilder()
            .UseLocalStorage(restoreDirectory).UseRdfProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await target.StartAsync();
        RdfDatabaseEngine restored = target.Application.Services.GetRequiredService<EntityForgeDbRuntime>().RdfDatabase;
        Assert.AreEqual(quad, restored.Find(new(Subject: quad.Subject, Graph: quad.Graph)).Single());
        DatabaseVersion beforeReplay = restored.CurrentVersion;
        Assert.AreEqual(beforeReplay, await restored.CommitAsync(transaction));
    }

    [TestMethod]
    public async Task Historical_archive_is_verified_before_reclaim_and_uses_explicit_restore_query_tier()
    {
        _directory = NewDirectory();
        string archiveDirectory = Path.Combine(_directory, "archives");
        await using EntityForgeDbHost host = EntityForgeDbServer.CreateBuilder()
            .UseLocalStorage(_directory)
            .UseHistoricalEvidenceArchive(archiveDirectory, TimeSpan.FromDays(365))
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0"))
            .Build();
        await host.StartAsync();
        EntityForgeDbRuntime runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
        await runtime.Database.ApplyAsync(Session());

        HistoricalEvidenceArchiveInfo archive = await runtime.CreateHistoricalArchiveAsync();
        HistoricalEvidenceArchiveInfo verified = await new LocalHistoricalEvidenceArchiveManager(
            new(archiveDirectory, TimeSpan.FromDays(365))).VerifyAsync(archive.ArchiveId);
        Assert.AreEqual(archive, verified);
        Assert.IsTrue(archive.RetainUntil > archive.CreatedAt);

        var query = new PortableEntityQueryRequest(Company().Identifier.AbsoluteUri,
            new PortableEntityProjectionRequest(Company().Identifier.AbsoluteUri,
                Fields: [new PortableEntityFieldRequest(Industry().Identifier.AbsoluteUri, "industry")]),
            Identities: ["https://example.test/Company|https://example.test/companyNumber|01234567"],
            IncludeEvidence: true);
        HistoricalEvidenceArchiveQueryResponse result = await runtime.QueryHistoricalArchiveAsync(
            new(archive.ArchiveId, query));

        Assert.AreEqual("archive", result.StorageTier);
        Assert.AreEqual(archive.SnapshotVersion.Value, result.SnapshotVersion);
        Assert.AreEqual(1, result.Result.Entities.Count);
        Assert.AreEqual("Technology", result.Result.Entities.Single().Fields["industry"].Single().LexicalValue);
        Assert.AreEqual(1, result.Result.Evidence.Count);

        string payload = Path.Combine(archiveDirectory, archive.PayloadPath);
        byte[] bytes = await File.ReadAllBytesAsync(payload);
        bytes[^1] ^= 1;
        await File.WriteAllBytesAsync(payload, bytes);
        await Assert.ThrowsExceptionAsync<EntityForgeDbBackupException>(async () =>
            await new LocalHistoricalEvidenceArchiveManager(new(archiveDirectory)).VerifyAsync(archive.ArchiveId));
    }

    [TestMethod]
    public async Task Archive_backed_semantic_reindex_verifies_checkpoint_before_evidence_reclamation()
    {
        _directory = NewDirectory();
        string archiveDirectory = Path.Combine(_directory, "archives");
        await using EntityForgeDbHost host = EntityForgeDbServer.CreateBuilder()
            .UseLocalStorage(_directory)
            .UseHistoricalEvidenceArchive(archiveDirectory, TimeSpan.FromDays(365))
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0"))
            .Build();
        await host.StartAsync();
        EntityForgeDbRuntime runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
        await runtime.Database.ApplyAsync(Session("01234567", Industry(), new StringSemanticValue("Alpha")));
        await runtime.Database.ApplyAsync(Session("01234567", Industry(), new StringSemanticValue("Gamma")));

        await runtime.Database.ReindexAsync();

        string manifestPath = Directory.EnumerateFiles(archiveDirectory, "*.json").Single();
        string archiveId = Path.GetFileNameWithoutExtension(manifestPath);
        HistoricalEvidenceArchiveInfo archive = await new LocalHistoricalEvidenceArchiveManager(
            new(archiveDirectory)).VerifyAsync(archiveId);
        Assert.IsTrue(archive.SnapshotVersion.Value > 0);
        Assert.AreEqual(1, (await runtime.Database.QueryAsync(
            new SemanticFieldEqualityQuery(Industry(), new StringSemanticValue("Gamma")))).Count);
    }

    [TestMethod]
    public async Task Backup_verification_rejects_corruption_and_restore_rejects_nonempty_target()
    {
        _directory = NewDirectory();
        Directory.CreateDirectory(_directory);
        string checkpoint = Path.Combine(_directory, "checkpoint");
        string backup = Path.Combine(_directory, "database.efbackup");
        string pages = Path.Combine(_directory, "pages");
        await File.WriteAllBytesAsync(pages, []);
        await new FileDatabaseCheckpointStore(checkpoint, pagePath: pages, walFloor: () => 0).SaveAsync(new(Guid.NewGuid(), new(1),
            new(DatabaseProfileId.Semantic, EntityForge.Db.Semantic.SemanticProfileCheckpointStore.SemanticCheckpointCodecId,
                EntityForge.Db.Semantic.SemanticProfileCheckpointStore.SemanticCheckpointCodecVersion, new byte[] { 1 })));
        var manager = new LocalDatabaseBackupManager();
        string changes = Path.Combine(_directory, "changes");
        await manager.CreateAsync(checkpoint, changes, pages, backup);
        byte[] bytes = await File.ReadAllBytesAsync(backup);
        bytes[^1] ^= 1;
        await File.WriteAllBytesAsync(backup, bytes);
        await Assert.ThrowsExceptionAsync<EntityForgeDbBackupException>(async () =>
            await manager.VerifyAsync(backup));

        string corruptTarget = Path.Combine(_directory, "corrupt-target");
        await Assert.ThrowsExceptionAsync<EntityForgeDbBackupException>(async () =>
            await manager.RestoreAsync(backup, corruptTarget,
                EntityForgeDbHostOptions.DefaultCheckpointFileName,
                EntityForgeDbHostOptions.DefaultCommitLogFileName,
                EntityForgeDbHostOptions.DefaultManifestFileName,
                EntityForgeDbHostOptions.DefaultChangeJournalFileName,
                EntityForgeDbHostOptions.DefaultPageFileName, new EntityForge.Db.Storage.Pages.GroupedRedoRestoreLogInitializer()));
        Assert.IsFalse(Directory.Exists(corruptTarget));

        await manager.CreateAsync(checkpoint, changes, pages, backup);
        string target = Path.Combine(_directory, "occupied");
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, "keep.txt"), "owned");
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
            await manager.RestoreAsync(backup, target, EntityForgeDbHostOptions.DefaultCheckpointFileName,
                EntityForgeDbHostOptions.DefaultCommitLogFileName,
                EntityForgeDbHostOptions.DefaultManifestFileName,
                EntityForgeDbHostOptions.DefaultChangeJournalFileName,
                EntityForgeDbHostOptions.DefaultPageFileName, new EntityForge.Db.Storage.Pages.GroupedRedoRestoreLogInitializer()));
    }

    [TestMethod]
    public async Task Cancelled_backup_creation_does_not_publish_a_partial_container()
    {
        _directory = NewDirectory();
        Directory.CreateDirectory(_directory);
        string checkpoint = Path.Combine(_directory, "checkpoint");
        string backup = Path.Combine(_directory, "database.efbackup");
        string pages = Path.Combine(_directory, "pages");
        await new FileDatabaseCheckpointStore(checkpoint).SaveAsync(new(Guid.NewGuid(), new(1),
            new(DatabaseProfileId.Semantic,
                EntityForge.Db.Semantic.SemanticProfileCheckpointStore.SemanticCheckpointCodecId,
                EntityForge.Db.Semantic.SemanticProfileCheckpointStore.SemanticCheckpointCodecVersion,
                new byte[] { 1 })));
        await File.WriteAllBytesAsync(pages, new byte[1024]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await new LocalDatabaseBackupManager().CreateAsync(checkpoint,
                Path.Combine(_directory, "changes"), pages, backup, cancellation.Token));

        Assert.IsFalse(File.Exists(backup));
        Assert.IsFalse(File.Exists(backup + ".new"));
    }

    [TestMethod]
    [TestCategory("ContainerSmoke")]
    public async Task Running_container_accepts_queries_and_preserves_idempotent_semantic_ingest()
    {
        string? serverUrl = Environment.GetEnvironmentVariable("ENTITYFORGE_DB_SMOKE_URL");
        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            Assert.Inconclusive("Set ENTITYFORGE_DB_SMOKE_URL to run this test.");
        }

        string expectedOutcome = Environment.GetEnvironmentVariable("ENTITYFORGE_DB_SMOKE_EXPECTED_OUTCOME")
            ?? nameof(SessionCommitOutcome.Applied);
        using var client = new HttpClient { BaseAddress = new Uri(serverUrl) };

        EntityForgeHostCapabilitiesTransport? capabilities =
            await client.GetFromJsonAsync<EntityForgeHostCapabilitiesTransport>("/v1/capabilities");
        Assert.AreEqual("semantic", capabilities!.Profile);
        HttpResponseMessage applied = await client.PostAsJsonAsync("/v1/semantic/sessions", Transport(ContainerSession()));
        Assert.AreEqual(HttpStatusCode.OK, applied.StatusCode, await applied.Content.ReadAsStringAsync());
        SessionCommitResult? commit = await applied.Content.ReadFromJsonAsync<SessionCommitResult>();
        Assert.AreEqual(expectedOutcome, commit!.Outcome.ToString());

        var request = new PortableEntityQueryRequest(Company().Identifier.AbsoluteUri,
            new PortableEntityProjectionRequest(Company().Identifier.AbsoluteUri,
                Fields: new[] { new PortableEntityFieldRequest(Industry().Identifier.AbsoluteUri, "industry") }),
            new PortableEntityFilterRequest("propertyComparison", Industry().Identifier.AbsoluteUri,
                "equal", new(JsonSerializer.SerializeToElement("Technology"))), IncludeEvidence: true);
        EntityProjectionTransportResponse? result = await (await client.PostAsJsonAsync("/v1/semantic/entities/query", request))
            .Content.ReadFromJsonAsync<EntityProjectionTransportResponse>();
        Assert.AreEqual("Technology", result!.Entities.Single().Fields["industry"].Single().LexicalValue);
    }

    [TestMethod]
    public async Task RDF_host_exposes_resumable_write_query_stream_export_and_exact_capabilities()
    {
        _directory = NewDirectory();
        await using EntityForgeDbHost host = EntityForgeDbServer.CreateBuilder()
            .UseLocalStorage(_directory).UseRdfProfile()
            .ConfigureServices(services => services.AddSingleton<IEntityForgeDbAuthorizationPolicy,
                AllowEntityForgeDbAuthorizationPolicy>())
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using HttpClient client = Client(host);
        var subject = new RdfIri("https://example.test/alice");
        var predicate = new RdfIri("https://schema.org/name");
        var request = new RdfDocumentPageRequest(Guid.NewGuid(), 0,
            [new(subject, predicate, new RdfLiteral("Alice"))]);
        HttpResponseMessage write = await client.PostAsJsonAsync("/v1/rdf/document-pages", request);
        Assert.AreEqual(HttpStatusCode.OK, write.StatusCode, await write.Content.ReadAsStringAsync());
        Guid importId = Guid.NewGuid();
        const string turtle = "@prefix schema: <https://schema.org/> . " +
            "<https://example.test/bob> schema:name \"Bob\" .";
        HttpResponseMessage imported = await client.PostAsync(
            $"/v1/rdf/import?documentId={importId}&pageNumber=0",
            new StringContent(turtle, Encoding.UTF8, "text/turtle"));
        Assert.AreEqual(HttpStatusCode.OK, imported.StatusCode, await imported.Content.ReadAsStringAsync());
        HttpResponseMessage replayed = await client.PostAsync(
            $"/v1/rdf/import?documentId={importId}&pageNumber=0",
            new StringContent(turtle, Encoding.UTF8, "text/turtle"));
        RdfDocumentPageCommit replay = (await replayed.Content.ReadFromJsonAsync<RdfDocumentPageCommit>())!;
        Assert.AreEqual(DatabaseCommitOutcome.IdempotentReplay, replay.Outcome);
        Guid jsonLdId = Guid.NewGuid();
        const string jsonLd = """
            {
              "@context": { "name": "https://schema.org/name" },
              "@id": "https://example.test/charlie",
              "name": "Charlie"
            }
            """;
        HttpResponseMessage jsonLdImported = await client.PostAsync(
            $"/v1/rdf/import?documentId={jsonLdId}&pageNumber=0",
            new StringContent(jsonLd, Encoding.UTF8, "application/ld+json"));
        Assert.AreEqual(HttpStatusCode.OK, jsonLdImported.StatusCode,
            await jsonLdImported.Content.ReadAsStringAsync());
        HttpResponseMessage remoteContext = await client.PostAsync(
            $"/v1/rdf/import?documentId={Guid.NewGuid()}&pageNumber=0",
            new StringContent("{\"@context\":\"https://example.test/context\"}",
                Encoding.UTF8, "application/ld+json"));
        Assert.AreEqual(HttpStatusCode.BadRequest, remoteContext.StatusCode);
        HttpResponseMessage malformed = await client.PostAsync(
            $"/v1/rdf/import?documentId={Guid.NewGuid()}&pageNumber=0",
            new StringContent("not turtle", Encoding.UTF8, "text/turtle"));
        Assert.AreEqual(HttpStatusCode.BadRequest, malformed.StatusCode);
        HttpResponseMessage unsupported = await client.PostAsync(
            $"/v1/rdf/import?documentId={Guid.NewGuid()}&pageNumber=0",
            new StringContent(turtle, Encoding.UTF8, "application/rdf+xml"));
        Assert.AreEqual(HttpStatusCode.UnsupportedMediaType, unsupported.StatusCode);

        const string shapes = """
            @prefix sh: <http://www.w3.org/ns/shacl#> .
            @prefix ex: <https://example.test/> .
            @prefix schema: <https://schema.org/> .
            ex:PersonShape a sh:NodeShape ; sh:targetNode ex:alice ;
                sh:property [ sh:path schema:email ; sh:minCount 1 ] .
            """;
        HttpResponseMessage validation = await client.PostAsync("/v1/rdf/shacl/validate?maximumResults=10",
            new StringContent(shapes, Encoding.UTF8, "text/turtle"));
        Assert.AreEqual(HttpStatusCode.OK, validation.StatusCode, await validation.Content.ReadAsStringAsync());
        RdfShaclValidationReport report = (await validation.Content
            .ReadFromJsonAsync<RdfShaclValidationReport>())!;
        Assert.IsFalse(report.Conforms);
        Assert.AreEqual(1, report.Results.Count);

        HttpResponseMessage query = await client.PostAsJsonAsync("/v1/rdf/quads",
            new RdfQuadQueryRequest(new(Subject: subject)));
        Assert.AreEqual(HttpStatusCode.OK, query.StatusCode);
        Assert.IsTrue((await query.Content.ReadAsStringAsync()).Contains("Alice", StringComparison.Ordinal));
        HttpResponseMessage stream = await client.PostAsJsonAsync("/v1/rdf/quads/stream",
            new RdfQuadQueryRequest(new(Subject: subject)));
        Assert.AreEqual("application/x-ndjson", stream.Content.Headers.ContentType!.MediaType);
        Assert.AreEqual(1, (await stream.Content.ReadAsStringAsync()).Split('\n',
            StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.IsTrue((await client.GetStringAsync("/v1/rdf/export")).Contains("Alice", StringComparison.Ordinal));
        HttpResponseMessage firstExportPage = await client.GetAsync("/v1/rdf/export?pageSize=1");
        Assert.IsTrue(firstExportPage.Headers.Contains("EntityForge-Snapshot-Version"));
        string exportContinuation = firstExportPage.Headers.GetValues("EntityForge-Continuation").Single();
        HttpResponseMessage secondExportPage = await client.GetAsync(
            $"/v1/rdf/export?pageSize=1&continuation={exportContinuation}");
        Assert.AreEqual(HttpStatusCode.OK, secondExportPage.StatusCode);
        Assert.IsFalse(String.IsNullOrWhiteSpace(await secondExportPage.Content.ReadAsStringAsync()));
        using var turtleExport = new HttpRequestMessage(HttpMethod.Get, "/v1/rdf/export");
        turtleExport.Headers.Accept.ParseAdd("text/turtle");
        HttpResponseMessage turtleResponse = await client.SendAsync(turtleExport);
        Assert.AreEqual("text/turtle", turtleResponse.Content.Headers.ContentType!.MediaType);
        Assert.IsTrue((await turtleResponse.Content.ReadAsStringAsync()).Contains("Bob", StringComparison.Ordinal));
        using var jsonLdExport = new HttpRequestMessage(HttpMethod.Get, "/v1/rdf/export");
        jsonLdExport.Headers.Accept.ParseAdd("application/ld+json");
        HttpResponseMessage jsonLdResponse = await client.SendAsync(jsonLdExport);
        Assert.AreEqual("application/ld+json", jsonLdResponse.Content.Headers.ContentType!.MediaType);
        Assert.IsTrue((await jsonLdResponse.Content.ReadAsStringAsync()).Contains(
            "https://example.test/charlie", StringComparison.Ordinal));
        RdfChangePageResponse changes = (await client.GetFromJsonAsync<RdfChangePageResponse>(
            "/v1/rdf/changes?maximumCount=10&routingKind=rdf.predicate&routingValue=https%3A%2F%2Fschema.org%2Fname"))!;
        Assert.AreEqual(3, changes.Changes.Count);
        Assert.AreEqual(3, changes.Changes.Sum(change => change.Mutations.Count));
        Assert.AreEqual(HttpStatusCode.OK, (await client.GetAsync("/v1/rdf/changes/bounds")).StatusCode);

        EntityForgeHostCapabilitiesTransport capabilities = (await client.GetFromJsonAsync<EntityForgeHostCapabilitiesTransport>(
            "/v1/capabilities"))!;
        EntityForgeSurfaceCapability rdf = capabilities.Surfaces.Single(surface =>
            surface.Identifier == EntityForgeSurfaceIds.RdfNative);
        CollectionAssert.Contains(rdf.Features.ToArray(), "turtle");
        CollectionAssert.Contains(capabilities.Surfaces.Single(surface =>
            surface.Identifier == EntityForgeSurfaceIds.RdfShacl).Features.ToArray(), "shacl-core-validation");
        RdfSparqlCapabilities sparql = (await client.GetFromJsonAsync<RdfSparqlCapabilities>(
            "/v1/rdf/sparql/capabilities"))!;
        CollectionAssert.Contains(sparql.Operators.ToArray(), "OFFSET");
        CollectionAssert.Contains(sparql.QueryForms.ToArray(), "ASK");
        CollectionAssert.Contains(sparql.QueryForms.ToArray(), "CONSTRUCT");
        CollectionAssert.Contains(sparql.UpdateForms.ToArray(), "INSERT DATA");
        Assert.AreEqual("preview", sparql.Maturity);
        Assert.AreEqual(EntityForgeSurfaceMaturity.Alpha, capabilities.Surfaces.Single(surface =>
            surface.Identifier == EntityForgeSurfaceIds.RdfSparql).Maturity);
        Assert.AreEqual(RdfSparql.MaximumTokens, sparql.MaximumTokens);
        Assert.AreEqual(RdfSparql.MaximumPathSteps, sparql.MaximumPathSteps);
        Assert.AreEqual(RdfSparql.MaximumPathExpansions, sparql.MaximumPathExpansions);
        RdfShaclCapabilities shacl = (await client.GetFromJsonAsync<RdfShaclCapabilities>(
            "/v1/rdf/shacl/capabilities"))!;
        Assert.IsFalse(shacl.MutatesData);
        RdfEntailmentCapabilities entailment = (await client.GetFromJsonAsync<RdfEntailmentCapabilities>(
            "/v1/rdf/entailment/capabilities"))!;
        Assert.IsFalse(entailment.MutatesAssertedData);
        RdfEntailmentResult inferred = (await client.GetFromJsonAsync<RdfEntailmentResult>(
            "/v1/rdf/entailment/rdfs?maximumResults=10"))!;
        Assert.AreEqual(3, inferred.AssertedQuadCount);
        string openApi = await client.GetStringAsync("/openapi/v1.json");
        Assert.IsTrue(openApi.Contains("/v1/rdf/sparql", StringComparison.Ordinal));
        Assert.IsTrue(openApi.Contains("/v1/rdf/sparql/update", StringComparison.Ordinal));
        Assert.IsTrue(openApi.Contains("/v1/rdf/shacl/validate", StringComparison.Ordinal));
        Assert.IsTrue(openApi.Contains("/v1/rdf/entailment/rdfs", StringComparison.Ordinal));
        Assert.IsFalse(openApi.Contains("/v1/graph/vertices", StringComparison.Ordinal));

        HttpResponseMessage update = await client.PostAsJsonAsync("/v1/rdf/sparql/update",
            new RdfSparqlUpdateRequest(Guid.NewGuid(), """
                PREFIX ex: <https://example.test/>
                INSERT DATA { ex:dora <https://schema.org/name> "Dora" }
                """));
        Assert.AreEqual(HttpStatusCode.OK, update.StatusCode, await update.Content.ReadAsStringAsync());
        HttpResponseMessage updatedQuery = await client.PostAsJsonAsync("/v1/rdf/sparql",
            new RdfSparqlQueryRequest("""
                PREFIX ex: <https://example.test/>
                SELECT ?name WHERE { ex:dora <https://schema.org/name> ?name }
                """));
        StringAssert.Contains(await updatedQuery.Content.ReadAsStringAsync(), "Dora");
    }

    [TestMethod]
    public async Task RDF_host_round_trips_quoted_triples_through_JSON_quad_contracts()
    {
        _directory = NewDirectory();
        await using EntityForgeDbHost host = EntityForgeDbServer.CreateBuilder()
            .UseLocalStorage(_directory).UseRdfProfile()
            .ConfigureServices(services => services.AddSingleton<IEntityForgeDbAuthorizationPolicy,
                AllowEntityForgeDbAuthorizationPolicy>())
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using HttpClient client = Client(host);
        var quoted = new RdfTripleTerm(new RdfIri("https://example.test/alice"),
            new RdfIri("https://example.test/worksFor"), new RdfIri("https://example.test/company"));
        var quad = new RdfQuad(quoted, new RdfIri("https://example.test/source"),
            new RdfIri("https://example.test/hr"));

        HttpResponseMessage write = await client.PostAsJsonAsync("/v1/rdf/document-pages",
            new RdfDocumentPageRequest(Guid.NewGuid(), 0, [quad]));
        Assert.AreEqual(HttpStatusCode.OK, write.StatusCode, await write.Content.ReadAsStringAsync());
        RdfQueryPage page = (await (await client.PostAsJsonAsync("/v1/rdf/quads",
            new RdfQuadQueryRequest(new(Subject: quoted)))).Content.ReadFromJsonAsync<RdfQueryPage>())!;
        Assert.AreEqual(quad, page.Quads.Single());
        HttpResponseMessage sparql = await client.PostAsJsonAsync("/v1/rdf/sparql", new RdfSparqlQueryRequest("""
            SELECT ?person ?company WHERE {
              << ?person <https://example.test/worksFor> ?company >>
                <https://example.test/source> <https://example.test/hr> .
            }
            """));
        Assert.AreEqual(HttpStatusCode.OK, sparql.StatusCode, await sparql.Content.ReadAsStringAsync());
        string sparqlJson = await sparql.Content.ReadAsStringAsync();
        Assert.IsTrue(sparqlJson.Contains("https://example.test/alice", StringComparison.Ordinal));
        Assert.IsTrue(sparqlJson.Contains("https://example.test/company", StringComparison.Ordinal));

        HttpResponseMessage composed = await client.PostAsJsonAsync("/v1/rdf/sparql",
            new RdfSparqlQueryRequest("""
                SELECT ?person ?reviewer WHERE {
                  { << ?person <https://example.test/worksFor> <https://example.test/company> >>
                      <https://example.test/source> <https://example.test/hr> }
                  UNION
                  { ?person <https://example.test/unknown> <https://example.test/value> }
                  OPTIONAL { ?person <https://example.test/reviewer> ?reviewer }
                }
                """));
        Assert.AreEqual(HttpStatusCode.OK, composed.StatusCode,
            await composed.Content.ReadAsStringAsync());
        string composedJson = await composed.Content.ReadAsStringAsync();
        Assert.IsTrue(composedJson.Contains("https://example.test/alice", StringComparison.Ordinal));
        Assert.IsFalse(composedJson.Contains("reviewer", StringComparison.Ordinal));

        HttpResponseMessage ask = await client.PostAsJsonAsync("/v1/rdf/sparql",
            new RdfSparqlQueryRequest("""
                ASK WHERE {
                  VALUES ?person { <https://example.test/alice> <https://example.test/unknown> }
                  << ?person <https://example.test/worksFor> <https://example.test/company> >>
                    <https://example.test/source> <https://example.test/hr> .
                  FILTER (isIRI(?person) && !BOUND(?missing))
                }
                """));
        Assert.AreEqual(HttpStatusCode.OK, ask.StatusCode, await ask.Content.ReadAsStringAsync());
        Assert.IsTrue((await ask.Content.ReadFromJsonAsync<RdfSparqlAskResult>())!.Value);
    }

    private EntityForgeDbHost BuildHost() => EntityForgeDbServer.CreateBuilder()
        .UseLocalStorage(_directory!)
        .ConfigureServices(services => services.AddSingleton<IEntityForgeDbAuthorizationPolicy,
            AllowEntityForgeDbAuthorizationPolicy>())
        .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0"))
        .Build();

    [TestMethod]
    public async Task Semantic_entity_documents_have_embedded_and_remote_parity()
    {
        _directory = NewDirectory();
        await using EntityForgeDbHost host = BuildHost();
        await host.StartAsync();
        EntityForgeDbRuntime runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
        await runtime.Database.ApplyAsync(Session());
        using HttpClient http = Client(host);
        var request = new EntityProjectionQueryRequest(Company().Identifier.AbsoluteUri)
        {
            Fields = [new(Industry().Identifier.AbsoluteUri, "industry")],
            First = 10,
            IncludeEvidence = true
        };

        EntityProjectionQueryResult remote = await new HttpEntityProjectionClient(http).QueryAsync(request);
        EntityProjectionQueryResult embedded = await new SemanticEntityProjectionClient(runtime.Database)
            .QueryAsync(request);

        Assert.AreEqual(embedded.SnapshotVersion, remote.SnapshotVersion);
        Assert.AreEqual(embedded.Document.Roots.Single().Identity, remote.Document.Roots.Single().Identity);
        Assert.AreEqual("Technology", ((LiteralValue)remote.Document.Roots.Single().Fields.Single()
            .Values.Single().Value).LexicalValue);
        Assert.AreEqual(1, remote.Document.Evidence.Count);
    }

    [TestMethod]
    public async Task Graph_entity_documents_have_embedded_remote_and_GraphQL_parity()
    {
        _directory = NewDirectory();
        await using EntityForgeDbHost host = EntityForgeDbServer.CreateBuilder()
            .UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureServices(services => services.AddSingleton<IEntityForgeDbAuthorizationPolicy,
                AllowEntityForgeDbAuthorizationPolicy>())
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using HttpClient http = Client(host);
        Guid company = Guid.NewGuid();
        Guid secondCompany = Guid.NewGuid();
        Guid employee = Guid.NewGuid();
        HttpResponseMessage write = await http.PostAsJsonAsync("/v1/graph/batches", new GraphBatchWriteRequest(
            Guid.NewGuid(),
            [
                new(company, ["Company"], new Dictionary<string, JsonElement>
                {
                    ["name"] = JsonSerializer.SerializeToElement("Acuity")
                }),
                new(secondCompany, ["Company"], new Dictionary<string, JsonElement>
                {
                    ["name"] = JsonSerializer.SerializeToElement("Northwind")
                }),
                new(employee, ["Person"], new Dictionary<string, JsonElement>
                {
                    ["name"] = JsonSerializer.SerializeToElement("Alex")
                })
            ],
            [new(Guid.NewGuid(), company, employee, "EMPLOYS")]));
        Assert.AreEqual(HttpStatusCode.OK, write.StatusCode, await write.Content.ReadAsStringAsync());

        var request = new EntityProjectionQueryRequest("Company")
        {
            Identities = [company.ToString("D")],
            Fields = [new("name", "companyName")]
        };
        var remote = new HttpEntityProjectionClient(http);
        EntityProjectionQueryResult remoteResult = await remote.QueryAsync(request);
        EntityForgeDbRuntime runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
        EntityProjectionQueryResult embedded = await new GraphEntityProjectionClient(runtime.GraphDatabase)
            .QueryAsync(request);
        Assert.AreEqual("Acuity", ((LiteralValue)remoteResult.Document.Roots.Single().Fields
            .Single().Values.Single().Value).LexicalValue);
        Assert.AreEqual(embedded.Document.Roots.Single().Identity, remoteResult.Document.Roots.Single().Identity);

        var firstPageRequest = new EntityProjectionQueryRequest("Company")
        {
            Fields = [new("name", "companyName")],
            First = 1
        };
        EntityProjectionQueryResult firstPage = await remote.QueryAsync(firstPageRequest);
        Assert.IsNotNull(firstPage.NextContinuation);
        EntityProjectionQueryResult secondPage = await remote.QueryAsync(firstPageRequest with
        {
            SnapshotVersion = firstPage.SnapshotVersion.Value,
            After = firstPage.NextContinuation
        });
        Assert.AreEqual(1, secondPage.Document.Roots.Count);
        Assert.AreNotEqual(firstPage.Document.Roots.Single().Identity, secondPage.Document.Roots.Single().Identity);
        Assert.IsNull(secondPage.NextContinuation);

        HttpResponseMessage graphQl = await http.PostAsJsonAsync("/graphql", new EntityForgeGraphQlRequest($$"""
            query {
              entities(type: "Company", first: 10) {
                nodes {
                  identity
                  companyName: field(source: "name") { values }
                  employees: relationship(source: "EMPLOYS", targetType: "Person") {
                    nodes { identity personName: field(source: "name") { values } }
                  }
                }
                pageInfo { snapshotVersion }
              }
            }
            """));
        Assert.AreEqual(HttpStatusCode.OK, graphQl.StatusCode, await graphQl.Content.ReadAsStringAsync());
        string json = await graphQl.Content.ReadAsStringAsync();
        StringAssert.Contains(json, "Acuity");
        StringAssert.Contains(json, "Alex");
        StringAssert.Contains(json, company.ToString("D"));
    }

    [TestMethod]
    public async Task RDF_entity_documents_have_embedded_remote_and_GraphQL_parity()
    {
        _directory = NewDirectory();
        await using EntityForgeDbHost host = EntityForgeDbServer.CreateBuilder()
            .UseLocalStorage(_directory).UseRdfProfile()
            .ConfigureServices(services => services.AddSingleton<IEntityForgeDbAuthorizationPolicy,
                AllowEntityForgeDbAuthorizationPolicy>())
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using HttpClient http = Client(host);
        const string subject = "https://example.test/company/acme";
        const string secondSubject = "https://example.test/company/northwind";
        const string companyType = "https://example.test/Company";
        const string personType = "https://example.test/Person";
        const string name = "https://example.test/name";
        const string employee = "https://example.test/person/alex";
        const string employs = "https://example.test/employs";
        RdfMutationWriteRequest[] mutations =
        [
            new("assert", Wire(new RdfQuad(new RdfIri(subject),
                new RdfIri("http://www.w3.org/1999/02/22-rdf-syntax-ns#type"), new RdfIri(companyType)))),
            new("assert", Wire(new RdfQuad(new RdfIri(subject), new RdfIri(name), new RdfLiteral("Acuity")))),
            new("assert", Wire(new RdfQuad(new RdfIri(secondSubject),
                new RdfIri("http://www.w3.org/1999/02/22-rdf-syntax-ns#type"), new RdfIri(companyType)))),
            new("assert", Wire(new RdfQuad(new RdfIri(secondSubject), new RdfIri(name), new RdfLiteral("Northwind")))),
            new("assert", Wire(new RdfQuad(new RdfIri(employee),
                new RdfIri("http://www.w3.org/1999/02/22-rdf-syntax-ns#type"), new RdfIri(personType)))),
            new("assert", Wire(new RdfQuad(new RdfIri(employee), new RdfIri(name), new RdfLiteral("Alex")))),
            new("assert", Wire(new RdfQuad(new RdfIri(subject), new RdfIri(employs), new RdfIri(employee))))
        ];
        HttpResponseMessage write = await http.PostAsJsonAsync("/v1/rdf/transactions",
            new RdfMutationBatchRequest(Guid.NewGuid(), mutations));
        Assert.AreEqual(HttpStatusCode.OK, write.StatusCode, await write.Content.ReadAsStringAsync());

        var request = new EntityProjectionQueryRequest(companyType)
        {
            Identities = [subject],
            Fields = [new(name, "companyName")]
        };
        EntityProjectionQueryResult remoteResult = await new HttpEntityProjectionClient(http).QueryAsync(request);
        EntityForgeDbRuntime runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
        EntityProjectionQueryResult embedded = await new RdfEntityProjectionClient(runtime.RdfDatabase)
            .QueryAsync(request);
        Assert.AreEqual("Acuity", ((LiteralValue)remoteResult.Document.Roots.Single().Fields
            .Single().Values.Single().Value).LexicalValue);
        Assert.AreEqual(embedded.Document.Roots.Single().Identity, remoteResult.Document.Roots.Single().Identity);

        var firstPageRequest = new EntityProjectionQueryRequest(companyType)
        {
            Fields = [new(name, "companyName")],
            First = 1
        };
        EntityProjectionQueryResult firstPage = await new HttpEntityProjectionClient(http).QueryAsync(firstPageRequest);
        Assert.IsNotNull(firstPage.NextContinuation);
        EntityProjectionQueryResult secondPage = await new HttpEntityProjectionClient(http).QueryAsync(firstPageRequest with
        {
            SnapshotVersion = firstPage.SnapshotVersion.Value,
            After = firstPage.NextContinuation
        });
        Assert.AreEqual(1, secondPage.Document.Roots.Count);
        Assert.AreNotEqual(firstPage.Document.Roots.Single().Identity, secondPage.Document.Roots.Single().Identity);
        Assert.IsNull(secondPage.NextContinuation);

        HttpResponseMessage graphQl = await http.PostAsJsonAsync("/graphql", new EntityForgeGraphQlRequest($$"""
            query {
              entities(type: "{{companyType}}", identities: ["{{subject}}"] ) {
                nodes {
                  identity
                  companyName: field(source: "{{name}}") { values }
                  employees: relationship(source: "{{employs}}", targetType: "{{personType}}") {
                    nodes { identity personName: field(source: "{{name}}") { values } }
                  }
                }
                pageInfo { snapshotVersion }
              }
            }
            """));
        Assert.AreEqual(HttpStatusCode.OK, graphQl.StatusCode, await graphQl.Content.ReadAsStringAsync());
        string json = await graphQl.Content.ReadAsStringAsync();
        StringAssert.Contains(json, "Acuity");
        StringAssert.Contains(json, "Alex");
        StringAssert.Contains(json, subject);
    }

    private static RdfQuadWriteRequest Wire(RdfQuad quad) => new(Wire(quad.Subject), Wire(quad.Predicate),
        Wire(quad.Object), quad.Graph is null ? null : Wire(quad.Graph));

    private static RdfTermWriteRequest Wire(RdfTerm term) => term switch
    {
        RdfIri iri => new("iri", Value: iri.Value),
        RdfBlankNode blank => new("blank", Identifier: blank.Identifier),
        RdfLiteral literal => new("literal", LexicalForm: literal.LexicalForm,
            Datatype: literal.Datatype, Language: literal.Language),
        RdfTripleTerm triple => new("triple", Subject: Wire(triple.Subject),
            Predicate: Wire(triple.Predicate), Object: Wire(triple.Object)),
        _ => throw new ArgumentOutOfRangeException(nameof(term))
    };

    private static HttpClient Client(EntityForgeDbHost host, string? bearerToken = null)
    {
        IServer server = host.Application.Services.GetRequiredService<IServer>();
        string address = server.Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var client = new HttpClient { BaseAddress = new Uri(address) };
        if (bearerToken is not null)
        {
            client.DefaultRequestHeaders.Authorization = new("Bearer", bearerToken);
        }

        return client;
    }

    private SemanticSessionTransportRequest Transport(SemanticSessionEnvelope session) => new(
        Convert.ToBase64String(_codec.Serialize(session.Start)),
        session.Pages.Select(page => Convert.ToBase64String(_codec.Serialize(page))).ToArray(),
        Convert.ToBase64String(_codec.Serialize(session.Close)));

    private static SemanticSessionEnvelope WithProgressPrecondition(
        SemanticSessionEnvelope session,
        SemanticSourceProgressPrecondition precondition)
    {
        SemanticChangeSessionStart start = session.Start;
        return new SemanticSessionEnvelope(
            new SemanticChangeSessionStart(start.SessionId, start.Kind, start.Source,
                start.ProducerActivityId, start.StartedAt, start.CorrelationId, precondition),
            session.Pages,
            session.Close);
    }

    private async Task ApplyAsync(HttpClient client, SemanticSessionEnvelope session)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync("/v1/semantic/sessions", Transport(session));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private SemanticSessionEnvelope SourceSnapshot(Uri source, string partitionName, string sourceVersion,
        params SemanticFieldChange[] fields)
    {
        Guid sessionId = Guid.NewGuid();
        var identity = new SemanticEntityIdentity(Company(), new SemanticIdentityScheme(
            new Uri("https://example.test/companyNumber"), "Company number", SemanticValueKind.String),
            new StringSemanticValue("01234567"));
        var start = new SemanticChangeSessionStart(sessionId, SemanticChangeSessionKind.SourceSnapshot,
            new SemanticChangeSessionSource(source, new Uri($"{source.AbsoluteUri}/{partitionName}"),
                sourceVersion, Guid.NewGuid(), sourceVersion), Guid.NewGuid(), DateTimeOffset.UtcNow);
        var provenance = new SemanticProvenance(source, "01234567", DateTimeOffset.UtcNow,
            new SemanticModelReference(new Uri("https://example.test/vocabulary"), "1",
                SemanticModelKind.Vocabulary));
        SemanticChangeSessionPage[] pages = [new(sessionId, 0,
            [new SemanticEntityChange(Guid.NewGuid(), SemanticEntityChangeKind.ReplaceSourceState,
                identity, provenance, fields: fields)])];
        return new(start, pages, new SemanticChangeSessionClose(sessionId, 1, 1,
            _codec.ComputeContentHash(pages), DateTimeOffset.UtcNow));
    }

    private SemanticSessionEnvelope Session()
    {
        Guid sessionId = Guid.NewGuid();
        Uri source = new("https://source.example/companies");
        var identity = new SemanticEntityIdentity(
            new SemanticTerm(new Uri("https://example.test/Company"), SemanticTermRole.Concept),
            new SemanticIdentityScheme(new Uri("https://example.test/companyNumber"),
                "Company number", SemanticValueKind.String), new StringSemanticValue("01234567"));
        var start = new SemanticChangeSessionStart(sessionId, SemanticChangeSessionKind.Incremental,
            new SemanticChangeSessionSource(source, new Uri("https://source.example/partition"), "1",
                Guid.NewGuid(), "1"), Guid.NewGuid(), DateTimeOffset.UtcNow);
        var change = new SemanticEntityChange(Guid.NewGuid(), SemanticEntityChangeKind.ReplaceSourceState,
            identity, new SemanticProvenance(source, "01234567", DateTimeOffset.UtcNow,
                new SemanticModelReference(new Uri("https://example.test/vocabulary"), "1",
                    SemanticModelKind.Vocabulary)), fields: new[]
            {
                new SemanticFieldChange(Industry(), SemanticFieldChangeKind.Set,
                    new StringSemanticValue("Technology"))
            });
        var pages = new[] { new SemanticChangeSessionPage(sessionId, 0, new[] { change }) };
        var close = new SemanticChangeSessionClose(sessionId, 1, 1,
            _codec.ComputeContentHash(pages), DateTimeOffset.UtcNow);
        return new(start, pages, close);
    }

    private SemanticSessionEnvelope Session(SemanticTerm field, SemanticValue value)
        => Session("01234567", field, value);

    private SemanticSessionEnvelope Session(string identityValue, SemanticTerm field, SemanticValue value)
        => Session(identityValue, new SemanticFieldChange(field, SemanticFieldChangeKind.Set, value));

    private SemanticSessionEnvelope Session(string identityValue, params SemanticFieldChange[] fields)
    {
        Guid sessionId = Guid.NewGuid();
        Uri source = new("https://source.example/companies");
        var identity = new SemanticEntityIdentity(Company(), new SemanticIdentityScheme(
            new Uri("https://example.test/companyNumber"), "Company number", SemanticValueKind.String),
            new StringSemanticValue(identityValue));
        var start = new SemanticChangeSessionStart(sessionId, SemanticChangeSessionKind.Incremental,
            new SemanticChangeSessionSource(source, new Uri("https://source.example/partition"), "1",
                Guid.NewGuid(), "1"), Guid.NewGuid(), DateTimeOffset.UtcNow);
        var change = new SemanticEntityChange(Guid.NewGuid(), SemanticEntityChangeKind.ReplaceSourceState,
            identity, new SemanticProvenance(source, identityValue, DateTimeOffset.UtcNow,
                new SemanticModelReference(new Uri("https://example.test/vocabulary"), "1",
                    SemanticModelKind.Vocabulary)), fields: fields);
        SemanticChangeSessionPage[] pages = [new(sessionId, 0, [change])];
        return new(start, pages, new SemanticChangeSessionClose(sessionId, 1, 1,
            _codec.ComputeContentHash(pages), DateTimeOffset.UtcNow));
    }

    private SemanticSessionEnvelope ContainerSession()
    {
        Guid sessionId = Guid.Parse("e7117c60-488c-4d7c-bac7-70a135761101");
        Uri source = new("https://source.example/container-smoke/companies");
        DateTimeOffset observedAt = DateTimeOffset.Parse("2026-08-12T12:00:00Z");
        var identity = new SemanticEntityIdentity(Company(),
            new SemanticIdentityScheme(new Uri("https://example.test/companyNumber"),
                "Company number", SemanticValueKind.String), new StringSemanticValue("01234567"));
        var start = new SemanticChangeSessionStart(sessionId, SemanticChangeSessionKind.Incremental,
            new SemanticChangeSessionSource(source, new Uri("https://source.example/container-smoke/partition"), "1",
                Guid.Parse("20d96a13-e5ea-42af-b560-eb01c85ca102"), "1"),
            Guid.Parse("1948f303-a59a-4614-9bf9-c1b5580a4103"), observedAt);
        var change = new SemanticEntityChange(Guid.Parse("1df685b2-7826-4df9-81ce-d93a4128d104"),
            SemanticEntityChangeKind.ReplaceSourceState, identity,
            new SemanticProvenance(source, "01234567", observedAt,
                new SemanticModelReference(new Uri("https://example.test/vocabulary"), "1",
                    SemanticModelKind.Vocabulary)), fields: new[]
            {
                new SemanticFieldChange(Industry(), SemanticFieldChangeKind.Set,
                    new StringSemanticValue("Technology"))
            });
        var pages = new[] { new SemanticChangeSessionPage(sessionId, 0, new[] { change }) };
        var close = new SemanticChangeSessionClose(sessionId, 1, 1,
            _codec.ComputeContentHash(pages), observedAt);
        return new(start, pages, close);
    }

    private static SemanticTerm Industry() =>
        new(new Uri("https://example.test/industry"), SemanticTermRole.Property);

    private static SemanticTerm Name() =>
        new(new Uri("https://example.test/name"), SemanticTermRole.Property);

    private static SemanticTerm Company() =>
        new(new Uri("https://example.test/Company"), SemanticTermRole.Concept);

    private string NewDirectory() => _testDirectories.Create(createDirectory: false);

    private sealed class Marker;

    private sealed class DenyAdministrationPolicy : IEntityForgeDbAuthorizationPolicy
    {
        public ValueTask<bool> AuthorizeAsync(HttpContext context,
            EntityForgeDbAdministrativeOperation operation, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(false);
    }

    private sealed class CapturingSecurityAuditSink : IEntityForgeDbSecurityAuditSink
    {
        public List<EntityForgeDbSecurityAuditEvent> Events { get; } = [];

        public ValueTask WriteAsync(EntityForgeDbSecurityAuditEvent auditEvent,
            CancellationToken cancellationToken = default)
        {
            lock (Events)
            {
                Events.Add(auditEvent);
            }

            return ValueTask.CompletedTask;
        }
    }
}
