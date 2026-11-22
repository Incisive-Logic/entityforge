namespace EntityForge.Db;

/// <summary>Identifies a phase in an instance-owned physical maintenance operation.</summary>
public enum DatabaseMaintenancePhase
{
    /// <summary>No maintenance operation has been started.</summary>
    Idle,

    /// <summary>The operation is waiting for exclusive maintenance admission.</summary>
    WaitingForAdmission,

    /// <summary>The operation is constructing or processing its replacement state.</summary>
    Building,

    /// <summary>A stale replacement needs another attempt to converge with intervening writes.</summary>
    CatchingUp,

    /// <summary>The operation is attempting to publish the replacement state.</summary>
    CuttingOver,

    /// <summary>The operation finished successfully.</summary>
    Completed,

    /// <summary>The operation ended through cancellation.</summary>
    Cancelled,

    /// <summary>The operation ended with an error.</summary>
    Failed
}

/// <summary>Describes the latest observable state of an instance-owned maintenance operation.</summary>
/// <param name="JobId">The maintenance job identity, or null before a job exists.</param>
/// <param name="Operation">The maintenance operation name, or null while idle.</param>
/// <param name="Phase">The most recently reported lifecycle phase.</param>
/// <param name="Attempt">The current attempt number; zero before an attempt starts.</param>
/// <param name="CompletedWork">Completed work units, using the operation's own measurement.</param>
/// <param name="TotalWork">Total work units when reported; zero does not imply that the operation is complete.</param>
/// <param name="Failure">Failure information when supplied by the operation.</param>
/// <param name="UpdatedAtUtc">The UTC time of this status update.</param>
public sealed record DatabaseMaintenanceStatus(Guid? JobId, string? Operation, DatabaseMaintenancePhase Phase,
    int Attempt, long CompletedWork, long TotalWork, string? Failure, DateTimeOffset UpdatedAtUtc);

/// <summary>Describes a completed maintenance operation.</summary>
/// <typeparam name="TVersion">The version type returned by the maintenance operation.</typeparam>
/// <param name="JobId">The completed maintenance job identity.</param>
/// <param name="Operation">The maintenance operation name.</param>
/// <param name="PublishedVersion">The version or result published by the operation.</param>
/// <param name="Attempts">The number of execution attempts used.</param>
/// <param name="Duration">The elapsed duration reported for the operation.</param>
public sealed record DatabaseMaintenanceResult<TVersion>(Guid JobId, string Operation, TVersion PublishedVersion,
    int Attempts, TimeSpan Duration);

/// <summary>Provides common local lifecycle information without exposing the durability kernel.</summary>
public interface IDatabaseOperationalStatus
{
    /// <summary>Gets the latest committed version visible to new readers.</summary>
    DatabaseVersion CurrentVersion
    {
        get;
    }

    /// <summary>Gets the earliest version that can still be opened as a snapshot.</summary>
    DatabaseVersion OldestReadableVersion
    {
        get;
    }

    /// <summary>Gets the latest maintenance lifecycle state.</summary>
    DatabaseMaintenanceStatus MaintenanceStatus
    {
        get;
    }
}
