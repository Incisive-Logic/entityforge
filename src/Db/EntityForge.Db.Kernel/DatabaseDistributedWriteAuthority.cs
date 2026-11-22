namespace EntityForge.Db.Kernel;

/// <summary>
/// Represents exclusive enrollment of one recovered database incarnation in distributed-write mode.
/// The lease must remain alive for the lifetime of the distributed participant.
/// </summary>
public sealed class DatabaseDistributedWriteLease : IAsyncDisposable
{
    private readonly DatabaseKernel _kernel;
    private readonly Guid _leaseId;
    private int _disposed;
    private readonly SemaphoreSlim _releaseGate = new(1, 1);

    internal DatabaseDistributedWriteLease(DatabaseKernel kernel, Guid leaseId, Guid databaseIncarnation)
    {
        _kernel = kernel;
        _leaseId = leaseId;
        DatabaseIncarnation = databaseIncarnation;
    }

    /// <summary>Gets the immutable physical ownership layout and format version of the enrolled database.</summary>
    public DatabaseStorageLayout StorageLayout => _kernel.StorageLayout;

    /// <summary>Gets the durable database incarnation enrolled by this lease.</summary>
    public Guid DatabaseIncarnation
    {
        get;
    }

    /// <summary>The durable operation creation-time floor, in Unix milliseconds, installed by retention.</summary>
    public long AuthoritativeOperationFloor
    {
        get
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return _kernel.AuthoritativeOperationFloor;
        }
    }

    /// <summary>Publishes a retention checkpoint that advances the authoritative operation floor and reclaims specified expired retry evidence.</summary>
    /// <param name="retainedSinceMilliseconds">Positive Unix-millisecond creation-time floor, no later than the current UTC time and no earlier than the installed floor.</param>
    /// <param name="expiredOperations">Operation identities older than the floor whose retry evidence may be reclaimed. The set is copied before asynchronous work begins.</param>
    /// <param name="cancellationToken">Cancels waiting or checkpoint work; cancellation does not undo a completed publication.</param>
    /// <returns>A task that completes after retention checkpoint publication.</returns>
    /// <exception cref="ObjectDisposedException">The lease has been released.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="expiredOperations"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The floor is nonpositive or in the future.</exception>
    /// <exception cref="InvalidOperationException">Enrollment is no longer current, the floor moves backwards, an identity is not expired, transfer retention is active, or the kernel cannot checkpoint.</exception>
    /// <remarks>The distributed coordinator must establish a safe retention horizon before calling this method. The lease is a local ownership boundary, not proof of cluster-wide agreement. Expired retry evidence is not restored by later releasing the lease.</remarks>
    public async ValueTask RetainOperationsAsync(long retainedSinceMilliseconds, IReadOnlySet<Guid> expiredOperations,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(expiredOperations);
        if (retainedSinceMilliseconds <= 0 || retainedSinceMilliseconds > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
        {
            throw new ArgumentOutOfRangeException(nameof(retainedSinceMilliseconds));
        }

        var detached = expiredOperations.ToHashSet();
        await _kernel.RetainOperationsAsync(_leaseId, retainedSinceMilliseconds, detached, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Releases distributed enrollment after draining its checkpoint-compaction work.</summary>
    /// <returns>A task that completes when local write admission is no longer fenced by this enrollment.</returns>
    /// <remarks>Concurrent releases are serialized and calls after a successful release have no effect. Stop the distributed participant before releasing its lease. A failed release is not marked complete and may leave the database requiring recovery.</remarks>
    public async ValueTask DisposeAsync()
    {
        await _releaseGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            await _kernel.ReleaseDistributedWriteAuthorityAsync(_leaseId).ConfigureAwait(false);
            Volatile.Write(ref _disposed, 1);
        }
        finally
        {
            _releaseGate.Release();
        }
    }
}

/// <summary>Exposes the neutral distributed-write ownership boundary of a database kernel.</summary>
public interface IDatabaseDistributedWriteAuthority
{
    /// <summary>Exclusively enrolls the recovered database incarnation in distributed-write mode.</summary>
    /// <param name="cancellationToken">Cancels waiting to acquire local write authority.</param>
    /// <returns>The ownership lease, which must remain alive while the distributed participant uses this database.</returns>
    /// <exception cref="InvalidOperationException">The kernel is not ready, does not permit external versioning, lacks a durable incarnation, is already enrolled, or has an incompatible local retention horizon.</exception>
    /// <remarks>Enrollment fences ordinary local writes; it does not create a distributed coordinator, grant user permissions, or establish cluster-wide consensus.</remarks>
    ValueTask<DatabaseDistributedWriteLease> EnterDistributedWriteModeAsync(
        CancellationToken cancellationToken = default);
}
