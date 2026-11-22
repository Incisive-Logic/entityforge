using EntityForge.Db.Query.Adapters;
using EntityForge.Db.Gql;
using System.Runtime.CompilerServices;
using System.Text.Json;
using EntityForge.Db.Rdf;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EntityForge.Db.Hosting;

internal static class QueryScanEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static Task SparqlAsync(RdfSparqlQueryRequest request, EntityForgeDbRuntime runtime,
        HttpContext context, CancellationToken cancellation) =>
        WriteAsync(context, runtime, token => SparqlPages(request, runtime, token), cancellation);

    private static async IAsyncEnumerable<object> SparqlPages(RdfSparqlQueryRequest request, EntityForgeDbRuntime runtime,
        [EnumeratorCancellation] CancellationToken cancellation)
    {
        var options = runtime.QueryScans;
        await using var source = new RdfQueryScanReader(runtime.RdfDatabase, options.MaximumExaminedElements, multiplePatterns: true);
        await foreach (var page in RdfSparql.ScanAsync(request.Query, source.ReadAsync,
            source.ReleaseAsync,
            new(options.MaximumRows, options.MaximumExaminedElements, options.PageSize, options.Timeout,
                options.MaximumSortTemporaryBytes), cancellation).ConfigureAwait(false))
        {
            yield return new
            {
                type = "page",
                snapshotVersion = page.SnapshotVersion,
                variables = page.Variables,
                rows = page.Rows
            };
        }
    }

    internal static Task GqlAsync(GraphGqlQueryRequest request, EntityForgeDbRuntime runtime,
        HttpContext context, CancellationToken cancellation) =>
        WriteAsync(context, runtime, token => GqlPages(request, runtime, context, token), cancellation);

    private static async IAsyncEnumerable<object> GqlPages(GraphGqlQueryRequest request, EntityForgeDbRuntime runtime,
        HttpContext context, [EnumeratorCancellation] CancellationToken cancellation)
    {
        if (request.IncludeSupportingGraph)
        {
            throw new NotSupportedException("GQL scans return bindings without a supporting graph.");
        }
        string statement = GraphGqlStandardSyntax.BindSessionUser(runtime.GqlProcedures?.Expand(request.Query) ?? request.Query,
            GraphGqlSessionUser.FromRequest(context));
        var (database, selected, lifetime) = runtime.SelectGqlGraph(statement);
        using var storeLifetime = lifetime;
        var options = runtime.QueryScans;
        await foreach (var page in GraphGqlScan.ExecuteNativeAsync(request with
        {
            Query = selected
        }, database,
            new(options.MaximumRows, options.MaximumExaminedElements, options.PageSize, options.Timeout,
                options.MaximumSortTemporaryBytes), cancellation).ConfigureAwait(false))
        {
            yield return new
            {
                type = "page",
                snapshotVersion = page.Version.Value,
                rows = page.Rows.Select(GraphPatternQueryResponseMapper.Match).ToArray(),
                diagnostics = page.Diagnostics ?? []
            };
        }
    }

    internal static async Task WriteAsync(HttpContext context, EntityForgeDbRuntime runtime,
        Func<CancellationToken, IAsyncEnumerable<object>> pages, CancellationToken cancellation)
    {
        if (!runtime.TryEnterQueryScan())
        {
            await Results.Json(new
            {
                code = "query_scan_capacity_exceeded",
                detail = "The host's concurrent scan capacity is exhausted."
            },
                statusCode: StatusCodes.Status429TooManyRequests).ExecuteAsync(context).ConfigureAwait(false);
            return;
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation, context.RequestAborted);
        deadline.CancelAfter(runtime.QueryScans.Timeout);
        try
        {
            context.Response.ContentType = "application/x-ndjson";
            context.Response.Headers.CacheControl = "no-store";
            await foreach (object page in pages(deadline.Token).WithCancellation(deadline.Token).ConfigureAwait(false))
            {
                await WriteFrame(page, deadline.Token).ConfigureAwait(false);
            }
            // Only a fully exhausted successful evaluator may emit this final frame.
            await WriteFrame(new
            {
                type = "complete",
                complete = true
            }, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!context.Response.HasStarted && !context.RequestAborted.IsCancellationRequested && !cancellation.IsCancellationRequested)
            {
                await Results.Json(new
                {
                    code = "query_scan_deadline_exceeded"
                }, statusCode: StatusCodes.Status504GatewayTimeout)
                    .ExecuteAsync(context).ConfigureAwait(false);
            }
            else
            {
                context.Abort();
            }
        }
        catch (Exception exception)
        {
            if (!context.Response.HasStarted && !context.RequestAborted.IsCancellationRequested)
            {
                IResult error = await EntityForgeDbHttpErrors.MapAsync(() => Task.FromException<IResult>(exception)).ConfigureAwait(false);
                await error.ExecuteAsync(context).ConfigureAwait(false);
            }
            else
            {
                context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("EntityForge.QueryScans")
                    .LogWarning(exception, "Query scan ended without a completion frame.");
                context.Abort();
            }
        }
        finally
        {
            runtime.LeaveQueryScan();
        }

        async Task WriteFrame(object value, CancellationToken token)
        {
            await JsonSerializer.SerializeAsync(context.Response.Body, value, Json, token).ConfigureAwait(false);
            await context.Response.WriteAsync("\n", token).ConfigureAwait(false);
            await context.Response.Body.FlushAsync(token).ConfigureAwait(false);
        }
    }
}
