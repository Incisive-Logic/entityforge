namespace EntityForge.Db.Durability;

/// <summary>Owns an encoded profile payload with its profile and codec identity.</summary>
public sealed record DurableProfilePayload
{
    /// <summary>Validates the profile and positive codec version and copies the encoded payload into owned memory.</summary>
    public DurableProfilePayload(DatabaseProfileId profile, string codecId, int codecVersion, ReadOnlyMemory<byte> content)
    {
        if (!Enum.IsDefined(profile))
        {
            throw new ArgumentOutOfRangeException(nameof(profile));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(codecId);
        if (codecVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(codecVersion));
        }

        Profile = profile;
        CodecId = codecId;
        CodecVersion = codecVersion;
        Content = content.ToArray();
    }

    /// <summary>Gets the profile that can interpret this payload.</summary>
    public DatabaseProfileId Profile
    {
        get;
    }

    /// <summary>Gets the registered payload codec identifier.</summary>
    public string CodecId
    {
        get;
    }

    /// <summary>Gets the positive codec version required to decode the payload.</summary>
    public int CodecVersion
    {
        get;
    }

    /// <summary>Gets the owned encoded payload bytes.</summary>
    public ReadOnlyMemory<byte> Content
    {
        get;
    }
}

/// <summary>Classifies durable records so recovery distinguishes user changes from system and reservation evidence.</summary>
public enum DurableCommittedChangeKind : byte
{
    /// <summary>System work without user-data effects.</summary>
    System = 1,
    /// <summary>A user-data operation with no committed effects.</summary>
    UserDataNoChange = 2,
    /// <summary>A user-data operation with a nonempty committed-change payload.</summary>
    UserDataWithChanges = 3,
    // Physical redo only, at an already checkpointed logical version. Never a user commit.
    /// <summary>Physical redo at an already checkpointed logical version; not a user commit.</summary>
    CheckpointMaintenance = 4,
    // Kernel reservation evidence. Version is the next native position, a conservative
    // reclamation boundary, not an allocated data version or a user receipt.
    /// <summary>Kernel reservation evidence whose version is a reclamation boundary, not a user-data version.</summary>
    PartitionEvidence = 5,
    /// <summary>Durable logical timestamp and read-promise evidence.</summary>
    TimestampEvidence = 6
}

/// <summary>Binds a durable transaction to its database incarnation, version, profile payload and change classification.</summary>
public sealed record DurableTransactionEnvelope
{
    /// <summary>Gets or initializes optional durable logical timestamp evidence.</summary>
    public DurableTimestampState? Timestamp
    {
        get; init;
    }

    /// <summary>Creates a validated transaction envelope; user-data-with-changes classification requires a nonempty committed-change payload.</summary>
    public DurableTransactionEnvelope(Guid databaseIncarnation, DatabaseVersion version, Guid transactionId,
        DurableProfilePayload payload, DurableCommittedChangeKind changeKind, CommittedChangePayload? committedChanges, DatabaseStorageLayout? storageLayout = null,
        DatabaseVersioningMode versioningMode = DatabaseVersioningMode.LocalOrAuthoritative)
    {
        if (!Enum.IsDefined(versioningMode))
        {
            throw new ArgumentOutOfRangeException(nameof(versioningMode));
        }

        VersioningMode = versioningMode;
        if (databaseIncarnation == Guid.Empty)
        {
            throw new ArgumentException("A durable database incarnation is required.", nameof(databaseIncarnation));
        }

        if (version.Value <= DatabaseVersion.Initial.Value)
        {
            throw new ArgumentOutOfRangeException(nameof(version));
        }

        if (transactionId == Guid.Empty)
        {
            throw new ArgumentException("A durable transaction requires an identity.", nameof(transactionId));
        }

        StorageLayout = storageLayout ?? DatabaseStorageLayout.Current;
        DatabaseIncarnation = databaseIncarnation;
        Version = version;
        TransactionId = transactionId;
        Payload = payload ?? throw new ArgumentNullException(nameof(payload));
        if (!Enum.IsDefined(changeKind))
        {
            throw new ArgumentOutOfRangeException(nameof(changeKind));
        }

        bool requiresChanges = changeKind == DurableCommittedChangeKind.UserDataWithChanges;
        if (requiresChanges != (committedChanges is not null))
        {
            throw new ArgumentException("The committed-change classification and payload do not agree.", nameof(committedChanges));
        }

        if (committedChanges is { HasEffects: false })
        {
            throw new ArgumentException("A durable change payload must contain at least one effect.", nameof(committedChanges));
        }

        ChangeKind = changeKind;
        CommittedChanges = committedChanges;
    }

    /// <summary>Gets the storage layout attested by the transaction.</summary>
    public DatabaseStorageLayout StorageLayout
    {
        get;
    }

    /// <summary>Gets the version allocation mode under which the transaction was recorded.</summary>
    public DatabaseVersioningMode VersioningMode
    {
        get;
    }

    /// <summary>Gets the database incarnation that owns the durable record.</summary>
    public Guid DatabaseIncarnation
    {
        get;
    }

    /// <summary>Gets the transaction's positive native database version.</summary>
    public DatabaseVersion Version
    {
        get;
    }

    /// <summary>Gets the stable native transaction identity.</summary>
    public Guid TransactionId
    {
        get;
    }

    /// <summary>Gets the owned, codec-identified profile payload.</summary>
    public DurableProfilePayload Payload
    {
        get;
    }

    /// <summary>Gets the durable change classification.</summary>
    public DurableCommittedChangeKind ChangeKind
    {
        get;
    }

    /// <summary>Gets the nonempty user-change payload when required by the classification, otherwise null.</summary>
    public CommittedChangePayload? CommittedChanges
    {
        get;
    }
}

/// <summary>Captures profile recovery state, replay evidence and retention floors at one native database version.</summary>
public sealed record DurableCheckpointEnvelope
{
    /// <summary>Gets or initializes the checkpoint's optional logical timestamp state.</summary>
    public DurableTimestampState? Timestamp
    {
        get; init;
    }

    /// <summary>Creates a validated checkpoint envelope and copies retained transaction evidence.</summary>
    public DurableCheckpointEnvelope(Guid databaseIncarnation, DatabaseVersion version, DurableProfilePayload profileState,
        IReadOnlyList<DurableCommittedTransaction>? committedTransactions = null,
        DatabaseChangeCursor? materializedChangeCursor = null, long authoritativeOperationFloor = 0,
        long localOperationFloor = 0, DatabaseStorageLayout? storageLayout = null,
        DatabaseVersioningMode versioningMode = DatabaseVersioningMode.LocalOrAuthoritative)
    {
        if (!Enum.IsDefined(versioningMode))
        {
            throw new ArgumentOutOfRangeException(nameof(versioningMode));
        }

        VersioningMode = versioningMode;
        if (databaseIncarnation == Guid.Empty)
        {
            throw new ArgumentException("A durable database incarnation is required.", nameof(databaseIncarnation));
        }

        if (version.Value < DatabaseVersion.Initial.Value)
        {
            throw new ArgumentOutOfRangeException(nameof(version));
        }

        StorageLayout = storageLayout ?? DatabaseStorageLayout.Current;
        DatabaseIncarnation = databaseIncarnation;
        Version = version;
        ProfileState = profileState ?? throw new ArgumentNullException(nameof(profileState));
        CommittedTransactions = committedTransactions?.ToArray() ?? Array.Empty<DurableCommittedTransaction>();
        if (authoritativeOperationFloor < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(authoritativeOperationFloor));
        }

        AuthoritativeOperationFloor = authoritativeOperationFloor;
        if (localOperationFloor < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(localOperationFloor));
        }

        LocalOperationFloor = localOperationFloor;
        MaterializedChangeCursor = materializedChangeCursor;
    }

    /// <summary>Exact physical image and independent redo allocation floor captured under checkpoint admission.</summary>
    public DurablePhysicalRecoveryEvidence? PhysicalRecovery
    {
        get; init;
    }

    /// <summary>Gets or initializes distributed transaction reservations retained for recovery.</summary>
    public IReadOnlyList<DurablePartitionTransaction> PartitionTransactions { get; init; } = Array.Empty<DurablePartitionTransaction>();

    /// <summary>Gets the version allocation mode recorded by the checkpoint.</summary>
    public DatabaseVersioningMode VersioningMode
    {
        get;
    }

    /// <summary>Gets the storage layout required to restore this checkpoint.</summary>
    public DatabaseStorageLayout StorageLayout
    {
        get;
    }

    /// <summary>Gets the database incarnation that owns the checkpoint.</summary>
    public Guid DatabaseIncarnation
    {
        get;
    }

    /// <summary>Gets the native database version captured by the checkpoint.</summary>
    public DatabaseVersion Version
    {
        get;
    }

    /// <summary>Gets the encoded, codec-identified profile recovery state.</summary>
    public DurableProfilePayload ProfileState
    {
        get;
    }

    /// <summary>Replaces encoded profile bytes without changing the captured recovery boundary.</summary>
    public DurableCheckpointEnvelope WithProfileState(DurableProfilePayload profileState) =>
        new(DatabaseIncarnation, Version, profileState, CommittedTransactions, MaterializedChangeCursor,
            AuthoritativeOperationFloor, LocalOperationFloor, StorageLayout, VersioningMode)
        {
            Timestamp = Timestamp,
            PhysicalRecovery = PhysicalRecovery,
            PartitionTransactions = PartitionTransactions
        };

    /// <summary>Gets retained transaction hashes and receipts used to recognize exact retries.</summary>
    public IReadOnlyList<DurableCommittedTransaction> CommittedTransactions
    {
        get;
    }

    /// <summary>Gets the committed-change boundary already materialized at checkpoint capture, if available.</summary>
    public DatabaseChangeCursor? MaterializedChangeCursor
    {
        get;
    }

    /// <summary>Gets the retained authoritative operation boundary.</summary>
    public long AuthoritativeOperationFloor
    {
        get;
    }

    /// <summary>Gets the retained local operation boundary.</summary>
    public long LocalOperationFloor
    {
        get;
    }
}

/// <summary>Retains identity-bound evidence for a completed native transaction and optional application operation.</summary>
public sealed record DurableCommittedTransaction
{
    /// <summary>Gets or initializes the transaction's optional logical timestamp evidence.</summary>
    public DurableTimestampState? Timestamp
    {
        get; init;
    }

    /// <summary>Creates validated replay evidence, copying its content hash and checking effect-count consistency with the change kind.</summary>
    public DurableCommittedTransaction(Guid transactionId, DatabaseVersion version,
        ReadOnlyMemory<byte> contentHash, DurableCommittedChangeKind changeKind, int effectCount,
        Guid? applicationOperationId = null)
    {
        if (transactionId == Guid.Empty)
        {
            throw new ArgumentException("A committed transaction identity is required.", nameof(transactionId));
        }

        if (version.Value <= DatabaseVersion.Initial.Value)
        {
            throw new ArgumentOutOfRangeException(nameof(version));
        }

        if (contentHash.IsEmpty)
        {
            throw new ArgumentException("A committed transaction content hash is required.", nameof(contentHash));
        }

        if (!Enum.IsDefined(changeKind))
        {
            throw new ArgumentOutOfRangeException(nameof(changeKind));
        }

        if (effectCount < 0 || (changeKind == DurableCommittedChangeKind.UserDataWithChanges) != (effectCount > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(effectCount));
        }

        TransactionId = transactionId;
        Version = version;
        ContentHash = contentHash.ToArray();
        ChangeKind = changeKind;
        EffectCount = effectCount;
        if (applicationOperationId == Guid.Empty)
        {
            throw new ArgumentException("Invalid operation link.", nameof(applicationOperationId));
        }

        ApplicationOperationId = applicationOperationId;
    }

    /// <summary>Gets the completed native transaction identifier.</summary>
    public Guid TransactionId
    {
        get;
    }

    /// <summary>Gets the positive native version assigned to the transaction.</summary>
    public DatabaseVersion Version
    {
        get;
    }

    /// <summary>Gets the owned content hash used to verify exact retries.</summary>
    public ReadOnlyMemory<byte> ContentHash
    {
        get;
    }

    /// <summary>Gets the durable change classification.</summary>
    public DurableCommittedChangeKind ChangeKind
    {
        get;
    }

    /// <summary>Gets the number of committed user-data effects.</summary>
    public int EffectCount
    {
        get;
    }

    /// <summary>Gets the original application operation identity, when linked to this native transaction.</summary>
    public Guid? ApplicationOperationId
    {
        get;
    }
}

/// <summary>Binds checkpoint state to the length-bound block-SHA-256 physical image commitment. The redo clock is not the logical version.</summary>
public sealed record DurablePhysicalRecoveryEvidence
{
    /// <summary>Creates image-bound recovery evidence from nonnegative redo and byte floors and a hexadecimal SHA-256 digest.</summary>
    public DurablePhysicalRecoveryEvidence(long walFloor, long pageBytes, string pageSha256)
    {
        if (walFloor < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(walFloor));
        }

        if (pageBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageBytes));
        }

        if (pageSha256 is null || pageSha256.Length != 64 || !pageSha256.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("A SHA-256 physical image digest is required.", nameof(pageSha256));
        }

        WalFloor = walFloor;
        PageBytes = pageBytes;
        PageSha256 = pageSha256.ToUpperInvariant();
    }

    /// <summary>Gets the independent physical redo allocation floor; it is not a logical database version.</summary>
    public long WalFloor
    {
        get;
    }

    /// <summary>Gets the byte length of the committed physical page image.</summary>
    public long PageBytes
    {
        get;
    }

    /// <summary>Gets the uppercase length-bound block-SHA-256 image commitment.</summary>
    public string PageSha256
    {
        get;
    }
}
