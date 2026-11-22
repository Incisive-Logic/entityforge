using EntityForge.Db.Semantic;
using EntityForge.Db.Kernel;
using EntityForge.Db.Telemetry;
using System.Diagnostics;

namespace EntityForge.Db.Semantic.Engine;

internal sealed record SemanticCommitPreparationSnapshot(
    int ActivePublications,
    int WaitingPublications,
    long AdmittedPublications,
    long RejectedPublications);

/// <summary>
/// Owns bounded admission to the local durability/publication frontier. Semantic preparation and
/// query planning remain outside this coordinator; version assignment, validation and publication
/// enter it in deterministic order.
/// </summary>
internal sealed class SemanticCommitPreparationCoordinator : IAsyncDisposable
{
    private readonly DatabaseCommitCoordinator _coordinator;
    private readonly EntityForgeDbTelemetry _telemetry;

    public SemanticCommitPreparationCoordinator(EntityForgeDbTelemetry telemetry,
        int maximumQueuedPublications = SemanticDatabaseEngine.DefaultMaximumQueuedPublications)
    {
        _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
        if (maximumQueuedPublications <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumQueuedPublications));
        }

        _coordinator = new(maximumQueuedPublications);
    }

    public async ValueTask WaitAsync(CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        int waiting = _coordinator.Snapshot().WaitingPublications + 1;
        try
        {
            await _coordinator.WaitAsync(cancellationToken).ConfigureAwait(false);
            _telemetry.RecordCommitQueue(Stopwatch.GetElapsedTime(started), waiting, "admitted");
        }
        catch (DatabaseCommitAdmissionException exception)
        {
            _telemetry.RecordCommitQueue(Stopwatch.GetElapsedTime(started), waiting, "rejected");
            throw new EntityForgeCommitAdmissionException(exception.MaximumQueuedPublications);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _telemetry.RecordCommitQueue(Stopwatch.GetElapsedTime(started), waiting, "cancelled");
            throw;
        }
    }

    public void Release()
    {
        _coordinator.Release();
    }

    public SemanticCommitPreparationSnapshot Snapshot()
    {
        DatabaseCommitCoordinatorSnapshot snapshot = _coordinator.Snapshot();
        return new(snapshot.ActivePublications, snapshot.WaitingPublications,
            snapshot.AdmittedPublications, snapshot.RejectedPublications);
    }

    public ValueTask DisposeAsync() => _coordinator.DisposeAsync();
}

/// <summary>Reports that the bounded commit-publication queue cannot admit another operation.</summary>
public sealed class EntityForgeCommitAdmissionException : EntityForgeDbException
{
    /// <summary>Creates an admission failure identifying the configured publication queue bound.</summary>
    public EntityForgeCommitAdmissionException(int maximumQueuedPublications)
        : base($"The local commit queue reached its configured limit of {maximumQueuedPublications} publications.")
    {
        MaximumQueuedPublications = maximumQueuedPublications;
    }

    /// <summary>Gets the maximum permitted queued publications.</summary>
    public int MaximumQueuedPublications
    {
        get;
    }
}
