using EntityForge.Db.Client;
using EntityForge.Db.Transport.Contracts;

namespace EntityForge.Db.AppModel.Storage;

/// <summary>Manual refresh/probe session. Connection changes discard stale responses.</summary>
public sealed class StorageProfileSession : IDisposable
{
    private readonly object _gate = new();
    private IStorageProfileClient? _client;
    private CancellationTokenSource? _request;

    /// <summary>Occurs when storage-profile request state, report or error changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Gets the latest storage-profile report, if available.</summary>
    public StorageProfileReport? Report
    {
        get; private set;
    }

    /// <summary>Gets the latest user-facing storage-profile failure, if any.</summary>
    public string? Error
    {
        get; private set;
    }

    /// <summary>Gets whether a storage-profile request is in progress.</summary>
    public bool IsBusy
    {
        get
        {
            lock (_gate)
            {
                return _request is not null;
            }
        }
    }

    /// <summary>Gets whether a compatible storage-profile client is bound.</summary>
    public bool IsAvailable
    {
        get
        {
            lock (_gate)
            {
                return _client is not null;
            }
        }
    }

    /// <summary>Binds the current connection's storage-profile client and invalidates previous request state.</summary>
    public void Bind(IStorageProfileClient? client)
    {
        lock (_gate)
        {
            _request?.Cancel();
            _request = null;
            _client = client;
            Report = null;
            Error = null;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Refreshes the current storage-profile report.</summary>
    public Task RefreshAsync() => RunAsync(false);

    /// <summary>Runs the requested storage probe and updates the observable report.</summary>
    public Task ProbeAsync() => RunAsync(true);

    private async Task RunAsync(bool probe)
    {
        IStorageProfileClient client;
        CancellationTokenSource request;
        lock (_gate)
        {
            if (_request is not null || _client is null)
            {
                return;
            }

            client = _client;
            _request = request = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            Error = null;
        }
        Changed?.Invoke(this, EventArgs.Empty);
        try
        {
            var report = await (probe ? client.ProbeAsync(request.Token) : client.GetAsync(request.Token)).ConfigureAwait(false);
            lock (_gate)
            {
                if (ReferenceEquals(_request, request) && !request.IsCancellationRequested)
                {
                    Report = report;
                }
            }
        }
        catch (Exception e) when (e is HttpRequestException or EntityForgeRemoteException or OperationCanceledException or InvalidDataException or System.Text.Json.JsonException)
        {
            lock (_gate)
            {
                if (ReferenceEquals(_request, request))
                {
                    Error = e is OperationCanceledException
                    ? "Request cancelled or timed out. A server flush already in progress may finish later. Refresh status before running another probe."
                    : e.Message;
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_request, request))
                {
                    _request = null;
                }
            }
            request.Dispose();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Cancels the active storage-profile request.</summary>
    public void Cancel()
    {
        lock (_gate)
        {
            _request?.Cancel();
        }
    }

    /// <summary>Cancels owned requests and releases their resources.</summary>
    public void Dispose() => Bind(null);
}
