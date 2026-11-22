using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using EntityForge.Db.Semantic;

namespace EntityForge.Db.AppModel.Optimizations;

/// <summary>Administrative client for evidence-led Semantic physical property placement.</summary>
public interface ISemanticPropertyOptimizationClient
{
    /// <summary>Reads measured semantic property-placement recommendations without applying changes.</summary>
    ValueTask<SemanticPropertyOptimizationReport> AnalyzeAsync(CancellationToken cancellationToken = default);

    /// <summary>Applies at most the requested positive number of property-storage changes.</summary>
    ValueTask<SemanticPropertyOptimizationResult> ApplyAsync(int maximumChanges = 1,
        CancellationToken cancellationToken = default);

    /// <summary>Clears accumulated optimization observations without changing logical data.</summary>
    ValueTask ResetObservationsAsync(CancellationToken cancellationToken = default);
}

/// <summary>Calls the host's semantic property-storage optimization administration endpoints.</summary>
public sealed class HttpSemanticPropertyOptimizationClient(HttpClient client)
    : ISemanticPropertyOptimizationClient
{
    private const string Path = "v1/semantic/property-storage/optimization";

    /// <inheritdoc />
    public async ValueTask<SemanticPropertyOptimizationReport> AnalyzeAsync(
        CancellationToken cancellationToken = default) =>
        await client.GetFromJsonAsync(Path,
            SemanticPropertyOptimizationJsonContext.Default.SemanticPropertyOptimizationReport,
            cancellationToken).ConfigureAwait(false)
        ?? throw new InvalidDataException("EntityForgeDB returned no Semantic optimization report.");

    /// <inheritdoc />
    public async ValueTask<SemanticPropertyOptimizationResult> ApplyAsync(int maximumChanges = 1,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumChanges, 1);
        using HttpResponseMessage response = await client.PostAsync(
            $"{Path}?maximumChanges={maximumChanges}", null, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync(
            SemanticPropertyOptimizationJsonContext.Default.SemanticPropertyOptimizationResult,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("EntityForgeDB returned no Semantic optimization result.");
    }

    /// <inheritdoc />
    public async ValueTask ResetObservationsAsync(CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await client.DeleteAsync(
            $"{Path}/observations", cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }
}

/// <summary>Describes an optimization session's availability and request lifecycle.</summary>
public enum SemanticPropertyOptimizationState
{
    /// <summary>No compatible optimization client is bound.</summary>
    Unavailable,
    /// <summary>The session is available but has no current report.</summary>
    Idle,
    /// <summary>An optimization request is in progress.</summary>
    Loading,
    /// <summary>A current optimization report is available.</summary>
    Ready,
    /// <summary>The last request failed.</summary>
    Failed,
    /// <summary>The last request was cancelled.</summary>
    Cancelled
}

/// <summary>Distinguishes confirmed administrative changes from uncertain mutation outcomes.</summary>
public enum OptimizationMutationOutcome
{
    /// <summary>No mutation outcome has been recorded.</summary>
    None,
    /// <summary>The server confirmed the mutation result.</summary>
    Confirmed,
    /// <summary>The request may have changed server state but no definitive outcome was received.</summary>
    Unknown
}

/// <summary>Owns one connection's optimization requests and their observable outcomes.</summary>
public sealed class SemanticPropertyOptimizationSession : IDisposable
{
    private readonly object _gate = new();
    private ISemanticPropertyOptimizationClient? _client;
    private CancellationTokenSource? _execution;

    /// <summary>Occurs when optimization request state or its report changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Gets the current request lifecycle state.</summary>
    public SemanticPropertyOptimizationState State
    {
        get; private set;
    }

    /// <summary>Gets the latest optimization report, if available.</summary>
    public SemanticPropertyOptimizationReport? Report
    {
        get; private set;
    }

    /// <summary>Gets the latest user-facing failure description, if any.</summary>
    public string? Error
    {
        get; private set;
    }

    /// <summary>Gets whether the last administrative mutation was confirmed or remains uncertain.</summary>
    public OptimizationMutationOutcome MutationOutcome
    {
        get; private set;
    }

    /// <summary>Gets whether a client is bound and no request is currently loading.</summary>
    public bool CanRun => _client is not null && State != SemanticPropertyOptimizationState.Loading;

    /// <summary>Gets whether a ready report permits mutation and no earlier mutation outcome remains unknown.</summary>
    public bool CanMutate => CanRun && State == SemanticPropertyOptimizationState.Ready && Report is not null && MutationOutcome != OptimizationMutationOutcome.Unknown;

    /// <summary>Binds a client and cancels previous work, optionally preserving an uncertain mutation outcome.</summary>
    public void Bind(ISemanticPropertyOptimizationClient? client, bool preserveUnknownOutcome = false)
    {
        lock (_gate)
        {
            var previous = _execution;
            _execution = null;
            previous?.Cancel();
            _client = client;
            Report = null;
            Error = null;
            if (!preserveUnknownOutcome || MutationOutcome != OptimizationMutationOutcome.Unknown)
            {
                MutationOutcome = OptimizationMutationOutcome.None;
            }

            State = client is null ? SemanticPropertyOptimizationState.Unavailable : SemanticPropertyOptimizationState.Idle;
        }
        OnChanged();
    }

    /// <summary>Refreshes the optimization report and observable request state.</summary>
    public ValueTask RefreshAsync(CancellationToken cancellationToken = default) => RunAsync(null, cancellationToken);

    /// <summary>Applies the next admitted optimization changes and tracks whether their outcome is confirmed.</summary>
    public ValueTask ApplyNextAsync(CancellationToken cancellationToken = default) =>
        RunAsync(async (client, token) => { await client.ApplyAsync(1, token).ConfigureAwait(false); }, cancellationToken);

    /// <summary>Requests an observation reset and tracks its administrative outcome.</summary>
    public ValueTask ResetObservationsAsync(CancellationToken cancellationToken = default) =>
        RunAsync((client, token) => client.ResetObservationsAsync(token), cancellationToken);

    private async ValueTask RunAsync(Func<ISemanticPropertyOptimizationClient, CancellationToken, ValueTask>? mutation,
        CancellationToken cancellationToken)
    {
        ISemanticPropertyOptimizationClient client;
        CancellationTokenSource current;
        lock (_gate)
        {
            if (!CanRun || (mutation is not null && !CanMutate))
            {
                return;
            }

            client = _client!;
            current = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _execution = current;
            State = SemanticPropertyOptimizationState.Loading;
            Error = null;
        }
        OnChanged();
        try
        {
            current.Token.ThrowIfCancellationRequested();
            if (mutation is not null)
            {
                lock (_gate)
                {
                    if (_execution != current)
                    {
                        return;
                    }

                    MutationOutcome = OptimizationMutationOutcome.Unknown;
                    Report = null;
                }
                await mutation(client, current.Token).ConfigureAwait(false);
                lock (_gate)
                {
                    if (_execution != current)
                    {
                        return;
                    }

                    MutationOutcome = OptimizationMutationOutcome.Confirmed;
                }
            }
            current.Token.ThrowIfCancellationRequested();
            var report = await client.AnalyzeAsync(current.Token).ConfigureAwait(false);
            lock (_gate)
            {
                if (_execution != current)
                {
                    return;
                }

                current.Token.ThrowIfCancellationRequested();
                Report = report;
                State = SemanticPropertyOptimizationState.Ready;
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidDataException or JsonException or OperationCanceledException)
        {
            lock (_gate)
            {
                if (_execution != current)
                {
                    return;
                }

                State = exception is OperationCanceledException ? SemanticPropertyOptimizationState.Cancelled : SemanticPropertyOptimizationState.Failed;
                Error = MutationOutcome switch
                {
                    OptimizationMutationOutcome.Confirmed => "The server confirmed the change, but refreshing its report did not complete. Refresh the report; do not repeat the change.",
                    OptimizationMutationOutcome.Unknown => "The change outcome is unknown. Check server state before making another change. This session will not retry it.",
                    _ => exception is OperationCanceledException ? "The request was cancelled or timed out." : exception.Message,
                };
            }
        }
        finally
        {
            lock (_gate)
            {
                if (_execution == current)
                {
                    _execution = null;
                }

                current.Dispose();
            }
            OnChanged();
        }
    }

    /// <summary>Cancels the active optimization request.</summary>
    public void Cancel()
    {
        lock (_gate)
        {
            _execution?.Cancel();
        }
    }

    /// <summary>Cancels owned work and releases the session's request resources.</summary>
    public void Dispose() => Bind(null);

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(SemanticPropertyOptimizationReport))]
[JsonSerializable(typeof(SemanticPropertyOptimizationResult))]
internal sealed partial class SemanticPropertyOptimizationJsonContext : JsonSerializerContext;
