using EntityForge.Db.Semantic.Query;
using EntityForge.Db.Query.Adapters;
using EntityForge.Db.Gql;
using System.Runtime.CompilerServices;
using EntityForge.Db.Graph;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using EntityForge.Db.Semantic.Engine;
using EntityForge.Query.Querying;
using EntityForge.Query.Results;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace EntityForge.Db.Hosting;

internal static class SemanticNativeQueryEndpoints
{
    // At most one small immutable projection per live engine. Never retain a
    // storage lease or key by version alone across distinct physical states.
    private sealed class ViewCache
    {
        internal readonly SemaphoreSlim Gate = new(1, 1);
        internal CacheEntry? Entry;
    }

    private sealed record CacheEntry(WeakReference<object> State, SemanticNativeQueryView View);

    private static readonly ConditionalWeakTable<SemanticDatabaseEngine, ViewCache> Views = new();

    internal static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapMethods("/sparql", ["GET", "POST"], SparqlProtocolEndpoint.QueryAsync);
        endpoints.MapPost("/v1/semantic/sparql", (RdfSparqlQueryRequest request, EntityForgeDbRuntime runtime,
            HttpContext context, CancellationToken cancellation) => EntityForgeDbHttpErrors.MapAsync(async () =>
                await BoundedQueryJsonResponse.CreateAsync(await SparqlAsync(runtime, request.Query, cancellation),
                    context, cancellation)));
        endpoints.MapPost("/v1/semantic/gql/query", (GraphGqlQueryRequest request, EntityForgeDbRuntime runtime,
            HttpContext context, CancellationToken cancellation) => EntityForgeDbHttpErrors.MapAsync(async () =>
        {
            if (request.SnapshotVersion is not null)
            {
                throw new NotSupportedException("Semantic native queries currently capture the latest committed snapshot only.");
            }
            var compiled = new GraphGqlQueryCompiler().CompileStandard(request with
            {
                Query = GraphGqlStandardSyntax.BindSessionUser(request.Query, GraphGqlSessionUser.FromRequest(context))
            });
            if (!runtime.TryEnterQueryScan())
            {
                throw new NotSupportedException("The active query limit has been reached.");
            }
            try
            {
                var (view, version) = await CaptureAsync(runtime.Database, cancellation);
                var result = await GqlAsync(view, version, compiled, request.IncludeSupportingGraph, cancellation);
                if (!request.IncludeSupportingGraph && (compiled.Union is not null ||
                    compiled.Pipeline is not null && !compiled.ReturnsEntityGraph || compiled.Projection is not null || result.Result is RecordResult))
                {
                    return await BoundedQueryJsonResponse.CreateAsync(GraphPatternQueryResponseMapper.Project(result) with
                    {
                        SnapshotVersion = version.Value
                    }, context, cancellation);
                }
                return await BoundedQueryJsonResponse.CreateAsync(GraphPatternQueryResponseMapper.Map(result, version.Value), context, cancellation);
            }
            finally
            {
                runtime.LeaveQueryScan();
            }
        }));
        endpoints.MapGet("/v1/semantic/native-query/capabilities", () => Results.Ok(new
        {
            status = "preview",
            readOnly = true,
            inference = "asserted",
            maximumEvidence = SemanticNativeQueryView.MaximumEvidence,
            maximumPayloadBytes = SemanticNativeQueryView.MaximumBytes,
            mapping = SemanticNativeQueryView.Vocabulary,
            limitations = new[]
            {
                "Latest committed snapshot only", "Bounded in-memory view", "No named GQL graph selection or stored procedures",
                "No remote SPARQL SERVICE", "No distributed gateway integration", "No native-language updates"
            }
        }));
    }

    internal static async ValueTask<(SemanticNativeQueryView View, DatabaseVersion Version)> CaptureAsync(
        SemanticDatabaseEngine database, CancellationToken cancellation)
    {
        var cache = Views.GetValue(database, _ => new());
        // Warm readers need no rebuild admission. Publish the view and its exact
        // state identity together, and hold a short state lease while comparing.
        using (var current = await database.AcquireCurrentDistributedStateAsync(cancellation))
        {
            cancellation.ThrowIfCancellationRequested();
            if (Volatile.Read(ref cache.Entry) is { } entry && entry.State.TryGetTarget(out var state) &&
                ReferenceEquals(state, current.State))
            {
                return (entry.View, current.State.Version);
            }
        }
        await cache.Gate.WaitAsync(cancellation);
        try
        {
            cancellation.ThrowIfCancellationRequested();
            // Capture atomically after admission. Queued readers need the latest state
            // when they execute, not a separately pinned intermediate state per waiter.
            // A preceding reader may already have built this exact immutable view.
            using var retained = await database.AcquireCurrentDistributedStateAsync(cancellation);
            var cached = Volatile.Read(ref cache.Entry);
            if (cached is { } existing && existing.State.TryGetTarget(out var captured) && ReferenceEquals(captured, retained.State))
            {
                return (existing.View, retained.State.Version);
            }
            var previous = cached?.View;
            Volatile.Write(ref cache.Entry, null);
            var relation = await retained.CaptureQueryRelationAsync(SemanticNativeQueryView.MaximumEvidence, cancellation);
            var view = SemanticNativeQueryView.Create(relation.Fields, relation.Relationships, cancellation, previous);
            // Larger valid views remain request-scoped. Bounds cover both assertion
            // count and logical payload; the public 10,000/32 MiB query limits remain.
            if ((long)relation.Fields.Length + relation.Relationships.Length <= 4096 && view.PayloadBytes <= 8 * 1024 * 1024)
            {
                Volatile.Write(ref cache.Entry, new(new(retained.State), view));
            }
            return (view, retained.State.Version);
        }
        finally
        {
            cache.Gate.Release();
        }
    }

    internal static async ValueTask<RdfSparqlQueryResult> SparqlAsync(EntityForgeDbRuntime runtime,
        string query, CancellationToken cancellation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (!runtime.TryEnterQueryScan())
        {
            throw new NotSupportedException("The active query limit has been reached.");
        }
        try
        {
            var (view, version) = await CaptureAsync(runtime.Database, cancellation);
            return RdfSparql.ExecuteDataset(view.SparqlDataset(version), query, cancellation);
        }
        finally
        {
            runtime.LeaveQueryScan();
        }
    }

    internal static async ValueTask<QueryResultEnvelope> GqlAsync(SemanticNativeQueryView view, DatabaseVersion version,
        CompiledGraphGqlQuery compiled, bool supportingGraph, CancellationToken cancellation)
    {
        var graph = view.Graph;
        var executor = new BoundedGraphPatternExecutor(null, capturedView: graph);
        ValueTask<QueryResultEnvelope> Execute(CompiledGraphGqlQuery branch) => executor.ExecuteCapturedResultAsync(
            branch.Query, new(version, graph, graph.StatisticsFor(version)), cancellation,
            branch.Projection is null ? null : property => GraphGqlPropertyProjectionShaper.Project(branch.Projection, property),
            supportingGraph, branch.StartCandidates);
        return GraphGqlCompletion.Complete(await compiled.ExecuteAsync(Execute, cancellation), compiled.OmitsResult);
    }
}
