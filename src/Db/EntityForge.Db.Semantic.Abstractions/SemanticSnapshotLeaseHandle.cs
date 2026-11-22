namespace EntityForge.Db.Semantic;

/// <summary>
/// Owns one durable Semantic snapshot lease and releases it when disposed. The handle does not renew
/// implicitly; callers choose when lease lifetime should be extended.
/// </summary>
public sealed class SemanticSnapshotLeaseHandle : IAsyncDisposable
{
    private readonly ISnapshotLeaseService _service;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SnapshotLease _lease;
    private bool _disposed;

    internal SemanticSnapshotLeaseHandle(ISnapshotLeaseService service, SnapshotLease lease)
    {
        _service = service;
        _lease = lease;
    }

    /// <summary>Gets the latest durable lease value acquired or renewed through this handle.</summary>
    public SnapshotLease Lease => _lease;

    /// <summary>Renews the owned lease using its current optimistic revision.</summary>
    public async ValueTask<SnapshotLease> RenewAsync(TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _lease = await _service.RenewSnapshotLeaseAsync(
                _lease.LeaseId, _lease.Revision, duration, cancellationToken).ConfigureAwait(false);
            return _lease;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Releases the durable lease. Repeated disposal is harmless.</summary>
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            await _service.ReleaseSnapshotLeaseAsync(_lease.LeaseId).ConfigureAwait(false);
            _disposed = true;
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>Provides ownership-oriented snapshot lease conveniences.</summary>
public static class SemanticSnapshotLeaseExtensions
{
    /// <summary>Acquires a durable snapshot lease that is released by asynchronous disposal.</summary>
    public static async ValueTask<SemanticSnapshotLeaseHandle> AcquireSnapshotLeaseHandleAsync(
        this ISnapshotLeaseService service, TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        SnapshotLease lease = await service.AcquireSnapshotLeaseAsync(duration, cancellationToken)
            .ConfigureAwait(false);
        return new(service, lease);
    }
}
