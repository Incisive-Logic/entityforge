using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace EntityForge.Db.Hosting;

// Query text, variables, headers and error messages deliberately never become attributes.
internal sealed class EntityForgeQueryTracing(RequestDelegate next)
{
    internal const string SourceName = "EntityForge.Db.Requests";
    private static readonly ActivitySource Source = new(SourceName);
    private static readonly Meter Meter = new(SourceName);
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("entityforge.db.query.duration", "ms");
    private static readonly Histogram<double> RequestDuration = Meter.CreateHistogram<double>("entityforge.db.request.duration", "ms");

    internal static bool IsQuery(string path) => path is "/graphql" or "/v1/entities/query"
        or "/v1/semantic/entities/query" or "/v1/semantic/entities/explain"
        or "/v1/semantic/aggregations/query" or "/v1/semantic/aggregations/explain"
        or "/v1/graph/gql/query" or "/v1/graph/gql/explain" or "/v1/rdf/sparql"
        or "/v1/semantic/gql/query" or "/v1/semantic/sparql" or "/sparql";

    public async Task InvokeAsync(HttpContext context, EntityForgeObservabilityOptions options)
    {
        string path = context.Request.Path.Value ?? "";
        bool query = IsQuery(path);
        string? route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
        if (!query && (route is null || !path.StartsWith("/v1/", StringComparison.Ordinal) ||
            path.StartsWith("/v1/observability/", StringComparison.Ordinal) || path == "/v1/telemetry"))
        {
            await next(context);
            return;
        }
        route ??= path; // Query paths are a fixed allowlist; never tag identifiers from raw paths.
        using var activity = Source.StartActivity(query ? "database.query" : "database.request", ActivityKind.Internal);
        activity?.SetTag("entityforge.query", query);
        activity?.SetTag("entityforge.instance", options.Instance);
        activity?.SetTag("http.route", route);
        activity?.SetTag("http.request.method", context.Request.Method);
        var started = Stopwatch.GetTimestamp();
        string outcome = "http-completed";
        try
        {
            await next(context);
            if (context.Response.StatusCode >= 400)
            {
                outcome = "http-error";
            }

            if (context.Items.ContainsKey("entityforge.graphql.errors"))
            {
                outcome = "graphql-error";
            }

            activity?.SetTag("http.response.status_code", context.Response.StatusCode);
        }
        catch (OperationCanceledException)
        {
            outcome = "cancelled";
            throw;
        }
        catch
        {
            outcome = "exception";
            throw;
        }
        finally
        {
            activity?.SetTag("entityforge.outcome", outcome);
            if (outcome != "http-completed")
            {
                activity?.SetStatus(ActivityStatusCode.Error);
            }

            var tags = new TagList { { "entityforge.instance", options.Instance }, { "http.route", route }, { "entityforge.outcome", outcome } };
            double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            RequestDuration.Record(elapsed, tags);
            if (query)
            {
                Duration.Record(elapsed, tags);
            }
        }
    }
}
