namespace EntityForge.Db.Kernel;

/// <summary>Describes one encoded durable commit in an ordered transfer window.</summary>
/// <param name="SourceDatabaseIncarnation">Identity of the database whose history is being transferred.</param>
/// <param name="SourceSequence">One-based position within this export window, independent of the database version.</param>
/// <param name="Version">Authoritative database version of the commit.</param>
/// <param name="TransactionId">Original transaction identity, retained when applying or retrying the commit.</param>
/// <param name="Profile">Profile that interprets the payload.</param>
/// <param name="CodecId">Identifier of the payload codec.</param>
/// <param name="CodecVersion">Version of the payload codec.</param>
/// <param name="Payload">Encoded transaction contents.</param>
/// <param name="ContentHash">SHA-256 digest of the payload.</param>
/// <param name="PreviousChainHash">Seed or preceding entry's integrity-chain hash.</param>
/// <param name="ChainHash">Integrity-chain hash after incorporating this entry.</param>
/// <remarks>The record does not copy its memory buffers. Keep their contents unchanged while the commit is in use. Hashes provide integrity checks, not sender authentication or proof of a complete transfer without a trusted expected boundary.</remarks>
public sealed record DatabaseTransferCommit(Guid SourceDatabaseIncarnation, long SourceSequence,
    DatabaseVersion Version, Guid TransactionId,
    DatabaseProfileId Profile, string CodecId, int CodecVersion, ReadOnlyMemory<byte> Payload,
    ReadOnlyMemory<byte> ContentHash, ReadOnlyMemory<byte> PreviousChainHash,
    ReadOnlyMemory<byte> ChainHash)
{
    private DatabaseStorageLayout _storageLayout = DatabaseStorageLayout.Current;

    /// <summary>Gets the storage layout required to interpret the transferred commit.</summary>
    /// <remarks>Defaults to the current layout for programmatic construction; serialized input must include it. The destination must use a compatible layout.</remarks>
    [System.Text.Json.Serialization.JsonRequired]
    public DatabaseStorageLayout StorageLayout
    {
        get => _storageLayout; init => _storageLayout = value ?? throw new InvalidDataException("Missing storage layout.");
    }
}


/// <summary>Exposes low-level retained-history export and authoritative commit application for database transfer integrations.</summary>
/// <remarks>These operations do not provide a complete transfer protocol or authenticate its sender. The kernel rejects them in partition-local versioning mode.</remarks>
public interface IDatabaseTransferRuntime
{
    /// <summary>Gets the database identity that transfer entries must preserve.</summary>
    Guid DatabaseIncarnation
    {
        get;
    }

    /// <summary>Gets the profile whose transaction payloads this runtime accepts.</summary>
    DatabaseProfileId Profile
    {
        get;
    }

    /// <summary>Gets the currently published database version.</summary>
    DatabaseVersion CurrentVersion
    {
        get;
    }

    /// <summary>Retains transfer history after a captured durable boundary until the returned lease is released.</summary>
    /// <param name="cancellationToken">Cancels waiting to acquire the retention boundary.</param>
    /// <returns>A lease owned by the caller; dispose it when the transfer no longer needs the protected history.</returns>
    /// <remarks>Acquisition does not recover history already reclaimed, create a snapshot, or produce a backup.</remarks>
    ValueTask<DatabaseTransferRetentionLease> RetainTransferHistoryAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Exports retained durable commits in ascending version order within a bounded version interval.</summary>
    /// <param name="afterExclusive">Lower version boundary, excluded from the export.</param>
    /// <param name="throughInclusive">Upper version boundary, included in the export; it must not exceed the durable version.</param>
    /// <param name="cancellationToken">Cancels history acquisition or enumeration.</param>
    /// <returns>Encoded commits with sequence numbers and an integrity chain rooted at the lower boundary.</returns>
    /// <remarks>The kernel captures retained matching commits before yielding entries. The caller must protect required history against reclamation and validate the expected transfer window; an empty export alone does not prove that an older interval contained no commits.</remarks>
    IAsyncEnumerable<DatabaseTransferCommit> ExportTransferCommitsAsync(DatabaseVersion afterExclusive,
        DatabaseVersion throughInclusive, CancellationToken cancellationToken = default);

    /// <summary>Applies one transferred commit at its authoritative version and returns its receipt.</summary>
    /// <param name="commit">The immutable payload and metadata of the source commit.</param>
    /// <param name="cancellationToken">Cancellation for admission and cancellable stages; cancellation is not proof that a write did not commit.</param>
    /// <returns>The commit receipt, or the retained receipt for an accepted replay.</returns>
    /// <remarks>The kernel checks database identity, profile, codec, storage layout and payload hash. This method does not validate the transfer window's sequence or chain; the surrounding transfer protocol must do so. Preserve transaction identity and payload when resolving an ambiguous outcome.</remarks>
    ValueTask<DatabaseCommitReceipt> ApplyTransferCommitAsync(DatabaseTransferCommit commit,
        CancellationToken cancellationToken = default);
}

/// <summary>Protects transfer history after a captured boundary until asynchronously disposed.</summary>
/// <remarks>This lease is local to the owning runtime and is not a durable checkpoint or snapshot lease.</remarks>
public sealed class DatabaseTransferRetentionLease : IAsyncDisposable
{
    private readonly Func<Guid, ValueTask> _release;
    private readonly Guid _id;
    private int _disposed;

    internal DatabaseTransferRetentionLease(Guid id, DatabaseVersion boundary, Func<Guid, ValueTask> release)
    {
        _id = id;
        Boundary = boundary;
        _release = release;
    }

    /// <summary>Gets the durable boundary captured when retention was acquired.</summary>
    public DatabaseVersion Boundary
    {
        get;
    }

    /// <summary>Releases history retention once; subsequent calls complete without releasing it again.</summary>
    /// <returns>The asynchronous release operation.</returns>
    public ValueTask DisposeAsync() => Interlocked.Exchange(ref _disposed, 1) == 0
        ? _release(_id) : ValueTask.CompletedTask;
}
