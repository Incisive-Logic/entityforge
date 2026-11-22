using EntityForge.Db;

namespace EntityForge.Db.Kernel;

/// <summary>Separately sampled admission counters for one database publication coordinator.</summary>
/// <param name="ActivePublications">Number of callers currently holding publication admission.</param>
/// <param name="WaitingPublications">Number of callers currently attempting to acquire admission.</param>
/// <param name="AdmittedPublications">Cumulative successful acquisitions, not a count of durable commits.</param>
/// <param name="RejectedPublications">Cumulative rejections caused by the queue limit; excludes cancellation and shutdown.</param>
/// <remarks>Counter reads are thread-safe but do not form an atomic snapshot across all fields.</remarks>
public sealed record DatabaseCommitCoordinatorSnapshot(
    int ActivePublications, int WaitingPublications, long AdmittedPublications, long RejectedPublications);

/// <summary>Bounds pending admission and serializes access to one database publication frontier.</summary>
public sealed class DatabaseCommitCoordinator : IAsyncDisposable
{
    /// <summary>Default maximum number of callers attempting to acquire publication admission.</summary>
    public const int DefaultMaximumQueuedPublications = 1024;
    private const int PublicationConcurrency = 1;
    private readonly SemaphoreSlim _publication = new(PublicationConcurrency, PublicationConcurrency);
    private readonly int _maximumQueuedPublications;
    private int _active;
    private int _waiting;
    private long _admitted;
    private long _rejected;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _disposeGate = new();
    private Task? _disposeTask;
    private int _closed;

    /// <summary>Creates a coordinator with one active publication slot and bounded pending admission.</summary>
    /// <param name="maximumQueuedPublications">Positive limit on callers awaiting acquisition.</param>
    /// <exception cref="ArgumentOutOfRangeException">The limit is zero or negative.</exception>
    public DatabaseCommitCoordinator(int maximumQueuedPublications = DefaultMaximumQueuedPublications)
    {
        if (maximumQueuedPublications <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumQueuedPublications));
        }

        _maximumQueuedPublications = maximumQueuedPublications;
    }

    /// <summary>Acquires the exclusive publication slot, rejecting admission when the pending limit is exceeded.</summary>
    /// <param name="cancellationToken">Cancels waiting without acquiring the slot.</param>
    /// <returns>A task that completes after admission is acquired.</returns>
    /// <remarks>After successful completion, call <see cref="Release"/> exactly once in a finally block.
    /// Do not release a failed or cancelled acquisition. No FIFO ordering is promised.</remarks>
    /// <exception cref="DatabaseCommitAdmissionException">The pending-admission limit is exceeded.</exception>
    /// <exception cref="OperationCanceledException">The caller cancels the wait.</exception>
    /// <exception cref="ObjectDisposedException">The coordinator is closing or closed.</exception>
    public async ValueTask WaitAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
        int waiting = Interlocked.Increment(ref _waiting);
        if (waiting > _maximumQueuedPublications)
        {
            Interlocked.Decrement(ref _waiting);
            Interlocked.Increment(ref _rejected);
            throw new DatabaseCommitAdmissionException(_maximumQueuedPublications);
        }
        try
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
            try
            {
                await _publication.WaitAsync(wait.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested &&
                !cancellationToken.IsCancellationRequested)
            {
                throw new ObjectDisposedException(nameof(DatabaseCommitCoordinator));
            }
            if (Volatile.Read(ref _closed) != 0)
            {
                _publication.Release();
                throw new ObjectDisposedException(nameof(DatabaseCommitCoordinator));
            }
            Interlocked.Increment(ref _active);
            Interlocked.Increment(ref _admitted);
        }
        finally
        {
            Interlocked.Decrement(ref _waiting);
        }
    }

    /// <summary>Releases one successfully acquired publication slot.</summary>
    /// <remarks>The holder must release even when coordinator disposal has been requested.
    /// Calling without a corresponding successful acquisition is invalid.</remarks>
    public void Release()
    {
        Interlocked.Decrement(ref _active);
        _publication.Release();
    }

    /// <summary>Reads current admission counters without taking the publication slot.</summary>
    /// <returns>Individually sampled active, waiting, admitted and queue-rejected counts.</returns>
    public DatabaseCommitCoordinatorSnapshot Snapshot() => new(
        Volatile.Read(ref _active), Volatile.Read(ref _waiting),
        Interlocked.Read(ref _admitted), Interlocked.Read(ref _rejected));

    /// <summary>Closes admission, interrupts pending waits, and waits for the current holder to release.</summary>
    /// <returns>The shared shutdown task; repeated calls observe the same shutdown.</returns>
    /// <remarks>Disposal does not release a slot held by a caller. Awaiting disposal while holding
    /// the slot without arranging its release prevents shutdown from completing.</remarks>
    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            return new(_disposeTask ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        Volatile.Write(ref _closed, 1);
        _shutdown.Cancel();
        await _publication.WaitAsync().ConfigureAwait(false);
        _shutdown.Dispose();
    }
}

/// <summary>Reports that a database publication request exceeded the pending-admission limit.</summary>
public sealed class DatabaseCommitAdmissionException : EntityForgeDbException
{
    /// <summary>Creates an admission failure carrying the configured queue limit.</summary>
    /// <param name="maximumQueuedPublications">The configured maximum number of pending acquisitions.</param>
    public DatabaseCommitAdmissionException(int maximumQueuedPublications)
        : base($"The database commit queue reached its configured limit of {maximumQueuedPublications} publications.")
        => MaximumQueuedPublications = maximumQueuedPublications;

    /// <summary>Gets the configured pending-admission limit that caused this rejection.</summary>
    public int MaximumQueuedPublications
    {
        get;
    }
}
