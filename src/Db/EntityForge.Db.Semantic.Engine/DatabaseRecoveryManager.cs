using EntityForge.Db.Semantic.Durability;
using EntityForge.Db.Semantic;
using EntityForge.Db.Semantic.Core;
using EntityForge.Db.Routing;
using EntityForge.Db;
using EntityForge.Db.Storage;
using EntityForge.Db.Telemetry;
using EntityForge.Semantics.Protobuf;
using EntityForge.Semantics;
using EntityForge.Values;
using System.Collections.Immutable;

namespace EntityForge.Db.Semantic.Engine;

internal sealed partial class SemanticProfileStateCoordinator : ISemanticProfileStateParticipant
{
    private const long DatabaseVersionIncrement = 1;
    private readonly ISemanticCommitLog _log;
    private readonly ISemanticCandidateSnapshotProvider _timestampCandidates;
    private readonly SemanticCommitPreparationCoordinator _commitGate;
    private readonly IShardRouter _router;
    private readonly SemanticDependencyTracker _dependencies;
    private readonly SemanticStateProjector _projector;
    private readonly SemanticCommittedChangePlanner _committedChanges;
    private readonly SourceSnapshotReconciler _sourceSnapshots;
    private readonly ResumableWriteStateProjector _resumableState;
    private readonly TransactionValidationIndex _validation;
    private readonly ISemanticDatabaseStatePublisher _state;
    private readonly EntityForgeDbTelemetry _telemetry;
    private readonly SemanticChangeSessionTransportCodec _codec = new();
    private readonly TimeProvider _timeProvider;
    private readonly IEntityForgeCommitObserver _observer;
    private readonly JsonValueAdmissionPolicy _jsonAdmissionPolicy;
    private readonly Dictionary<Guid, PreparedDependencies> _preparedDependencies = [];
    private readonly SemanticContributionRevisionCatalog _contributionRevisions;
    private int _recoveredCommitCount;
    private bool _stagedDuringRecovery = true;
    private DatabaseVersion _checkpointRecoveryFloor;

    public SemanticProfileStateCoordinator(ISemanticCommitLog log, ISemanticCandidateSnapshotProvider candidates,
        IShardRouter router, SemanticDependencyTracker dependencies, SemanticStateProjector projector,
        SourceSnapshotReconciler sourceSnapshots, ResumableWriteStateProjector resumableState,
        TransactionValidationIndex validation, ISemanticDatabaseStatePublisher state,
        EntityForgeDbTelemetry telemetry, TimeProvider timeProvider,
        IEntityForgeCommitObserver observer, JsonValueAdmissionPolicy jsonAdmissionPolicy,
        SemanticConformanceMode conformanceMode, SemanticContributionRevisionLimits contributionRevisionLimits,
        SemanticCommitPreparationCoordinator commitGate)
    {
        _log = log;
        _router = router;
        _dependencies = dependencies;
        _timestampCandidates = candidates;
        _commitGate = commitGate;
        _projector = projector;
        _sourceSnapshots = sourceSnapshots;
        _resumableState = resumableState;
        _committedChanges = new(candidates, state, sourceSnapshots, projector, telemetry, conformanceMode, ReadPlanningEvidence);
        _validation = validation;
        _state = state;
        _telemetry = telemetry;
        _timeProvider = timeProvider;
        _observer = observer;
        _jsonAdmissionPolicy = jsonAdmissionPolicy ?? throw new ArgumentNullException(nameof(jsonAdmissionPolicy));
        _contributionRevisions = new(contributionRevisionLimits);
        _log.BindProfile(this);
    }

    public async ValueTask<IDisposable> EnterMaintenancePublicationAsync(CancellationToken cancellationToken)
    {
        await _commitGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new MaintenancePublicationAdmission(_commitGate);
    }

    private sealed class MaintenancePublicationAdmission(SemanticCommitPreparationCoordinator gate) : IDisposable
    {
        private SemanticCommitPreparationCoordinator? _gate = gate;

        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }

    public async ValueTask RecoverAsync(CancellationToken cancellationToken)
    {
        using EntityForgeDbOperation operation = _telemetry.StartOperation("db.recover");
        try
        {
            await _log.RecoverAsync(cancellationToken).ConfigureAwait(false);
            _telemetry.RecordValidationRecovery(_validation.RecoveryFloor.Value, _recoveredCommitCount);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            operation.RecordException(exception);
            if (exception is EntityForgeDbRecoveryException)
            {
                throw;
            }

            throw new EntityForgeDbRecoveryException("EntityForgeDB recovery failed.", exception);
        }
    }

    public async ValueTask<SemanticCommittedChangePlan> PlanCommittedChangesAsync(
        DurableDatabaseCommit commit, CancellationToken cancellationToken)
    {
        // A revision begin can race receiving validation before native admission.
        // Recheck at the same kernel boundary as the immutable read dependencies.
        if (commit.Kind == DurableDatabaseCommitKind.SemanticTransaction &&
            commit.Contributions.Any(c => c.Read is not null && _contributionRevisions.IsManaged(c.Session.Start.Source)))
        {
            throw new EntityForge.Db.Kernel.DatabasePartitionConflictException();
        }

        if (commit.Kind is DurableDatabaseCommitKind.ContributionRevision)
        {
            SemanticContributionRevisionChange change = commit.ContributionRevision!;
            SemanticSessionEnvelope? session = _contributionRevisions.Validate(change);
            if (session is null)
            {
                return SemanticCommittedChangePlan.NoChanges;
            }

            commit = DurableDatabaseCommit.ForSemanticSession(session) with
            {
                Version = commit.Version
            };
        }
        return await _committedChanges.PlanAsync(commit, cancellationToken).ConfigureAwait(false);
    }

    public void CaptureLogicalEffects(Guid operation) => _committedChanges.RetainEffects(operation);

    public void ReleaseLogicalEffects(Guid? operation) => _committedChanges.ReleaseEffects(operation);

    public IDisposable PinSnapshot(DatabaseVersion version) => _state.PinSnapshot(version);

    public DatabaseVersion GetSnapshotRetentionFloor() => _state.RetentionFloor(_state.Working, _timeProvider.GetUtcNow());

    public DatabaseVersion GetPhysicalHistoryRetentionFloor()
    {
        DatabaseVersion leaseFloor = GetSnapshotRetentionFloor();
        return leaseFloor.Value < _checkpointRecoveryFloor.Value ? leaseFloor : _checkpointRecoveryFloor;
    }

    public DatabaseVersion GetCurrentVersion() => _state.Working.Version;

    public bool HasActiveSnapshotRetention() =>
        GetSnapshotRetentionFloor().Value < GetCurrentVersion().Value;

    public SemanticContributionRevision? GetPublishedContributionRevision(SemanticContributionScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return FromKernel(_contributionRevisions.GetPublished(scope));
    }

    public SemanticContributionRevisionInfo? GetContributionRevision(Guid generationId) =>
        _contributionRevisions.Get(generationId);

    public SemanticSessionEnvelope GetSealedContributionRevision(Guid generationId) =>
        _contributionRevisions.GetSealedSession(generationId);

    public bool IsContributionRevisionManaged(SemanticChangeSessionSource source) =>
        _contributionRevisions.IsManaged(source);

    public async ValueTask ApplyAsync(DurableDatabaseCommit commit, bool isRecovery,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SemanticDatabaseState state = _state.Working;
        if (commit.Version.Value <= state.Version.Value ||
            !commit.UsesAuthoritativeVersion &&
            commit.Version.Value != state.Version.Value + DatabaseVersionIncrement)
        {
            throw new InvalidDataException("Commit version does not validly advance semantic state.");
        }

        if (isRecovery && commit.Kind is DurableDatabaseCommitKind.SemanticTransaction)
        {
            _ = await _committedChanges.PlanAsync(commit, cancellationToken,
                recoverFromPredecessor: true).ConfigureAwait(false);
        }
        else if (isRecovery && commit.Kind is DurableDatabaseCommitKind.ContributionRevision &&
                 commit.ContributionRevision!.Kind is SemanticContributionRevisionChangeKind.Publish)
        {
            DurableDatabaseCommit published = DurableDatabaseCommit.ForSemanticSession(
                _contributionRevisions.GetSealedSession(commit.ContributionRevision.GenerationId)) with
            {
                Version = commit.Version
            };
            _ = await _committedChanges.PlanAsync(published, cancellationToken,
                recoverFromPredecessor: true).ConfigureAwait(false);
        }
        if (!isRecovery)
        {
            _observer.Reached(EntityForgeCommitStage.AfterDurableCommit);
        }

        Guid? preparationId = commit.Kind is DurableDatabaseCommitKind.SemanticTransaction
            ? commit.TransactionId
            : commit.Kind is DurableDatabaseCommitKind.ContributionRevision &&
                commit.ContributionRevision!.Kind is SemanticContributionRevisionChangeKind.Publish
                ? commit.ContributionRevision.GenerationId : null;
        PreparedDependencies? prepared = preparationId is Guid id &&
            _preparedDependencies.Remove(id, out PreparedDependencies? value) ? value : null;
        Apply(commit, state, prepared);
        _stagedDuringRecovery = isRecovery;
        if (isRecovery)
        {
            _recoveredCommitCount++;
            _telemetry.RecordRecoveryRecord(commit.Kind.ToString());
        }
    }

    public ValueTask PublishStagedStateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _state.Publish(_state.Working);
        DatabaseProfileWorkTelemetry.Record("semantic", "publish", "state-publication", 1);
        if (!_stagedDuringRecovery)
        {
            _observer.Reached(EntityForgeCommitStage.AfterPublication);
        }

        _stagedDuringRecovery = false;
        return ValueTask.CompletedTask;
    }


    public void Prepare(Guid transactionId, IReadOnlyList<SemanticWriteDependency> writes,
        IReadOnlyList<SemanticPredicateDependency> predicateReads,
        IReadOnlyList<SemanticPredicateDependency> predicateWrites)
    {
        if (transactionId == Guid.Empty)
        {
            throw new ArgumentException("A preparation identity is required.",
            nameof(transactionId));
        }

        _preparedDependencies.Add(transactionId, new(writes, predicateReads, predicateWrites));
    }

    public void CancelPreparation(Guid transactionId) => _preparedDependencies.Remove(transactionId);

    public ValueTask StagePreparedStateAsync(DurableDatabaseCommit commit,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (commit.Kind is DurableDatabaseCommitKind.SemanticTransaction &&
            _committedChanges.TryGetPrepared(commit.TransactionId,
                out SemanticTransactionPlan? plan))
        {
            var before = _state.Working;
            var prior = _stagedPlanningEvidence ?? _planningEvidence;
            _stagedPlanningEvidence = prior is not null && prior.Version == before.Version
                ? prior.Apply(new SemanticTimestampEffects(before, plan!.State, plan.CommittedEffects))
                : null;
            _state.Stage(plan!.State);
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask AbandonPreparedStatesAsync(IReadOnlyList<Guid> transactionIds,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (Guid transactionId in transactionIds)
        {
            _preparedDependencies.Remove(transactionId);
            _committedChanges.Abandon(transactionId);
        }
        _stagedPlanningEvidence = null;
        if (_state is SemanticDatabaseStateStore store)
        {
            store.RestorePublishedPreparationState();
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask BeginPreparedPublicationAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _stagedPlanningEvidence = null;
        if (_state is SemanticDatabaseStateStore store)
        {
            store.RestorePublishedPreparationState();
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask RestoreCheckpointAsync(SemanticCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _stagedDuringRecovery = true;
        _checkpointRecoveryFloor = checkpoint.Version;
        _state.Stage(SemanticCheckpointMapper.Restore(checkpoint));
        _contributionRevisions.Restore(checkpoint.ContributionRevisions.Select(item =>
            new SemanticContributionRevisionSnapshot(item.Scope, ToKernel(item.Revision)!, item.Start,
                item.Pages, item.Close,
                ToKernel(item.ExpectedPublishedRevision),
                (EntityForge.Db.Kernel.DatabaseStagedGenerationStatus)item.Status,
                item.PartHashes, item.ManifestFingerprint)).ToArray());
        _validation.ResetForRecovery(checkpoint.Version);
        return ValueTask.CompletedTask;
    }

    public ValueTask<SemanticCheckpoint> CaptureCheckpointAsync(CancellationToken cancellationToken) =>
        CaptureCheckpointAsync(true, cancellationToken);

    public ValueTask<SemanticCheckpoint> CaptureCheckpointAsync(bool includeSourceMetadata,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SemanticDatabaseState state = _state.Working;
        if (state.ResumableWrites.Count > 0)
        {
            throw new EntityForgeDbCheckpointException("A checkpoint cannot pass active resumable-write intent.",
                new InvalidOperationException("Close or commit every resumable write before creating a checkpoint."));
        }

        DatabaseVersion retentionFloor = _state.RetentionFloor(state, _timeProvider.GetUtcNow());
        if (retentionFloor.Value < state.Version.Value)
        {
            throw new EntityForgeSnapshotRetentionException(state.Version, retentionFloor);
        }

        SemanticCheckpoint checkpoint = SemanticCheckpointMapper.Capture(state, includeSourceMetadata) with
        {
            ContributionRevisions = _contributionRevisions.Capture().Select(item =>
                new SemanticContributionRevisionCheckpoint(item.Scope, FromKernel(item.Identity)!, item.Start,
                    item.Pages, item.Close,
                    FromKernel(item.ExpectedPublishedGeneration),
                    (SemanticContributionRevisionStatus)item.Status, item.PartHashes,
                    item.ManifestFingerprint)).ToArray()
        };
        return ValueTask.FromResult(checkpoint);
    }

    public void AdvanceCheckpointRecoveryFloor(DatabaseVersion version)
    {
        if (version.Value < _checkpointRecoveryFloor.Value)
        {
            throw new InvalidOperationException("A checkpoint recovery floor cannot move backwards.");
        }

        _checkpointRecoveryFloor = version;
        // Publication can overlap newer commits and snapshot pins. Only retire
        // validation through the captured checkpoint and the retained read floor.
        var retainedFloor = _state.RetentionFloor(_state.Working, _timeProvider.GetUtcNow());
        _validation.AdvanceRecoveryFloor(new(Math.Min(version.Value, retainedFloor.Value)));
        _state.PrunePreparationHistory(version);
    }

    public void RetainAppliedSessions(IReadOnlySet<Guid> expired)
    {
        SemanticDatabaseState state = _state.Working;
        _state.Publish(state with
        {
            AppliedSessions = RetainedSessions(state.AppliedSessions, expired)
        });
    }

    internal static IReadOnlyDictionary<Guid, AppliedSession> RetainedSessions(
        IReadOnlyDictionary<Guid, AppliedSession> sessions, IReadOnlySet<Guid> expired)
    {
        // Maintenance commonly expires no sessions. Preserve the persistent map
        // so the next write does not rebuild the entire replay history.
        if (expired.Count == 0)
        {
            return sessions;
        }

        return (sessions as ImmutableDictionary<Guid, AppliedSession> ?? sessions.ToImmutableDictionary())
            .RemoveRange(expired);
    }

    private void Apply(DurableDatabaseCommit commit, SemanticDatabaseState state,
        PreparedDependencies? prepared = null)
    {
        TimestampSession = null;
        TimestampSessions = null;
        IReadOnlyList<SemanticWriteDependency> writes = prepared?.Writes ??
            Array.Empty<SemanticWriteDependency>();
        IReadOnlyList<SemanticPredicateDependency> predicateWrites = prepared?.PredicateWrites ??
            Array.Empty<SemanticPredicateDependency>();
        if (commit.Kind is DurableDatabaseCommitKind.ContributionRevision)
        {
            SemanticSessionEnvelope? published = ApplyContributionRevision(commit.ContributionRevision!);
            if (published is null)
            {
                _state.Stage(state with
                {
                    Version = commit.Version,
                    PublicationDelta = SemanticStatePublicationDelta.Empty
                });
                _validation.Record(new(commit.Version, writes,
                    Array.Empty<SemanticPredicateDependency>(), predicateWrites));
                return;
            }
            commit = DurableDatabaseCommit.ForSemanticSession(published) with
            {
                Version = commit.Version
            };
        }
        if (commit.Kind is DurableDatabaseCommitKind.PhysicalMaintenance or DurableDatabaseCommitKind.MappingRegistration or DurableDatabaseCommitKind.ApplicationOperation)
        {
            _state.Stage(state with
            {
                Version = commit.Version,
                PublicationDelta = SemanticStatePublicationDelta.Empty
            });
        }
        else if (commit.Kind is DurableDatabaseCommitKind.SemanticTransaction)
        {
            TimestampSessions = commit.Contributions.Select(c => c.Session).ToArray();
            TimestampSession = TimestampSessions.Count == 1 ? TimestampSessions[0] : null;
            if (_committedChanges.TryTakePrepared(commit.TransactionId,
                    out SemanticTransactionPlan? preparedPlan))
            {
                // Recovery has no live preparation entry. The plan carries evidence derived
                // from its hydrated logical predecessor, including removed values.
                writes = preparedPlan!.OwnershipWrites;
                predicateWrites = preparedPlan.PredicateWrites;
                _state.Stage(preparedPlan!.State);
            }
            else
            {
                var contributions = commit.Contributions.Select(c =>
                {
                    Validate(c.Session);
                    ValidateJsonValues(c.Session.Pages.SelectMany(page => page.Changes)
                        .SelectMany(change => change.Fields).Select(field => field.Value));
                    var supplied = c.Session.Pages.OrderBy(page => page.PageNumber).SelectMany(page => page.Changes).ToArray();
                    return (Session: c.Session, Changes: _sourceSnapshots.Expand(state, c.Session, supplied));
                }).ToArray();
                var changes = contributions.SelectMany(c => c.Changes).ToArray();
                writes = _dependencies.Capture(changes);
                predicateWrites = SemanticPredicateDependency.WritesFrom(state, changes);
                LocalShardTopology.RequireLocalParticipant(_router.Route(writes));
                SemanticDatabaseState recovered = _projector.Apply(state, changes,
                    commit.TransactionId, SemanticTransactionPlanner.ReplayHash(TimestampSessions), commit.Version);
                _state.Stage(_sourceSnapshots.ApplyOwnerCoverage(
                    _sourceSnapshots.UpdateMembership(recovered, contributions, commit.Version), commit.Contributions));
            }
        }
        else if (commit.Kind is DurableDatabaseCommitKind.PropertyStorage)
        {
            SemanticPropertyStorageChange storage = commit.PropertyStorage!;
            _state.Stage(state with
            {
                Version = commit.Version,
                PropertyStorage = state.PropertyStorage.Apply(storage)
            });
            predicateWrites = new[] { SemanticPredicateDependency.CatalogBinding(storage.Property) };
        }
        else if (commit.Kind is DurableDatabaseCommitKind.GovernanceRule)
        {
            SemanticGovernanceRuleChange rule = commit.GovernanceRule!;
            _state.Stage(state with
            {
                Version = commit.Version,
                Governance = state.Governance.Apply(rule)
            });
            predicateWrites = new[] { GovernanceDependency(rule.Kind, rule.Identifier) };
        }
        else if (commit.Kind is DurableDatabaseCommitKind.VocabularyPublication)
        {
            SemanticVocabularyPublicationChange publication = commit.VocabularyPublication!;
            SemanticCatalogSnapshot catalog = new SemanticCatalogRegistry(state)
                .Publish(publication).Snapshot();
            _state.Stage(state with
            {
                Version = commit.Version,
                Catalog = catalog
            });
        }
        else if (commit.Kind is DurableDatabaseCommitKind.SnapshotLease)
        {
            _state.Stage(state with
            {
                Version = commit.Version,
                SnapshotLeases = state.SnapshotLeases.Apply(commit.SnapshotLease!)
            });
        }
        else if (commit.Kind is DurableDatabaseCommitKind.ResumableWrite)
        {
            _state.Stage(_resumableState.Apply(state, commit.ResumableWrite!, commit.Version));
        }
        else
        {
            throw new InvalidDataException($"Unsupported recovered commit kind '{commit.Kind}'.");
        }

        _validation.Record(prepared is null
            ? new(commit.Version, writes, Array.Empty<SemanticPredicateDependency>(), predicateWrites)
            : new(commit.Version, prepared.Writes, prepared.PredicateReads, prepared.PredicateWrites));
    }

    private SemanticSessionEnvelope? ApplyContributionRevision(SemanticContributionRevisionChange change)
    {
        switch (change.Kind)
        {
            case SemanticContributionRevisionChangeKind.Begin:
                if (change.Start is null || change.Start.SessionId != change.GenerationId)
                {
                    throw new InvalidDataException("A contribution-revision begin has inconsistent identity.");
                }

                _contributionRevisions.Begin(change.Start, ToKernel(change.ExpectedPublishedRevision));
                return null;
            case SemanticContributionRevisionChangeKind.AcceptPart:
                if (change.Page is null || change.Page.SessionId != change.GenerationId)
                {
                    throw new InvalidDataException("A contribution-revision part has inconsistent identity.");
                }

                _contributionRevisions.AcceptPart(change.Page);
                return null;
            case SemanticContributionRevisionChangeKind.Seal:
                if (change.Close is null || change.Close.SessionId != change.GenerationId)
                {
                    throw new InvalidDataException("A contribution-revision seal has inconsistent identity.");
                }

                _contributionRevisions.Seal(change.Close);
                return null;
            case SemanticContributionRevisionChangeKind.Publish:
                return _contributionRevisions.Publish(change.GenerationId).Session;
            case SemanticContributionRevisionChangeKind.Abandon:
                _contributionRevisions.Abandon(change.GenerationId);
                return null;
            default:
                throw new InvalidDataException($"Unsupported contribution-revision change kind '{change.Kind}'.");
        }
    }

    private static EntityForge.Db.Kernel.DatabaseGenerationIdentity? ToKernel(
        SemanticContributionRevision? identity) => identity is null ? null :
        new(identity.GenerationId, identity.Revision, identity.DefinitionHash);

    private static SemanticContributionRevision? FromKernel(
        EntityForge.Db.Kernel.DatabaseGenerationIdentity? identity) => identity is null ? null :
        new(identity.GenerationId, identity.Revision, identity.DefinitionHash);

    private void ValidateJsonValues(IEnumerable<SemanticValue?> values)
    {
        foreach (JsonSemanticValue json in values.OfType<JsonSemanticValue>())
        {
            _jsonAdmissionPolicy.Validate(json.CanonicalValue);
        }
    }

    private sealed record PreparedDependencies(IReadOnlyList<SemanticWriteDependency> Writes,
        IReadOnlyList<SemanticPredicateDependency> PredicateReads,
        IReadOnlyList<SemanticPredicateDependency> PredicateWrites);


    private void Validate(SemanticSessionEnvelope session)
    {
        if (session.Pages.Count != session.Close.PageCount ||
            session.Pages.Sum(page => page.Changes.Count) != session.Close.ChangeCount)
        {
            throw new InvalidDataException("Semantic session counts do not match.");
        }

        if (session.Start.SessionId != session.Close.SessionId ||
            session.Pages.Any(page => page.SessionId != session.Start.SessionId))
        {
            throw new InvalidDataException("Semantic session identifiers do not match.");
        }

        var hashes = session.Close.OperationHash is null
            ? (ContentHash: _codec.ComputeContentHash(session.Pages), OperationHash: (string?)null)
            : _codec.ComputeHashes(session.Start, session.Pages);
        if (hashes.ContentHash != session.Close.ContentHash)
        {
            throw new InvalidDataException("Semantic session hash does not match.");
        }

        if (session.Close.OperationHash is not null &&
            hashes.OperationHash != session.Close.OperationHash)
        {
            throw new InvalidDataException("Semantic session operation hash does not match.");
        }

        Guid[] changeIds = session.Pages.SelectMany(page => page.Changes)
            .Select(change => change.ChangeId).ToArray();
        if (changeIds.Distinct().Count() != changeIds.Length)
        {
            throw new InvalidDataException("A semantic session cannot contain duplicate change identifiers.");
        }
    }

    private static SemanticPredicateDependency GovernanceDependency(
        SemanticGovernanceRuleKind kind, Uri identifier) => kind switch
        {
            SemanticGovernanceRuleKind.Constraint => SemanticPredicateDependency.Constraint(identifier),
            SemanticGovernanceRuleKind.Policy => SemanticPredicateDependency.Policy(identifier),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind,
                "Unsupported governance rule kind.")
        };
}
