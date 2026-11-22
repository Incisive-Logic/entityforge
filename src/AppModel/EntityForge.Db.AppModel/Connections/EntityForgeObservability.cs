using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace EntityForge.Db.AppModel.Connections;

/// <summary>A sampled metric value at a Unix timestamp.</summary>
/// <param name="Time">Seconds since the Unix epoch, including any fractional part.</param>
/// <param name="Value">The finite sample value, or null when the backend sample is unavailable or nonfinite; null is not zero.</param>
public sealed record HistoryPoint(double Time, double? Value);

/// <summary>A named metric series returned by the host's history backend.</summary>
/// <param name="Name">Display name of the metric.</param>
/// <param name="Unit">Display unit, such as milliseconds or queries per second.</param>
/// <param name="Points">Returned samples; missing samples do not establish zero activity.</param>
public sealed record HistorySeries(string Name, string Unit, HistoryPoint[] Points);

/// <summary>A bounded history response for a requested time window.</summary>
/// <param name="Start">Start of the backend query window.</param>
/// <param name="End">End of the backend query window.</param>
/// <param name="StepSeconds">Requested sampling interval in seconds.</param>
/// <param name="Series">Metric series, which may have gaps or no samples.</param>
public sealed record HistoryReport(DateTimeOffset Start, DateTimeOffset End, int StepSeconds, HistorySeries[] Series);

/// <summary>A query trace summary returned by the configured trace backend.</summary>
/// <param name="Id">Trace identifier used to request detail.</param>
/// <param name="StartedAt">Recorded start time.</param>
/// <param name="DurationMs">Reported trace duration in milliseconds.</param>
/// <param name="Route">Recorded HTTP route, when supplied by instrumentation.</param>
/// <param name="Outcome">Recorded outcome label, when supplied by instrumentation.</param>
public sealed record ProfileTrace(string Id, DateTimeOffset StartedAt, double DurationMs, string? Route = null, string? Outcome = null);

/// <summary>A bounded trace search result, not an exhaustive query audit log.</summary>
/// <param name="Traces">Returned trace summaries in descending start-time order.</param>
/// <param name="LimitReached">Whether the response reached its search cap; more matching traces may exist.</param>
/// <param name="Start">Start of the search window.</param>
/// <param name="End">End of the search window.</param>
public sealed record ProfileSearch(ProfileTrace[] Traces, bool LimitReached, DateTimeOffset Start, DateTimeOffset End);

/// <summary>A span belonging to the selected server instance within a trace.</summary>
/// <param name="Id">Span identifier.</param>
/// <param name="ParentId">Parent identifier, or an empty string when absent. The parent need not be included in this response.</param>
/// <param name="Name">Instrumented operation name.</param>
/// <param name="OffsetMs">Milliseconds from the earliest selected span's start, not necessarily the start of the entire distributed trace.</param>
/// <param name="DurationMs">Span duration in milliseconds.</param>
/// <param name="Outcome">Recorded outcome label, when present.</param>
/// <param name="Route">Recorded HTTP route, when present.</param>
public sealed record ProfileSpan(string Id, string ParentId, string Name, double OffsetMs, double DurationMs, string? Outcome, string? Route);

/// <summary>Bounded detail for one trace, restricted to the selected server instance.</summary>
/// <param name="Id">Requested trace identifier.</param>
/// <param name="Spans">Selected spans in start-time order; spans from other services or instances are omitted.</param>
/// <param name="Truncated">Whether selected spans exceeded the response cap. False does not imply that every service's span is included.</param>
public sealed record ProfileDetail(string Id, ProfileSpan[] Spans, bool Truncated);

/// <summary>Reports which observability backends the host has configured.</summary>
/// <param name="History">Whether a history backend is configured; this is not a live health check.</param>
/// <param name="Profiler">Whether a trace backend is configured; this is not a guarantee that traces are retained.</param>
/// <param name="Instance">Server instance identifier used to select telemetry.</param>
public sealed record ObservabilityConfiguration(bool History, bool Profiler, string Instance);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(HistoryReport))]
[JsonSerializable(typeof(ProfileSearch))]
[JsonSerializable(typeof(ProfileDetail))]
[JsonSerializable(typeof(ObservabilityConfiguration))]
internal sealed partial class ObservabilityJsonContext : JsonSerializerContext;

public sealed partial class EntityForgeConnectionSession
{
    /// <summary>Gets whether discovered capabilities advertise observability; access and backend availability are checked by each request.</summary>
    public bool SupportsObservability => Current?.Capabilities.Surfaces.Any(s => s.Features.Contains("observability-v1")) == true;

    /// <summary>Reads a bounded history window from the connected host.</summary>
    /// <param name="minutes">Lookback duration; the server accepts 1 through 1,440 minutes.</param>
    /// <param name="token">Cancellation for this request, also linked to the connection lifetime.</param>
    /// <returns>Validated metric series. Gaps and null samples must not be displayed as measured zeroes.</returns>
    /// <remarks>The request has a ten-second deadline. Unsupported access, backend failures and invalid responses fail rather than returning an empty success report.</remarks>
    public Task<HistoryReport> ReadHistoryAsync(int minutes, CancellationToken token = default) =>
        ReadObservability("history?minutes=" + minutes, ObservabilityJsonContext.Default.HistoryReport, token);

    /// <summary>Searches recorded query traces for the connected server instance.</summary>
    /// <param name="minutes">Lookback duration from 1 through 1,440 minutes, validated by the server.</param>
    /// <param name="token">Cancellation for this request, also linked to the connection lifetime.</param>
    /// <param name="minimumDurationMs">Minimum trace duration, from zero through 86,400,000 milliseconds.</param>
    /// <returns>At most 100 traces. Inspect the limit flag before interpreting the result as exhaustive.</returns>
    /// <remarks>Sampling, retention and backend ingestion affect visibility; absence from this result does not prove that a query never ran.</remarks>
    public Task<ProfileSearch> FindTracesAsync(int minutes, CancellationToken token = default, int minimumDurationMs = 0) =>
        ReadObservability("traces?minutes=" + minutes + "&minimumDurationMs=" + minimumDurationMs, ObservabilityJsonContext.Default.ProfileSearch, token);

    /// <summary>Reads bounded span detail for a recorded query trace.</summary>
    /// <param name="id">Trace identifier obtained from a search result.</param>
    /// <param name="token">Cancellation for this request, also linked to the connection lifetime.</param>
    /// <returns>At most 500 selected spans and an explicit truncation flag.</returns>
    /// <remarks>The host filters out other services and server instances, so a span's parent may be absent. A missing trace fails rather than returning fabricated detail.</remarks>
    public Task<ProfileDetail> ReadTraceAsync(string id, CancellationToken token = default) =>
        ReadObservability("trace?id=" + Uri.EscapeDataString(id), ObservabilityJsonContext.Default.ProfileDetail, token);

    /// <summary>Reads the host's observability configuration without probing backend health.</summary>
    /// <param name="token">Cancellation for this request, also linked to the connection lifetime.</param>
    /// <returns>Configured backend flags and the telemetry instance identifier.</returns>
    public Task<ObservabilityConfiguration> ReadObservabilityConfigurationAsync(CancellationToken token = default) =>
        ReadObservability("configuration", ObservabilityJsonContext.Default.ObservabilityConfiguration, token);

    private static void ValidateObservability<T>(T value)
    {
        bool valid = value switch
        {
            HistoryReport r => r.StepSeconds > 0 && r.End > r.Start && r.Series is { Length: <= 16 } && r.Series.All(s =>
                s is not null && s.Points is { Length: <= 300 } && s.Points.All(p => p is not null && double.IsFinite(p.Time) &&
                    p.Time >= r.Start.ToUnixTimeSeconds() - 1 && p.Time <= r.End.ToUnixTimeSeconds() + 1 &&
                    (!p.Value.HasValue || double.IsFinite(p.Value.Value)))),
            ProfileSearch r => r.Traces is { Length: <= 100 } && r.Traces.All(t => t is not null &&
                System.Text.RegularExpressions.Regex.IsMatch(t.Id ?? "", "^[a-fA-F0-9]{32}$") && double.IsFinite(t.DurationMs) && t.DurationMs >= 0),
            ProfileDetail r => r.Spans is { Length: <= 500 } && r.Spans.All(s => s is not null && double.IsFinite(s.OffsetMs) &&
                double.IsFinite(s.DurationMs) && s.OffsetMs >= 0 && s.DurationMs >= 0),
            ObservabilityConfiguration => true,
            _ => false
        };
        if (!valid)
        {
            throw new InvalidDataException("Invalid observability response.");
        }
    }

    private async Task<T> ReadObservability<T>(string path, JsonTypeInfo<T> type, CancellationToken token)
    {
        HttpClient client;
        CancellationTokenSource request;
        lock (_gate)
        {
            if (_client is null || _lifetime is null || !SupportsObservability)
            {
                throw new InvalidOperationException("Observability is unavailable on this host.");
            }

            client = _client;
            request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, token);
        }
        using (request)
        {
            request.CancelAfter(DiscoveryTimeout);
            using var response = await client.GetAsync("v1/observability/" + path, HttpCompletionOption.ResponseHeadersRead, request.Token);
            if (!response.IsSuccessStatusCode)
            {
                string error = response.StatusCode switch
                {
                    System.Net.HttpStatusCode.ServiceUnavailable => "Observability is not configured on this server. Configure Prometheus and Tempo endpoints.",
                    System.Net.HttpStatusCode.GatewayTimeout => "The observability backend timed out. Check backend health and retry.",
                    System.Net.HttpStatusCode.BadGateway => "The observability backend is unreachable or returned invalid data. Check its address, health and logs.",
                    _ => InspectionError(response.StatusCode) ?? "Observability request failed."
                };
                throw new InvalidOperationException(error);
            }
            using var json = await ReadInspection(response, request.Token);
            var value = json.RootElement.Deserialize(type) ?? throw new InvalidDataException("Invalid observability response.");
            request.Token.ThrowIfCancellationRequested();
            ValidateObservability(value);
            lock (_gate)
            {
                if (client != _client)
                {
                    throw new OperationCanceledException("Connection changed.");
                }
            }

            return value;
        }
    }
}
