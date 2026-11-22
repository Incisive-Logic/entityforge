using System.Net.Http.Json;

namespace EntityForge.Db.AppModel.Queries;

/// <summary>A client-owned handle for remote semantic snapshot retention.</summary>
/// <remarks>Keep the lease alive while paging or inspecting its results, then dispose it. Provider expiry and renewal failures can still make the snapshot unavailable; the version alone does not prove an active lease.</remarks>
public interface ISemanticQuerySnapshotLease : IDisposable
{
    /// <summary>Gets the retained snapshot's version, not a renewal or health indication.</summary>
    long SnapshotVersion
    {
        get;
    }
}

/// <summary>Acquires retention handles for semantic projection queries.</summary>
public interface ISemanticQuerySnapshotProvider
{
    /// <summary>Acquires a snapshot lease whose lifetime belongs to the caller.</summary>
    /// <param name="cancellationToken">Cancellation for acquisition, not for the lifetime of a successfully returned lease.</param>
    /// <returns>A disposable snapshot handle.</returns>
    ValueTask<ISemanticQuerySnapshotLease> AcquireAsync(CancellationToken cancellationToken = default);
}

/// <summary>Keeps a query snapshot alive while its session owns the results.</summary>
/// <param name="client">Authenticated client with the database base address. The caller owns it and must keep it usable for renewal and release.</param>
/// <remarks>
/// Requests a five-minute lease and attempts renewal once per minute. A renewal failure
/// ends maintenance; subsequent requests detect snapshot loss, while already cached
/// views remain available. There is no renewal-health notification on this interface.
/// Disposing the returned lease initiates asynchronous cancellation and best-effort
/// release; it does not wait for a server acknowledgment. Server expiry bounds retention
/// when release cannot reach the host. The provider does not dispose the supplied client.
/// </remarks>
public sealed class HttpSemanticQuerySnapshotProvider(HttpClient client) : ISemanticQuerySnapshotProvider
{
    private const string Route = "v1/semantic/snapshot-leases";
    private const int DurationSeconds = 300;
    private static readonly TimeSpan RenewalInterval = TimeSpan.FromMinutes(1);

    /// <inheritdoc cref="ISemanticQuerySnapshotProvider.AcquireAsync"/>
    /// <exception cref="HttpRequestException">The acquisition request fails or returns an unsuccessful HTTP status.</exception>
    /// <exception cref="InvalidDataException">The host returns no lease body.</exception>
    public async ValueTask<ISemanticQuerySnapshotLease> AcquireAsync(CancellationToken cancellationToken = default)
    {
        using var response = await client.PostAsJsonAsync(Route, new SnapshotLeaseRequest(DurationSeconds), QueryJsonContext.Default.SnapshotLeaseRequest, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var lease = await response.Content.ReadFromJsonAsync(QueryJsonContext.Default.SnapshotLeaseResponse, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The host returned no snapshot lease.");
        return new Lease(client, lease);
    }


    private sealed class Lease : ISemanticQuerySnapshotLease
    {
        private readonly CancellationTokenSource _lifetime = new();
        private readonly Task _maintenance;
        private int _disposed;

        public long SnapshotVersion
        {
            get;
        }

        public Lease(HttpClient client, SnapshotLeaseResponse lease)
        {
            SnapshotVersion = lease.SnapshotVersion;
            _maintenance = MaintainAsync(client, lease);
        }

        private async Task MaintainAsync(HttpClient client, SnapshotLeaseResponse lease)
        {
            string route = $"{Route}/{lease.LeaseId}";
            try
            {
                while (true)
                {
                    await Task.Delay(RenewalInterval, _lifetime.Token).ConfigureAwait(false);
                    using var response = await client.PutAsJsonAsync(route,
                        new SnapshotLeaseRequest(DurationSeconds, lease.Revision), QueryJsonContext.Default.SnapshotLeaseRequest, _lifetime.Token).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    lease = await response.Content.ReadFromJsonAsync(QueryJsonContext.Default.SnapshotLeaseResponse, _lifetime.Token).ConfigureAwait(false)
                        ?? throw new InvalidDataException("The host returned no renewed snapshot lease.");
                }
            }
            // A disconnected host or lost lease is reported by the next query request; cached views remain usable.
            catch (Exception)
            {
            }
            finally
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    using var response = await client.DeleteAsync(route, timeout.Token).ConfigureAwait(false);
                }
                catch (Exception)
                {
                } // Server expiry bounds retention if release cannot reach the host.
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _ = StopAsync();
            }
        }

        private async Task StopAsync()
        {
            _lifetime.Cancel();
            await _maintenance.ConfigureAwait(false);
            _lifetime.Dispose();
        }
    }
}
