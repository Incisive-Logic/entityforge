using System.Text.Json;
using System.Text.Json.Serialization;

namespace EntityForge.Db.AppModel.Connections;

/// <summary>Summarizes accepted measurements for one instrument identity in the host process.</summary>
/// <remarks>Identity comprises meter, name, kind and unit. Tags are not retained; measurements from
/// multiple databases in the process are combined. Values are accumulated as doubles since the
/// listener started, not over a polling interval. Histogram summaries do not provide percentiles.</remarks>
/// <param name="Meter">The publishing meter name.</param>
/// <param name="Name">The instrument name.</param>
/// <param name="Unit">The instrument's declared unit, or null when unspecified.</param>
/// <param name="Kind">The instrument kind: Counter, UpDownCounter or Histogram.</param>
/// <param name="Samples">The number of accepted measurement callbacks, not the number of requests.</param>
/// <param name="Sum">The sum of accepted measurements; counter measurements are increments.</param>
/// <param name="Minimum">The smallest accepted individual measurement.</param>
/// <param name="Maximum">The largest accepted individual measurement.</param>
/// <param name="Last">The last accepted individual measurement, not the cumulative sum.</param>
/// <param name="LastObservedAt">The host's UTC time when the last accepted measurement was recorded.</param>
public sealed record LiveMetric(string Meter, string Name, string? Unit, string Kind,
    long Samples, double Sum, double Minimum, double Maximum, double Last, DateTimeOffset LastObservedAt);

/// <summary>A bounded snapshot of the host's process-wide live metric listener.</summary>
/// <remarks>Readings are observational and are not persisted database state. Compare session identities
/// before calculating differences between reports; restarting the listener resets its accumulators.</remarks>
/// <param name="Version">The report format version; the client currently accepts version 1.</param>
/// <param name="ProcessSession">An identifier generated for this listener lifetime.</param>
/// <param name="Scope">The aggregation scope; currently <c>process</c>.</param>
/// <param name="StartedAt">The host's UTC time when the listener was created.</param>
/// <param name="CapturedAt">The host's UTC time when the snapshot was captured.</param>
/// <param name="MaximumMetrics">The maximum number of distinct instrument identities retained by the listener.</param>
/// <param name="DroppedMeasurements">The number of measurements rejected because of non-finite values,
/// non-finite accumulated sums, or a new instrument identity exceeding the retention limit.</param>
/// <param name="Metrics">The retained instrument summaries, ordered by meter and name by the host.</param>
public sealed record LiveTelemetryReport(int Version, Guid ProcessSession, string Scope, DateTimeOffset StartedAt,
    DateTimeOffset CapturedAt, int MaximumMetrics, long DroppedMeasurements, IReadOnlyList<LiveMetric> Metrics);

/// <summary>Combines the last successful report with the outcome of the latest refresh.</summary>
/// <param name="Report">The latest successful report, retained on refresh failure, or null if none was received.</param>
/// <param name="CheckedAt">The client's UTC time when the refresh outcome was recorded; this is not the report capture time.</param>
/// <param name="Error">A displayable refresh error, or null after success. A non-null error means any retained report is stale.</param>
public sealed record TelemetryObservation(LiveTelemetryReport? Report, DateTimeOffset CheckedAt, string? Error);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(LiveTelemetryReport))]
internal sealed partial class TelemetryJsonContext : JsonSerializerContext;

public sealed partial class EntityForgeConnectionSession
{
    private readonly SemaphoreSlim _telemetryGate = new(1, 1);

    /// <summary>Gets the latest refresh observation, or null before a check or after a connection reset.</summary>
    /// <remarks>Inspect <see cref="TelemetryObservation.Error"/> before treating a retained report as current.</remarks>
    public TelemetryObservation? Telemetry
    {
        get; private set;
    }

    /// <summary>Occurs when a refresh outcome is published or telemetry is reset.</summary>
    /// <remarks>Handlers run on the calling continuation's thread; UI subscribers must arrange their own dispatch.</remarks>
    public event EventHandler? TelemetryChanged;

    /// <summary>Gets whether the connected host advertises the live-metrics-v1 administration capability.</summary>
    /// <remarks>This capability does not establish that the current credential has administration permission.</remarks>
    public bool SupportsLiveTelemetry => Current?.Capabilities.Surfaces.Any(s =>
        s.Identifier == EntityForge.Db.Transport.Contracts.EntityForgeSurfaceIds.Administration && s.Features.Contains("live-metrics-v1")) == true;

    /// <summary>Refreshes the current connection's administration-only live telemetry observation.</summary>
    /// <returns>A task that completes after publishing an outcome, or immediately if another refresh is
    /// running or no connection is available.</returns>
    /// <remarks>The request uses the connection lifetime and discovery timeout. Expected transport,
    /// timeout and report-validation failures are recorded in <see cref="TelemetryObservation.Error"/>
    /// while retaining the previous successful report. An outcome from a replaced client is discarded.
    /// Successful publication raises <see cref="TelemetryChanged"/> without UI-thread dispatch.</remarks>
    public async Task RefreshTelemetryAsync()
    {
        if (!await _telemetryGate.WaitAsync(0))
        {
            return;
        }

        try
        {
            HttpClient client;
            CancellationTokenSource request;
            lock (_gate)
            {
                if (_client is null || _lifetime is null || Current is null)
                {
                    return;
                }

                client = _client;
                request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            }
            using (request)
            {
                request.CancelAfter(DiscoveryTimeout);
                LiveTelemetryReport? report = null;
                string? error = null;
                try
                {
                    if (!SupportsLiveTelemetry)
                    {
                        error = "This host does not advertise live metric inspection.";
                    }
                    else if (CanAdminister == false)
                    {
                        error = "This credential does not have administration access to telemetry.";
                    }
                    else
                    {
                        using var response = await client.GetAsync("v1/telemetry", HttpCompletionOption.ResponseHeadersRead, request.Token);
                        error = InspectionError(response.StatusCode);
                        if (error is null)
                        {
                            using var json = await ReadInspection(response, request.Token);
                            report = json.RootElement.Deserialize(TelemetryJsonContext.Default.LiveTelemetryReport);
                            if (report is null || report.Version != 1 || report.ProcessSession == Guid.Empty || report.Scope != "process" ||
                                report.Metrics is null || report.Metrics.Count > 128 || report.Metrics.Any(m => m is null || string.IsNullOrWhiteSpace(m.Name) ||
                                    string.IsNullOrWhiteSpace(m.Meter) || m.Samples <= 0 || m.Kind is not ("Counter" or "UpDownCounter" or "Histogram") ||
                                    !double.IsFinite(m.Sum) || !double.IsFinite(m.Minimum) || !double.IsFinite(m.Maximum) || !double.IsFinite(m.Last)))
                            {
                                throw new InvalidDataException("Invalid telemetry report.");
                            }
                        }
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or InvalidDataException or ObjectDisposedException)
                {
                    report = null;
                    error = "Live telemetry could not be refreshed. Any displayed readings are from the last successful check.";
                }
                lock (_gate)
                {
                    if (client != _client)
                    {
                        return;
                    }

                    Telemetry = new(report ?? Telemetry?.Report, DateTimeOffset.UtcNow, error);
                }
                TelemetryChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            _telemetryGate.Release();
        }
    }

    private void ResetTelemetry()
    {
        Telemetry = null;
        TelemetryChanged?.Invoke(this, EventArgs.Empty);
    }
}
