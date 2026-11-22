using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using EntityForge.Db.Client;
using EntityForge.Db.Transport.Contracts;

namespace EntityForge.Db.AppModel.Connections;

/// <summary>Observed committed-change journal storage and retention metrics.</summary>
/// <param name="RetainedBytes">Journal bytes retained by the server.</param>
/// <param name="RetentionPressureBytes">Retained bytes above the configured byte target, or zero.</param>
/// <param name="SegmentCount">Number of retained journal segments.</param>
/// <param name="MaterializationDebt">Changes awaiting materialization or visible publication, as reported by the implementation.</param>
/// <param name="RetentionFloorVersion">Server-reported journal retention floor, or null when unavailable.</param>
public sealed record OperationalJournalStatus(long RetainedBytes, long RetentionPressureBytes,
    int SegmentCount, long MaterializationDebt, long? RetentionFloorVersion);

/// <summary>One server operational-status response; it does not describe a whole cluster.</summary>
/// <param name="Ready">Whether the server reports readiness.</param>
/// <param name="MaintenanceFailure">Last maintenance failure type name, or null.</param>
/// <param name="Profile">Server profile identifier.</param>
/// <param name="Version">Observed database version.</param>
/// <param name="WalBytes">Server-reported write-ahead log size in bytes.</param>
/// <param name="ChangeJournal">Journal status when available.</param>
/// <param name="DatabaseIncarnation">Database identity used to distinguish separate database lifetimes.</param>
public sealed record OperationalStatusReport(bool Ready, string? MaintenanceFailure, string Profile,
    long Version, long WalBytes, OperationalJournalStatus? ChangeJournal, Guid DatabaseIncarnation);

/// <summary>Latest reachability check together with any retained successful status report.</summary>
/// <param name="Report">Latest successful report, which may be stale after failure.</param>
/// <param name="ReceivedAt">UTC time that Report was received, unchanged when retaining stale data.</param>
/// <param name="CheckedAt">UTC time of the latest published check.</param>
/// <param name="Reachable">Whether the liveness request received an HTTP response; not a readiness guarantee.</param>
/// <param name="Error">Latest check failure or unsupported-capability message, or null.</param>
public sealed record OperationalStatusObservation(OperationalStatusReport? Report, DateTimeOffset? ReceivedAt,
    DateTimeOffset CheckedAt, bool Reachable, string? Error);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(OperationalStatusReport))]
internal sealed partial class OperationalStatusJsonContext : JsonSerializerContext;

public sealed partial class EntityForgeConnectionSession
{
    private readonly SemaphoreSlim _statusGate = new(1, 1);

    /// <summary>Gets the last status observation, or null before a check has been published.</summary>
    public OperationalStatusObservation? OperationalStatus
    {
        get; private set;
    }

    /// <summary>Raised after publishing a status observation; callbacks are not marshalled to a UI thread.</summary>
    public event EventHandler? OperationalStatusChanged;

    /// <summary>Independent diagnostics; a not-ready host can still explain its state.</summary>
    /// <param name="cancellationToken">Cancels acquisition or checking. Caller cancellation prevents publication of a new observation.</param>
    /// <returns>Completion of the check, or an immediate return if disconnected or another check is active.</returns>
    /// <remarks>Uses liveness and advertised administrative status endpoints with a ten-second discovery deadline. Reachability and readiness are distinct. Failed checks retain the prior report and its receipt time. Responses from replaced connections are discarded.</remarks>
    public async Task RefreshOperationalStatusAsync(CancellationToken cancellationToken = default)
    {
        if (!await _statusGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            HttpClient client;
            CancellationTokenSource request;
            bool supported;
            lock (_gate)
            {
                if (_client is null || _lifetime is null || Current is null)
                {
                    return;
                }

                client = _client;
                request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
                supported = Current.Capabilities.Surfaces.Any(s => s.Identifier == EntityForgeSurfaceIds.Administration && s.Features.Contains("status"));
            }
            using (request)
            {
                request.CancelAfter(DiscoveryTimeout);
                bool reachable = false;
                OperationalStatusReport? report = null;
                string? error = null;
                try
                {
                    using var live = await client.GetAsync("health/live", request.Token).ConfigureAwait(false);
                    reachable = true; // An HTTP response establishes reachability, not readiness.
                    live.EnsureSuccessStatusCode();
                    if (!supported)
                    {
                        error = "This host does not advertise operational diagnostics. Cluster topology and remote-owner status are not inferred.";
                    }
                    else
                    {
                        using var response = await client.GetAsync("v1/status", request.Token).ConfigureAwait(false);
                        error = response.StatusCode switch
                        {
                            HttpStatusCode.Unauthorized => "Authentication required or token rejected. Reconnect with a valid token.",
                            HttpStatusCode.Forbidden => "This token does not have permission to read operational status.",
                            HttpStatusCode.NotFound => "Operational status is advertised but the endpoint is unavailable on this host.",
                            _ => null
                        };
                        if (error is null)
                        {
                            response.EnsureSuccessStatusCode();
                            report = await response.Content.ReadFromJsonAsync(OperationalStatusJsonContext.Default.OperationalStatusReport, request.Token).ConfigureAwait(false)
                                ?? throw new InvalidDataException("The host returned no operational status.");
                            if (String.IsNullOrWhiteSpace(report.Profile) || report.DatabaseIncarnation == Guid.Empty)
                            {
                                throw new InvalidDataException("The host returned incomplete operational status.");
                            }
                        }
                    }
                }
                catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or JsonException or InvalidDataException or ObjectDisposedException)
                {
                    report = null;
                    error = exception is OperationCanceledException ? "Status check timed out. Retained observations are stale."
                        : exception is JsonException or InvalidDataException ? "The host returned invalid operational status."
                        : "Could not retrieve operational status. Retained observations are stale; Studio will retry.";
                }
                lock (_gate)
                {
                    if (!ReferenceEquals(client, _client) || cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    var now = DateTimeOffset.UtcNow;
                    OperationalStatus = new(report ?? OperationalStatus?.Report,
                        report is null ? OperationalStatus?.ReceivedAt : now, now, reachable, error);
                }
                OperationalStatusChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            _statusGate.Release();
        }
    }
}
