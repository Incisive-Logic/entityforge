using EntityForge.Db.Semantic;

namespace EntityForge.Db.Semantic.Engine;

/// <summary>Reports a semantic transaction conflict against a later committed version.</summary>
public sealed class EntityForgeWriteConflictException : EntityForgeDbException
{
    /// <summary>Creates a conflict failure identifying the read version, conflicting commit and conflict category.</summary>
    public EntityForgeWriteConflictException(DatabaseVersion readVersion,
        DatabaseVersion conflictingVersion,
        EntityForgeConflictKind kind = EntityForgeConflictKind.OwnershipWrite)
        : base($"Transaction prepared at version {readVersion.Value} has a '{kind}' conflict with version {conflictingVersion.Value}.")
    {
        ReadVersion = readVersion;
        ConflictingVersion = conflictingVersion;
        Kind = kind;
    }

    /// <summary>Gets the version from which the transaction read.</summary>
    public DatabaseVersion ReadVersion
    {
        get;
    }

    /// <summary>Gets the committed version responsible for the conflict.</summary>
    public DatabaseVersion ConflictingVersion
    {
        get;
    }

    /// <summary>Gets the category of dependency conflict.</summary>
    public EntityForgeConflictKind Kind
    {
        get;
    }
}

/// <summary>Reports that optimistic validation cannot cover the requested read version.</summary>
public sealed class EntityForgeValidationHistoryUnavailableException : EntityForgeDbException
{
    /// <summary>Creates a validation failure identifying the requested version and available history floor.</summary>
    public EntityForgeValidationHistoryUnavailableException(DatabaseVersion readVersion,
        DatabaseVersion recoveryFloor, Exception innerException)
        : base($"Transaction version {readVersion.Value} precedes validation recovery floor {recoveryFloor.Value}.",
            innerException)
    {
        ReadVersion = readVersion;
        RecoveryFloor = recoveryFloor;
    }

    /// <summary>Gets the transaction read version that could not be validated.</summary>
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
