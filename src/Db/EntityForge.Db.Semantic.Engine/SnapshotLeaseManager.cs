using EntityForge.Db.Semantic.Durability;
using EntityForge.Db.Semantic;
using EntityForge.Db.Semantic.Core;
using EntityForge.Db.Storage;
using EntityForge.Db.Telemetry;

namespace EntityForge.Db.Semantic.Engine;

internal sealed class SnapshotLeaseManager
{
    private const long InitialLeaseRevision = 1;
    private const long LeaseRevisionIncrement = 1;
    private const long DatabaseVersionIncrement = 1;
    private readonly DatabaseMutationContext _context;
    private readonly EntityForgeDbTelemetry _telemetry;
    private readonly TimeProvider _timeProvider;

    public SnapshotLeaseManager(DatabaseMutationContext context, EntityForgeDbTelemetry telemetry,
        TimeProvider timeProvider)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public ValueTask<SnapshotLease> AcquireAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        ValidateDuration(duration);
        return CommitAsync(Guid.NewGuid(), SnapshotLeaseChangeKind.Acquire, duration, cancellationToken);
    }

    public async ValueTask<SemanticChangeBootstrap> AcquireBootstrapAsync(
        TimeSpan duration,
        Func<CancellationToken, ValueTask<DatabaseChangeFeedBounds>> getChangeBounds,
        CancellationToken cancellationToken)
    {
        ValidateDuration(duration);
        ArgumentNullException.ThrowIfNull(getChangeBounds);
        SnapshotLeaseCommit committed = await CommitCoreAsync(Guid.NewGuid(),
            SnapshotLeaseChangeKind.Acquire, duration, cancellationToken, getChangeBounds)
            .ConfigureAwait(false);
        return new(committed.Lease, committed.Bounds!.Current);
    }

    public ValueTask<SnapshotLease> GetAsync(Guid leaseId, CancellationToken cancellationToken)
    {
        ValidateLeaseId(leaseId);
        cancellationToken.ThrowIfCancellationRequested();
        SnapshotLease? lease = _context.State.Current.SnapshotLeases.Active.GetValueOrDefault(leaseId);
        if (lease is null)
        {
            throw Failure(leaseId, EntityForgeSnapshotLeaseFailure.NotFound,
                $"Snapshot lease '{leaseId}' does not exist.");
        }

        if (lease.ExpiresAtUtc <= _timeProvider.GetUtcNow())
        {
            throw Failure(leaseId, EntityForgeSnapshotLeaseFailure.Expired,
                $"Snapshot lease '{leaseId}' has expired.");
        }

        return ValueTask.FromResult(lease);
    }

    public ValueTask<SnapshotLease> RenewAsync(Guid leaseId, long expectedRevision, TimeSpan duration,
        CancellationToken cancellationToken)
    {
        ValidateLeaseId(leaseId);
        ValidateRevision(expectedRevision);
        ValidateDuration(duration);
        return CommitAsync(leaseId, SnapshotLeaseChangeKind.Renew, duration, cancellationToken,
            expectedRevision);
    }

    public async ValueTask ReleaseAsync(Guid leaseId, CancellationToken cancellationToken)
    {
        ValidateLeaseId(leaseId);
        _ = await CommitAsync(leaseId, SnapshotLeaseChangeKind.Release, TimeSpan.Zero,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<SnapshotLease> CommitAsync(Guid leaseId, SnapshotLeaseChangeKind kind,
        TimeSpan duration, CancellationToken cancellationToken, long? expectedRevision = null)
        => (await CommitCoreAsync(leaseId, kind, duration, cancellationToken, null, expectedRevision)
            .ConfigureAwait(false)).Lease;

    private async ValueTask<SnapshotLeaseCommit> CommitCoreAsync(Guid leaseId, SnapshotLeaseChangeKind kind,
        TimeSpan duration, CancellationToken cancellationToken,
        Func<CancellationToken, ValueTask<DatabaseChangeFeedBounds>>? getChangeBounds,
        long? expectedRevision = null)
    {
        using EntityForgeDbOperation operation = _telemetry.StartOperation(
            $"db.snapshot_lease.{kind.ToString().ToLowerInvariant()}");
        bool entered = false;
        try
        {
            await _context.Coordinator.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            SemanticDatabaseState state = _context.State.Current;
            SnapshotLease? existing = state.SnapshotLeases.Active.GetValueOrDefault(leaseId);
            if (kind is not SnapshotLeaseChangeKind.Acquire && existing is null)
            {
                throw Failure(leaseId, EntityForgeSnapshotLeaseFailure.NotFound,
                    $"Snapshot lease '{leaseId}' does not exist.");
            }

            DateTimeOffset now = _timeProvider.GetUtcNow();
            if (kind is SnapshotLeaseChangeKind.Renew && existing!.ExpiresAtUtc <= now)
            {
                throw Failure(leaseId, EntityForgeSnapshotLeaseFailure.Expired,
                    $"Snapshot lease '{leaseId}' has expired.");
            }

            if (expectedRevision is not null && existing!.Revision != expectedRevision.Value)
            {
                throw Failure(leaseId, EntityForgeSnapshotLeaseFailure.RevisionMismatch,
                    $"Snapshot lease '{leaseId}' revision {existing.Revision} does not match expected " +
                    $"revision {expectedRevision.Value}.");
            }

            DatabaseVersion snapshot = existing?.SnapshotVersion ?? state.Version;
            long revision = existing is null ? InitialLeaseRevision :
                checked(existing.Revision + LeaseRevisionIncrement);
            DateTimeOffset expiry = kind is SnapshotLeaseChangeKind.Release
                ? existing!.ExpiresAtUtc : now.Add(duration);
            var change = new SnapshotLeaseChange(leaseId, kind, snapshot, expiry, revision);
            _context.Observer.Reached(EntityForgeCommitStage.BeforeDurableCommit);
            DatabaseVersion version = await _context.Log.CommitAsync(
                DurableDatabaseCommit.ForSnapshotLease(change), cancellationToken).ConfigureAwait(false);
            SnapshotLeaseState leases = _context.State.Current.SnapshotLeases;
            _telemetry.RecordSnapshotLeaseChange(kind.ToString());
            SnapshotLease lease = kind is SnapshotLeaseChangeKind.Release ? existing! : leases.Active[leaseId];
            DatabaseChangeFeedBounds? bounds = getChangeBounds is null
                ? null : await getChangeBounds(cancellationToken).ConfigureAwait(false);
            return new(lease, bounds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            operation.SetOutcome("cancelled");
            throw;
        }
        catch (Exception exception)
        {
            operation.RecordException(exception);
            if (exception is EntityForgeDbException)
            {
                throw;
            }

            throw new EntityForgeDbCommitException("The snapshot lease mutation failed.", exception);
        }
        finally
        {
            if (entered)
            {
                _context.Coordinator.Release();
            }
        }
    }

    private sealed record SnapshotLeaseCommit(SnapshotLease Lease, DatabaseChangeFeedBounds? Bounds);

    private static void ValidateLeaseId(Guid leaseId)
    {
        if (leaseId == Guid.Empty)
        {
            throw new ArgumentException("A snapshot lease ID cannot be empty.", nameof(leaseId));
        }
    }

    private static void ValidateDuration(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), duration,
                "A snapshot lease duration must be positive.");
        }
    }

    private static void ValidateRevision(long revision)
    {
        if (revision <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(revision));
        }
    }

    private static EntityForgeSnapshotLeaseException Failure(Guid leaseId,
        EntityForgeSnapshotLeaseFailure failure, string message) => new(leaseId, failure, message);
}
