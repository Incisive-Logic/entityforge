using EntityForge.Db.Semantic;

namespace EntityForge.Db.Semantic.Core;

/// <summary>Classifies conflicts found against committed semantic dependency history.</summary>
public enum TransactionConflictKind
{
    /// <summary>Two writes overlap the same source-owned contribution.</summary>
    OwnershipWrite,
    /// <summary>A transaction read overlaps a later committed write.</summary>
    ReadAfterWrite,
    /// <summary>A transaction write overlaps a later committed read dependency.</summary>
    WriteAfterRead
}

/// <summary>Identifies the committed version and dependency category responsible for a conflict.</summary>
/// <param name="ConflictingVersion">The committed version whose dependencies conflict.</param>
/// <param name="Kind">The category of dependency conflict.</param>
public sealed record TransactionConflict(
    DatabaseVersion ConflictingVersion,
    TransactionConflictKind Kind);

/// <summary>Retains ownership and predicate dependencies for a committed semantic transaction.</summary>
/// <param name="Version">The committed database version.</param>
/// <param name="OwnershipWrites">The source-owned contributions written by the transaction.</param>
/// <param name="PredicateReads">The predicate and catalog dependencies read by the transaction.</param>
/// <param name="PredicateWrites">The predicates affected by the transaction's writes.</param>
public sealed record CommittedTransactionDependencies(
    DatabaseVersion Version,
    IReadOnlyList<SemanticWriteDependency> OwnershipWrites,
    IReadOnlyList<SemanticPredicateDependency> PredicateReads,
    IReadOnlyList<SemanticPredicateDependency> PredicateWrites);

/// <summary>Validates semantic transaction dependencies against retained commit history.</summary>
public sealed class TransactionValidationIndex
{
    private const int MinimumCommitCountWithLastEntry = 1;
    private const int BinarySearchPartitionDivisor = 2;
    private readonly List<CommittedTransactionDependencies> _commits = new();
    private readonly object _gate = new();
    private DatabaseVersion _recoveryFloor;

    /// <summary>Creates validation history at the supplied recovery floor.</summary>
    public TransactionValidationIndex(DatabaseVersion recoveryFloor)
    {
        _recoveryFloor = recoveryFloor;
    }

    /// <summary>Gets the oldest version for which transaction validation remains supported.</summary>
    public DatabaseVersion RecoveryFloor
    {
        get
        {
            lock (_gate)
            {
                return _recoveryFloor;
            }
        }
    }

    internal int RetainedCommitCount
    {
        get
        {
            lock (_gate)
            {
                return _commits.Count;
            }
        }
    }

    // Called under commit admission after durable checkpoint publication. A
    // detached checkpoint may finish after newer commits: retain that tail.
    /// <summary>Advances the floor after durable checkpoint publication, dropping older metadata while retaining newer commits.</summary>
    public void AdvanceRecoveryFloor(DatabaseVersion recoveryFloor)
    {
        lock (_gate)
        {
            if (recoveryFloor.Value < _recoveryFloor.Value)
            {
                throw new InvalidOperationException("Validation recovery floor cannot move backwards.");
            }

            int remove = FirstVersionAfter(recoveryFloor);
            if (remove != 0)
            {
                _commits.RemoveRange(0, remove);
            }

            _recoveryFloor = recoveryFloor;
        }
    }

    /// <summary>Clears retained commit dependencies and installs the recovered validation floor.</summary>
    public void ResetForRecovery(DatabaseVersion recoveryFloor)
    {
        lock (_gate)
        {
            _commits.Clear();
            _recoveryFloor = recoveryFloor;
        }
    }

    /// <summary>Appends dependencies at a strictly increasing version above the recovery floor.</summary>
    public void Record(CommittedTransactionDependencies dependencies)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        lock (_gate)
        {
            if (_commits.Count >= MinimumCommitCountWithLastEntry &&
                _commits[^1].Version.Value >= dependencies.Version.Value)
            {
                throw new InvalidDataException("Validation metadata commit versions must be strictly increasing.");
            }

            if (dependencies.Version.Value <= _recoveryFloor.Value)
            {
                throw new InvalidDataException("Validation metadata cannot precede its recovery floor.");
            }

            _commits.Add(dependencies);
        }
    }

    /// <summary>Returns the first conflicting retained commit, or null; throws when the read version predates available history.</summary>
    public TransactionConflict? Validate(
        DatabaseVersion readVersion,
        IReadOnlyList<SemanticWriteDependency> ownershipWrites,
        IReadOnlyList<SemanticPredicateDependency> predicateReads,
        IReadOnlyList<SemanticPredicateDependency> predicateWrites)
    {
        lock (_gate)
        {
            if (readVersion.Value < _recoveryFloor.Value)
            {
                throw new ValidationHistoryUnavailableException(readVersion, _recoveryFloor);
            }

            int firstCandidate = FirstVersionAfter(readVersion);
            for (int index = firstCandidate; index < _commits.Count; index++)
            {
                CommittedTransactionDependencies committed = _commits[index];
                if (committed.OwnershipWrites.Any(existing =>
                        ownershipWrites.Any(candidate => candidate.ConflictsWith(existing))))
                {
                    return new(committed.Version, TransactionConflictKind.OwnershipWrite);
                }

                if (predicateReads.Any(read => committed.PredicateWrites.Any(read.Overlaps)))
                {
                    return new(committed.Version, TransactionConflictKind.ReadAfterWrite);
                }

                if (predicateWrites.Any(write => committed.PredicateReads.Any(write.Overlaps)))
                {
                    return new(committed.Version, TransactionConflictKind.WriteAfterRead);
                }
            }
            return null;
        }
    }

    private int FirstVersionAfter(DatabaseVersion readVersion)
    {
        int lower = 0;
        int upper = _commits.Count;
        while (lower < upper)
        {
            int middle = lower + ((upper - lower) / BinarySearchPartitionDivisor);
            if (_commits[middle].Version.Value <= readVersion.Value)
            {
                lower = middle + 1;
            }
            else
            {
                upper = middle;
            }
        }
        return lower;
    }
}

/// <summary>Indicates that a transaction's read version predates the retained validation history.</summary>
public sealed class ValidationHistoryUnavailableException : Exception
{
    /// <summary>Creates an exception identifying the requested read version and available recovery floor.</summary>
    public ValidationHistoryUnavailableException(
        DatabaseVersion readVersion,
        DatabaseVersion recoveryFloor)
        : base($"Transaction version {readVersion.Value} precedes validation recovery floor {recoveryFloor.Value}.")
    {
        ReadVersion = readVersion;
        RecoveryFloor = recoveryFloor;
    }

    /// <summary>Gets the transaction version that could not be validated.</summary>
    public DatabaseVersion ReadVersion
    {
        get;
    }

    /// <summary>Gets the oldest version supported by retained validation history.</summary>
    public DatabaseVersion RecoveryFloor
    {
        get;
    }
}
