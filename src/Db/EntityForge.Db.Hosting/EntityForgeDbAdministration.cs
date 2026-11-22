using Microsoft.AspNetCore.Http;
using EntityForge.Db.Transport.Contracts;
using EntityForge.Db.Client;

namespace EntityForge.Db.Hosting;

/// <summary>Identifies a privileged EntityForgeDB administrative operation.</summary>
public enum EntityForgeDbAdministrativeOperation
{
    /// <summary>Read database operational status.</summary>
    ReadStatus = 0,

    /// <summary>Read filesystem capacity and existing probe results.</summary>
    ReadStorageProfile = 8,

    /// <summary>Run a bounded I/O probe in the storage directory.</summary>
    RunStorageProbe = 9,

    /// <summary>Create a durable database checkpoint.</summary>
    CreateCheckpoint = 1,

    /// <summary>Create a separately verifiable backup artifact.</summary>
    CreateBackup = 2,

    /// <summary>Create a retained historical evidence archive.</summary>
    CreateHistoricalArchive = 3,

    /// <summary>Read historical archive contents through supported query operations.</summary>
    QueryHistoricalArchive = 4,

    /// <summary>Read Semantic property-placement analysis and recommendations.</summary>
    AnalyzePropertyStorage = 5,

    /// <summary>Apply a supported physical property-placement optimization.</summary>
    ApplyPropertyStorageOptimization = 6,

    /// <summary>Clear collected property-workload observations without deleting data.</summary>
    ResetPropertyStorageObservations = 7,

    /// <summary>Manage catalog graphs, reclaim retired stores and explicitly expire catalog receipts.</summary>
    ManageGqlGraphs = 10
}

/// <summary>Decides whether the current request may perform an administrative operation.</summary>
public interface IEntityForgeDbAuthorizationPolicy
{
    /// <summary>Returns whether the request is authorized for the named operation.</summary>
    ValueTask<bool> AuthorizeAsync(HttpContext context, EntityForgeDbAdministrativeOperation operation,
        CancellationToken cancellationToken = default);
}

/// <summary>Allows administration requests for an explicitly trusted embedded host.</summary>
public sealed class AllowEntityForgeDbAuthorizationPolicy : IEntityForgeDbAuthorizationPolicy
{
    /// <inheritdoc />
    public ValueTask<bool> AuthorizeAsync(HttpContext context, EntityForgeDbAdministrativeOperation operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(true);
    }
}

/// <summary>Denies administration unless the application explicitly establishes an authorization boundary.</summary>
public sealed class DenyEntityForgeDbAuthorizationPolicy : IEntityForgeDbAuthorizationPolicy
{
    /// <inheritdoc />
    public ValueTask<bool> AuthorizeAsync(HttpContext context, EntityForgeDbAdministrativeOperation operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(false);
    }
}

/// <summary>Bounds concurrent and elapsed work performed by administrative jobs.</summary>
public sealed record EntityForgeDbAdministrationOptions
{
    /// <summary>Default maximum number of concurrent administrative jobs.</summary>
    public const int DefaultMaximumConcurrentJobs = 1;

    /// <summary>Default administrative job timeout in minutes.</summary>
    public const int DefaultJobTimeoutMinutes = 10;

    /// <summary>Creates validated administrative limits.</summary>
    public EntityForgeDbAdministrationOptions(int maximumConcurrentJobs = DefaultMaximumConcurrentJobs,
        TimeSpan? jobTimeout = null)
    {
        if (maximumConcurrentJobs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumConcurrentJobs));
        }

        TimeSpan timeout = jobTimeout ?? TimeSpan.FromMinutes(DefaultJobTimeoutMinutes);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(jobTimeout));
        }

        MaximumConcurrentJobs = maximumConcurrentJobs;
        JobTimeout = timeout;
    }

    /// <summary>Gets the maximum concurrent checkpoint or backup jobs.</summary>
    public int MaximumConcurrentJobs
    {
        get;
    }

    /// <summary>Gets the maximum duration of one administrative job.</summary>
    public TimeSpan JobTimeout
    {
        get;
    }
}

/// <summary>Describes a bounded backup request beneath the configured backup directory.</summary>
internal sealed record EntityForgeDbBackupRequest(string Name);

/// <summary>Requests execution of the ordinary entity query contract against an explicit archive tier.</summary>
internal sealed record HistoricalEvidenceArchiveQueryRequest(string ArchiveId, PortableEntityQueryRequest Query);

/// <summary>Returns ordinary query results together with explicit archive-tier service metadata.</summary>
internal sealed record HistoricalEvidenceArchiveQueryResponse(string StorageTier, string ArchiveId,
    long SnapshotVersion, long RestoreMilliseconds, long QueryMilliseconds, EntityProjectionTransportResponse Result);

/// <summary>Reports whether the database can serve traffic and explains every blocking reason.</summary>
internal sealed record EntityForgeDbReadiness(
    bool Ready,
    string State,
    IReadOnlyList<string> Reasons);

internal sealed class EntityForgeDbAdministrativeJobCoordinator : IDisposable
{
    private readonly SemaphoreSlim _admission;
    private readonly TimeSpan _timeout;
    private readonly object _gate = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _active;
    private bool _stopping;

    public EntityForgeDbAdministrativeJobCoordinator(EntityForgeDbAdministrationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _admission = new(options.MaximumConcurrentJobs, options.MaximumConcurrentJobs);
        _timeout = options.JobTimeout;
    }

    public async ValueTask<T> ExecuteAsync<T>(Func<CancellationToken, ValueTask<T>> operation,
        CancellationToken cancellationToken, bool drainOnCancellation = false)
    {
        lock (_gate)
        {
            if (_stopping)
            {
                throw new EntityForgeDbAdministrativeJobRejectedException("The administrative job coordinator is stopping.");
            }
            _active++;
        }
        try
        {
            return await ExecuteAdmittedAsync(operation, cancellationToken, drainOnCancellation).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                if (--_active == 0 && _stopping)
                {
                    _drained.TrySetResult();
                }
            }
        }
    }

    internal Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _stopping = true;
            if (_active == 0)
            {
                _drained.TrySetResult();
            }
            return _drained.Task.WaitAsync(cancellationToken);
        }
    }

    private async ValueTask<T> ExecuteAdmittedAsync<T>(Func<CancellationToken, ValueTask<T>> operation,
        CancellationToken cancellationToken, bool drainOnCancellation)
    {
        if (!await _admission.WaitAsync(TimeSpan.Zero, cancellationToken).ConfigureAwait(false))
        {
            throw new EntityForgeDbAdministrativeJobRejectedException(
                "The configured administrative job concurrency limit has been reached.");
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                drainOnCancellation ? CancellationToken.None : cancellationToken);
            timeout.CancelAfter(_timeout);
            try
            {
                return await operation(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when ((drainOnCancellation || !cancellationToken.IsCancellationRequested) &&
                                                     timeout.IsCancellationRequested)
            {
                throw new EntityForgeDbAdministrativeJobTimeoutException(
                    $"The administrative job exceeded its {_timeout} time limit.");
            }
        }
        finally
        {
            _admission.Release();
        }
    }

    public void Dispose() => _admission.Dispose();
}

internal sealed class EntityForgeDbAdministrativeJobRejectedException(string message) : Exception(message);

internal sealed class EntityForgeDbAdministrativeJobTimeoutException(string message) : Exception(message);
