using EntityForge.Db;
using EntityForge.Semantics;

namespace EntityForge.Db.Semantic;

/// <summary>Thrown when a semantic change session is incomplete, inconsistent or invalid.</summary>
public sealed class SemanticSessionValidationException : EntityForgeDbException
{
    /// <summary>Creates an instance describing this database failure.</summary>
    /// <param name="message">Description of the failure.</param>
    public SemanticSessionValidationException(string message) : base(message)
    {
    }

    /// <summary>Creates an instance describing this database failure.</summary>
    /// <param name="message">Description of the failure.</param>
    /// <param name="innerException">Underlying exception that caused this failure.</param>
    public SemanticSessionValidationException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>Thrown when a guarded source-partition transition does not follow its declared predecessor.</summary>
public sealed class SemanticSourceProgressConflictException : EntityForgeDbException
{
    /// <summary>Creates an instance describing this database failure.</summary>
    /// <param name="expected">Source-progress precondition required by the operation.</param>
    /// <param name="actual">Observed source progress, or null when the source has none.</param>
    public SemanticSourceProgressConflictException(SemanticSourceProgressPrecondition expected,
        SemanticSourcePosition? actual)
        : base(actual is null
            ? "The source partition has no progress matching the declared predecessor."
            : $"The source partition is currently at generation '{actual.GenerationId:D}', version " +
              $"'{actual.SourceVersion}', position '{actual.SourcePosition}', which does not match the declared predecessor.")
    {
        Expected = expected ?? throw new ArgumentNullException(nameof(expected));
        Actual = actual;
    }

    /// <summary>Gets the required source-frontier guard.</summary>
    public SemanticSourceProgressPrecondition Expected
    {
        get;
    }

    /// <summary>Gets the observed source position, or null when no progress exists.</summary>
    public SemanticSourcePosition? Actual
    {
        get;
    }
}

/// <summary>Thrown when checkpoint reclamation would violate an active snapshot lease.</summary>
public sealed class EntityForgeSnapshotRetentionException : EntityForgeDbException
{
    /// <summary>Creates an instance describing this database failure.</summary>
    /// <param name="requestedCheckpoint">Checkpoint boundary requested by the caller.</param>
    /// <param name="retentionFloor">Oldest database version protected or still available.</param>
    public EntityForgeSnapshotRetentionException(
        DatabaseVersion requestedCheckpoint,
        DatabaseVersion retentionFloor)
        : base($"Checkpoint version {requestedCheckpoint.Value} would pass active snapshot retention floor {retentionFloor.Value}.")
    {
        RequestedCheckpoint = requestedCheckpoint;
        RetentionFloor = retentionFloor;
    }

    /// <summary>Gets the checkpoint boundary that was requested.</summary>
    public DatabaseVersion RequestedCheckpoint
    {
        get;
    }

    /// <summary>Gets the oldest version protected by active leases.</summary>
    public DatabaseVersion RetentionFloor
    {
        get;
    }
}

/// <summary>Classifies a failed snapshot-lease operation.</summary>
public enum EntityForgeSnapshotLeaseFailure
{
    /// <summary>The requested lease identity is unknown.</summary>
    NotFound,

    /// <summary>The requested lease has expired.</summary>
    Expired,

    /// <summary>The lease revision differs from the caller's expected revision.</summary>
    RevisionMismatch
}

/// <summary>Thrown when a snapshot lease cannot satisfy an operation.</summary>
public sealed class EntityForgeSnapshotLeaseException : EntityForgeDbException
{
    /// <summary>Creates an instance describing this database failure.</summary>
    /// <param name="leaseId">Identifier of the affected snapshot lease.</param>
    /// <param name="failure">Machine-readable classification of the failure.</param>
    /// <param name="message">Description of the failure.</param>
    public EntityForgeSnapshotLeaseException(Guid leaseId, EntityForgeSnapshotLeaseFailure failure,
        string message) : base(message)
    {
        LeaseId = leaseId;
        Failure = failure;
    }

    /// <summary>Gets the affected lease identity.</summary>
    public Guid LeaseId
    {
        get;
    }

    /// <summary>Gets the machine-readable lease failure category.</summary>
    public EntityForgeSnapshotLeaseFailure Failure
    {
        get;
    }
}

/// <summary>Thrown when an explicit semantic snapshot is no longer available.</summary>
public sealed class EntityForgeSnapshotUnavailableException : EntityForgeDbException
{
    /// <summary>Creates an instance describing this database failure.</summary>
    /// <param name="requested">Snapshot version requested by the caller.</param>
    /// <param name="retentionFloor">Oldest database version protected or still available.</param>
    public EntityForgeSnapshotUnavailableException(DatabaseVersion requested,
        DatabaseVersion retentionFloor)
        : base($"Snapshot version {requested.Value} is older than retention floor {retentionFloor.Value}.")
    {
        Requested = requested;
        RetentionFloor = retentionFloor;
    }

    /// <summary>Gets the unavailable snapshot version requested by the caller.</summary>
    public DatabaseVersion Requested
    {
        get;
    }

    /// <summary>Gets the oldest retained version reported with the failure.</summary>
    public DatabaseVersion RetentionFloor
    {
        get;
    }
}

/// <summary>Wraps an unexpected failure while executing a semantic query.</summary>
public sealed class EntityForgeDbQueryException : EntityForgeDbException
{
    /// <summary>Creates an instance describing this database failure.</summary>
    /// <param name="message">Description of the failure.</param>
    /// <param name="innerException">Underlying exception that caused this failure.</param>
    public EntityForgeDbQueryException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>Thrown when a query requests behavior not supported by the active profile.</summary>
public sealed class EntityForgeQueryCapabilityException : EntityForgeDbException
{
    /// <summary>Creates an instance describing this database failure.</summary>
    /// <param name="message">Description of the failure.</param>
    public EntityForgeQueryCapabilityException(string message) : base(message)
    {
    }

    /// <summary>Creates an instance describing this database failure.</summary>
    /// <param name="message">Description of the failure.</param>
    /// <param name="innerException">Underlying exception that caused this failure.</param>
    public EntityForgeQueryCapabilityException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Thrown when a bounded recursive query would expand beyond its declared entity limit.</summary>
public sealed class EntityForgeQueryExpansionLimitException : EntityForgeDbException
{
    /// <summary>Creates an instance describing this database failure.</summary>
    /// <param name="maximumExpandedEntities">Configured maximum entity-expansion count.</param>
    public EntityForgeQueryExpansionLimitException(int maximumExpandedEntities)
        : base($"The query expanded more than {maximumExpandedEntities} related entities. " +
            "Narrow the root set or projection, or explicitly raise the bounded expansion limit.") =>
        MaximumExpandedEntities = maximumExpandedEntities;

    /// <summary>Gets the configured expansion budget that was exceeded.</summary>
    public int MaximumExpandedEntities
    {
        get;
    }
}

/// <summary>Classifies an invalid, mismatched or expired entity-query continuation.</summary>
public enum EntityForgeQueryContinuationFailure
{
    /// <summary>The token cannot be decoded as a valid continuation.</summary>
    Malformed,

    /// <summary>The token belongs to a different query definition.</summary>
    QueryMismatch,

    /// <summary>The token belongs to a different snapshot.</summary>
    SnapshotMismatch,

    /// <summary>The continuation's snapshot is no longer retained.</summary>
    SnapshotExpired,

    /// <summary>The required resume position is unavailable.</summary>
    PositionMissing
}

/// <summary>Thrown when an entity-query continuation cannot safely resume its original query.</summary>
public sealed class EntityForgeQueryContinuationException : EntityForgeDbException
{
    /// <summary>Creates an instance describing this database failure.</summary>
    /// <param name="failure">Machine-readable classification of the failure.</param>
    /// <param name="message">Description of the failure.</param>
    /// <param name="innerException">Underlying exception that caused this failure.</param>
    public EntityForgeQueryContinuationException(EntityForgeQueryContinuationFailure failure,
        string message, Exception innerException) : base(message, innerException) => Failure = failure;

    /// <summary>Gets why the continuation cannot safely resume the query.</summary>
    public EntityForgeQueryContinuationFailure Failure
    {
        get;
    }
}

/// <summary>Thrown when a configured governance evaluator cannot produce a decision.</summary>
public sealed class EntityForgeGovernanceEvaluationException : EntityForgeDbException
{
    /// <summary>Creates an instance describing this database failure.</summary>
    /// <param name="message">Description of the failure.</param>
    public EntityForgeGovernanceEvaluationException(string message) : base(message)
    {
    }

    /// <summary>Creates an instance describing this database failure.</summary>
    /// <param name="message">Description of the failure.</param>
    /// <param name="innerException">Underlying exception that caused this failure.</param>
    public EntityForgeGovernanceEvaluationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Thrown when rejecting governance enforcement prevents a semantic commit.</summary>
public sealed class EntityForgeGovernanceViolationException : EntityForgeDbException
{
    /// <summary>Creates an instance describing this database failure.</summary>
    /// <param name="decision">Governance decision that rejected the commit.</param>
    public EntityForgeGovernanceViolationException(SemanticGovernanceDecision decision)
        : base($"Governance rule '{decision.RuleIdentifier}' rejected the semantic transaction.")
    {
        Decision = decision;
    }

    /// <summary>Gets the governance decision that rejected the commit.</summary>
    public SemanticGovernanceDecision Decision
    {
        get;
    }
}
