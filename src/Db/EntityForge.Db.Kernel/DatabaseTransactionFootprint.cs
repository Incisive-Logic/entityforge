namespace EntityForge.Db.Kernel;

/// <summary>A definite pre-durability conflict with retained participant dependencies.</summary>
public sealed class DatabasePartitionConflictException()
    : EntityForgeDbException("The native transaction conflicts with a prepared partition transaction or its selected state.");

/// <summary>Owned conflict resources reconstructed from an immutable native transaction.</summary>
public sealed class DatabaseTransactionFootprint
{
    private readonly HashSet<string> _reads;
    private readonly HashSet<string> _writes;
    private readonly HashSet<string> _changes;
    private readonly bool _wholePartition;

    private DatabaseTransactionFootprint(bool wholePartition)
    {
        _wholePartition = wholePartition;
        _reads = [];
        _writes = [];
        _changes = [];
    }

    // Change notifications conflict with predicate reads, but not with other
    // notifications. Independent rows may change the same indexed property.
    /// <summary>Captures nonempty read, write and change-notification resource identities, with an optional source read version.</summary>
    public DatabaseTransactionFootprint(DatabaseVersion? readVersion, IEnumerable<string> reads, IEnumerable<string> writes, IEnumerable<string>? changes = null)
    {
        ReadVersion = readVersion;
        _reads = new(reads, StringComparer.Ordinal);
        _writes = new(writes, StringComparer.Ordinal);
        _changes = new(changes ?? [], StringComparer.Ordinal);
        if (_reads.Any(String.IsNullOrEmpty) || _writes.Any(String.IsNullOrEmpty) || _changes.Any(String.IsNullOrEmpty))
        {
            throw new ArgumentException("Conflict resources must have nonempty identities.");
        }
    }

    /// <summary>Gets a conservative footprint that excludes conflicting work across the whole partition.</summary>
    public static DatabaseTransactionFootprint WholePartition { get; } = new(true);

    /// <summary>Gets an empty data footprint for operation-metadata-only work.</summary>
    public static DatabaseTransactionFootprint OperationMetadata { get; } = new(false);

    /// <summary>Gets the selected native read version, if the footprint is tied to one.</summary>
    public DatabaseVersion? ReadVersion
    {
        get;
    }

    // A fully described no-op has no dependencies to reserve. It must not turn
    // an otherwise disjoint multi-owner operation into whole-partition exclusion.
    // Write-only footprints still require the conservative path.
    /// <summary>Gets whether the complete footprint and read version permit scoped reservation rather than whole-partition exclusion.</summary>
    public bool IsScopedPreparation => !_wholePartition && ReadVersion is not null && (_reads.Count != 0 || IsEmpty);

    private bool IsEmpty => !_wholePartition && _reads.Count == 0 && _writes.Count == 0 && _changes.Count == 0;

    internal bool WritesAny(HashSet<string> resources) => _wholePartition || _writes.Overlaps(resources) || _changes.Overlaps(resources);

    // Diagnostic evidence only: never used to certify a read. Unsupported or
    // oversized footprints remain unknown rather than exporting a partial set.
    internal void TraceWrites(System.Diagnostics.Activity trace)
    {
        const int maximumDiagnosticResources = 256;
        bool complete = !_wholePartition && _writes.Count + _changes.Count <= maximumDiagnosticResources;
        trace.SetTag("entityforge.scope_complete", complete);
        if (complete)
        {
            trace.SetTag("entityforge.scope_resources", System.Text.Json.JsonSerializer.Serialize(_writes.Concat(_changes).Distinct(StringComparer.Ordinal)
                .Select(value => Convert.ToHexString(DatabaseUtf8Hash.Hash(value)))
                .Order(StringComparer.Ordinal).ToArray()));
        }
    }

    /// <summary>Checks write/read, write/write and change/predicate intersections; empty data footprints do not conflict.</summary>
    public bool ConflictsWith(DatabaseTransactionFootprint other) =>
        !IsEmpty && !other.IsEmpty && (_wholePartition || other._wholePartition ||
            _writes.Overlaps(other._reads) || _writes.Overlaps(other._writes) || _reads.Overlaps(other._writes) || _changes.Overlaps(other._reads) || _reads.Overlaps(other._changes));
}

/// <summary>
/// Profiles describe every read/absence dependency and write, or return whole-partition exclusion.
/// Resource identity must remain stable across recovery. Mapping and unsupported commands are global.
/// </summary>
public interface IDatabaseProfilePartitionReservations
{
    /// <summary>Describes every dependency and mutation resource for the exact transaction payload, or returns whole-partition exclusion.</summary>
    DatabaseTransactionFootprint DescribeTransaction(Guid transactionId, ReadOnlyMemory<byte> payload);

    /// <summary>
    /// Entered only under kernel commit admission for exact, durably prepared bytes. Historical
    /// conflict evidence can have been checkpointed; its reservation now protects those dependencies.
    /// Current mutation validation and publication must still run. The scope must not escape planning.
    /// </summary>
    IDisposable EnterPreparedCompletion(Guid transactionId);
}

/// <summary>Describes all affected resources of an already validated, privately planned append.
/// Called only under commit ownership after planning; unknown effects must return whole-partition exclusion.
/// This evidence is for read isolation only and never replaces transaction reservation validation.</summary>
public interface IDatabasePreparedAppendReadIsolation
{
    /// <summary>Returns the complete write footprint of the currently owned planned operation.</summary>
    DatabaseTransactionFootprint DescribePreparedAppendWrites(Guid operation);
}
