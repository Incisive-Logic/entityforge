namespace EntityForge.Db.Rdf;

/// <summary>Retains one exact RDF read view for its owning database instance until disposed.</summary>
/// <remarks>Do not pass this lease to another database, even when both databases report the same version. A version number does not identify the database or retain a view by itself.</remarks>
public sealed class RdfSnapshotLease : IDisposable
{
    private Action? _release;
    private readonly Func<bool>? _context;
    private readonly DatabaseVersion _version;

    internal RdfSnapshotLease(object owner, DatabaseVersion version, Action release, Func<bool>? context = null)
    {
        Owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _version = version;
        _release = release ?? throw new ArgumentNullException(nameof(release));
        _context = context;
    }

    internal object Owner
    {
        get;
    }

    /// <summary>Gets the leased database version.</summary>
    public DatabaseVersion Version => IsActive ? _version : throw new ObjectDisposedException(nameof(RdfSnapshotLease));

    /// <summary>Gets whether the lease remains active.</summary>
    public bool IsActive => Volatile.Read(ref _release) is not null && (_context?.Invoke() ?? true);

    /// <summary>Releases the retained view. Repeated disposal has no effect.</summary>
    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}

/// <summary>Indicates that an RDF version is outside the retained history.</summary>
public sealed class RdfSnapshotTooOldException(DatabaseVersion requested)
    : EntityForgeDbException($"RDF snapshot {requested.Value} is no longer retained.")

{ /// <summary>Gets the unavailable version.</summary>
    public DatabaseVersion Requested { get; } = requested;
}
