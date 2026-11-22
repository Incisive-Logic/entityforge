namespace EntityForge.Db.Durability;

/// <summary>Describes a durable participant reservation and its terminal decision.</summary>
public enum DurablePartitionTransactionState : byte
{
    /// <summary>The exact native command is retained pending a decision.</summary>
    Prepared = 1,
    /// <summary>The command has committed at a positive local version.</summary>
    Committed = 2,
    /// <summary>The retained command has an abort decision and no committed local version.</summary>
    Aborted = 3
}

/// <summary>A selected native command and its distributed identity, independent of the local log position.</summary>
public sealed record DurablePartitionTransaction
{
    /// <summary>The maximum number of retained participant transactions, 1,024.</summary>
    public const int MaximumTransactions = 1024;
    /// <summary>The maximum native command payload size, thirty-two mebibytes.</summary>
    public const int MaximumPayloadBytes = 32 * 1024 * 1024;

    /// <summary>Creates validated participant evidence, owning the exact payload and binding state to an optional committed local version.</summary>
    public DurablePartitionTransaction(Guid operation, string descriptor, DatabaseVersion readVersion,
        ReadOnlyMemory<byte> payload, DurablePartitionTransactionState state, DatabaseVersion? localVersion = null,
        DurablePartitionCoordinator? coordinator = null, DurableTimestampState? timestamp = null)
    {
        if (operation == Guid.Empty || descriptor is null || descriptor.Length != 64 || !descriptor.All(Uri.IsHexDigit) ||
            readVersion.Value < 0 || payload.IsEmpty || payload.Length > MaximumPayloadBytes || !ValidState(state, localVersion))
        {
            throw new ArgumentException("Invalid partition transaction evidence.");
        }

        Operation = operation;
        Descriptor = descriptor.ToUpperInvariant();
        ReadVersion = readVersion;
        Payload = payload.ToArray();
        State = state;
        LocalVersion = localVersion;
        Coordinator = coordinator;
        Timestamp = timestamp;
    }

    /// <summary>Gets the distributed application operation identifier.</summary>
    public Guid Operation
    {
        get;
    }

    /// <summary>Gets the uppercase descriptor hash binding the exact selected operation.</summary>
    public string Descriptor
    {
        get;
    }

    /// <summary>Gets the native version at which the command was selected.</summary>
    public DatabaseVersion ReadVersion
    {
        get;
    }

    /// <summary>Gets the privately owned native command bytes.</summary>
    public ReadOnlyMemory<byte> Payload
    {
        get;
    }

    /// <summary>Gets the durable preparation or decision state.</summary>
    public DurablePartitionTransactionState State
    {
        get;
    }

    /// <summary>Gets the committed native version; null for prepared or aborted state.</summary>
    public DatabaseVersion? LocalVersion
    {
        get;
    }

    /// <summary>Gets optional home-authority selection evidence.</summary>
    public DurablePartitionCoordinator? Coordinator
    {
        get;
    }

    /// <summary>Gets optional durable logical timestamp evidence.</summary>
    public DurableTimestampState? Timestamp
    {
        get;
    }

    /// <summary>Gets the retained payload, coordinator and timestamp byte charge.</summary>
    public long EvidenceBytes => Payload.Length + (Coordinator?.EncodedLength ?? 0) + (Timestamp is null ? 0 : DurableTimestampState.EncodedLength);

    private static bool ValidState(DurablePartitionTransactionState state, DatabaseVersion? version) =>
        Enum.IsDefined(state) && (state == DurablePartitionTransactionState.Committed) == (version is not null) &&
        version is not { Value: <= 0 };

    // Only a privately owned immutable payload can enter this constructor.
    // Terminal projection must not copy potentially large command bytes again.
    private DurablePartitionTransaction(DurablePartitionTransaction source, DurablePartitionTransactionState state,
        DatabaseVersion? version, DurableTimestampState? timestamp)
    {
        if (!ValidState(state, version))
        {
            throw new ArgumentException("Invalid partition transaction evidence.");
        }

        Operation = source.Operation;
        Descriptor = source.Descriptor;
        ReadVersion = source.ReadVersion;
        Payload = source.Payload;
        Coordinator = source.Coordinator;
        State = state;
        LocalVersion = version;
        Timestamp = timestamp;
    }

    /// <summary>Projects the evidence to a validated state and version, preserving owned payload bytes and existing timestamp evidence unless replaced.</summary>
    public DurablePartitionTransaction Complete(DurablePartitionTransactionState state, DatabaseVersion? version = null, DurableTimestampState? timestamp = null)
    {
        timestamp ??= Timestamp;
        return state == State && version == LocalVersion && timestamp == Timestamp
            ? this : new(this, state, version, timestamp);
    }
}
