using EntityForge.Db;

namespace EntityForge.Db.Graph;

/// <summary>Protects a graph snapshot version from reclamation until the lease is disposed.</summary>
/// <remarks>
/// Obtain a lease from the database that will perform the read and keep it alive for all
/// snapshot pages. A lease belongs to one database instance; it cannot be transferred to
/// another instance or persisted for use after reopen. It retains graph state, not change-feed
/// history, and does not create a write transaction. Dispose it promptly after the last read.
/// Reading <see cref="Version"/> after disposal does not restore snapshot protection.
/// </remarks>
public sealed class GraphSnapshotLease : IDisposable
{
    private Action? _release;

    internal GraphSnapshotLease(object owner, DatabaseVersion version, Action release)
    {
        Owner = owner ?? throw new ArgumentNullException(nameof(owner));
        Version = version;
        _release = release ?? throw new ArgumentNullException(nameof(release));
    }

    /// <summary>Gets the protected database version.</summary>
    public DatabaseVersion Version
    {
        get;
    }

    internal object Owner
    {
        get;
    }

    internal bool IsActive => Volatile.Read(ref _release) is not null;

    /// <summary>Releases the snapshot protection. Repeated calls have no effect.</summary>
    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}
