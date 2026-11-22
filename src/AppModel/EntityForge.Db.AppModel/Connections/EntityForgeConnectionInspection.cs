using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using EntityForge.Db.Transport.Contracts;

namespace EntityForge.Db.AppModel.Connections;

/// <summary>Access-discovery state, including an optionally retained earlier report after failure.</summary>
/// <param name="Report">Access report, or null when none is available. Check Error before treating it as current.</param>
/// <param name="CheckedAt">UTC time of the access observation.</param>
/// <param name="Error">Failure or unsupported-capability message, or null.</param>
public sealed record AccessObservation(EntityForgeAccessReport? Report, DateTimeOffset CheckedAt, string? Error);

/// <summary>Outcome lookup for one operation and database identity.</summary>
/// <param name="OperationId">Requested operation identifier.</param>
/// <param name="DatabaseIncarnation">Requested database incarnation.</param>
/// <param name="Profile">Expected operation transport profile name.</param>
/// <param name="Report">Validated retained outcome, or null when none was established.</param>
/// <param name="CheckedAt">UTC observation time.</param>
/// <param name="Error">Lookup error, or null. An error does not prove the operation failed.</param>
public sealed record OperationObservation(Guid OperationId, Guid DatabaseIncarnation, string Profile,
    EntityOperationStatusTransport? Report, DateTimeOffset CheckedAt, string? Error);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(EntityForgeAccessReport))]
[JsonSerializable(typeof(EntityOperationStatusTransport))]
internal sealed partial class InspectionJsonContext : JsonSerializerContext;

/// <summary>Interprets and validates retained operation outcomes without inferring unsafe retry guarantees.</summary>
public static class OperationOutcome
{
    /// <summary>Explains a supported operation state or returns an unknown-state explanation.</summary>
    /// <param name="state">The case-sensitive transport state name.</param>
    /// <returns>Display text that distinguishes committed, unreadable, missing and expired outcomes.</returns>
    public static string Explanation(string state) => state switch
    {
        "NotFound" => "No retained operation was found. This does not establish failure or prove that no changes occurred. Check the operation ID and database identity.",
        "PlanBound" => "The operation identity is bound to a plan. No terminal outcome has been established. Look up the same ID again.",
        "Committed" => "The operation committed and readability was confirmed.",
        "CommittedPendingReadability" => "The operation committed, but readability has not yet been confirmed. Do not submit it as a new operation.",
        "Aborted" => "The server reports a terminal aborted operation.",
        "Expired" => "The retained operation outcome has expired. Expiry does not mean failure; do not infer that replay is safe.",
        _ => "The host returned an unsupported outcome. No result is inferred."
    };

    /// <summary>Checks transport version, operation identity, database identity, profile and state/receipt consistency.</summary>
    /// <param name="report">The deserialized response.</param>
    /// <param name="operation">Expected operation identifier.</param>
    /// <param name="incarnation">Expected database incarnation.</param>
    /// <param name="profile">Expected case-sensitive transport profile.</param>
    /// <param name="status">HTTP response status; Gone requires an expired outcome without a receipt.</param>
    /// <exception cref="InvalidDataException">Identity, version, state or receipt validation fails.</exception>
    /// <remarks>This is structural validation, not a fresh database read or proof of rollback. Malformed receipt JSON may also raise JSON element access errors.</remarks>
    public static void Validate(EntityOperationStatusTransport report, Guid operation, Guid incarnation, string profile, HttpStatusCode status)
    {
        if (report.Version != EntityOperationStatusTransport.CurrentVersion || report.OperationId != operation ||
            report.DatabaseIncarnation != incarnation || report.Profile != profile)
        {
            throw new InvalidDataException("The response identifies a different operation, database or profile.");
        }

        if (status == HttpStatusCode.Gone)
        {
            if (report.State != "Expired" || report.Receipt is not null)
            {
                throw new InvalidDataException("Invalid expiry response.");
            }

            return;
        }
        if (report.State is "Committed" or "CommittedPendingReadability")
        {
            if (report.Receipt is not { ValueKind: JsonValueKind.Object } receipt)
            {
                throw new InvalidDataException("The commit receipt is missing.");
            }

            JsonElement Required(string name) => receipt.EnumerateObject().FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
            if (!Required("DatabaseIncarnation").TryGetGuid(out var returnedIncarnation) || returnedIncarnation != incarnation ||
                !Required("TransactionId").TryGetGuid(out var returnedOperation) || returnedOperation != operation)
            {
                throw new InvalidDataException("The commit receipt identifies a different operation or database.");
            }
        }
        else if (report.State is not ("NotFound" or "PlanBound" or "Aborted") || report.Receipt is not null)
        {
            throw new InvalidDataException("The host returned an unsupported outcome or receipt combination.");
        }
    }
}

public sealed partial class EntityForgeConnectionSession
{
    private readonly SemaphoreSlim _accessGate = new(1, 1);
    private CancellationTokenSource? _operationRequest;

    /// <summary>Gets the latest access observation. An error can accompany a retained stale report.</summary>
    public AccessObservation? Access
    {
        get; private set;
    }

    /// <summary>Gets the latest operation lookup state, or null after connection reset.</summary>
    public OperationObservation? Operation
    {
        get; private set;
    }

    /// <summary>Gets whether an operation lookup is pending.</summary>
    public bool IsLookingUpOperation => _operationRequest is not null;

    /// <summary>Raised after access state changes; callbacks are not marshalled to a UI thread.</summary>
    public event EventHandler? AccessChanged;

    /// <summary>Raised when lookup state changes; callbacks are not marshalled to a UI thread.</summary>
    public event EventHandler? OperationChanged;

    /// <summary>Gets whether the connected host advertises access discovery.</summary>
    public bool SupportsAccessDiscovery => Current?.Capabilities.Supports(EntityForgeSurfaceIds.AccessDiscovery) == true;

    /// <summary>Gets whether the connected host advertises application-operation resolution.</summary>
    public bool SupportsOperationLookup => Current?.Capabilities.Surfaces.Any(s => s.Identifier == EntityForgeSurfaceIds.ApplicationOperations && s.Features.Contains("resolve")) == true;

    /// <summary>Gets administrative access from an error-free observation, or null when permission is unknown.</summary>
    public bool? CanAdminister => Access is { Error: null, Report: { } report } ? report.CanAdminister : null;

    /// <summary>Refreshes access discovery without overlapping an existing check.</summary>
    /// <param name="cancellationToken">Cancels acquisition or checking; caller cancellation prevents publication.</param>
    /// <returns>Completion, or an immediate return when disconnected or another access check is active.</returns>
    /// <remarks>Uses the discovery deadline and retains prior report data on failure with an error. Responses from replaced clients are discarded.</remarks>
    public async Task RefreshAccessAsync(CancellationToken cancellationToken = default)
    {
        if (!await _accessGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            HttpClient client;
            CancellationTokenSource request;
            bool supported;
            Guid? incarnation;
            lock (_gate)
            {
                if (_client is null || _lifetime is null || Current is null)
                {
                    return;
                }

                client = _client;
                request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
                supported = SupportsAccessDiscovery;
                incarnation = Current.Capabilities.DatabaseIncarnation;
            }
            using (request)
            {
                request.CancelAfter(DiscoveryTimeout);
                var access = await ReadAccessDiscoveryAsync(client, supported, incarnation, request.Token).ConfigureAwait(false);
                lock (_gate)
                {
                    if (!ReferenceEquals(client, _client) || cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    Access = access.Observation with
                    {
                        Report = access.Observation.Report ?? Access?.Report
                    };
                }
                AccessChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            _accessGate.Release();
        }
    }

    /// <summary>Looks up one retained operation outcome, replacing any pending lookup.</summary>
    /// <param name="operationId">Nonempty operation identifier; reuse the original identifier after an uncertain write.</param>
    /// <param name="incarnation">Nonempty database identity matching the connection when advertised.</param>
    /// <param name="cancellationToken">Cancels lookup, not the operation being inspected.</param>
    /// <returns>Completion with the validated report or lookup error stored in Operation.</returns>
    /// <exception cref="ArgumentException">An identifier is empty.</exception>
    /// <exception cref="InvalidOperationException">Disconnected, unsupported lookup/profile, or mismatched database incarnation.</exception>
    /// <remarks>Requests have a fifteen-second deadline and a one-MiB response limit. Missing, expired or cancelled lookup results do not prove failure or safe replay of the write.</remarks>
    public async Task LookupOperationAsync(Guid operationId, Guid incarnation, CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty || incarnation == Guid.Empty)
        {
            throw new ArgumentException("Enter a valid operation ID and database incarnation.");
        }

        HttpClient client;
        CancellationTokenSource current;
        string profile;
        lock (_gate)
        {
            if (_client is null || _lifetime is null || Current is null)
            {
                throw new InvalidOperationException("Connect to a database first.");
            }

            if (!SupportsOperationLookup)
            {
                throw new InvalidOperationException("This host does not advertise operation lookup.");
            }

            if (Current.Capabilities.DatabaseIncarnation is { } known && incarnation != known)
            {
                throw new InvalidOperationException("The database incarnation does not match this connection.");
            }

            client = _client;
            profile = Current.Capabilities.Profile.Identifier switch
            {
                "property-graph" => "PropertyGraph",
                "rdf" => "Rdf",
                "semantic" => "Semantic",
                _ => throw new InvalidOperationException("Unsupported profile.")
            };
            var previous = _operationRequest;
            current = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            current.CancelAfter(TimeSpan.FromSeconds(15));
            _operationRequest = current;
            previous?.Cancel();
            Operation = new(operationId, incarnation, profile, null, DateTimeOffset.UtcNow, null);
        }
        OperationChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            string route = $"v1/application/operations/{operationId:D}?databaseIncarnation={incarnation:D}&expectedProfile={profile}";
            using var response = await client.GetAsync(route, HttpCompletionOption.ResponseHeadersRead, current.Token).ConfigureAwait(false);
            string? error = response.StatusCode == HttpStatusCode.Gone ? null : InspectionError(response.StatusCode);
            if (error is not null)
            {
                throw new InvalidDataException(error);
            }

            using var json = await ReadInspection(response, current.Token).ConfigureAwait(false);
            var report = json.RootElement.Deserialize(InspectionJsonContext.Default.EntityOperationStatusTransport) ?? throw new InvalidDataException("Empty operation response.");
            OperationOutcome.Validate(report, operationId, incarnation, profile, response.StatusCode);
            lock (_gate)
            {
                if (_operationRequest != current || !ReferenceEquals(client, _client))
                {
                    return;
                }

                current.Token.ThrowIfCancellationRequested();
                Operation = new(operationId, incarnation, profile, report, DateTimeOffset.UtcNow, null);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or InvalidDataException or ObjectDisposedException or InvalidOperationException or FormatException)
        {
            lock (_gate)
            {
                if (_operationRequest != current || !ReferenceEquals(client, _client))
                {
                    return;
                }

                Operation = new(operationId, incarnation, profile, null, DateTimeOffset.UtcNow,
                    ex is OperationCanceledException ? "Lookup stopped or timed out. No operation outcome was established."
                    : ex is InvalidDataException ? ex.Message : "Could not retrieve a valid operation outcome. Retry lookup using the same ID.");
            }
        }
        finally
        {
            lock (_gate)
            {
                if (_operationRequest == current)
                {
                    _operationRequest = null;
                }

                current.Dispose();
            }
            OperationChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Stops waiting for lookup without cancelling the underlying database operation or establishing its outcome.</summary>
    public void CancelOperationLookup()
    {
        lock (_gate)
        {
            var request = _operationRequest;
            _operationRequest = null;
            request?.Cancel();
            if (request is not null && Operation is { } observation)
            {
                Operation = observation with
                {
                    Error = "Stopped waiting. No operation outcome was established."
                };
            }
        }
        OperationChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ResetInspection()
    {
        var request = _operationRequest;
        _operationRequest = null;
        request?.Cancel();
        Access = null;
        Operation = null;
    }

    private static string? InspectionError(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized => "The credential is missing, invalid or lacks the required role. Reconnect with an appropriate token.",
        HttpStatusCode.Forbidden => "The current credential is not permitted to perform this request.",
        HttpStatusCode.NotFound => "The advertised endpoint was not found. This is not an operation NotFound outcome.",
        HttpStatusCode.Conflict => "The host rejected the database incarnation or profile. Reconnect and verify the destination.",
        HttpStatusCode.TooManyRequests => "The host is busy. Retry this read later.",
        HttpStatusCode.UpgradeRequired => "The host requires HTTPS.",
        >= HttpStatusCode.OK and < HttpStatusCode.MultipleChoices => null,
        _ => $"The host rejected the request (HTTP {(int)status}). No outcome was established."
    };

    private static async Task<JsonDocument> ReadInspection(HttpResponseMessage response, CancellationToken cancellation)
    {
        const int maximum = 1024 * 1024;
        if (response.Content.Headers.ContentLength > maximum)
        {
            throw new InvalidDataException("The inspection response exceeds 1 MiB.");
        }

        await using var input = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
        using var output = new MemoryStream();
        byte[] buffer = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(buffer, cancellation).ConfigureAwait(false)) != 0)
        {
            if (output.Length + count > maximum)
            {
                throw new InvalidDataException("The inspection response exceeds 1 MiB.");
            }

            output.Write(buffer, 0, count);
        }
        return JsonDocument.Parse(output.ToArray());
    }
}
