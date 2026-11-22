using EntityForge.Db.Query.Adapters;
using EntityForge.Db.Gql;
using EntityForge.Db.Semantic;
using EntityForge.Db.Semantic.Engine;
using EntityForge.Query.Projection;
using EntityForge.Query.Querying;
using EntityForge.Semantics.Protobuf;
using EntityForge.Semantics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Graph;
using EntityForge.Db.Rdf;
using EntityForge.Db.Client;
using EntityForge.Db;
using System.Text.Json;
using System.Reflection;
using EntityForge.Db.Storage.Local;
using EntityForge.Db.Transport.Contracts;

namespace EntityForge.Db.Hosting;

/// <summary>Contains the serialized start, page and close messages of one semantic change session.</summary>
internal sealed record SemanticSessionTransportRequest(
    string Start,
    IReadOnlyList<string> Pages,
    string Close);

internal sealed record SemanticContributionRevisionBeginRequest(
    string Start, SemanticContributionRevision? ExpectedPublishedRevision);

internal sealed record SemanticContributionRevisionPartRequest(string Page);

internal sealed record SemanticContributionRevisionSealRequest(string Close);

internal sealed record SemanticContributionRevisionLookupResponse(SemanticContributionRevision? Revision);

internal sealed record SemanticContributionRevisionStatusResponse(SemanticContributionRevisionInfo? Revision);

/// <summary>Requests a bounded page of committed semantic changes.</summary>
internal sealed record SemanticChangePageRequest(
    string? Cursor = null,
    int MaximumCount = DatabaseChangeFeedLimits.DefaultSubscriptionPageSize,
    string? RoutingKind = null,
    string? RoutingValue = null);

/// <summary>Requests creation of a graph vertex.</summary>
internal sealed record GraphVertexWriteRequest(Guid Id, IReadOnlyList<string> Labels);

/// <summary>Requests creation of a directed graph edge.</summary>
internal sealed record GraphEdgeWriteRequest(Guid Id, Guid From, Guid To, string Label);

/// <summary>Requests assignment of a graph property using a JSON transport value.</summary>
internal sealed record GraphPropertyWriteRequest(string Name, JsonElement Value);

/// <summary>Describes one graph vertex in an atomic batch write.</summary>
/// <param name="Id">Vertex identity.</param>
/// <param name="Labels">Labels assigned to the vertex.</param>
/// <param name="Properties">Optional JSON property values interpreted by the graph transport.</param>
public sealed record GraphBatchVertexWriteRequest(Guid Id, IReadOnlyList<string> Labels,
    IReadOnlyDictionary<string, JsonElement>? Properties = null);

/// <summary>Describes one directed graph edge in an atomic batch write.</summary>
/// <param name="Id">Edge identity.</param>
/// <param name="From">Source vertex identity.</param>
/// <param name="To">Target vertex identity.</param>
/// <param name="Label">Relationship label.</param>
/// <param name="Properties">Optional JSON property values interpreted by the graph transport.</param>
public sealed record GraphBatchEdgeWriteRequest(Guid Id, Guid From, Guid To, string Label,
    IReadOnlyDictionary<string, JsonElement>? Properties = null);

/// <summary>Requests one atomic batch of graph vertices, edges and their properties.</summary>
/// <param name="TransactionId">Transaction identity used for retry resolution.</param>
/// <param name="Vertices">Vertices supplied by the atomic batch.</param>
/// <param name="Edges">Edges supplied by the same atomic batch.</param>
public sealed record GraphBatchWriteRequest(
    Guid TransactionId,
    IReadOnlyList<GraphBatchVertexWriteRequest> Vertices,
    IReadOnlyList<GraphBatchEdgeWriteRequest> Edges);

/// <summary>Maps the HTTP surface for an EntityForgeDB profile.</summary>
public static class EntityForgeDbEndpointRouteBuilderExtensions
{
    private const string ProductName = "EntityForgeDB";
    private static readonly string ProductVersion = typeof(EntityForgeDbEndpointRouteBuilderExtensions).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        .Split('+', 2)[0] ?? "unknown";

    /// <summary>Maps health, capabilities, status, checkpoint and profile-specific endpoints.</summary>
    public static IEndpointRouteBuilder MapEntityForgeDbEndpoints(this IEndpointRouteBuilder endpoints,
        DatabaseProfileId profile)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
        endpoints.MapGet("/health/ready", (EntityForgeDbRuntime runtime) =>
        {
            EntityForgeDbReadiness readiness = runtime.GetReadiness();
            return readiness.Ready ? Results.Ok(readiness) : Results.Json(readiness,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        });
        endpoints.MapGet("/v1/capabilities", (EntityForgeDbRuntime runtime, HostedEntityApplications applications) =>
        {
            var capabilities = Capabilities(runtime.Profile);
            return Results.Ok(capabilities with
            {
                HostKind = "standalone",
                DatabaseIncarnation = applications.DatabaseIncarnation,
                Surfaces = [.. capabilities.Surfaces,
                    Surface(EntityForgeSurfaceIds.AccessDiscovery, EntityForgeSurfaceMaturity.Stable, ["current-credential"]),
                    Surface(EntityForgeSurfaceIds.ApplicationOperations, EntityForgeSurfaceMaturity.Stable, ["submit", "resolve", "abort"])]
            });
        });
        EntityForgeAccessEndpoints.Map(endpoints);
        EntityForgeAdministrationJobEndpoints.Map(endpoints);
        EntityForgeLiveTelemetry.Map(endpoints);
        EntityForgeObservability.Map(endpoints);
        endpoints.MapGet("/v1/storage-profile", async (HttpContext context, StorageProfileService storage,
            IEntityForgeDbAuthorizationPolicy authorization) =>
            await AuthorizeAndExecuteAsync(context, authorization, EntityForgeDbAdministrativeOperation.ReadStorageProfile,
                () => Task.FromResult(Results.Ok(storage.Capture()) as IResult)));
        endpoints.MapPost("/v1/storage-profile/probe", async (HttpContext context, StorageProfileService storage,
            IEntityForgeDbAuthorizationPolicy authorization, CancellationToken cancellation) =>
            await AuthorizeAndExecuteAsync(context, authorization, EntityForgeDbAdministrativeOperation.RunStorageProbe,
                async () =>
                {
                    try
                    {
                        return Results.Ok(await storage.ProbeAsync(cancellation));
                    }
                    catch (InvalidOperationException e)
                    {
                        return Problem(409, "storage_probe_unavailable", e);
                    }
                    catch (OperationCanceledException e) when (!cancellation.IsCancellationRequested)
                    {
                        return Problem(504, "storage_probe_timeout", e);
                    }
                    catch (IOException e)
                    {
                        return Problem(503, "storage_probe_io_failure", e);
                    }
                    catch (UnauthorizedAccessException e)
                    {
                        return Problem(503, "storage_probe_access_failure", e);
                    }
                }));
        EntityProjectionTransportEndpoints.Map(endpoints);
        EntityOperationTransportEndpoints.Map(endpoints);
        EntityReadScopeEndpoints.Map(endpoints);
        endpoints.MapGet("/v1/status", async (HttpContext context, EntityForgeDbRuntime runtime,
            IEntityForgeDbAuthorizationPolicy authorization, CancellationToken cancellation) =>
            await AuthorizeAndExecuteAsync(context, authorization, EntityForgeDbAdministrativeOperation.ReadStatus,
                async () => Results.Ok(await runtime.GetOperationalStatusAsync(cancellation)
                    .ConfigureAwait(false))).ConfigureAwait(false));
        if (profile is DatabaseProfileId.PropertyGraph)
        {
            GraphEntityForgeDbEndpoints.Map(endpoints);
        }

        if (profile is DatabaseProfileId.Rdf)
        {
            RdfEntityForgeDbEndpoints.Map(endpoints);
        }

        endpoints.MapGet("/openapi/v1.json", (EndpointDataSource dataSource) => Results.Json(
            RuntimeOpenApiDocument.Create(dataSource, ProductName, ProductVersion),
            contentType: "application/vnd.oai.openapi+json;version=3.0"));
        if (profile is DatabaseProfileId.Semantic)
        {
            endpoints.MapGet("/v1/semantic/catalog", async (EntityForgeDbRuntime runtime,
                CancellationToken cancellationToken) => Results.Ok(EntityForgeQueryMetadataMapper.Catalog(
                    await runtime.Database.GetResolvedSemanticCatalogAsync(cancellationToken)
                        .ConfigureAwait(false))));
            endpoints.MapPost("/v1/semantic/vocabularies", async (
                SemanticVocabularyPublicationRequest request, EntityForgeDbRuntime runtime,
                CancellationToken cancellationToken) => Results.Ok(await runtime.Database.PublishVocabularyAsync(
                    EntityForgeQueryMetadataMapper.Vocabulary(request), cancellationToken).ConfigureAwait(false)));
            endpoints.MapGet("/v1/semantic/property-storage/optimization", async (
                HttpContext context, EntityForgeDbRuntime runtime,
                IEntityForgeDbAuthorizationPolicy authorization, CancellationToken cancellation) =>
                await AuthorizeAndExecuteAsync(context, authorization,
                    EntityForgeDbAdministrativeOperation.AnalyzePropertyStorage,
                    async () => Results.Ok(await runtime.Database.AnalyzePropertyStorageAsync(
                        cancellationToken: cancellation).ConfigureAwait(false))).ConfigureAwait(false));
            endpoints.MapPost("/v1/semantic/property-storage/optimization", async (
                HttpContext context, EntityForgeDbRuntime runtime,
                IEntityForgeDbAuthorizationPolicy authorization, int? maximumChanges,
                CancellationToken cancellation) => await AuthorizeAndExecuteAsync(context, authorization,
                    EntityForgeDbAdministrativeOperation.ApplyPropertyStorageOptimization,
                    async () => Results.Ok(await runtime.Database.ApplyPropertyStorageOptimizationAsync(
                        maximumChanges: maximumChanges ?? 1, cancellationToken: cancellation)
                        .ConfigureAwait(false))).ConfigureAwait(false));
            endpoints.MapDelete("/v1/semantic/property-storage/optimization/observations", async (
                HttpContext context, EntityForgeDbRuntime runtime,
                IEntityForgeDbAuthorizationPolicy authorization, CancellationToken cancellation) =>
                await AuthorizeAndExecuteAsync(context, authorization,
                    EntityForgeDbAdministrativeOperation.ResetPropertyStorageObservations, async () =>
                    {
                        await runtime.Database.ResetPropertyStorageObservationsAsync(cancellation)
                            .ConfigureAwait(false);
                        return Results.NoContent();
                    }).ConfigureAwait(false));
            SemanticEntityForgeDbEndpoints.Map(endpoints);
        }
        endpoints.MapPost("/v1/checkpoints", async (HttpContext context, EntityForgeDbRuntime runtime,
            IEntityForgeDbAuthorizationPolicy authorization, CancellationToken cancellation) =>
            await AuthorizeAndExecuteAsync(context, authorization,
                EntityForgeDbAdministrativeOperation.CreateCheckpoint, async () => Results.Ok(new
                {
                    version = await runtime.CreateCheckpointAsync(cancellation).ConfigureAwait(false)
                })).ConfigureAwait(false));
        endpoints.MapPost("/v1/backups", async (HttpContext context, EntityForgeDbRuntime runtime,
            IEntityForgeDbAuthorizationPolicy authorization, EntityForgeDbBackupRequest request,
            CancellationToken cancellation) => await AuthorizeAndExecuteAsync(context, authorization,
                EntityForgeDbAdministrativeOperation.CreateBackup, async () => Results.Ok(
                    await runtime.CreateNamedBackupAsync(request.Name, cancellation).ConfigureAwait(false)))
                .ConfigureAwait(false));
        endpoints.MapPost("/v1/archives", async (HttpContext context, EntityForgeDbRuntime runtime,
            IEntityForgeDbAuthorizationPolicy authorization, CancellationToken cancellation) =>
            await AuthorizeAndExecuteAsync(context, authorization,
                EntityForgeDbAdministrativeOperation.CreateHistoricalArchive,
                async () => Results.Ok(await runtime.CreateHistoricalArchiveAsync(cancellation).ConfigureAwait(false)))
                .ConfigureAwait(false));
        if (profile is DatabaseProfileId.Semantic)
        {
            endpoints.MapPost("/v1/archives/query", async (HttpContext context, EntityForgeDbRuntime runtime,
                IEntityForgeDbAuthorizationPolicy authorization, HistoricalEvidenceArchiveQueryRequest request,
                CancellationToken cancellation) => await AuthorizeAndExecuteAsync(context, authorization,
                    EntityForgeDbAdministrativeOperation.QueryHistoricalArchive,
                    async () => Results.Ok(await runtime.QueryHistoricalArchiveAsync(request, cancellation)
                        .ConfigureAwait(false))).ConfigureAwait(false));
        }

        return endpoints;
    }

    private static EntityForgeHostCapabilitiesTransport Capabilities(DatabaseProfileId profile) => profile switch
    {
        DatabaseProfileId.PropertyGraph => new(ProductName, ProductVersion, "property-graph",
        [
            Surface(EntityForgeSurfaceIds.GraphNative, EntityForgeSurfaceMaturity.Stable,
                ["vertex-lookup", "incoming-adjacency", "outgoing-adjacency", "as-of-version", "graph-record-evidence"]),
            Surface(EntityForgeSurfaceIds.GraphGql, EntityForgeSurfaceMaturity.Alpha,
                ["fixed-graph-pattern", "iso-gql-subset"]),
            EntityProjection(),
            GraphQlProjection(),
            Administration(),
        ]),
        DatabaseProfileId.Semantic => new(ProductName, ProductVersion, "semantic",
        [
            Surface(EntityForgeSurfaceIds.SemanticNative, EntityForgeSurfaceMaturity.Stable,
                ["identity-selection",
                    "scalar-string-filter",
                    "field-projection",
                    "bounded-multi-hop-projection",
                    "deterministic-order-limit",
                    "preferred-provider",
                    "evidence",
                    "explain-analyze",
                    "continuation",
                    "typed-scalar-filter",
                    "as-of-version",
                    "query-invalidation",
                    "query-invalidation-stream",
                    "temporal-query-scopes",
                    "historical-evidence-archive",
                    "archive-restore-query",
                    "semantic-assertion-policy",
                    "boolean-predicates",
                    "ordered-scalar-comparisons",
                    "evidence-aware-aggregation"]),
            Surface(EntityForgeSurfaceIds.SemanticCatalog, EntityForgeSurfaceMaturity.Stable,
                ["resolved-logical-catalog", "vocabulary-publication", "property-storage-optimization"]),
            Surface(EntityForgeSurfaceIds.SemanticGql, EntityForgeSurfaceMaturity.Preview,
                ["read-only", "asserted-evidence", "iso-gql-subset", "assertion-provenance"]),
            Surface(EntityForgeSurfaceIds.SemanticSparql, EntityForgeSurfaceMaturity.Preview,
                ["read-only", "asserted-evidence", "sparql-1.1-query-subset", "sparql-protocol", "assertion-provenance"]),
            EntityProjection(),
            GraphQlProjection(semantic: true),
            ChangeFeed(),
            Surface(EntityForgeSurfaceIds.SnapshotLeases, EntityForgeSurfaceMaturity.Stable,
                ["acquire", "renew", "release"]),
            Administration(semantic: true),
        ]),
        DatabaseProfileId.Rdf => new(ProductName, ProductVersion, "rdf",
        [
            Surface(EntityForgeSurfaceIds.RdfNative, EntityForgeSurfaceMaturity.Stable,
                ["quad-storage",
                    "named-graphs",
                    "subject-predicate-object-graph-pattern",
                    "n-triples",
                    "n-quads",
                    "turtle",
                    "json-ld-1.1",
                    "resumable-document-pages",
                    "content-negotiated-rdf-import-export",
                    "atomic-assert-retract-transactions",
                    "http-quad-pattern",
                    "ndjson-quad-stream",
                    "cursor-backed-quad-pages",
                    "snapshot-consistent-stream",
                    "rdf-star-storage",
                    "turtle-star-import-export",
                    "n-triples-star-import",
                    "n-quads-star-import-export",
                    "rdf-star-quad-pattern"]),
            Surface(EntityForgeSurfaceIds.RdfSparql, EntityForgeSurfaceMaturity.Alpha,
                ["basic-graph-pattern",
                    "named-graph-pattern",
                    "optional-union",
                    "select-distinct-offset",
                    "ask-values-practical-filters",
                    "sparql-1.2-triple-term-pattern"]),
            Surface(EntityForgeSurfaceIds.RdfShacl, EntityForgeSurfaceMaturity.Preview, ["shacl-core-validation"]),
            Surface(EntityForgeSurfaceIds.RdfEntailment, EntityForgeSurfaceMaturity.Preview,
                ["rdfs-query-time-entailment"]),
            EntityProjection(),
            GraphQlProjection(),
            ChangeFeed(),
            Administration(),
        ]),
        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, "Unsupported database profile.")
    };

    private static EntityForgeSurfaceCapability Surface(string identifier, EntityForgeSurfaceMaturity maturity,
        IReadOnlyList<string> features) => new(identifier, maturity, features);

    private static EntityForgeSurfaceCapability EntityProjection() => Surface(
        EntityForgeSurfaceIds.EntityProjection, EntityForgeSurfaceMaturity.Stable, ["entity-document-query"]);

    private static EntityForgeSurfaceCapability GraphQlProjection(bool semantic = false) => Surface(
        EntityForgeSurfaceIds.GraphQlProjection, EntityForgeSurfaceMaturity.Preview, semantic
            ? ["graphql-query-adapter", "schema-introspection", "atomic-source-mutations"]
            : ["graphql-query-adapter", "schema-introspection", "atomic-native-mutations"]);

    private static EntityForgeSurfaceCapability ChangeFeed() => Surface(
        EntityForgeSurfaceIds.ChangeFeed, EntityForgeSurfaceMaturity.Stable, ["cursor-pages", "bounds"]);

    private static EntityForgeSurfaceCapability Administration(bool semantic = false) => Surface(
        EntityForgeSurfaceIds.Administration, EntityForgeSurfaceMaturity.Stable,
        ["status", "checkpoints", "backups", "archives", "storage-profile", "storage-probe", "administration-jobs-v1", "live-metrics-v1", "observability-v1", .. (semantic ? new[] { "semantic-optimization-jobs-v1" } : Array.Empty<string>())]);

    private static object OpenApiDocument(DatabaseProfileId profile)
    {
        var paths = new Dictionary<string, object>
        {
            ["/health/live"] = Operation("get", "Process liveness", protectedOperation: false),
            ["/health/ready"] = Operation("get", "Database readiness", protectedOperation: false),
            ["/v1/capabilities"] = Operation("get", "Database capabilities", protectedOperation: false),
            ["/v1/storage-profile"] = Operation("get", "Server filesystem capacity and last scratch-file probe"),
            ["/v1/storage-profile/probe"] = Operation("post", "Run a bounded scratch-file storage probe"),
            ["/v1/status"] = Operation("get", "Database operational status"),
            ["/v1/security/access"] = Operation("get", "Current credential access without secrets"),
            ["/v1/telemetry"] = Operation("get", "Bounded process-local database metric aggregates since monitoring began"),
            ["/v1/administration/target"] = Operation("get", "Review standalone administration targets", responseSchema: nameof(AdministrationTarget)),
            ["/v1/administration/jobs/{id}"] = new Dictionary<string, object>
            {
                ["get"] = AdministrationJobOperation(submit: false),
                ["post"] = AdministrationJobOperation(submit: true)
            },
            ["/v1/checkpoints"] = Operation("post", "Create a durable checkpoint"),
            ["/v1/backups"] = Operation("post", "Create a verified local backup",
                nameof(EntityForgeDbBackupRequest)),
            ["/v1/archives"] = Operation("post", "Create an immutable verified historical evidence archive",
                responseSchema: nameof(HistoricalEvidenceArchiveInfo)),
            ["/v1/entities/query"] = Operation("post", "Execute a transport-neutral entity-document query",
                nameof(EntityProjectionQueryRequest), nameof(EntityProjectionTransportResponse)),
            ["/graphql"] = Operation("post", "Execute profile-aware GraphQL queries or atomic profile mutations",
                nameof(EntityForgeGraphQlRequest), nameof(EntityForgeGraphQlResponse))
        };
        if (profile is DatabaseProfileId.Semantic)
        {
            paths["/v1/semantic/catalog"] = Operation("get", "Resolved logical Semantic catalog",
                responseSchema: nameof(SemanticCatalogTransport));
            paths["/v1/semantic/property-storage/optimization"] = new Dictionary<string, object>
            {
                ["get"] = OperationDetails("Analyze Semantic property placement"),
                ["post"] = OperationDetails("Apply a bounded Semantic property optimization")
            };
            paths["/v1/semantic/property-storage/optimization/observations"] =
                Operation("delete", "Reset Semantic property workload observations");
            paths["/v1/semantic/sessions"] = Operation("post", "Apply a semantic session");
            paths["/v1/semantic/gql/query"] = Operation("post", "Query the read-only asserted semantic GQL view",
                nameof(GraphGqlQueryRequest), "GraphGqlQueryResponse");
            paths["/v1/semantic/sparql"] = Operation("post", "Query the read-only asserted semantic RDF view");
            paths["/v1/semantic/native-query/capabilities"] = Operation("get", "Get semantic native-query view bounds and restrictions");
            paths["/sparql"] = new Dictionary<string, object>
            {
                ["get"] = SparqlProtocolEndpoint.Describe(false, true),
                ["post"] = SparqlProtocolEndpoint.Describe(true, true)
            };
            paths["/v1/semantic/sessions/bulk"] = Operation("post", "Bulk-load an initial semantic session");
            paths["/v1/semantic/snapshot-leases"] = Operation("post", "Acquire a semantic snapshot lease",
                nameof(SnapshotLeaseRequest), nameof(SnapshotLeaseResponse));
            paths["/v1/semantic/snapshot-leases/{leaseId}"] = new Dictionary<string, object>
            {
                ["get"] = OperationDetails("Get a semantic snapshot lease"),
                ["put"] = OperationDetails("Renew a semantic snapshot lease",
                    nameof(SnapshotLeaseRenewalRequest), nameof(SnapshotLeaseResponse)),
                ["delete"] = OperationDetails("Release a semantic snapshot lease")
            };
            paths["/v1/semantic/changes"] = Operation("get", "Read committed semantic changes",
                responseSchema: nameof(SemanticChangePageResponse));
            paths["/v1/semantic/entities/query"] = Operation("post", "Execute a native Semantic entity query",
                nameof(PortableEntityQueryRequest), nameof(EntityProjectionTransportResponse));
            paths["/v1/archives/query"] = Operation("post",
                "Restore an explicit historical archive and execute the portable entity query contract",
                nameof(HistoricalEvidenceArchiveQueryRequest), nameof(HistoricalEvidenceArchiveQueryResponse));
            paths["/v1/semantic/entities/explain"] = Operation("post", "Explain a native Semantic entity query",
                nameof(PortableEntityQueryExplainRequest), nameof(SemanticEntityQueryExplanation));
            paths["/v1/semantic/aggregations/query"] = Operation("post", "Execute evidence-aware Semantic aggregation",
                nameof(PortableSemanticAggregationRequest), nameof(PortableSemanticAggregationResponse));
            paths["/v1/semantic/aggregations/explain"] = Operation("post", "Explain Semantic aggregation",
                nameof(PortableSemanticAggregationExplainRequest), nameof(SemanticAggregationExplanation));
            paths["/v1/semantic/entities/invalidation"] = Operation("post", "Check whether a query result changed",
                nameof(SemanticEntityQueryInvalidationRequest), nameof(SemanticEntityQueryInvalidationResponse));
            paths["/v1/semantic/entities/invalidation/stream"] = Operation("post",
                "Stream resumable query invalidation events using Server-Sent Events",
                nameof(SemanticEntityQuerySubscriptionRequest));
        }
        else if (profile is DatabaseProfileId.PropertyGraph)
        {
            paths["/v1/graph/batches"] = Operation("post", "Atomically write a graph batch",
                nameof(GraphBatchWriteRequest));
            paths["/v1/graph/vertices"] = Operation("post", "Create a graph vertex");
            paths["/v1/graph/edges"] = Operation("post", "Create a graph edge");
            paths["/v1/graph/vertices/{id}"] = Operation("get", "Get a graph vertex");
            paths["/v1/graph/vertices/{id}/outgoing"] = Operation("get", "Traverse outgoing graph edges");
            paths["/v1/graph/vertices/{id}/incoming"] = Operation("get", "Traverse incoming graph edges");
            paths["/v1/graph/vertices/{id}/properties"] = Operation("post", "Set a graph vertex property");
            paths["/v1/graph/edges/{id}/properties"] = Operation("post", "Set a graph edge property");
            paths["/v1/graph/gql/query"] = Operation("post", "Execute a bounded ISO GQL graph pattern",
                nameof(GraphGqlQueryRequest), "GraphGqlQueryResponse");
            paths["/v1/graph/gql/update"] = Operation("post", "Execute an atomic bounded GQL mutation",
                nameof(GraphGqlMutationRequest));
            paths["/v1/administration/gql/graphs/{id}"] = Operation("post", "Administer a managed GQL graph with a durable request identity",
                nameof(ManagedGqlGraphRequest));
            paths["/v1/administration/gql/retention"] = Operation("get", "Inspect managed graph retention without deleting stores");
            paths["/v1/administration/gql/catalog/compact"] = Operation("post", "Expire reviewed catalog receipts and advance the request generation", requestSchema: nameof(ManagedGqlCompactionRequest));
            paths["/v1/administration/gql/retention/{id}/reclaim"] = Operation("post", "Reclaim a retired managed graph by exact store and incarnation identity", requestSchema: nameof(ManagedGqlReclamationRequest));
            paths["/v1/graph/gql/explain"] = Operation("post", "Explain a bounded ISO GQL graph pattern",
                nameof(GraphGqlExplainRequest), nameof(GraphPatternQueryExplanation));
            paths["/v1/graph/gql/capabilities"] = Operation("get", "Get exact alpha GQL capabilities");
        }
        else if (profile is DatabaseProfileId.Rdf)
        {
            paths["/v1/rdf/transactions"] = Operation("post", "Atomically assert and retract RDF quads",
                nameof(RdfMutationBatchRequest), nameof(RdfMutationBatchResponse));
            paths["/v1/rdf/document-pages"] = Operation("post", "Commit a bounded resumable RDF document page",
                nameof(RdfDocumentPageRequest), nameof(RdfDocumentPageCommit));
            paths["/v1/rdf/import"] = Operation("post", "Import one bounded RDF document page");
            paths["/v1/rdf/quads"] = Operation("post", "Query an RDF quad pattern",
                nameof(RdfQuadQueryRequest));
            paths["/v1/rdf/quads/stream"] = Operation("post", "Stream an RDF quad pattern as NDJSON",
                nameof(RdfQuadQueryRequest));
            paths["/v1/rdf/sparql"] = Operation("post", "Execute a bounded SPARQL query",
                nameof(RdfSparqlQueryRequest));
            paths["/sparql"] = new Dictionary<string, object>
            {
                ["get"] = SparqlProtocolEndpoint.Describe(false),
                ["post"] = SparqlProtocolEndpoint.Describe(true)
            };
            paths["/v1/rdf/sparql/capabilities"] = Operation("get", "Get exact bounded SPARQL capabilities");
            paths["/sparql/update"] = new Dictionary<string, object>
            {
                ["post"] = SparqlProtocolEndpoint.DescribeUpdate(),
                ["get"] = SparqlProtocolEndpoint.DescribeServiceOperation()
            };
            paths["/sparql/graphs"] = new[] { "get", "head", "put", "post", "delete" }
                .ToDictionary(method => method, SparqlProtocolEndpoint.DescribeGraphStore);
            paths["/sparql/graphs/{graphId}"] = new[] { "get", "head", "put", "post", "delete" }
                .ToDictionary(method => method, SparqlProtocolEndpoint.DescribeDirectGraphStore);
            paths["/v1/rdf/sparql/update"] = Operation("post", "Execute bounded atomic SPARQL Update",
                nameof(RdfSparqlUpdateRequest));
            paths["/v1/rdf/shacl/validate"] = Operation("post", "Validate a bounded RDF snapshot using SHACL Core");
            paths["/v1/rdf/shacl/capabilities"] = Operation("get", "Get exact bounded SHACL capabilities");
            paths["/v1/rdf/entailment/rdfs"] = Operation("get", "Calculate bounded RDFS consequences");
            paths["/v1/rdf/entailment/capabilities"] = Operation("get", "Get exact bounded entailment capabilities");
            paths["/v1/rdf/export"] = Operation("get", "Export RDF as N-Quads, Turtle, or JSON-LD");
            paths["/v1/rdf/changes"] = Operation("get", "Read committed RDF changes",
                responseSchema: nameof(RdfChangePageResponse));
            paths["/v1/rdf/changes/bounds"] = Operation("get", "Read RDF change-feed bounds");
        }
        return new
        {
            openapi = "3.0.3",
            info = new
            {
                title = ProductName,
                version = ProductVersion
            },
            paths,
            components = new
            {
                schemas = OpenApiSchemas(),
                securitySchemes = new Dictionary<string, object>
                {
                    ["bearerAuth"] = new { type = "http", scheme = "bearer" }
                }
            }
        };
    }

    private static object OpenApiSchemas() => new Dictionary<string, object>
    {
        [nameof(ManagedGqlReclamationRequest)] = new
        {
            type = "object",
            required = new[] { "incarnation" },
            properties = new
            {
                incarnation = new
                {
                    type = "string",
                    format = "uuid"
                }
            }
        },
        [nameof(ManagedGqlGraphRequest)] = new
        {
            type = "object",
            required = new[] { "statement", "generation" },
            properties = new
            {
                statement = new
                {
                    type = "string"
                },
                generation = new
                {
                    type = "integer",
                    format = "int64",
                    minimum = 1
                }
            }
        },
        [nameof(ManagedGqlCompactionRequest)] = new
        {
            type = "object",
            required = new[] { "generation", "version" },
            properties = new
            {
                generation = new
                {
                    type = "integer",
                    format = "int64",
                    minimum = 1
                },
                version = new
                {
                    type = "integer",
                    format = "int64",
                    minimum = 0
                }
            }
        },
        [nameof(AdministrationTarget)] = new { type = "object", required = new[] { "version", "databaseIncarnation", "profile", "dataDirectory", "backupDirectory" } },
        [nameof(AdministrationRequest)] = new
        {
            type = "object",
            required = new[] { "databaseIncarnation", "operation", "expectedTarget" },
            properties = new
            {
                databaseIncarnation = new
                {
                    type = "string",
                    format = "uuid"
                },
                operation = new
                {
                    type = "string",
                    @enum = new[] { "checkpoint", "backup", "gql-host-backup", "optimize", "reset-observations" }
                },
                backupName = new
                {
                    type = "string",
                    maxLength = 80,
                    pattern = "^[A-Za-z0-9_-]+$"
                },
                expectedTarget = new
                {
                    type = "string"
                }
            }
        },
        [nameof(AdministrationJob)] = new { type = "object", required = new[] { "version", "id", "databaseIncarnation", "operation", "target", "state", "updatedAt" } },
        [nameof(EntityProjectionQueryRequest)] = new { type = "object", required = new[] { "type" } },
        [nameof(EntityProjectionTransportResponse)] = new
        {
            type = "object",
            required = new[] { "queryId", "snapshotVersion", "entities", "evidence" }
        },
        [nameof(PortableEntityQueryRequest)] = new { type = "object", required = new[] { "rootConcept", "projection" } },
        [nameof(PortableEntityTemporalScopeRequest)] = new { type = "object", required = new[] { "dimension" } },
        [nameof(PortableSemanticAssertionPolicyRequest)] = new { type = "object" },
        [nameof(PortableSemanticAggregationRequest)] = new
        {
            type = "object",
            required = new[] { "source", "measures" }
        },
        [nameof(PortableSemanticAggregationResponse)] = new
        {
            type = "object",
            required = new[] { "queryId", "snapshotVersion", "rows", "evidence", "inputEntityCount" }
        },
        [nameof(PortableSemanticAggregationExplainRequest)] = new { type = "object", required = new[] { "query" } },
        [nameof(PortableEntityQueryExplainRequest)] = new
        {
            type = "object",
            required = new[] { "query" },
            properties = new
            {
                query = new
                {
                    type = "object"
                },
                analyze = new
                {
                    type = "boolean"
                }
            }
        },
        [nameof(PortableQueryLiteralRequest)] = new
        {
            type = "object",
            required = new[] { "value", "kind" },
            properties = new
            {
                value = new
                {
                },
                kind = new
                {
                    type = "string"
                },
                datatype = new
                {
                    type = "string",
                    format = "uri"
                },
                language = new
                {
                    type = "string"
                }
            }
        },
        [nameof(EntityForgeGraphQlRequest)] = new { type = "object", required = new[] { "query" } },
        [nameof(GraphGqlQueryRequest)] = new
        {
            type = "object",
            required = new[] { "query" },
            properties = new
            {
                query = new
                {
                    type = "string"
                },
                snapshotVersion = new
                {
                    type = "integer",
                    format = "int64"
                },
                includeSupportingGraph = new
                {
                    type = "boolean",
                    @default = false
                },
                parameters = new
                {
                    type = "object",
                    description = "Named scalar string, number, boolean or null values for $name placeholders. Maximum 32 parameters.",
                    additionalProperties = true
                }
            }
        },
        [nameof(GraphGqlMutationRequest)] = new
        {
            type = "object",
            required = new[] { "statement" },
            properties = new
            {
                statement = new
                {
                    type = "string"
                },
                transactionId = new
                {
                    type = "string",
                    format = "uuid"
                },
                parameters = new
                {
                    type = "object",
                    maxProperties = 32,
                    description = "Named scalar string, number, boolean or null values for $name placeholders.",
                    additionalProperties = true
                }
            }
        },
        ["GraphGqlQueryResponse"] = new
        {
            oneOf = new[]
            {
            new Dictionary<string, string> { ["$ref"] = "#/components/schemas/GraphProjectedQueryResponse" },
            new Dictionary<string, string> { ["$ref"] = "#/components/schemas/GraphPatternQueryResponse" }
            }
        },
        [nameof(GraphProjectedQueryResponse)] = new
        {
            type = "object",
            required = new[] { "queryId", "snapshotVersion", "matches" },
            additionalProperties = false,
            properties = new
            {
                diagnostics = new
                {
                    type = "array",
                    items = new
                    {
                        type = "object"
                    }
                },
                queryId = new
                {
                    type = "string",
                    format = "uuid"
                },
                snapshotVersion = new
                {
                    type = "integer",
                    format = "int64"
                },
                matches = new
                {
                    type = "array",
                    items = new
                    {
                        type = "object"
                    }
                }
            }
        },
        [nameof(GraphGqlExplainRequest)] = new { type = "object", required = new[] { "query" } },
        [nameof(GraphPatternQueryResponse)] = new
        {
            type = "object",
            required = new[] { "queryId", "snapshotVersion", "nodes", "edges", "matches", "evidence" }
        },
        [nameof(EntityForgeGraphQlResponse)] = new { type = "object" },
        [nameof(SemanticEntityQueryInvalidationRequest)] = new
        {
            type = "object",
            required = new[] { "query", "observedSnapshotVersion" }
        },
        [nameof(SemanticEntityQueryInvalidationResponse)] = new
        {
            type = "object",
            required = new[] { "isInvalidated", "observedSnapshotVersion", "hasMore", "scannedChanges" }
        },
        [nameof(SemanticEntityQuerySubscriptionRequest)] = new
        {
            type = "object",
            required = new[] { "query", "observedSnapshotVersion" }
        },
        [nameof(SemanticChangePageResponse)] = new
        {
            type = "object",
            required = new[] { "changes", "hasMore" }
        },
        [nameof(SnapshotLeaseRequest)] = new { type = "object", required = new[] { "durationSeconds" } },
        [nameof(SnapshotLeaseRenewalRequest)] = new
        {
            type = "object",
            required = new[] { "expectedRevision", "durationSeconds" }
        },
        [nameof(SnapshotLeaseResponse)] = new
        {
            type = "object",
            required = new[] { "leaseId", "snapshotVersion", "expiresAtUtc", "revision" }
        },
        [nameof(EntityForgeDbBackupRequest)] = new
        {
            type = "object",
            required = new[] { "name" },
            properties = new
            {
                name = new
                {
                    type = "string"
                }
            }
        },
        [nameof(HistoricalEvidenceArchiveInfo)] = new
        {
            type = "object",
            required = new[] { "archiveId", "profile", "snapshotVersion", "payloadSha256" }
        },
        [nameof(HistoricalEvidenceArchiveQueryRequest)] = new
        {
            type = "object",
            required = new[] { "archiveId", "query" }
        },
        [nameof(HistoricalEvidenceArchiveQueryResponse)] = new
        {
            type = "object",
            required = new[] { "storageTier", "archiveId", "snapshotVersion", "result" }
        }
    };

    private static object AdministrationJobOperation(bool submit)
    {
        var operation = (Dictionary<string, object>)OperationDetails(submit
            ? "Execute a reviewed checkpoint or unique backup once per retained job ID"
            : "Resolve a retained administration receipt", submit ? nameof(AdministrationRequest) : null, nameof(AdministrationJob));
        var id = new
        {
            name = "id",
            @in = "path",
            required = true,
            schema = new
            {
                type = "string",
                format = "uuid"
            }
        };
        var database = new
        {
            name = "databaseIncarnation",
            @in = "query",
            required = true,
            schema = new
            {
                type = "string",
                format = "uuid"
            }
        };
        operation["parameters"] = submit ? new[] { id } : new[] { id, database };
        return operation;
    }

    private static object Operation(string method, string summary,
        string? requestSchema = null, string? responseSchema = null, bool protectedOperation = true)
        => new Dictionary<string, object>
        {
            [method] = OperationDetails(summary, requestSchema, responseSchema, protectedOperation)
        };

    private static object OperationDetails(string summary,
        string? requestSchema = null, string? responseSchema = null, bool protectedOperation = true)
    {
        var operation = new Dictionary<string, object>
        {
            ["summary"] = summary,
            ["responses"] = new Dictionary<string, object>
            {
                ["200"] = Response("Successful operation", responseSchema),
                ["400"] = new { description = "Invalid request" },
                ["404"] = new { description = "Requested resource does not exist" },
                ["409"] = new { description = "Request conflicts with current state" },
                ["410"] = new { description = "Requested snapshot is no longer retained" },
                ["422"] = new { description = "Query capability is unavailable" },
                ["500"] = new { description = "Database failure" }
            }
        };
        if (requestSchema is not null)
        {
            operation["requestBody"] = new
            {
                required = true,
                content = new Dictionary<string, object>
                {
                    ["application/json"] = new { schema = Reference(requestSchema) }
                }
            };
        }

        if (protectedOperation)
        {
            operation["security"] = new[] { new Dictionary<string, string[]> { ["bearerAuth"] = [] } };
        }

        return operation;
    }

    private static object Response(string description, string? schema) => schema is null
        ? new
        {
            description
        }
        : new
        {
            description,
            content = new Dictionary<string, object>
            {
                ["application/json"] = new { schema = Reference(schema) }
            }
        };

    private static Dictionary<string, string> Reference(string schema) => new() { ["$ref"] = $"#/components/schemas/{schema}" };

    private static Func<T1, T2, Task<IResult>> Execute<T1, T2>(Func<T1, T2, Task<IResult>> operation) =>
        async (first, second) => await MapExceptionAsync(() => operation(first, second)).ConfigureAwait(false);

    private static async Task<IResult> MapExceptionAsync(Func<Task<IResult>> operation)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (ArgumentException exception)
        {
            return Problem(StatusCodes.Status400BadRequest, "request_invalid", exception);
        }
        catch (SemanticSessionValidationException exception)
        {
            return Problem(StatusCodes.Status400BadRequest, "semantic_session_invalid", exception);
        }
        catch (SemanticSourceProgressConflictException exception)
        {
            return Problem(StatusCodes.Status409Conflict, "source_progress_conflict", exception);
        }
        catch (EntityForgeDbAdministrativeJobRejectedException exception)
        {
            return Problem(StatusCodes.Status429TooManyRequests, "administrative_job_limit", exception);
        }
        catch (EntityForgeDbAdministrativeJobTimeoutException exception)
        {
            return Problem(StatusCodes.Status504GatewayTimeout, "administrative_job_timeout", exception);
        }
        catch (EntityForgeDbException exception)
        {
            return Problem(StatusCodes.Status500InternalServerError, "database_failure", exception);
        }
    }

    private static async Task<IResult> AuthorizeAndExecuteAsync(HttpContext context,
        IEntityForgeDbAuthorizationPolicy authorization, EntityForgeDbAdministrativeOperation operation,
        Func<Task<IResult>> execute)
    {
        if (!await authorization.AuthorizeAsync(context, operation, context.RequestAborted).ConfigureAwait(false))
        {
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden,
                title: "administrative_operation_forbidden",
                extensions: new Dictionary<string, object?>
                {
                    ["contractVersion"] = 1,
                    ["code"] = "administrative_operation_forbidden",
                    ["retryable"] = false,
                    ["operation"] = operation.ToString()
                });
        }

        return await MapExceptionAsync(execute).ConfigureAwait(false);
    }

    private static IResult Problem(int status, string code, Exception exception) => Results.Problem(
        statusCode: status, title: code,
        detail: status < StatusCodes.Status500InternalServerError ? exception.Message : null,
        extensions: new Dictionary<string, object?>
        {
            ["contractVersion"] = 1,
            ["code"] = code,
            ["retryable"] = status is StatusCodes.Status408RequestTimeout or
                StatusCodes.Status429TooManyRequests or StatusCodes.Status502BadGateway or
                StatusCodes.Status503ServiceUnavailable or StatusCodes.Status504GatewayTimeout
        });
}
