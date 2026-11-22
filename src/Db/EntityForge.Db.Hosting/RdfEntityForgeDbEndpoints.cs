using EntityForge.Db.Query.Adapters;
using System.Text.Json;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace EntityForge.Db.Hosting;

/// <summary>Requests one bounded, durably resumable RDF document page.</summary>
internal sealed record RdfDocumentPageRequest(Guid DocumentId, long PageNumber, IReadOnlyList<RdfQuad> Quads);

/// <summary>Requests an RDF quad-pattern query.</summary>
internal sealed record RdfQuadQueryRequest(RdfQuadPattern Pattern,
    int PageSize = 100, string? Continuation = null);

/// <summary>Requests execution of the supported bounded SPARQL surface.</summary>
internal sealed record RdfSparqlQueryRequest(string Query);

/// <summary>Requests one idempotent atomic bounded SPARQL Update.</summary>
internal sealed record RdfSparqlUpdateRequest(Guid TransactionId, string Update);

/// <summary>Requests one idempotent atomic set of RDF assertions and retractions.</summary>
internal sealed record RdfMutationBatchRequest(Guid TransactionId, IReadOnlyList<RdfMutationWriteRequest> Mutations);

/// <summary>Reports the durable version assigned to an RDF mutation batch.</summary>
internal sealed record RdfMutationBatchResponse(long Version, int MutationCount);

/// <summary>One committed RDF mutation set returned by the durable change feed.</summary>
internal sealed record RdfCommittedChangeResponse(long Version, Guid TransactionId, string Cursor,
    RdfCommittedChangeKind Kind, int EffectCount, IReadOnlyList<RdfMutation> Mutations);

/// <summary>Describes whether an RDF feed item is exact or requires consumers to refresh their view.</summary>
internal enum RdfCommittedChangeKind : byte
{
    MutationBatch = 1,
    DatasetReset = 2
}

/// <summary>One bounded page from the durable RDF change feed.</summary>
internal sealed record RdfChangePageResponse(IReadOnlyList<RdfCommittedChangeResponse> Changes,
    string? NextCursor, bool HasMore);

internal static class RdfEntityForgeDbEndpoints
{
    internal const int MaximumMutationBatchSize = 4_096;

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapMethods("/sparql", ["GET", "POST"], SparqlProtocolEndpoint.QueryAsync);
        endpoints.MapPost("/sparql/update", SparqlProtocolEndpoint.UpdateAsync);
        endpoints.MapGet("/sparql/update", SparqlProtocolEndpoint.DescriptionAsync);
        endpoints.MapMethods("/sparql/graphs", ["GET", "HEAD", "PUT", "POST", "DELETE"], SparqlProtocolEndpoint.GraphStoreAsync);
        endpoints.MapMethods("/sparql/graphs/{**graphId}", ["GET", "HEAD", "PUT", "POST", "DELETE"], SparqlProtocolEndpoint.GraphStoreAsync);
        endpoints.MapPost("/v1/rdf/transactions", (RdfMutationBatchRequest request,
            EntityForgeDbRuntime runtime, CancellationToken cancellation) => EntityForgeDbHttpErrors.MapAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(request);
            if (request.TransactionId == Guid.Empty)
            {
                throw new ArgumentException("An RDF transaction identity is required.", nameof(request));
            }

            if (request.Mutations is null || request.Mutations.Count is 0 or > MaximumMutationBatchSize)
            {
                throw new ArgumentOutOfRangeException(nameof(request),
                    $"An RDF transaction must contain 1-{MaximumMutationBatchSize} mutations.");
            }

            RdfMutation[] mutations = request.Mutations.Select(RdfMutationTransport.Map).ToArray();
            DatabaseVersion version = await runtime.RdfDatabase.CommitAsync(
                new(request.TransactionId, mutations), cancellation).ConfigureAwait(false);
            return Results.Ok(new RdfMutationBatchResponse(version.Value, request.Mutations.Count));
        }));

        endpoints.MapPost("/v1/rdf/document-pages", (RdfDocumentPageRequest request,
            EntityForgeDbRuntime runtime, CancellationToken cancellation) => EntityForgeDbHttpErrors.MapAsync(async () =>
        {
            RdfDocumentPageCommit result = await runtime.RdfDatabase.CommitDocumentPageAsync(
                new(request.DocumentId), new(request.PageNumber, request.Quads), cancellation).ConfigureAwait(false);
            return Results.Ok(result);
        }));

        endpoints.MapPost("/v1/rdf/import", ImportAsync);

        endpoints.MapPost("/v1/rdf/quads", (RdfQuadQueryRequest request, EntityForgeDbRuntime runtime,
            HttpContext context, CancellationToken cancellation) => EntityForgeDbHttpErrors.MapAsync(() =>
                QuadPageAsync(context, request, runtime, cancellation)));

        endpoints.MapDelete("/v1/rdf/quads/continuations/{continuation}", (string continuation,
            EntityForgeDbRuntime runtime, CancellationToken cancellation) => EntityForgeDbHttpErrors.MapAsync(async () =>
        {
            await runtime.RdfDatabase.ReleaseQueryContinuationAsync(continuation, cancellation).ConfigureAwait(false);
            return Results.NoContent();
        }));

        endpoints.MapPost("/v1/rdf/sparql", (RdfSparqlQueryRequest request, EntityForgeDbRuntime runtime,
            HttpContext context, CancellationToken cancellation) => EntityForgeDbHttpErrors.MapAsync(async () =>
                await BoundedQueryJsonResponse.CreateAsync(
                    await runtime.RdfDatabase.QuerySparqlAsync(request.Query, cancellation).ConfigureAwait(false),
                    context, cancellation).ConfigureAwait(false)));
        endpoints.MapGet("/v1/rdf/sparql/capabilities", () => Results.Ok(RdfSparql.Capabilities));
        endpoints.MapPost("/v1/rdf/sparql/scan", QueryScanEndpoints.SparqlAsync);
        endpoints.MapPost("/v1/rdf/sparql/update", (RdfSparqlUpdateRequest request,
            EntityForgeDbRuntime runtime, CancellationToken cancellation) => EntityForgeDbHttpErrors.MapAsync(async () =>
        {
            if (request.TransactionId == Guid.Empty)
            {
                throw new ArgumentException("A SPARQL Update transaction identity is required.");
            }

            ArgumentException.ThrowIfNullOrWhiteSpace(request.Update);
            IReadOnlyList<RdfMutation> mutations;
            try
            {
                mutations = RdfSparqlUpdate.Parse(request.Update, request.TransactionId.ToString("N"));
            }
            catch (NotSupportedException)
            {
                RdfSparqlUpdateCommit committed = await runtime.RdfDatabase.CommitSparqlUpdateAsync(
                    request.TransactionId, request.Update, cancellation).ConfigureAwait(false);
                return Results.Ok(new RdfMutationBatchResponse(committed.Version.Value, committed.MutationCount));
            }
            DatabaseVersion version = await runtime.RdfDatabase.CommitAsync(
                new(request.TransactionId, mutations), cancellation).ConfigureAwait(false);
            return Results.Ok(new RdfMutationBatchResponse(version.Value, mutations.Count));
        }));
        endpoints.MapPost("/v1/rdf/shacl/validate", ValidateShaclAsync);
        endpoints.MapGet("/v1/rdf/shacl/capabilities", () => Results.Ok(RdfShacl.Capabilities));
        endpoints.MapGet("/v1/rdf/entailment/rdfs", (EntityForgeDbRuntime runtime, int? maximumResults,
            long? snapshotVersion, CancellationToken cancellation) => EntityForgeDbHttpErrors.MapAsync(() =>
                Task.FromResult<IResult>(Results.Ok(runtime.RdfDatabase.InferRdfs(
                    maximumResults ?? RdfEntailment.MaximumDerivedQuads,
                    snapshotVersion is null ? null : new DatabaseVersion(snapshotVersion.Value), cancellation)))));
        endpoints.MapGet("/v1/rdf/entailment/capabilities", () => Results.Ok(RdfEntailment.Capabilities));

        endpoints.MapPost("/v1/rdf/quads/stream", StreamQuadsAsync);
        endpoints.MapGet("/v1/rdf/export", (HttpContext context, EntityForgeDbRuntime runtime,
            string? format, int? pageSize, string? continuation, CancellationToken cancellation) =>
            EntityForgeDbHttpErrors.MapAsync(() => ExportAsync(
                context, runtime, format, pageSize, continuation, cancellation)));
        endpoints.MapGet("/v1/rdf/changes", (EntityForgeDbRuntime runtime, string? cursor,
            int? maximumCount, string? routingKind, string? routingValue, CancellationToken cancellation) =>
            EntityForgeDbHttpErrors.MapAsync(async () =>
            {
                int count = maximumCount ?? DatabaseChangeFeedLimits.DefaultSubscriptionPageSize;
                DatabaseChangeSelector selector = routingKind is null && routingValue is null
                    ? new(DatabaseProfileId.Rdf)
                    : new(DatabaseProfileId.Rdf, [new(routingKind ?? throw new ArgumentException(
                        "A routing kind is required."), routingValue ?? throw new ArgumentException(
                        "A routing value is required."))]);
                DatabaseChangePage page = await runtime.RdfDatabase.ReadChangePageAfterAsync(
                    cursor is null ? null : new(cursor), selector, count, cancellation).ConfigureAwait(false);
                return Results.Ok(Map(page));
            }));
        endpoints.MapGet("/v1/rdf/changes/bounds", async (EntityForgeDbRuntime runtime,
            CancellationToken cancellation) => Results.Ok(
                await runtime.RdfDatabase.GetChangeFeedBoundsAsync(cancellation).ConfigureAwait(false)));
    }

    private static Task<IResult> ImportAsync(HttpContext context, EntityForgeDbRuntime runtime,
        Guid documentId, long pageNumber, CancellationToken cancellationToken) =>
        EntityForgeDbHttpErrors.MapAsync(async () =>
        {
            if (documentId == Guid.Empty)
            {
                throw new ArgumentException("A document identity is required.");
            }

            string scope = $"{documentId:N}:{pageNumber}";
            IReadOnlyList<RdfQuad> quads = await ParseDocumentAsync(
                context.Request, scope, cancellationToken).ConfigureAwait(false);
            RdfDocumentPageCommit committed = await runtime.RdfDatabase.CommitDocumentPageAsync(
                new(documentId), new(pageNumber, quads), cancellationToken).ConfigureAwait(false);
            return Results.Ok(committed);
        });

    private static Task<IResult> ValidateShaclAsync(HttpContext context, EntityForgeDbRuntime runtime,
        int? maximumResults, long? snapshotVersion, CancellationToken cancellationToken) =>
        EntityForgeDbHttpErrors.MapAsync(async () =>
        {
            IReadOnlyList<RdfQuad> shapes = await ParseDocumentAsync(
                context.Request, "shacl-shapes", cancellationToken).ConfigureAwait(false);
            return Results.Ok(runtime.RdfDatabase.ValidateShacl(shapes,
                maximumResults ?? RdfShacl.MaximumResults,
                snapshotVersion is null ? null : new DatabaseVersion(snapshotVersion.Value), cancellationToken));
        });

    private static async Task<IReadOnlyList<RdfQuad>> ParseDocumentAsync(
        HttpRequest request, string scope, CancellationToken cancellationToken)
    {
        string mediaType = request.ContentType?.Split(';', 2)[0].Trim().ToLowerInvariant() ?? "";
        await using var buffered = new MemoryStream();
        await request.Body.CopyToAsync(buffered, cancellationToken).ConfigureAwait(false);
        buffered.Position = 0;
        return mediaType switch
        {
            "application/n-quads" => RdfSyntax.ParseNQuads(buffered, scope),
            "application/n-triples" => RdfSyntax.ParseNTriples(buffered, scope),
            "text/turtle" or "application/x-turtle" => RdfSyntax.ParseTurtle(buffered, scope),
            "application/ld+json" => RdfSyntax.ParseJsonLd(buffered, scope),
            _ => throw new RdfMediaTypeException(mediaType)
        };
    }

    internal static async Task<IResult> QuadPageAsync(HttpContext context, RdfQuadQueryRequest request,
        EntityForgeDbRuntime runtime, CancellationToken cancellation)
    {
        var page = await runtime.RdfDatabase.QueryPageAsync(request.Pattern, request.PageSize,
            request.Continuation, cancellation).ConfigureAwait(false);
        try
        {
            IResult result = await BoundedQueryJsonResponse.CreateAsync(page, context, cancellation).ConfigureAwait(false);
            return page.NextContinuation is { } continuation
                ? new RetainedRdfPageResult(result, runtime.RdfDatabase, continuation) : result;
        }
        catch
        {
            // The cursor has advanced, but the client never received this page.
            if (page.NextContinuation is { } continuation)
            {
                await runtime.RdfDatabase.ReleaseQueryContinuationAsync(continuation, CancellationToken.None).ConfigureAwait(false);
            }
            throw;
        }
    }

    private sealed class RetainedRdfPageResult(IResult result, IRdfDatabase database, string continuation) : IResult
    {
        public async Task ExecuteAsync(HttpContext context)
        {
            bool delivered = false;
            try
            {
                await result.ExecuteAsync(context).ConfigureAwait(false);
                delivered = !context.RequestAborted.IsCancellationRequested;
            }
            finally
            {
                // Some response writers handle cancellation without throwing.
                // Retain a resumable cursor only after a successful response write.
                if (!delivered)
                {
                    await database.ReleaseQueryContinuationAsync(continuation, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
    }

    internal static async Task StreamQuadsAsync(HttpContext context, RdfQuadQueryRequest request,
        EntityForgeDbRuntime runtime, CancellationToken cancellationToken)
    {
        context.Response.ContentType = "application/x-ndjson";
        string? continuation = request.Continuation;
        try
        {
            do
            {
                RdfQueryPage page = await runtime.RdfDatabase.QueryPageAsync(request.Pattern,
                    request.PageSize, continuation, cancellationToken).ConfigureAwait(false);
                // Retain the new cursor before any write can fail or disconnect.
                continuation = page.NextContinuation;
                foreach (RdfQuad quad in page.Quads)
                {
                    await JsonSerializer.SerializeAsync(context.Response.Body, quad,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    await context.Response.WriteAsync("\n", cancellationToken).ConfigureAwait(false);
                }
                await context.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
            } while (continuation is not null);
        }
        catch (Exception exception) when (!context.Response.HasStarted && !cancellationToken.IsCancellationRequested)
        {
            IResult error = await EntityForgeDbHttpErrors.MapAsync(() => Task.FromException<IResult>(exception)).ConfigureAwait(false);
            await error.ExecuteAsync(context).ConfigureAwait(false);
        }
        finally
        {
            if (continuation is not null)
            {
                await runtime.RdfDatabase.ReleaseQueryContinuationAsync(continuation, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    internal static async Task<IResult> ExportAsync(HttpContext context, EntityForgeDbRuntime runtime,
        string? format, int? pageSize, string? continuation, CancellationToken cancellationToken)
    {
        if (format is not null && !String.Equals(format, "turtle", StringComparison.OrdinalIgnoreCase) &&
            !String.Equals(format, "nquads", StringComparison.OrdinalIgnoreCase) &&
            !String.Equals(format, "jsonld", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("RDF export format must be 'nquads', 'turtle', or 'jsonld'.", nameof(format));
        }

        bool turtle = String.Equals(format, "turtle", StringComparison.OrdinalIgnoreCase) ||
            (format is null && context.Request.GetTypedHeaders().Accept?.Any(value =>
                String.Equals(value.MediaType.Value, "text/turtle", StringComparison.OrdinalIgnoreCase)) is true);
        bool jsonLd = String.Equals(format, "jsonld", StringComparison.OrdinalIgnoreCase) ||
            (format is null && context.Request.GetTypedHeaders().Accept?.Any(value =>
                String.Equals(value.MediaType.Value, "application/ld+json", StringComparison.OrdinalIgnoreCase)) is true);
        RdfQueryPage page = await runtime.RdfDatabase.QueryPageAsync(
            new(MatchDefaultGraphOnly: turtle), pageSize ?? 1_000, continuation, cancellationToken)
            .ConfigureAwait(false);
        context.Response.Headers["EntityForge-Snapshot-Version"] = page.SnapshotVersion.ToString();
        if (page.NextContinuation is not null)
        {
            context.Response.Headers["EntityForge-Continuation"] = page.NextContinuation;
        }

        if (page.ContinuationExpiresAtUtc is not null)
        {
            context.Response.Headers["EntityForge-Continuation-Expires"] =
                page.ContinuationExpiresAtUtc.Value.ToString("O");
        }

        IResult result = jsonLd
            ? Results.Stream(stream => RdfSyntax.WriteJsonLdAsync(page.Quads, stream, cancellationToken).AsTask(),
                "application/ld+json; charset=utf-8")
            : turtle
                ? Results.Stream(stream => RdfSyntax.WriteTurtleAsync(page.Quads, stream, cancellationToken).AsTask(),
                    "text/turtle; charset=utf-8")
                : Results.Stream(stream => RdfSyntax.WriteNQuadsAsync(page.Quads, stream, cancellationToken).AsTask(),
                    "application/n-quads; charset=utf-8");
        return page.NextContinuation is { } next
            ? new RetainedRdfPageResult(result, runtime.RdfDatabase, next) : result;
    }

    private static RdfChangePageResponse Map(DatabaseChangePage page) => new(
        page.Changes.Select(Map).ToArray(), page.NextCursor?.Value, page.HasMore);

    private static RdfCommittedChangeResponse Map(CommittedChangeSet change)
    {
        bool reset = change.Payload.RequiresSnapshotRefresh;
        IReadOnlyList<RdfMutation> mutations = reset ? [] :
            StringComparer.Ordinal.Equals(change.Payload.CodecId,
                RdfProfileRuntime.CommittedTransactionCodecId)
                ? RdfProfileRuntime.DecodeTransaction(change.TransactionId, change.Payload.Content.Span).Mutations
                : throw new InvalidDataException(
                    $"Unsupported RDF change payload codec '{change.Payload.CodecId}'.");
        return new(change.Version.Value, change.TransactionId, change.Cursor.Value,
            reset ? RdfCommittedChangeKind.DatasetReset : RdfCommittedChangeKind.MutationBatch,
            change.Payload.EffectCount, mutations);
    }
}

internal sealed class RdfMediaTypeException(string mediaType) : Exception(
    $"RDF media type '{mediaType}' is unsupported.");
