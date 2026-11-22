using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EntityForge.Db.AppModel.Connections;

/// <summary>Identifies the connected database and server-side destinations shown before an administration job.</summary>
/// <param name="Version">Target contract version; the client requires version 1.</param>
/// <param name="DatabaseIncarnation">Identity of the database incarnation that will receive the operation.</param>
/// <param name="Profile">Server-reported database profile.</param>
/// <param name="DataDirectory">Server-side data directory, not a path on the client machine.</param>
/// <param name="BackupDirectory">Server-side directory used to construct a backup destination.</param>
public sealed record AdministrationTarget(int Version, Guid DatabaseIncarnation, string Profile, string DataDirectory, string BackupDirectory);

/// <summary>Captures a reviewed job identity, connection and expected server target before submission.</summary>
/// <param name="Id">Job identity retained for subsequent outcome lookup.</param>
/// <param name="DatabaseIncarnation">Database identity against which the plan was prepared.</param>
/// <param name="Endpoint">Connection endpoint that must still be selected when submitting.</param>
/// <param name="Operation">Operation name; an outcome-only lookup may use an empty name.</param>
/// <param name="BackupName">Backup name for a backup operation, otherwise normally null.</param>
/// <param name="ExpectedTarget">Reviewed server path; an outcome-only lookup may leave it empty.</param>
public sealed record AdministrationPlan(Guid Id, Guid DatabaseIncarnation, Uri Endpoint, string Operation, string? BackupName, string ExpectedTarget);

/// <summary>Reports a retained server job outcome; a running or unknown receipt is not proof of completion.</summary>
/// <param name="Version">Receipt contract version; the client requires version 1.</param>
/// <param name="Id">Job identity used for submission and subsequent lookup.</param>
/// <param name="DatabaseIncarnation">Database incarnation that owns the job.</param>
/// <param name="Operation">Checkpoint, backup, optimize or reset-observations operation name.</param>
/// <param name="Target">Server-side target of the operation.</param>
/// <param name="State">Running, Succeeded, Failed or Unknown.</param>
/// <param name="UpdatedAt">Server-reported time of the receipt's last update.</param>
/// <param name="Result">Operation-specific result object, required for a successful receipt.</param>
/// <param name="Message">Optional server-provided explanation.</param>
public sealed record AdministrationReceipt(int Version, Guid Id, Guid DatabaseIncarnation, string Operation,
    string Target, string State, DateTimeOffset UpdatedAt, JsonElement? Result, string? Message);

internal sealed record AdministrationRequest(Guid DatabaseIncarnation, string Operation, string? BackupName, string ExpectedTarget);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(AdministrationTarget))]
[JsonSerializable(typeof(AdministrationReceipt))]
[JsonSerializable(typeof(AdministrationRequest))]
internal sealed partial class AdministrationJsonContext : JsonSerializerContext;

public sealed partial class EntityForgeConnectionSession
{
    private CancellationTokenSource? _administrationRequest;

    /// <summary>Gets the most recent submitted or resolved plan, including its identity for outcome lookup.</summary>
    public AdministrationPlan? LastAdministrationPlan
    {
        get; private set;
    }

    /// <summary>Gets the last validated receipt, or null while waiting or when an outcome was not established.</summary>
    public AdministrationReceipt? AdministrationReceipt
    {
        get; private set;
    }

    /// <summary>Gets the latest request error or uncertain-outcome explanation; null does not imply job completion.</summary>
    public string? AdministrationError
    {
        get; private set;
    }

    /// <summary>Gets whether the client is awaiting an administration response, independently of server job state.</summary>
    public bool AdministrationBusy => _administrationRequest is not null;

    /// <summary>Occurs when administration request state changes; handlers are not dispatched to a UI thread.</summary>
    public event EventHandler? AdministrationChanged;

    /// <summary>Gets whether the connected host advertises retained administration jobs; this is not an authorization grant.</summary>
    public bool SupportsAdministrationJobs => Current?.Capabilities.Surfaces.Any(s =>
        s.Identifier == EntityForge.Db.Transport.Contracts.EntityForgeSurfaceIds.Administration && s.Features.Contains("administration-jobs-v1")) == true;

    /// <summary>Gets whether both optimization and retained semantic optimization jobs are advertised.</summary>
    public bool SupportsOptimizationJobs => SupportsOptimization && Current?.Capabilities.Surfaces.Any(s =>
        s.Identifier == EntityForge.Db.Transport.Contracts.EntityForgeSurfaceIds.Administration && s.Features.Contains("semantic-optimization-jobs-v1")) == true;

    /// <summary>Reads and validates the current server target and returns a plan for user review without submitting a job.</summary>
    /// <param name="operation">One of checkpoint, backup, optimize or reset-observations.</param>
    /// <param name="backupName">For backup, 1–80 ASCII letters, digits, hyphens or underscores.</param>
    /// <returns>A fresh job identity bound to the current endpoint, database incarnation and expected server path.</returns>
    /// <exception cref="ArgumentException">The operation or required backup name is invalid.</exception>
    /// <exception cref="InvalidOperationException">Capabilities, authorization, connection or target identity do not permit preparing the plan.</exception>
    /// <remarks>Preparation uses the connection lifetime and discovery timeout. Review the returned target before calling <see cref="RunAdministrationAsync"/>.</remarks>
    public async Task<AdministrationPlan> PrepareAdministrationAsync(string operation, string? backupName)
    {
        if (!SupportsAdministrationJobs || CanAdminister == false)
        {
            throw new InvalidOperationException("This connection does not allow supported administration jobs.");
        }

        if (operation is not ("checkpoint" or "backup" or "optimize" or "reset-observations"))
        {
            throw new ArgumentException("Choose a supported operation.");
        }

        if (operation is "optimize" or "reset-observations" && !SupportsOptimizationJobs)
        {
            throw new InvalidOperationException("This host does not advertise retained Semantic optimization jobs.");
        }

        if (operation == "backup" && (string.IsNullOrWhiteSpace(backupName) || backupName.Length > 80 ||
            backupName.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))))
        {
            throw new ArgumentException("Use 1–80 letters, digits, hyphens or underscores for the backup name.");
        }

        var snapshot = Current!;
        var client = ConnectedClient;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime!.Token);
        request.CancelAfter(DiscoveryTimeout);
        using var response = await client.GetAsync("v1/administration/target", HttpCompletionOption.ResponseHeadersRead, request.Token);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(InspectionError(response.StatusCode));
        }

        using var json = await ReadInspection(response, request.Token);
        var target = json.RootElement.Deserialize(AdministrationJsonContext.Default.AdministrationTarget) ?? throw new InvalidDataException("Missing administration target.");
        if (!ReferenceEquals(Current, snapshot) || target.Version != 1 || target.DatabaseIncarnation != snapshot.Capabilities.DatabaseIncarnation)
        {
            throw new InvalidOperationException("The connection or database identity changed. Review the target again.");
        }

        Guid id = Guid.NewGuid();
        string path = operation != "backup" ? target.DataDirectory : target.BackupDirectory.TrimEnd('/', '\\') +
            (target.BackupDirectory.Contains('\\') ? "\\" : "/") + $"{backupName}-{id:N}.efbackup";
        return new(id, target.DatabaseIncarnation, snapshot.Profile.Endpoint, operation, backupName, path);
    }

    /// <summary>Submits a reviewed plan once and records the returned receipt or an uncertain-outcome error.</summary>
    /// <param name="plan">The plan whose endpoint and database identity must still match the connection.</param>
    /// <returns>A task completing when the client request ends, not necessarily when the server job completes.</returns>
    /// <exception cref="InvalidOperationException">Another request is active, the reviewed connection is unavailable, or this session already submitted the identity.</exception>
    /// <remarks>Inspect <see cref="AdministrationReceipt"/> and <see cref="AdministrationError"/>. After an interrupted response, call <see cref="ResolveAdministrationAsync"/> with the same identity rather than submitting a new job.</remarks>
    public Task RunAdministrationAsync(AdministrationPlan plan) => AdministrationRequestAsync(plan, submit: true);

    /// <summary>Looks up a retained job receipt on the connected database without submitting work.</summary>
    /// <param name="id">Nonempty identity of the job whose outcome is uncertain.</param>
    /// <returns>A task completing after lookup state is recorded in the receipt and error properties.</returns>
    /// <exception cref="InvalidOperationException">No matching authorized connection is available, the identity is empty, or another request is active.</exception>
    /// <remarks>A missing receipt leaves the outcome unknown; it does not establish that the job failed or can safely be repeated.</remarks>
    public Task ResolveAdministrationAsync(Guid id)
    {
        if (Current?.Capabilities.DatabaseIncarnation is not { } database || id == Guid.Empty)
        {
            throw new InvalidOperationException("Connect to the same database and enter a valid job ID.");
        }

        var plan = LastAdministrationPlan is { } last && last.Id == id && last.DatabaseIncarnation == database && last.Endpoint == Current.Profile.Endpoint
            ? last : new AdministrationPlan(id, database, Current.Profile.Endpoint, "", null, "");
        return AdministrationRequestAsync(plan, submit: false);
    }

    private async Task AdministrationRequestAsync(AdministrationPlan plan, bool submit)
    {
        HttpClient client;
        CancellationTokenSource request;
        lock (_gate)
        {
            if (AdministrationBusy)
            {
                throw new InvalidOperationException("An administration request is already in progress.");
            }

            if (submit && plan.Operation is "optimize" or "reset-observations" && !SupportsOptimizationJobs)
            {
                throw new InvalidOperationException("The host no longer advertises retained Semantic optimization jobs.");
            }

            if (!SupportsAdministrationJobs || CanAdminister == false || Current?.Profile.Endpoint != plan.Endpoint || Current.Capabilities.DatabaseIncarnation != plan.DatabaseIncarnation)
            {
                throw new InvalidOperationException("The reviewed connection is no longer available or authorized. Review it again.");
            }
            // A repeated click must resolve the original request, never submit it again.
            if (submit && LastAdministrationPlan?.Id == plan.Id)
            {
                throw new InvalidOperationException("This job was already submitted. Use Check outcome.");
            }

            client = ConnectedClient;
            request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime!.Token);
            request.CancelAfter(TimeSpan.FromSeconds(15));
            _administrationRequest = request;
            LastAdministrationPlan = plan;
            AdministrationReceipt = null;
            AdministrationError = null;
        }
        AdministrationChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            string route = $"v1/administration/jobs/{plan.Id:D}";
            using var message = new HttpRequestMessage(submit ? HttpMethod.Post : HttpMethod.Get,
                submit ? route : route + $"?databaseIncarnation={plan.DatabaseIncarnation:D}");
            if (submit)
            {
                message.Content = JsonContent.Create(new AdministrationRequest(plan.DatabaseIncarnation, plan.Operation, plan.BackupName, plan.ExpectedTarget),
                AdministrationJsonContext.Default.AdministrationRequest);
            }

            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, request.Token);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidDataException(response.StatusCode == System.Net.HttpStatusCode.NotFound
                ? "No retained receipt was found. Outcome unknown; this does not prove the job failed or make replay safe." : InspectionError(response.StatusCode));
            }

            using var json = await ReadInspection(response, request.Token);
            var receipt = json.RootElement.Deserialize(AdministrationJsonContext.Default.AdministrationReceipt) ?? throw new InvalidDataException("Missing job receipt.");
            if (receipt.Version != 1 || receipt.Id != plan.Id || receipt.DatabaseIncarnation != plan.DatabaseIncarnation ||
                receipt.Operation is not ("checkpoint" or "backup" or "optimize" or "reset-observations") ||
                (!string.IsNullOrEmpty(plan.Operation) && (receipt.Operation != plan.Operation || receipt.Target != plan.ExpectedTarget)) ||
                receipt.State is not ("Running" or "Succeeded" or "Failed" or "Unknown") ||
                (receipt.State == "Succeeded" && receipt.Result is not { ValueKind: JsonValueKind.Object }))
            {
                throw new InvalidDataException("The job receipt is invalid or belongs to a different target.");
            }

            lock (_gate)
            {
                if (_administrationRequest != request || client != _client)
                {
                    return;
                }

                AdministrationReceipt = receipt;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidDataException or JsonException or ObjectDisposedException)
        {
            lock (_gate)
            {
                if (_administrationRequest == request)
                {
                    AdministrationError = ex is InvalidDataException ? ex.Message : "The response was interrupted. Outcome unknown: use Check outcome with the same job ID. Stopping the wait does not stop server work.";
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                if (_administrationRequest == request)
                {
                    _administrationRequest = null;
                }

                request.Dispose();
            }
            AdministrationChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Cancels the client's current wait without requesting cancellation of server work.</summary>
    /// <remarks>The job may continue. Reconnect to the same database and resolve the retained job identity before deciding what to do next. With no active request, this method does nothing.</remarks>
    public void StopAdministrationWait()
    {
        lock (_gate)
        {
            if (_administrationRequest is null)
            {
                return;
            }

            var request = _administrationRequest;
            _administrationRequest = null;
            request.Cancel();
            AdministrationReceipt = null;
            AdministrationError = "Stopped waiting. The server operation may continue; reconnect to the same database and check this job ID.";
        }
        AdministrationChanged?.Invoke(this, EventArgs.Empty);
    }
}
