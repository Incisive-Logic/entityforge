using EntityForge.Db.Semantic.Durability;
using EntityForge.Db.Semantic;
using EntityForge.Db.Semantic.Core;
using EntityForge.Db.Routing;
using EntityForge.Db.Storage;
using EntityForge.Db.Telemetry;
using EntityForge.Semantics.Protobuf;
using EntityForge.Semantics;

namespace EntityForge.Db.Semantic.Engine;

internal sealed record ResumedWriteState(
    ResumableWriteTransaction Prepared,
    IReadOnlyList<SemanticEntityChange> Changes,
    IReadOnlyList<SemanticWriteDependency> OwnershipWrites,
    IReadOnlyList<SemanticPredicateDependency> PredicateWrites,
    PropertyStorageCatalogState PropertyStorage,
    GovernanceCatalogState Governance,
    RoutedParticipantSet Participants,
    IReadOnlyList<SemanticPredicateDependency> PredicateReads);

internal sealed class ResumableWriteManager
{
    private const long DatabaseVersionIncrement = 1;
    private readonly DatabaseMutationContext _context;
    private readonly ResumableWriteStateProjector _projector;
    private readonly SemanticDependencyTracker _dependencies;
    private readonly IShardRouter _router;
    private readonly EntityForgeDbTelemetry _telemetry;
    private readonly TimeProvider _timeProvider;

    public ResumableWriteManager(DatabaseMutationContext context,
        ResumableWriteStateProjector projector, SemanticDependencyTracker dependencies,
        IShardRouter router,
        EntityForgeDbTelemetry telemetry, TimeProvider timeProvider)
    {
        _context = context;
        _projector = projector;
        _dependencies = dependencies;
        _router = router;
        _telemetry = telemetry;
        _timeProvider = timeProvider;
    }

    public ValueTask<ResumableWriteTransaction> PrepareAsync(SemanticSessionEnvelope session,
        TimeSpan leaseDuration, Action admission, CancellationToken cancellationToken) => MutateAsync(
        "db.resumable_write.prepare", "The resumable write could not be prepared.",
        state =>
        {
            _context.State.ReadView.Catalog.Validate(session);
            return new(Guid.NewGuid(), Guid.NewGuid(), state.Version,
                _timeProvider.GetUtcNow().Add(leaseDuration), session,
                state.PropertyStorage.Version, state.PropertyStorage.Storage.Values.ToArray(),
                state.Governance.Version, state.Governance.Rules.Values.ToArray(),
                Array.Empty<SemanticPredicateDescriptor>());
        }, admission, cancellationToken);

    public ValueTask<ResumableWriteTransaction> PromoteAsync(EntityForgeWriteTransaction transaction,
        TimeSpan leaseDuration, Action admission, CancellationToken cancellationToken) => MutateAsync(
        "db.resumable_write.promote", "The write transaction could not be promoted.",
        _ => new(Guid.NewGuid(), Guid.NewGuid(), transaction.ReadVersion,
            _timeProvider.GetUtcNow().Add(leaseDuration), transaction.Session,
            transaction.PropertyStorage.Version, transaction.PropertyStorage.Storage.Values.ToArray(),
            transaction.Governance.Version, transaction.Governance.Rules.Values.ToArray(),
            transaction.PredicateReads.Select(item => item.ToDescriptor()).ToArray()), admission, cancellationToken);

    public async ValueTask<ResumedWriteState> ResumeAsync(Guid transactionId,
        CancellationToken cancellationToken)
    {
        using EntityForgeDbOperation operation = _telemetry.StartOperation("db.resumable_write.resume");
        bool entered = false;
        try
        {
            await _context.Coordinator.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            SemanticDatabaseState state = _context.State.Current;
            if (!state.ResumableWrites.TryGetValue(transactionId, out ResumableWriteTransaction? prepared))
            {
                throw new KeyNotFoundException($"Resumable transaction '{transactionId}' does not exist.");
            }

            if (prepared.ExpiresAtUtc <= _timeProvider.GetUtcNow())
            {
                throw new InvalidOperationException("An expired resumable transaction cannot be resumed.");
            }

            SemanticEntityChange[] changes = prepared.Session.Pages.OrderBy(page => page.PageNumber)
                .SelectMany(page => page.Changes).ToArray();
            IReadOnlyList<SemanticWriteDependency> writes = _dependencies.Capture(changes);
            RoutedParticipantSet participants = _router.Route(writes);
            IReadOnlyList<SemanticPredicateDependency> predicateWrites =
                SemanticPredicateDependency.WritesFrom(state, changes);
            return new(prepared, changes, writes, predicateWrites,
            new(prepared.PropertyStorageCatalogVersion,
                    prepared.PropertyStorage.ToDictionary(storage => storage.Property)),
                new(prepared.GovernanceCatalogVersion,
                    prepared.GovernanceRules.ToDictionary(rule => rule.Identifier)),
                participants, prepared.PredicateReads.Select(
                    SemanticPredicateDependency.FromDescriptor).ToArray());
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

            throw new EntityForgeDbQueryException("The resumable write could not be resumed.", exception);
        }
        finally
        {
            if (entered)
            {
                _context.Coordinator.Release();
            }
        }
    }

    public async ValueTask AbandonAsync(Guid transactionId, CancellationToken cancellationToken)
    {
        using EntityForgeDbOperation operation = _telemetry.StartOperation("db.resumable_write.abandon");
        bool entered = false;
        try
        {
            await _context.Coordinator.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            if (!_context.State.Current.ResumableWrites.ContainsKey(transactionId))
            {
                throw new KeyNotFoundException($"Resumable transaction '{transactionId}' does not exist.");
            }

            await CloseAsync(transactionId).ConfigureAwait(false);
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

            throw new EntityForgeDbCommitException("The resumable write could not be abandoned.", exception);
        }
        finally
        {
            if (entered)
            {
                _context.Coordinator.Release();
            }
        }
    }

    public async ValueTask CloseAsync(Guid transactionId)
    {
        SemanticDatabaseState state = _context.State.Current;
        if (!state.ResumableWrites.TryGetValue(transactionId, out ResumableWriteTransaction? prepared))
        {
            return;
        }

        var change = new ResumableWriteChange(ResumableWriteChangeKind.Close, prepared);
        _ = await _context.Log.CommitAsync(DurableDatabaseCommit.ForResumableWrite(change),
            CancellationToken.None).ConfigureAwait(false);
        _telemetry.RecordResumableWriteChange(change.Kind.ToString());
    }

    private async ValueTask<ResumableWriteTransaction> MutateAsync(string operationName,
        string failureMessage, Func<SemanticDatabaseState, ResumableWriteTransaction> create,
        Action admission, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(admission);
        using EntityForgeDbOperation operation = _telemetry.StartOperation(operationName);
        bool entered = false;
        try
        {
            await _context.Coordinator.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            admission();
            SemanticDatabaseState state = _context.State.Current;
            ResumableWriteTransaction prepared = create(state);
            var change = new ResumableWriteChange(ResumableWriteChangeKind.Prepare, prepared);
            _ = await _context.Log.CommitAsync(DurableDatabaseCommit.ForResumableWrite(change),
                cancellationToken).ConfigureAwait(false);
            _telemetry.RecordResumableWriteChange(change.Kind.ToString());
            return prepared;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            operation.SetOutcome("cancelled");
            throw;
        }
        catch (Exception exception)
        {
            operation.RecordException(exception);
            if (exception is EntityForgeDbException or SemanticContributionRevisionException)
            {
                throw;
            }

            throw new EntityForgeDbCommitException(failureMessage, exception);
        }
        finally
        {
            if (entered)
            {
                _context.Coordinator.Release();
            }
        }
    }

    private static CommittedTransactionDependencies EmptyDependencies(DatabaseVersion version) =>
        new(version, Array.Empty<SemanticWriteDependency>(), Array.Empty<SemanticPredicateDependency>(),
            Array.Empty<SemanticPredicateDependency>());
}
