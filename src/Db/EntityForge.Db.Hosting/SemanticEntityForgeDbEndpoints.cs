using EntityForge.Db.Semantic.Query;
using EntityForge.Db.Query.Adapters;
using EntityForge.Db.Semantic;
using EntityForge.Db.Semantic.Engine;
using EntityForge.Db.Client;
using EntityForge.Query.Projection;
using EntityForge.Query.Querying;
using EntityForge.Semantics.Protobuf;
using EntityForge.Semantics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace EntityForge.Db.Hosting;

internal static class SemanticEntityForgeDbEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        SemanticNativeQueryEndpoints.Map(endpoints);
        endpoints.MapPost("/v1/semantic/sessions", Execute(async (SemanticSessionTransportRequest request,
            EntityForgeDbRuntime runtime, CancellationToken cancellation) =>
        {
            SessionCommitResult result = await runtime.Database.Operations().ApplyAsync(Decode(request), cancellation)
                .ConfigureAwait(false);
            return Results.Ok(result);
        }));
        endpoints.MapPost("/v1/semantic/sessions/bulk", Execute(async (SemanticSessionTransportRequest request,
            EntityForgeDbRuntime runtime, CancellationToken cancellation) =>
        {
            SessionCommitResult result = await runtime.Database.BulkLoadAsync(Decode(request), cancellation)
                .ConfigureAwait(false);
            return Results.Ok(result);
        }));
        MapRevisions(endpoints);
        endpoints.MapPost("/v1/semantic/snapshot-leases", Execute(async (SnapshotLeaseRequest request,
            EntityForgeDbRuntime runtime, CancellationToken cancellation) => Results.Ok(
                SnapshotLeaseResponseMapper.Map(await runtime.Database.AcquireSnapshotLeaseAsync(
                    SnapshotLeaseResponseMapper.Duration(request.DurationSeconds), cancellation)
                    .ConfigureAwait(false)))));
        endpoints.MapGet("/v1/semantic/snapshot-leases/{leaseId:guid}", GetLeaseAsync);
        endpoints.MapPut("/v1/semantic/snapshot-leases/{leaseId:guid}", RenewLeaseAsync);
        endpoints.MapDelete("/v1/semantic/snapshot-leases/{leaseId:guid}", ReleaseLeaseAsync);
        endpoints.MapGet("/v1/semantic/changes", ReadChangesAsync);
        endpoints.MapPost("/v1/semantic/entities/query", Execute(async (PortableEntityQueryRequest request,
            EntityForgeDbRuntime runtime, CancellationToken cancellation) =>
        {
            var queries = new SemanticEntityQueryApplicationService(runtime.Database);
            CompiledHostedEntityQuery query = await queries.CompileAsync(request, cancellation).ConfigureAwait(false);
            HostedEntityQueryResult result = await queries.ExecuteAsync(query, cancellation).ConfigureAwait(false);
            return Results.Ok(EntityProjectionTransport.Encode(result.ToEntityProjectionResult()));
        }));
        endpoints.MapPost("/v1/semantic/entities/explain", Execute(async (PortableEntityQueryExplainRequest request,
            EntityForgeDbRuntime runtime, CancellationToken cancellation) =>
        {
            ArgumentNullException.ThrowIfNull(request.Query);
            var queries = new SemanticEntityQueryApplicationService(runtime.Database);
            CompiledHostedEntityQuery query = await queries.CompileAsync(request.Query, cancellation)
                .ConfigureAwait(false);
            return Results.Ok(await queries.ExplainAsync(query, request.Analyze, cancellation).ConfigureAwait(false));
        }));
        endpoints.MapPost("/v1/semantic/aggregations/query", Execute(async (PortableSemanticAggregationRequest request,
            EntityForgeDbRuntime runtime, CancellationToken cancellation) =>
        {
            ArgumentNullException.ThrowIfNull(request.Source);
            var queries = new SemanticEntityQueryApplicationService(runtime.Database);
            CompiledHostedEntityQuery source = await queries.CompileAsync(request.Source, cancellation)
                .ConfigureAwait(false);
            SemanticAggregationQuery query = PortableSemanticAggregationMapper.Compile(request, source);
            SemanticAggregationResult result = await runtime.Database.AggregateEntitiesAsync(query,
                source.RequestedSnapshot, cancellation).ConfigureAwait(false);
            return Results.Ok(PortableSemanticAggregationMapper.Map(result));
        }));
        endpoints.MapPost("/v1/semantic/aggregations/explain", Execute(async (
            PortableSemanticAggregationExplainRequest request, EntityForgeDbRuntime runtime,
            CancellationToken cancellation) =>
        {
            ArgumentNullException.ThrowIfNull(request.Query);
            var queries = new SemanticEntityQueryApplicationService(runtime.Database);
            CompiledHostedEntityQuery source = await queries.CompileAsync(request.Query.Source, cancellation)
                .ConfigureAwait(false);
            SemanticAggregationQuery query = PortableSemanticAggregationMapper.Compile(request.Query, source);
            return Results.Ok(await runtime.Database.ExplainAggregationAsync(query, request.Analyze,
                source.RequestedSnapshot, cancellation).ConfigureAwait(false));
        }));
        endpoints.MapPost("/v1/semantic/entities/invalidation", Execute(async (
            SemanticEntityQueryInvalidationRequest request, EntityForgeDbRuntime runtime,
            CancellationToken cancellation) =>
        {
            ArgumentNullException.ThrowIfNull(request.Query);
            var queries = new SemanticEntityQueryApplicationService(runtime.Database);
            CompiledHostedEntityQuery query = await queries.CompileAsync(request.Query, cancellation)
                .ConfigureAwait(false);
            if (request.ObservedSnapshotVersion <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(request.ObservedSnapshotVersion));
            }

            DatabaseChangeCursor? cursor = string.IsNullOrWhiteSpace(request.Cursor)
                ? null : new DatabaseChangeCursor(request.Cursor);
            SemanticEntityQueryInvalidation result = await queries.CheckInvalidationAsync(
                query, new(request.ObservedSnapshotVersion), cursor, request.MaximumChanges, cancellation)
                .ConfigureAwait(false);
            return Results.Ok(SemanticEntityQueryInvalidationResponseMapper.Map(result));
        }));
        endpoints.MapPost("/v1/semantic/entities/invalidation/stream", SemanticEntityQueryEventStream.WriteAsync);
    }

    private static SemanticSessionEnvelope Decode(SemanticSessionTransportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Start);
        ArgumentNullException.ThrowIfNull(request.Pages);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Close);
        var codec = new SemanticChangeSessionTransportCodec();
        try
        {
            return new(codec.DeserializeStart(Convert.FromBase64String(request.Start)),
                request.Pages.Select(item => codec.DeserializePage(Convert.FromBase64String(item))).ToArray(),
                codec.DeserializeClose(Convert.FromBase64String(request.Close)));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new SemanticSessionValidationException("The HTTP semantic-session transport is invalid.", exception);
        }
    }

    private static SemanticChangeSessionStart DecodeStart(string payload) => DecodeMessage(payload,
        (codec, value) => codec.DeserializeStart(value), "start");

    private static SemanticChangeSessionPage DecodePage(string payload) => DecodeMessage(payload,
        (codec, value) => codec.DeserializePage(value), "page");

    private static SemanticChangeSessionClose DecodeClose(string payload) => DecodeMessage(payload,
        (codec, value) => codec.DeserializeClose(value), "close");

    private static T DecodeMessage<T>(string payload,
        Func<SemanticChangeSessionTransportCodec, byte[], T> decode, string kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        try
        {
            return decode(new(), Convert.FromBase64String(payload));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new SemanticSessionValidationException($"The HTTP semantic contribution {kind} is invalid.", exception);
        }
    }

    private static DatabaseChangeSelector Selector(SemanticChangePageRequest request)
    {
        bool hasKind = !string.IsNullOrWhiteSpace(request.RoutingKind);
        bool hasValue = !string.IsNullOrWhiteSpace(request.RoutingValue);
        if (hasKind != hasValue)
        {
            throw new ArgumentException("routingKind and routingValue must be supplied together.");
        }

        return hasKind
            ? new(DatabaseProfileId.Semantic,
                [new CommittedChangeRoutingKey(request.RoutingKind!, request.RoutingValue!)])
            : new(DatabaseProfileId.Semantic);
    }

    internal static void MapRevisions(IEndpointRouteBuilder endpoints)
    {
        var revisions = endpoints.MapGroup("/v1/semantic/contribution-revisions").AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var runtime = http.RequestServices.GetRequiredService<HostedEntityApplications>();
            var actual = runtime.DatabaseIncarnation;
            if (!Guid.TryParse(http.Request.Query["databaseIncarnation"], out var expected) || expected != actual ||
                http.Request.Query["expectedProfile"] != DatabaseProfileId.Semantic.ToString())
            {
                return Results.Conflict(new
                {
                    code = "revision_destination_mismatch"
                });
            }

            http.Response.Headers["X-EntityForge-Database-Incarnation"] = actual.ToString("D");
            http.Response.Headers["X-EntityForge-Profile"] = DatabaseProfileId.Semantic.ToString();
            return await next(context);
        });
        revisions.MapGet("/published", GetPublishedRevisionAsync);
        revisions.MapGet("/{generationId:guid}", GetRevisionAsync);
        revisions.MapPost("/begin", Execute(async (
            SemanticContributionRevisionBeginRequest request, HostedEntityApplications runtime,
            CancellationToken cancellation) =>
        {
            var start = DecodeStart(request.Start);
            return RevisionReceipt(start.SessionId, await runtime.Semantic.Revisions.BeginRevisionAsync(
                start, request.ExpectedPublishedRevision, cancellation).ConfigureAwait(false));
        }));
        revisions.MapPost("/parts", Execute(async (
            SemanticContributionRevisionPartRequest request, HostedEntityApplications runtime,
            CancellationToken cancellation) =>
        {
            var page = DecodePage(request.Page);
            return RevisionReceipt(page.SessionId, await runtime.Semantic.Revisions.AcceptRevisionPartAsync(page, cancellation).ConfigureAwait(false));
        }));
        revisions.MapPost("/seal", Execute(async (
            SemanticContributionRevisionSealRequest request, HostedEntityApplications runtime,
            CancellationToken cancellation) =>
        {
            var close = DecodeClose(request.Close);
            return RevisionReceipt(close.SessionId, await runtime.Semantic.Revisions.SealRevisionAsync(close, cancellation).ConfigureAwait(false));
        }));
        revisions.MapPost("/{generationId:guid}/publish", PublishRevisionAsync);
        revisions.MapPost("/{generationId:guid}/abandon", AbandonRevisionAsync);
    }

    private static Task<IResult> GetPublishedRevisionAsync(string sourceSystem, string sourcePartition,
        HostedEntityApplications runtime, CancellationToken cancellation) => EntityForgeDbHttpErrors.MapAsync(async () =>
        Results.Ok(new
        {
            Scope = new SemanticContributionScope(new Uri(sourceSystem), new Uri(sourcePartition)),
            Revision =
            await runtime.Semantic.Revisions.GetPublishedRevisionAsync(
                new(new Uri(sourceSystem), new Uri(sourcePartition)), cancellation).ConfigureAwait(false)
        }));

    private static Task<IResult> GetRevisionAsync(Guid generationId, HostedEntityApplications runtime,
        CancellationToken cancellation) => EntityForgeDbHttpErrors.MapAsync(async () =>
        Results.Ok(new
        {
            GenerationId = generationId,
            Revision =
            await runtime.Semantic.Revisions.GetRevisionAsync(generationId, cancellation).ConfigureAwait(false)
        }));

    private static Task<IResult> PublishRevisionAsync(Guid generationId, HostedEntityApplications runtime,
        CancellationToken cancellation) => EntityForgeDbHttpErrors.MapAsync(async () => RevisionReceipt(generationId,
            await runtime.Semantic.Revisions.PublishRevisionAsync(generationId, cancellation).ConfigureAwait(false)));

    private static Task<IResult> AbandonRevisionAsync(Guid generationId, HostedEntityApplications runtime,
        CancellationToken cancellation) => EntityForgeDbHttpErrors.MapAsync(async () => RevisionReceipt(generationId,
            await runtime.Semantic.Revisions.AbandonRevisionAsync(generationId, cancellation).ConfigureAwait(false)));

    private static IResult RevisionReceipt(Guid generationId, DatabaseCommitReceipt receipt) => Results.Ok(new { GenerationId = generationId, Receipt = receipt });

    private static Task<IResult> ReadChangesAsync([AsParameters] SemanticChangePageRequest request,
        EntityForgeDbRuntime runtime, CancellationToken cancellation) => EntityForgeDbHttpErrors.MapAsync(async () =>
    {
        DatabaseChangeCursor? cursor = string.IsNullOrWhiteSpace(request.Cursor)
            ? null
            : new DatabaseChangeCursor(request.Cursor);
        DatabaseChangeSelector selector = Selector(request);
        SemanticCommittedChangePage page = await runtime.Database.ReadSemanticChangePageAfterAsync(
            cursor, selector, request.MaximumCount, cancellation).ConfigureAwait(false);
        return Results.Ok(SemanticChangeFeedResponseMapper.Map(page));
    });

    private static Task<IResult> RenewLeaseAsync(Guid leaseId, SnapshotLeaseRenewalRequest request,
        EntityForgeDbRuntime runtime, CancellationToken cancellation) => EntityForgeDbHttpErrors.MapAsync(async () =>
    {
        SnapshotLease lease = await runtime.Database.RenewSnapshotLeaseAsync(leaseId,
            request.ExpectedRevision, SnapshotLeaseResponseMapper.Duration(request.DurationSeconds), cancellation)
            .ConfigureAwait(false);
        return Results.Ok(SnapshotLeaseResponseMapper.Map(lease));
    });

    private static Task<IResult> GetLeaseAsync(Guid leaseId, EntityForgeDbRuntime runtime,
        CancellationToken cancellation) => EntityForgeDbHttpErrors.MapAsync(async () => Results.Ok(
            SnapshotLeaseResponseMapper.Map(await runtime.Database.GetSnapshotLeaseAsync(
                leaseId, cancellation).ConfigureAwait(false))));

    private static Task<IResult> ReleaseLeaseAsync(Guid leaseId, EntityForgeDbRuntime runtime,
        CancellationToken cancellation) => EntityForgeDbHttpErrors.MapAsync(async () =>
    {
        await runtime.Database.ReleaseSnapshotLeaseAsync(leaseId, cancellation).ConfigureAwait(false);
        return Results.NoContent();
    });

    private static Func<T1, T2, Task<IResult>> Execute<T1, T2>(Func<T1, T2, Task<IResult>> operation) =>
        async (first, second) => await EntityForgeDbHttpErrors.MapAsync(() => operation(first, second)).ConfigureAwait(false);

    private static Func<T1, T2, T3, Task<IResult>> Execute<T1, T2, T3>(Func<T1, T2, T3, Task<IResult>> operation) =>
        async (first, second, third) => await EntityForgeDbHttpErrors.MapAsync(
            () => operation(first, second, third)).ConfigureAwait(false);

}
