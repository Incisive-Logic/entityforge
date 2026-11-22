using EntityForge.Db.Query.Adapters;
using EntityForge.Db.Graph;
using EntityForge.Db.Gql;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Query.Planning;
using EntityForge.Db.Semantic;
using EntityForge.Db.Rdf;
using EntityForge.Db.Semantic.Engine;
using EntityForge.Db;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using System.Text.Json;
using EntityForge.Query.Results;
using EntityForge.Query.Querying;
using EntityForge.Query.Projection;

namespace EntityForge.Db.Hosting;

internal static class GraphEntityForgeDbEndpoints
{
    private const int MaximumBatchMutations = 100_000;

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        ManagedGqlGraphEndpoints.Map(endpoints);
        endpoints.MapPost("/v1/graph/batches", (GraphBatchWriteRequest request,
            EntityForgeDbRuntime runtime, CancellationToken cancellation) => EntityForgeDbHttpErrors.MapAsync(async () =>
        {
            GraphMutation[] mutations = Mutations(request).ToArray();
            if (mutations.Length == 0)
            {
                throw new ArgumentException("A graph batch must contain at least one mutation.", nameof(request));
            }

            if (mutations.Length > MaximumBatchMutations)
            {
                throw new ArgumentException($"A graph batch cannot exceed {MaximumBatchMutations:N0} mutations.",
                    nameof(request));
            }

            DatabaseCommitReceipt receipt = await runtime.GraphDatabase.CommitWithReceiptAsync(
                new(request.TransactionId, mutations), cancellation).ConfigureAwait(false);
            return Results.Ok(new
            {
                version = receipt.Version.Value,
                mutationCount = mutations.Length,
                receipt
            });
        }));
        endpoints.MapPost("/v1/graph/vertices", (GraphVertexWriteRequest request,
            EntityForgeDbRuntime runtime, CancellationToken cancellation) => EntityForgeDbHttpErrors.MapAsync(async () =>
        {
            var id = new GraphVertexId(request.Id);
            DatabaseVersion version = await runtime.GraphDatabase.CommitAsync(new(Guid.CreateVersion7(),
                [new CreateVertex(id, request.Labels)]), cancellation).ConfigureAwait(false);
            return Results.Ok(new
            {
                version = version.Value,
                id = id.Value
            });
        }));
        endpoints.MapPost("/v1/graph/edges", (GraphEdgeWriteRequest request,
            EntityForgeDbRuntime runtime, CancellationToken cancellation) => EntityForgeDbHttpErrors.MapAsync(async () =>
        {
            DatabaseVersion version = await runtime.GraphDatabase.CommitAsync(new(Guid.CreateVersion7(),
                [new CreateEdge(new(request.Id), new(request.From), new(request.To), request.Label)]), cancellation)
                .ConfigureAwait(false);
            return Results.Ok(new
            {
                version = version.Value,
                id = request.Id
            });
        }));
        endpoints.MapGet("/v1/graph/vertices/{id:guid}", (Guid id, EntityForgeDbRuntime runtime) =>
            runtime.GraphDatabase.GetVertex(new(id)) is { } vertex
                ? Results.Ok(GraphHttpResponseMapper.Map(vertex)) : Results.NotFound());
        endpoints.MapGet("/v1/graph/vertices/{id:guid}/outgoing", (Guid id, string? label,
            EntityForgeDbRuntime runtime) => Results.Ok(runtime.GraphDatabase.Outgoing(new GraphVertexId(id), label)
                .Select(GraphHttpResponseMapper.Map)));
        endpoints.MapGet("/v1/graph/vertices/{id:guid}/incoming", (Guid id, string? label,
            EntityForgeDbRuntime runtime) => Results.Ok(runtime.GraphDatabase.Incoming(new GraphVertexId(id), label)
                .Select(GraphHttpResponseMapper.Map)));
        endpoints.MapPost("/v1/graph/vertices/{id:guid}/properties", (Guid id,
            GraphPropertyWriteRequest request, EntityForgeDbRuntime runtime, CancellationToken cancellation) =>
            EntityForgeDbHttpErrors.MapAsync(async () =>
            {
                DatabaseVersion version = await runtime.GraphDatabase.CommitAsync(new(Guid.CreateVersion7(),
                    [new SetVertexProperty(new(id), request.Name, GraphPropertyJson.Map(request.Value))]),
                    cancellation).ConfigureAwait(false);
                return Results.Ok(new
                {
                    version = version.Value,
                    id,
                    property = request.Name
                });
            }));
        endpoints.MapPost("/v1/graph/edges/{id:guid}/properties", (Guid id,
            GraphPropertyWriteRequest request, EntityForgeDbRuntime runtime, CancellationToken cancellation) =>
            EntityForgeDbHttpErrors.MapAsync(async () =>
            {
                DatabaseVersion version = await runtime.GraphDatabase.CommitAsync(new(Guid.CreateVersion7(),
                    [new SetEdgeProperty(new(id), request.Name, GraphPropertyJson.Map(request.Value))]),
                    cancellation).ConfigureAwait(false);
                return Results.Ok(new
                {
                    version = version.Value,
                    id,
                    property = request.Name
                });
            }));
        endpoints.MapPost("/v1/graph/gql/query", (GraphGqlQueryRequest request,
            EntityForgeDbRuntime runtime, HttpContext context, CancellationToken cancellation) => EntityForgeDbHttpErrors.MapAsync(async () =>
        {
            string statement = GraphGqlStandardSyntax.BindSessionUser(runtime.GqlProcedures?.Expand(request.Query) ?? request.Query,
                GraphGqlSessionUser.FromRequest(context));
            var (database, selectedStatement, lifetime) = runtime.SelectGqlGraph(statement);
            using var storeLifetime = lifetime;
            CompiledGraphGqlQuery compiled = new GraphGqlQueryCompiler().CompileStandard(request with
            {
                Query = selectedStatement
            });
            using var lease = await database.AcquireSnapshotAsync(
                compiled.SnapshotVersion is { } value ? new(value) : null, cancellation).ConfigureAwait(false);
            DatabaseVersion snapshot = lease.Version;
            ValueTask<QueryResultEnvelope> Execute(CompiledGraphGqlQuery branch) => database.QueryProjectionAsync(
                branch.Query, snapshot, branch.Projection is null ? null : property =>
                    GraphGqlPropertyProjectionShaper.Project(branch.Projection, property), request.IncludeSupportingGraph, cancellation, branch.StartCandidates);
            QueryResultEnvelope result = await compiled.ExecuteAsync(Execute, cancellation).ConfigureAwait(false);
            result = GraphGqlCompletion.Complete(result, compiled.OmitsResult);
            if (!request.IncludeSupportingGraph && (compiled.Union is not null || compiled.Pipeline is not null && !compiled.ReturnsEntityGraph || compiled.Projection is not null || result.Result is RecordResult))
            {
                return await BoundedQueryJsonResponse.CreateAsync(GraphPatternQueryResponseMapper.Project(result) with
                {
                    SnapshotVersion = snapshot.Value
                }, context, cancellation).ConfigureAwait(false);
            }

            return await BoundedQueryJsonResponse.CreateAsync(GraphPatternQueryResponseMapper.Map(result, snapshot.Value),
                context, cancellation).ConfigureAwait(false);
        }));
        endpoints.MapPost("/v1/graph/gql/update", (GraphGqlMutationRequest request,
            EntityForgeDbRuntime runtime, HttpContext context, CancellationToken cancellation) => EntityForgeDbHttpErrors.MapAsync(async () =>
        {
            var (database, statement, lifetime) = runtime.SelectGqlGraph(runtime.GqlProcedures?.Expand(request.Statement) ?? request.Statement);
            using var storeLifetime = lifetime;
            var result = await GraphGqlUpdateExecution.ApplyAsync(database, request with
            {
                Statement = statement
            }, cancellation, null,
                GraphGqlSessionUser.FromRequest(context)).ConfigureAwait(false);
            return Results.Ok(new
            {
                version = result.Version.Value,
                mutationCount = result.MutationCount,
                result = result.Result
            });
        }));
        endpoints.MapPost("/v1/graph/gql/query/scan", QueryScanEndpoints.GqlAsync);
        endpoints.MapGet("/v1/graph/gql/capabilities", () => Results.Ok(new GraphGqlQueryCapabilities(
            "preview", ["MATCH", "OPTIONAL MATCH", "RETURN", "INSERT", "SET", "REMOVE", "DELETE", "FOR", "FILTER", "LET", "NEXT", "FINISH"],
            ["ISO GQL grammar validation with bounded parsing",
                "named nodes, directed relationships and label predicates",
                "bounded quantified relationships and path selection",
                "standalone repeated one-edge groups with anonymous unconstrained boundary nodes",
                "finite repeated chains with optional endpoint anchors, node/edge group lists and per-iteration predicates",
                "unbounded flat repeated chains with node/edge group lists and per-iteration predicates",
                "repeated endpoint bindings from MATCH, LET, FOR, CALL imports and correlated VALUE/EXISTS queries",
                "mixed fixed factors and sequences of simple one-edge repeated groups",
                "multiple multi-edge repeated groups with optional scalar anchors, quantified relationships and whole-path modes",
                "anonymous repeated paths and non-selective multi-path matches with whole-match edge uniqueness",
                "independently selected multi-path matches under explicit REPEATABLE ELEMENTS with singleton joins after selection",
                "DIFFERENT RELATIONSHIPS and REPEATABLE ELEMENTS (finite, or a single unbounded TRAIL/ACYCLIC/SIMPLE path)",
                "unbounded repeatable ANY SHORTEST and ALL SHORTEST walks over one quantified relationship with a simple label",
                "named paths, PATH_LENGTH and ELEMENTS",
                "named unquantified parenthesized groups covering the complete path",
                "inner path modes on complete unquantified groups with no conflicting outer mode",
                "whole-path group predicates before ANY/SHORTEST selection for patterns with node endpoints",
                "WALK, TRAIL, ACYCLIC and SIMPLE modes on supported connected paths",
                "COUNT, SUM, MIN, MAX, AVG and COLLECT_LIST",
                "STDDEV_SAMP, STDDEV_POP, PERCENTILE_CONT and PERCENTILE_DISC",
                "scalar projections, grouping, ordering, DISTINCT and paging",
                "USE CURRENT_GRAPH and USE CURRENT_PROPERTY_GRAPH within the request graph",
                "configured local named graphs and HOME_GRAPH, with one physical graph per request",
                "managed open-graph CREATE/DROP through the administrative entrance when managed storage is configured",
                "SESSION_USER bound to the request authorization identifier",
                "per-key NULLS FIRST/LAST and ASCENDING/DESCENDING sort directions",
                "CASE including label, normalization, direction and endpoint predicates; COALESCE and NULLIF",
                "three-valued Boolean expressions and truth tests",
                "numeric functions including LOG(base, argument) and LN",
                "GQL syntax and selected numeric/list data-exception status codes",
                "CAST for matching target types, integer-to-decimal, integer/decimal-to-float and exact decimal/float-to-integer or float-to-decimal conversion, recursively in lists",
                "Boolean label expressions on named nodes and fixed-length named relationships",
                "IS DIRECTED, IS SOURCE OF and IS DESTINATION OF graph predicates",
                "plain and nested parenthesized path groups with WHERE predicates",
                "element WHERE predicates, including supported fixed selective paths and finite repeated chains",
                "Unicode character functions and normalization",
                "list and record expressions within supported execution limits",
                "FOR IN list unnesting with ORDINALITY or OFFSET, FILTER and LET",
                "NEXT composition across linear and named-column UNION queries, YIELD selection and renaming, FINISH",
                "UNION, UNION DISTINCT and UNION ALL",
                "CALL subqueries, correlated EXISTS and single-column VALUE scalar subqueries",
                "registered named read and write procedures with positional arguments and YIELD aliases",
                "temporal values, date/duration literals, captured statement clocks and property storage",
                "qualified duration type predicates and matching-type casts, preserving zero-value family identity",
                "atomic INSERT, SET, REMOVE and DELETE with durable retries; SET reads its statement input state",
                "ELEMENT_ID and property inspection predicates"],
            ["complete ISO/IEC 39075 conformance",
                "Cypher-only statements, functions and operators",
                "session, schema and graph-type language commands; general catalog command composition",
                "explicit transaction language commands",
                "full quantified multi-element path patterns",
                "undirected stored relationships",
                "selective pattern predicates with element imports from other scopes or unsupported path composition",
                "CAST string parsing, non-identity temporal conversion and union-target conversion branch selection",
                "inline subqueries in mutation property expressions",
                "temporal component lookup through property notation",
                "duration casts between year-to-month and day-to-second families",
                "procedure catalog commands, typed/output parameters, recursive named procedures and cross-graph requests"],
            new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["maximumQueryCharacters"] = GraphGqlQueryCompiler.MaximumQueryCharacters,
                ["maximumResponseBytes"] = BoundedQueryJsonResponse.MaximumBytes,
                ["maximumPatternClauses"] = GraphGqlQueryCompiler.MaximumPatternClauses,
                ["maximumCallDepth"] = 8,
                ["maximumCallInvocations"] = 1_000,
                ["maximumWhereTokens"] = 256,
                ["maximumParameters"] = 32,
                ["maximumPredicates"] = GraphGqlQueryCompiler.MaximumPredicates
                ,
                ["maximumAggregateInputRows"] = GraphGqlQueryCompiler.MaximumAggregateInputRows,
                ["maximumSpecifiedHops"] = GraphPatternLimits.MaximumSpecifiedHops,
                ["maximumMaterializedPathElements"] = GraphPatternLimits.MaximumMaterializedPathElements,
                ["maximumResultRows"] = GraphGqlQueryCompiler.MaximumAggregateInputRows,
                ["maximumMatchesAndOffset"] = EntityTraversalLimits.MaximumExpandedEntities,
                ["defaultMaximumExpansions"] = EntityTraversalLimits.DefaultMaximumExpandedEntities
            })));
        endpoints.MapPost("/v1/graph/gql/explain", (GraphGqlExplainRequest request,
            EntityForgeDbRuntime runtime, HttpContext context, CancellationToken cancellation) => EntityForgeDbHttpErrors.MapAsync(async () =>
        {
            string statement = GraphGqlStandardSyntax.BindSessionUser(runtime.GqlProcedures?.Expand(request.Query.Query) ?? request.Query.Query,
                GraphGqlSessionUser.FromRequest(context));
            var (database, selectedStatement, lifetime) = runtime.SelectGqlGraph(statement);
            using var storeLifetime = lifetime;
            CompiledGraphGqlQuery compiled = new GraphGqlQueryCompiler(DateTimeOffset.UtcNow).Compile(request.Query with
            {
                Query = GraphGqlStandardSyntax.PrepareParameterized(selectedStatement, request.Query.Parameters)
            });
            if (compiled.Pipeline is not null || compiled.Union is not null)
            {
                using var lease = await database.AcquireSnapshotAsync(
                    compiled.SnapshotVersion is { } requested ? new(requested) : null, cancellation).ConfigureAwait(false);
                DatabaseVersion captured = lease.Version;
                async ValueTask<object> Plan(CompiledGraphGqlQuery query)
                {
                    if (query.Union is { } union)
                    {
                        var branches = new List<object>();
                        foreach (var branch in union.Branches)
                        {
                            branches.Add(await Plan(branch).ConfigureAwait(false));
                        }
                        return new
                        {
                            operation = union.AllConjunctions.All(all => all) ? "UNION ALL" :
                                union.AllConjunctions.All(all => !all) ? "UNION DISTINCT" : "UNION",
                            conjunctions = union.AllConjunctions.Select(all => all ? "ALL" : "DISTINCT").ToArray(),
                            branches
                        };
                    }
                    var seed = await database.ExplainAsync(query.Query, false, captured, cancellation).ConfigureAwait(false);
                    var stages = new List<object>();
                    foreach (var stage in query.Pipeline?.Stages ?? [])
                    {
                        var valueSubqueries = new List<object>();
                        foreach (var predicate in stage.ValueSubqueries ?? [])
                        {
                            valueSubqueries.Add(await Plan(predicate.CallPlan!).ConfigureAwait(false));
                        }
                        var components = new List<object>();
                        foreach (var component in stage.MatchComponents ?? [])
                        {
                            components.Add(await Plan(component.Plan).ConfigureAwait(false));
                        }
                        stages.Add(new
                        {
                            operation = stage.Operation,
                            distinct = stage.Distinct,
                            count = stage.Operation is "LIMIT" or "SKIP" ? (int?)stage.Count : null,
                            plan = (stage.MatchPlan ?? stage.CallPlan) is { } match ? await Plan(match).ConfigureAwait(false) : null,
                            valueSubqueries,
                            components
                        });
                    }
                    var relationshipRanges = query.Query.Relationships.Select(relationship => new
                    {
                        binding = relationship.Binding,
                        minimumHops = relationship.Hops.Minimum,
                        maximumHops = relationship.Hops.Maximum
                    }).ToArray();
                    return new
                    {
                        operation = "ROW PIPELINE",
                        singletonSeed = query.Pipeline?.SingletonSeed ?? false,
                        seed,
                        relationshipRanges,
                        stages
                    };
                }
                object plan = await Plan(compiled).ConfigureAwait(false);
                int? actualMatches = null;
                if (request.Analyze)
                {
                    ValueTask<QueryResultEnvelope> Execute(CompiledGraphGqlQuery branch) => database.QueryProjectionAsync(
                        branch.Query, captured, branch.Projection is null ? null : property =>
                            GraphGqlPropertyProjectionShaper.Project(branch.Projection, property), request.Query.IncludeSupportingGraph, cancellation, branch.StartCandidates);
                    var result = await compiled.ExecuteAsync(Execute, cancellation).ConfigureAwait(false);
                    actualMatches = ((GraphPatternResult)result.Result).Matches.Count;
                }
                return Results.Ok(new
                {
                    snapshotVersion = captured.Value,
                    plan,
                    analyzed = request.Analyze,
                    actualMatches
                });
            }
            DatabaseVersion? snapshot = compiled.SnapshotVersion is { } value ? new(value) : null;
            return Results.Ok(await database.ExplainAsync(
                compiled.Query, request.Analyze, snapshot, cancellation).ConfigureAwait(false));
        }));
    }

    private static IEnumerable<GraphMutation> Mutations(GraphBatchWriteRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        foreach (GraphBatchVertexWriteRequest vertex in request.Vertices)
        {
            var id = new GraphVertexId(vertex.Id);
            yield return new CreateVertex(id, vertex.Labels);
            foreach ((string name, JsonElement value) in vertex.Properties ??
                new Dictionary<string, JsonElement>(StringComparer.Ordinal))
            {
                yield return new SetVertexProperty(id, name, GraphPropertyJson.Map(value));
            }
        }
        foreach (GraphBatchEdgeWriteRequest edge in request.Edges)
        {
            var id = new GraphEdgeId(edge.Id);
            yield return new CreateEdge(id, new(edge.From), new(edge.To), edge.Label);
            foreach ((string name, JsonElement value) in edge.Properties ??
                new Dictionary<string, JsonElement>(StringComparer.Ordinal))
            {
                yield return new SetEdgeProperty(id, name, GraphPropertyJson.Map(value));
            }
        }
    }


}

internal sealed record GraphDatabaseValueResponse(string Kind, object Value,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Qualifier = null);

internal sealed record GraphVertexResponse(Guid Id, IReadOnlyCollection<string> Labels,
    IReadOnlyDictionary<string, GraphDatabaseValueResponse> Properties);

internal sealed record GraphEdgeResponse(Guid Id, Guid From, Guid To, string Label,
    IReadOnlyDictionary<string, GraphDatabaseValueResponse> Properties);

internal static class GraphHttpResponseMapper
{
    public static GraphVertexResponse Map(GraphVertex vertex) => new(vertex.Id.Value, vertex.Labels,
        vertex.Properties.ToDictionary(item => item.Key, item => Map(item.Value), StringComparer.Ordinal));

    public static GraphEdgeResponse Map(GraphEdge edge) => new(edge.Id.Value, edge.From.Value, edge.To.Value,
        edge.Label, edge.Properties.ToDictionary(item => item.Key, item => Map(item.Value), StringComparer.Ordinal));

    private static GraphDatabaseValueResponse Map(DatabaseValue value) => value switch
    {
        DatabaseValue.String item => new("string", item.Value),
        DatabaseValue.Int64 item => new("int64", item.Value),
        DatabaseValue.Double item => new("double", Double.IsFinite(item.Value) ? (object)item.Value :
            item.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        DatabaseValue.Boolean item => new("boolean", item.Value),
        DatabaseValue.DateTimeOffset item => new("dateTimeOffset", item.Value),
        DatabaseValue.Date item => new("date", item.Value.ToString()),
        DatabaseValue.Duration item => new("duration", item.Value.ToString(), item.Qualifier switch
        {
            DatabaseDurationQualifier.YearToMonth => "yearToMonth",
            DatabaseDurationQualifier.DayToSecond => "dayToSecond",
            _ => null
        }),
        DatabaseValue.Point item => new("point", item.Value.ToString()),
        DatabaseValue.LocalDateTime item => new("localDateTime", item.Value.ToString()),
        DatabaseValue.LocalTime item => new("localTime", item.Value.ToString()),
        DatabaseValue.Uuid item => new("uuid", item.Value.ToString("D")),
        DatabaseValue.Decimal item => new("decimal", item.Value),
        DatabaseValue.ZonedTime item => new("zonedTime", item.Value.ToString()),
        DatabaseValue.ZonedDateTime item => new("zonedDateTime", item.Value.ToString()),
        DatabaseValue.Uri item => new("uri", item.Value.AbsoluteUri),
        DatabaseValue.Binary item => new("binary", Convert.ToBase64String(item.Value.Span)),
        DatabaseValue.Json item => new("json", item.Value.ToJsonElement()),
        DatabaseValue.Array item => new("array", item.Value.Select(Map).ToArray()),
        _ => throw new ArgumentOutOfRangeException(nameof(value), value.Kind,
            "Unsupported graph database value kind.")
    };
}

internal static class EntityForgeDbHttpErrors
{
    public static async Task<IResult> MapAsync(Func<Task<IResult>> operation)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (ArgumentException exception)
        {
            return Problem(StatusCodes.Status400BadRequest, "request_invalid", exception);
        }
        catch (Exception exception) when (exception is FormatException or OverflowException)
        {
            return Problem(StatusCodes.Status400BadRequest, "request_invalid", exception);
        }
        catch (RdfSparqlRemoteExecutionException exception)
        {
            return Problem(StatusCodes.Status500InternalServerError, "rdf_sparql_remote_failure", exception);
        }
        catch (RdfGraphStorePreconditionException exception)
        {
            return Problem(StatusCodes.Status412PreconditionFailed, "graph_store_precondition_failed", exception);
        }
        catch (InvalidDataException exception)
        {
            return Problem(StatusCodes.Status400BadRequest, "document_invalid", exception);
        }
        catch (RdfMediaTypeException exception)
        {
            return Problem(StatusCodes.Status415UnsupportedMediaType, "rdf_media_type_unsupported", exception);
        }
        catch (RdfShaclValidationLimitException exception)
        {
            return Problem(StatusCodes.Status422UnprocessableEntity, "rdf_shacl_limit_exceeded", exception);
        }
        catch (RdfEntailmentLimitException exception)
        {
            return Problem(StatusCodes.Status422UnprocessableEntity, "rdf_entailment_limit_exceeded", exception);
        }
        catch (RdfSparqlWorkLimitException exception)
        {
            return Problem(StatusCodes.Status422UnprocessableEntity, "rdf_sparql_work_limit_exceeded", exception);
        }
        catch (QueryScanWorkLimitException exception)
        {
            return Problem(StatusCodes.Status422UnprocessableEntity, "query_scan_work_limit_exceeded", exception);
        }
        catch (QueryResponseSizeException exception)
        {
            return Problem(StatusCodes.Status422UnprocessableEntity, "query_response_size_exceeded", exception);
        }
        catch (QuerySortBudgetException exception)
        {
            return Problem(StatusCodes.Status422UnprocessableEntity, "query_scan_sort_budget_exceeded", exception);
        }
        catch (GraphPatternExpansionLimitException exception)
        {
            return Problem(StatusCodes.Status422UnprocessableEntity, "graph_expansion_limit_exceeded", exception);
        }
        catch (EntityQueryContinuationException exception)
        {
            return Problem(StatusCodes.Status400BadRequest, "entity_continuation_invalid", exception);
        }
        catch (EntityForgeQueryCapabilityException exception)
        {
            return Problem(StatusCodes.Status422UnprocessableEntity, "query_capability_unavailable", exception);
        }
        catch (EntityForgeQueryExpansionLimitException exception)
        {
            return Problem(StatusCodes.Status422UnprocessableEntity, "query_expansion_limit_exceeded", exception);
        }
        catch (EntityForgeQueryContinuationException exception) when
            (exception.Failure is EntityForgeQueryContinuationFailure.SnapshotExpired)
        {
            return Problem(StatusCodes.Status410Gone, "query_continuation_expired", exception);
        }
        catch (EntityForgeQueryContinuationException exception)
        {
            return Problem(StatusCodes.Status400BadRequest, "query_continuation_invalid", exception);
        }
        catch (EntityForgeChangeCursorExpiredException exception)
        {
            return Problem(StatusCodes.Status410Gone, "change_cursor_expired", exception);
        }
        catch (EntityForgeChangeCursorInvalidException exception)
        {
            return Problem(StatusCodes.Status400BadRequest, "change_cursor_invalid", exception);
        }
        catch (EntityForgeSnapshotUnavailableException exception)
        {
            return Problem(StatusCodes.Status410Gone, "snapshot_unavailable", exception);
        }
        catch (EntityForgeSnapshotLeaseException exception) when
            (exception.Failure is EntityForgeSnapshotLeaseFailure.Expired)
        {
            return Problem(StatusCodes.Status410Gone, "snapshot_lease_expired", exception);
        }
        catch (EntityForgeSnapshotLeaseException exception) when
            (exception.Failure is EntityForgeSnapshotLeaseFailure.NotFound)
        {
            return Problem(StatusCodes.Status404NotFound, "snapshot_lease_not_found", exception);
        }
        catch (EntityForgeSnapshotLeaseException exception)
        {
            return Problem(StatusCodes.Status409Conflict, "snapshot_lease_conflict", exception);
        }
        catch (EntityForgeGovernanceViolationException exception)
        {
            return Problem(StatusCodes.Status409Conflict, "governance_violation", exception);
        }
        catch (EntityForgeWriteConflictException exception)
        {
            return Problem(StatusCodes.Status409Conflict, "write_conflict", exception);
        }
        catch (Exception exception) when (exception is GraphWriteConflictException or EntityForge.Db.Rdf.Contracts.RdfWriteConflictException)
        {
            return Problem(StatusCodes.Status409Conflict, "write_conflict", exception);
        }
        catch (SemanticSourceProgressConflictException exception)
        {
            return Problem(StatusCodes.Status409Conflict, "source_progress_conflict", exception);
        }
        catch (SemanticContributionRevisionConflictException exception)
        {
            return ContributionRevisionConflict(exception);
        }
        catch (SemanticContributionRevisionException exception)
        {
            return Problem(StatusCodes.Status409Conflict, "contribution_revision_invalid_state", exception);
        }
        catch (NotSupportedException exception)
        {
            return Problem(StatusCodes.Status422UnprocessableEntity, "graph_query_capability_unavailable", exception);
        }
        catch (GraphSnapshotTooOldException exception)
        {
            return Problem(StatusCodes.Status410Gone, "graph_snapshot_unavailable", exception);
        }
        catch (RdfQueryContinuationException exception)
        {
            return Problem(StatusCodes.Status410Gone, "rdf_query_continuation_unavailable", exception);
        }
        catch (RdfQueryCapacityException exception)
        {
            return Problem(StatusCodes.Status429TooManyRequests, "rdf_query_capacity_exceeded", exception);
        }
        catch (RdfSnapshotTooOldException exception)
        {
            return Problem(StatusCodes.Status410Gone, "rdf_snapshot_unavailable", exception);
        }
        catch (SemanticSessionValidationException exception)
        {
            return Problem(StatusCodes.Status400BadRequest, "semantic_session_invalid", exception);
        }
        catch (EntityForgeDbIdempotencyException exception)
        {
            return Problem(StatusCodes.Status409Conflict, "idempotency_conflict", exception);
        }
        catch (EntityForgeDbException exception)
        {
            return Problem(StatusCodes.Status500InternalServerError, "database_failure", exception);
        }
    }

    private static IResult Problem(int status, string code, Exception exception)
    {
        var extensions = new Dictionary<string, object?>
        {
            ["contractVersion"] = 1,
            ["code"] = code,
            ["retryable"] = status is StatusCodes.Status408RequestTimeout or
                StatusCodes.Status429TooManyRequests or StatusCodes.Status502BadGateway or
                StatusCodes.Status503ServiceUnavailable or StatusCodes.Status504GatewayTimeout
        };
        if (exception.Data["EntityForge.GqlStatus"] is string gqlStatus)
        {
            extensions["gqlStatus"] = gqlStatus;
        }
        return Results.Problem(statusCode: status, title: code,
            detail: status < StatusCodes.Status500InternalServerError ? exception.Message : null, extensions: extensions);
    }

    private static IResult ContributionRevisionConflict(
        SemanticContributionRevisionConflictException exception) => Results.Problem(
        statusCode: StatusCodes.Status409Conflict, title: "contribution_revision_conflict",
        detail: exception.Message,
        extensions: new Dictionary<string, object?>
        {
            ["contractVersion"] = 1,
            ["code"] = "contribution_revision_conflict",
            ["retryable"] = false,
            ["generationId"] = exception.GenerationId,
            ["expected"] = exception.Expected,
            ["actual"] = exception.Actual
        });
}
