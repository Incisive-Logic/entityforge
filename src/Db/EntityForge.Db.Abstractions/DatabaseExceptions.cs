namespace EntityForge.Db;

/// <summary>Base exception for EntityForge database failures.</summary>
public abstract class EntityForgeDbException : Exception
{
    /// <summary>Creates an instance describing this database failure.</summary>
    /// <param name="message">Description of the failure.</param>
    protected EntityForgeDbException(string message) : base(message)
    {
    }

    /// <summary>Creates an instance describing this database failure.</summary>
    /// <param name="message">Description of the failure.</param>
    /// <param name="innerException">Underlying exception that caused this failure.</param>
    protected EntityForgeDbException(string message, Exception? innerException) : base(message, innerException)
    {
    }
}

/// <summary>Wraps an underlying storage-access failure.</summary>
/// <param name="message">Description of the failure.</param>
/// <param name="innerException">Underlying failure that caused this exception.</param>
public class EntityForgeDbStorageException(string message, Exception innerException)
    : EntityForgeDbException(message, innerException);

/// <summary>Reports an invalid or unsupported persisted data format.</summary>
public sealed class EntityForgeDbFormatException : EntityForgeDbException
{
    /// <summary>Creates an instance describing this database failure.</summary>
    /// <param name="message">Description of the failure.</param>
    public EntityForgeDbFormatException(string message) : base(message)
    {
    }

    /// <summary>Creates an instance describing this database failure.</summary>
    /// <param name="message">Description of the failure.</param>
    /// <param name="innerException">Underlying exception that caused this failure.</param>
    public EntityForgeDbFormatException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>Thrown when an operation requires a database capability that is not available.</summary>
public sealed class EntityForgeDatabaseCapabilityException : EntityForgeDbException
{
    /// <summary>Creates an instance describing this database failure.</summary>
    /// <param name="message">Description of the failure.</param>
    public EntityForgeDatabaseCapabilityException(string message) : base(message)
    {
    }

    /// <summary>Creates an instance describing this database failure.</summary>
    /// <param name="message">Description of the failure.</param>
    /// <param name="innerException">Underlying exception that caused this failure.</param>
    public EntityForgeDatabaseCapabilityException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Reports a backup creation, verification or restoration failure.</summary>
public sealed class EntityForgeDbBackupException : EntityForgeDbException
{
    /// <summary>Creates an instance describing this database failure.</summary>
    /// <param name="message">Description of the failure.</param>
    public EntityForgeDbBackupException(string message) : base(message)
    {
    }

    /// <summary>Creates an instance describing this database failure.</summary>
    /// <param name="message">Description of the failure.</param>
    /// <param name="innerException">Underlying exception that caused this failure.</param>
    public EntityForgeDbBackupException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>Wraps a failure while recovering database state.</summary>
/// <param name="message">Description of the failure.</param>
/// <param name="innerException">Underlying failure that caused this exception.</param>
public sealed class EntityForgeDbRecoveryException(string message, Exception innerException)
    : EntityForgeDbException(message, innerException);

/// <summary>Wraps a failure while publishing a checkpoint.</summary>
/// <param name="message">Description of the failure.</param>
/// <param name="innerException">Underlying failure that caused this exception.</param>
public sealed class EntityForgeDbCheckpointException(string message, Exception innerException)
    : EntityForgeDbException(message, innerException);

/// <summary>Wraps a failure encountered during commit processing; inspect the operation outcome before retrying.</summary>
/// <param name="message">Description of the failure.</param>
/// <param name="innerException">Underlying failure that caused this exception.</param>
public sealed class EntityForgeDbCommitException(string message, Exception innerException)
    : EntityForgeDbException(message, innerException);

/// <summary>Signals that the current database instance requires recovery before further work.</summary>
/// <param name="message">Description of the failure.</param>
public sealed class EntityForgeDbRecoveryRequiredException(string message) : EntityForgeDbException(message);

/// <summary>Reports reuse of a transaction identifier with different command content.</summary>
/// <param name="transactionId">The transaction identity reused for different content.</param>
public sealed class EntityForgeDbIdempotencyException(Guid transactionId)
    : EntityForgeDbException($"Transaction identity '{transactionId}' was reused with different content.")
{
    /// <summary>The transaction identity whose retained content conflicts with the attempted reuse.</summary>
    public Guid TransactionId { get; } = transactionId;
}
