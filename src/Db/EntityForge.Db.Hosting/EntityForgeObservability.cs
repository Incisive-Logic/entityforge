using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace EntityForge.Db.Hosting;

internal sealed record EntityForgeObservabilityOptions(string Instance, Uri? Prometheus = null, Uri? Tempo = null);

internal sealed record HistoryPoint(double Time, double? Value);

internal sealed record HistorySeries(string Name, string Unit, HistoryPoint[] Points);

internal sealed record HistoryReport(DateTimeOffset Start, DateTimeOffset End, int StepSeconds, HistorySeries[] Series);

internal sealed record ProfileTrace(string Id, DateTimeOffset StartedAt, double DurationMs, string? Route = null, string? Outcome = null);

internal sealed record ProfileSearch(ProfileTrace[] Traces, bool LimitReached, DateTimeOffset Start, DateTimeOffset End);

internal sealed record ProfileSpan(string Id, string ParentId, string Name, double OffsetMs, double DurationMs, string? Outcome, string? Route);

internal sealed record ProfileDetail(string Id, ProfileSpan[] Spans, bool Truncated);

internal sealed class ObservabilityTraceNotFoundException : Exception;

// A bounded read adapter, not a general-purpose PromQL/TraceQL proxy. Backend addresses stay server-side.
internal sealed class EntityForgeObservability(EntityForgeObservabilityOptions options, HttpClient client)
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    internal static bool ValidInstance(string value) => Regex.IsMatch(value, "^[a-zA-Z0-9_.-]{1,128}$");

    private string Selector => "{service_name=\"EntityForge.Db.Server\",service_instance_id=\"" + options.Instance + "\"}";

    internal string SearchExpression => "{ resource.service.name = \"EntityForge.Db.Server\" && resource.service.instance.id = \"" + options.Instance + "\" && span.entityforge.query = true }";

    private static (DateTimeOffset Start, DateTimeOffset End) Range(int minutes)
    {
        if (minutes is < 1 or > 1440)
        {
            throw new ArgumentException("Range must be between 1 and 1440 minutes.");
        }

        var end = DateTimeOffset.UtcNow;
        return (end.AddMinutes(-minutes), end);
    }

    internal async Task<HistoryReport> History(int minutes, CancellationToken token)
    {
        var (start, end) = Range(minutes);
        int step = Math.Max(5, minutes * 60 / 240);
        string histogram = "entityforge_db_query_duration_milliseconds";
        var queries = new[]
        {
            ("Queries", "queries/s", $"sum(rate({histogram}_count{Selector}[1m]))"),
            ("Query duration · p95", "ms", $"histogram_quantile(0.95, sum by (le) (rate({histogram}_bucket{Selector}[5m])))"),
            ("Commits", "commits/s", $"sum(rate(entityforge_db_commits_total{Selector}[1m]))"),
            ("Commit queue · p95", "ms", $"histogram_quantile(0.95, sum by (le) (rate(entityforge_db_commit_queue_wait_milliseconds_bucket{Selector}[5m])))")
        };
        var series = new List<HistorySeries>();
        foreach (var (name, unit, query) in queries)
        {
            using var json = await Get(options.Prometheus, $"api/v1/query_range?query={Uri.EscapeDataString(query)}&start={start.ToUnixTimeSeconds()}&end={end.ToUnixTimeSeconds()}&step={step}", token);
            var root = json.RootElement;
            if (root.GetProperty("status").GetString() != "success" || root.TryGetProperty("warnings", out var warnings) && warnings.GetArrayLength() > 0)
            {
                throw new InvalidDataException("Incomplete history response.");
            }

            var points = new List<HistoryPoint>();
            foreach (var result in root.GetProperty("data").GetProperty("result").EnumerateArray())
            {
                foreach (var point in result.GetProperty("values").EnumerateArray())
                {
                    if (points.Count >= 300)
                    {
                        throw new InvalidDataException("Too many history points.");
                    }

                    double? value = double.TryParse(point[1].GetString(), NumberStyles.Float, Invariant, out var number) && double.IsFinite(number) ? number : null;
                    points.Add(new(point[0].GetDouble(), value));
                }
            }

            series.Add(new(name, unit, points.ToArray()));
        }
        return new(start, end, step, series.ToArray());
    }

    internal async Task<ProfileSearch> Search(int minutes, CancellationToken token, int minimumDurationMs = 0)
    {
        var (start, end) = Range(minutes);
        if (minimumDurationMs is < 0 or > 86400000)
        {
            throw new ArgumentException("Invalid minimum duration.");
        }

        string expression = SearchExpression.TrimEnd(' ', '}') + $" && duration >= {minimumDurationMs}ms }} | select(span.http.route, span.entityforge.outcome)";
        using var json = await Get(options.Tempo, $"api/search?q={Uri.EscapeDataString(expression)}&start={start.ToUnixTimeSeconds()}&end={end.ToUnixTimeSeconds()}&limit=100&spss=1", token);
        if (json.RootElement.TryGetProperty("metrics", out var metrics) && metrics.TryGetProperty("failedBlocks", out var failed) && failed.GetInt32() > 0)
        {
            throw new InvalidDataException("Incomplete trace search.");
        }

        var traces = json.RootElement.GetProperty("traces").EnumerateArray().Take(100).Select(t => new ProfileTrace(
            NormalizeTraceId(t.GetProperty("traceID").GetString()), FromNano(t.GetProperty("startTimeUnixNano")),
            t.TryGetProperty("durationMs", out var duration) ? duration.GetDouble() : 0,
            SearchAttribute(t, "http.route"), SearchAttribute(t, "entityforge.outcome"))).ToArray();
        return new(traces.OrderByDescending(t => t.StartedAt).ToArray(), traces.Length == 100, start, end);
    }

    private static string NormalizeTraceId(string? id)
    {
        // Tempo's protobuf JSON omits zero durations and leading zeroes in trace IDs.
        if (id is null || !Regex.IsMatch(id, "^[a-fA-F0-9]{1,32}$"))
        {
            throw new InvalidDataException("Invalid backend trace ID.");
        }

        return id.PadLeft(32, '0').ToLowerInvariant();
    }

    private static string? SearchAttribute(JsonElement trace, string key)
    {
        if (!trace.TryGetProperty("spanSets", out var sets))
        {
            return null;
        }

        foreach (var set in sets.EnumerateArray())
        {
            foreach (var span in set.GetProperty("spans").EnumerateArray())
            {
                if ((Attribute(span, "span." + key) ?? Attribute(span, key)) is { } value)
                {
                    return value;
                }
            }
        }

        return null;
    }

    private static double Nano(JsonElement value) => double.Parse(value.ToString(), Invariant);

    private static DateTimeOffset FromNano(JsonElement value) => DateTimeOffset.FromUnixTimeMilliseconds((long)(Nano(value) / 1_000_000));

    private static string? Attribute(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty("attributes", out var attrs))
        {
            return null;
        }

        foreach (var attr in attrs.EnumerateArray())
        {
            if (attr.GetProperty("key").GetString() == key)
            {
                var value = attr.GetProperty("value");
                return value.EnumerateObject().FirstOrDefault().Value.ToString();
            }
        }

        return null;
    }

    internal async Task<ProfileDetail> Detail(string id, CancellationToken token)
    {
        if (!Regex.IsMatch(id, "^[a-fA-F0-9]{32}$"))
        {
            throw new ArgumentException("Invalid trace ID.");
        }

        using var json = await Get(options.Tempo, "api/traces/" + id, token);
        var root = json.RootElement;
        var resources = root.TryGetProperty("batches", out var batches) ? batches : root.GetProperty("resourceSpans");
        var selected = new List<JsonElement>();
        foreach (var batch in resources.EnumerateArray())
        {
            var resource = batch.GetProperty("resource");
            if (Attribute(resource, "service.instance.id") != options.Instance || Attribute(resource, "service.name") != "EntityForge.Db.Server")
            {
                continue;
            }

            var scopes = batch.TryGetProperty("scopeSpans", out var modern) ? modern : batch.GetProperty("instrumentationLibrarySpans");
            foreach (var scope in scopes.EnumerateArray())
            {
                foreach (var span in scope.GetProperty("spans").EnumerateArray())
                {
                    selected.Add(span);
                }
            }
        }
        // Never return spans from other services/databases in the same distributed trace.
        if (!selected.Any(s => string.Equals(Attribute(s, "entityforge.query"), "true", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ObservabilityTraceNotFoundException();
        }

        double first = selected.Min(s => Nano(s.GetProperty("startTimeUnixNano")));
        return new(id, selected.OrderBy(s => Nano(s.GetProperty("startTimeUnixNano"))).Take(500).Select(s => new ProfileSpan(
            s.GetProperty("spanId").GetString()!, s.TryGetProperty("parentSpanId", out var parent) ? parent.GetString() ?? "" : "",
            s.GetProperty("name").GetString()!, (Nano(s.GetProperty("startTimeUnixNano")) - first) / 1_000_000,
            Math.Max(0, (Nano(s.GetProperty("endTimeUnixNano")) - Nano(s.GetProperty("startTimeUnixNano"))) / 1_000_000),
            Attribute(s, "entityforge.outcome"), Attribute(s, "http.route"))).ToArray(), selected.Count > 500);
    }

    private async Task<JsonDocument> Get(Uri? endpoint, string path, CancellationToken token)
    {
        if (endpoint is null)
        {
            throw new NotSupportedException();
        }

        using var response = await client.GetAsync(new Uri(endpoint, path), HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        if (response.StatusCode == System.Net.HttpStatusCode.PartialContent)
        {
            throw new InvalidDataException("Incomplete backend response.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var buffer = new MemoryStream();
        var bytes = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(bytes, token)) != 0)
        {
            if (buffer.Length + read > 4 * 1024 * 1024)
            {
                throw new InvalidDataException("Backend response too large.");
            }

            buffer.Write(bytes, 0, read);
        }
        return JsonDocument.Parse(buffer.ToArray());
    }

    internal static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/v1/observability/{view}", async (string view, HttpContext context, IEntityForgeDbAuthorizationPolicy policy, EntityForgeObservability backend, EntityForgeObservabilityOptions options) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!await policy.AuthorizeAsync(context, EntityForgeDbAdministrativeOperation.ReadStatus, context.RequestAborted))
            {
                return Results.StatusCode(403);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            try
            {
                if (view == "configuration")
                {
                    return Results.Ok(new
                    {
                        history = options.Prometheus is not null,
                        profiler = options.Tempo is not null,
                        instance = options.Instance
                    });
                }

                if (view == "trace")
                {
                    return Results.Ok(await backend.Detail(context.Request.Query["id"].ToString(), timeout.Token));
                }

                int minutes = int.TryParse(context.Request.Query["minutes"], out var value) ? value : 15;
                return view switch
                {
                    "history" => Results.Ok(await backend.History(minutes, timeout.Token)),
                    "traces" => Results.Ok(await backend.Search(minutes, timeout.Token, int.TryParse(context.Request.Query["minimumDurationMs"], out var duration) ? duration : 0)),
                    _ => Results.NotFound()
                };
            }
            catch (NotSupportedException)
            {
                return Results.Problem("Configure the observability backend on the server.", statusCode: 503);
            }
            catch (ObservabilityTraceNotFoundException)
            {
                return Results.NotFound();
            }
            catch (ArgumentException)
            {
                return Results.BadRequest();
            }
            catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
            {
                return Results.Problem("The observability backend timed out. Check backend health and retry.", statusCode: 504);
            }
            catch (HttpRequestException)
            {
                return Results.Problem("Cannot reach the observability backend. Check its server-side address, network access and service health.", statusCode: 502);
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or InvalidOperationException or FormatException or OverflowException or KeyNotFoundException)
            {
                return Results.Problem("The observability backend returned an invalid or incomplete response. Check backend compatibility and logs.", statusCode: 502);
            }
        });
    }
}
