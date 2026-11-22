using System.Diagnostics;
using EntityForge.Db.Semantic.Core;
using EntityForge.Db.Semantic;
using EntityForge.Db.Semantic.Durability;
using EntityForge.Db.Storage;
using EntityForge.Db.Telemetry;
using EntityForge.Semantics.Protobuf;
using EntityForge.Semantics;

namespace EntityForge.Db.Semantic.Engine;

internal sealed partial class SemanticCommittedChangePlanner
{
    internal static readonly AsyncLocal<Action?> AfterProjection = new();
    internal static readonly AsyncLocal<Action?> ReusedPlanningEvidence = new();
    private readonly ISemanticCandidateSnapshotProvider _candidates;
    private readonly ISemanticDatabaseStateReader _state;
    private readonly SourceSnapshotReconciler _sourceSnapshots;
    private readonly SemanticTransactionPlanner _transactions;
    private readonly EntityForgeDbTelemetry _telemetry;
    private readonly SemanticConformanceMode _conformanceMode;
    private readonly Dictionary<Guid, SemanticTransactionPlan> _prepared = [];
    private readonly Func<DatabaseVersion, IReadOnlyList<SemanticEntityIdentity>, CancellationToken, SemanticEvidenceSlice?>? _planningEvidence;

    public SemanticCommittedChangePlanner(ISemanticCandidateSnapshotProvider candidates,
        ISemanticDatabaseStateReader state, SourceSnapshotReconciler sourceSnapshots,
        SemanticStateProjector projector, EntityForgeDbTelemetry telemetry,
        SemanticConformanceMode conformanceMode,
        Func<DatabaseVersion, IReadOnlyList<SemanticEntityIdentity>, CancellationToken, SemanticEvidenceSlice?>? planningEvidence = null)
    {
        _candidates = candidates;
        _state = state;
        _sourceSnapshots = sourceSnapshots;
        _transactions = new(projector, telemetry, sourceSnapshots);
        _telemetry = telemetry;
        _conformanceMode = conformanceMode;
        _planningEvidence = planningEvidence;
    }

    public async ValueTask<SemanticCommittedChangePlan> PlanAsync(
        DurableDatabaseCommit commit, CancellationToken cancellationToken,
        bool recoverFromPredecessor = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (commit.Kind is DurableDatabaseCommitKind.GovernanceRule)
        {
            return GovernanceChanges(commit.GovernanceRule!);
        }

        if (commit.Kind is not DurableDatabaseCommitKind.SemanticTransaction)
        {
            return SemanticCommittedChangePlan.NoChanges;
        }

        if (_retainedEffects.TryGetValue(commit.TransactionId, out var retained) &&
            CanRebaseEffects(commit, retained, cancellationToken))
        {
            long reused = DatabaseProfileWorkTelemetry.Start();
            var result = RebaseEffects(commit, retained);
            DatabaseProfileWorkTelemetry.Record("semantic", "reuse", "entity-change", retained.Changes.Count, reused);
            return result;
        }
        try
        {
            long started = Stopwatch.GetTimestamp();
            long allocated = GC.GetTotalAllocatedBytes();
            SemanticSessionEnvelope session = commit.Contributions[0].Session;
            SemanticDatabaseState state = _state.Working;
            var contributions = new List<(SemanticSessionEnvelope Session, IReadOnlyList<SemanticEntityChange> Changes)>();
            var supplied = commit.Contributions.SelectMany(c => c.Session.Pages.OrderBy(p => p.PageNumber)
                .SelectMany(p => p.Changes)).ToArray();
            foreach (var contribution in commit.Contributions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourceChanges = contribution.Session.Pages.OrderBy(p => p.PageNumber).SelectMany(p => p.Changes).ToArray();
                var expanded = _sourceSnapshots.Expand(state, contribution.Session, sourceChanges);
                SemanticSourcePartitionGuard.Validate(state, contribution.Session.Start, expanded);
                contributions.Add((contribution.Session, expanded));
            }
            var ownerPartitions = new Dictionary<SourceOwner, Uri>();
            foreach (var contribution in contributions)
            {
                foreach (var change in contribution.Changes)
                {
                    var owner = new SourceOwner(change.Subject, change.Provenance.SourceSystem, change.Provenance.SourceRecord);
                    var partition = contribution.Session.Start.Source.SourcePartition;
                    if (ownerPartitions.TryGetValue(owner, out var previous) && previous != partition)
                    {
                        throw new SemanticSessionValidationException("A transaction cannot assign one source owner to different partitions.");
                    }

                    ownerPartitions[owner] = partition;
                }
            }

            IReadOnlyList<SemanticEntityChange> changes = contributions.SelectMany(c => c.Changes).ToArray();
            _telemetry.RecordPlanningStage("source_expansion", started, allocated);

            started = Stopwatch.GetTimestamp();
            allocated = GC.GetTotalAllocatedBytes();
            SemanticPlanningState working = await HydrateAsync(state, changes, recoverFromPredecessor,
                cancellationToken).ConfigureAwait(false);
            SemanticPartitionDependencies.Validate(working.State, commit);
            _telemetry.RecordPlanningStage("state_hydration", started, allocated);
            started = Stopwatch.GetTimestamp();
            allocated = GC.GetTotalAllocatedBytes();
            SemanticTransactionPlan plan = _transactions.Plan(working.State, contributions,
                commit.Version, working.ExistingRelationshipTargets);
            plan = plan with
            {
                State = _sourceSnapshots.ApplyOwnerCoverage(plan.State, commit.Contributions)
            };
            AfterProjection.Value?.Invoke();
            if (_conformanceMode is not SemanticConformanceMode.Open && state.Catalog.Vocabularies.Count > 0)
            {
                SemanticValidationResult conformance = new SemanticVocabularyConformanceValidator()
                    .Validate(supplied, state.Catalog.Vocabularies);
                if (conformance.Issues.Count > 0)
                {
                    plan = plan with
                    {
                        CommittedEffects = new(plan.CommittedEffects.Effects.Concat(
                        conformance.Issues.Select(issue =>
                            (SemanticCommittedEffect)new SemanticConformanceIssueRecorded(
                                session.Start.SessionId, issue))).ToArray())
                    };
                }
            }
            _telemetry.RecordPlanningStage("transaction_projection", started, allocated);
            DatabaseProfileWorkTelemetry.Record("semantic", "compile", "entity-change", changes.Count, started);
            _effectPredecessors[session.Start.SessionId] = state;
            _effectCommits[session.Start.SessionId] = commit;
            _effectChanges[session.Start.SessionId] = changes;
            _prepared[session.Start.SessionId] = plan with
            {
                State = plan.State with
                {
                    Fields = [],
                    Relationships = []
                }
            };
            if (commit.IsInitialBulkLoad)
            {
                var routing = new CommittedChangeRoutingSummary(overflowedKinds:
                [
                    SemanticChangeRoutingKinds.Entity,
                    SemanticChangeRoutingKinds.Concept,
                    SemanticChangeRoutingKinds.Term,
                    SemanticChangeRoutingKinds.EffectKind,
                    SemanticChangeRoutingKinds.Model,
                    SemanticChangeRoutingKinds.IdentityScheme,
                    SemanticChangeRoutingKinds.GovernanceRule
                ]);
                return new(new(SemanticCommittedChangeCodecs.DatasetReset,
                    SemanticCommittedChangeCodecs.DatasetResetVersion, ReadOnlyMemory<byte>.Empty,
                    plan.CommittedEffects.Effects.Count, routing, CommittedChangeKind.DatasetReset),
                    plan.CommittedEffects,
                    new(state.PropertyStorage.Version, state.PropertyStorage.Storage.Values.ToArray()))
                {
                    SourceMetadata = plan.State.SourceMetadataChanges
                };
            }
            return Encode(plan.CommittedEffects) with
            {
                SourceMetadata = plan.State.SourceMetadataChanges,
                CompleteChanges = changes,
                PropertyStorage = new(state.PropertyStorage.Version,
                    state.PropertyStorage.Storage.Values.ToArray())
            };
        }
        catch
        {
            Abandon(commit.TransactionId);
            throw;
        }
    }

    public bool TryTakePrepared(Guid sessionId, out SemanticTransactionPlan? plan)
    {
        if (!_prepared.Remove(sessionId, out plan))
        {
            return false;
        }
        // These inputs belong to preparation, not committed history. A logical
        // retry owns its separate RetainedEffects entry until explicit release.
        _effectPredecessors.Remove(sessionId);
        _effectChanges.Remove(sessionId);
        _effectCommits.Remove(sessionId);
        return true;
    }

    public bool TryGetPrepared(Guid sessionId, out SemanticTransactionPlan? plan) =>
        _prepared.TryGetValue(sessionId, out plan);

    public void Abandon(Guid sessionId)
    {
        _prepared.Remove(sessionId);
        _effectPredecessors.Remove(sessionId);
        _effectChanges.Remove(sessionId);
        _effectCommits.Remove(sessionId);
        // A rejected candidate's version may next be used by a metadata commit. Its
        // speculative evidence must not be reused merely because that version matches.
        _transactions.InvalidatePreparedIndexes();
    }

    private async ValueTask<SemanticPlanningState> HydrateAsync(SemanticDatabaseState state,
        IReadOnlyList<SemanticEntityChange> changes, bool recoverFromPredecessor,
        CancellationToken cancellationToken)
    {
        if (state.AssertionStatistics.FieldCount == 0 && state.AssertionStatistics.RelationshipCount == 0)
        {
            return new(state with
            {
                Fields = [],
                Relationships = []
            }, new HashSet<SemanticEntityIdentity>());
        }

        SemanticEntityIdentity[] subjects = changes.Select(change => change.Subject).Distinct().ToArray();
        HashSet<SemanticEntityIdentity> subjectSet = subjects.ToHashSet();
        SemanticEntityIdentity[] targets = changes.SelectMany(change => change.Relationships)
            .Select(relationship => relationship.Target).OfType<SemanticEntityIdentity>()
            .Where(target => !subjectSet.Contains(target))
            .Distinct().ToArray();
        SemanticEntityIdentity[] existingSubjects = subjects
            .Where(state.SourceMemberships.ContainsSubject).ToArray();
        SemanticEvidenceSlice evidence = new([], []);
        SemanticEvidenceSlice? retainedEvidence = !recoverFromPredecessor && existingSubjects.Length != 0
            ? _planningEvidence?.Invoke(state.Version, existingSubjects, cancellationToken) : null;
        if (retainedEvidence is not null)
        {
            evidence = retainedEvidence;
            ReusedPlanningEvidence.Value?.Invoke();
        }
        HashSet<SemanticEntityIdentity> existingTargets = targets
            .Where(state.SourceMemberships.ContainsSubject).ToHashSet();
        SemanticEntityIdentity[] unknownTargets = targets
            .Where(target => !existingTargets.Contains(target)).ToArray();
        IReadOnlySet<SemanticEntityIdentity> cachedTargets = new HashSet<SemanticEntityIdentity>();
        bool targetsResolved = unknownTargets.Length == 0 ||
            _transactions.TryFindExistingIdentities(state, unknownTargets, out cachedTargets);
        if (targetsResolved && unknownTargets.Length != 0)
        {
            existingTargets.UnionWith(cachedTargets);
        }

        if ((existingSubjects.Length != 0 && retainedEvidence is null) || !targetsResolved)
        {
            // Recovery can plan a WAL tail after physical redo has already exposed newer
            // generations. Hydration must therefore see the exact logical predecessor, not
            // whichever physical generation happens to be current.
            using ISemanticCandidateSnapshot snapshot = recoverFromPredecessor
                ? _candidates.CaptureCandidateSnapshot(state.Version)
                : _candidates.CaptureCandidateSnapshot();
            if (existingSubjects.Length != 0 && retainedEvidence is null)
            {
                evidence = await snapshot.ReadOwnedEntityEvidenceAsync(existingSubjects, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (!targetsResolved)
            {
                existingTargets.UnionWith(await snapshot.FindExistingIdentitiesAsync(
                    unknownTargets, cancellationToken).ConfigureAwait(false));
            }
        }
        return new(state with
        {
            Fields = evidence.Fields,
            Relationships = evidence.Relationships
        }, existingTargets);
    }

    private SemanticCommittedChangePlan GovernanceChanges(SemanticGovernanceRuleChange change)
    {
        SemanticDatabaseState state = _state.Working;
        state.Governance.Rules.TryGetValue(change.Identifier, out SemanticGovernanceRule? before);
        SemanticGovernanceRule after = state.Governance.Apply(change).Rules[change.Identifier];
        return Encode(new SemanticCommittedEffects([new SemanticGovernanceRulePublished(before, after)]));
    }

    private SemanticCommittedChangePlan Encode(SemanticCommittedEffects effects)
    {
        if (effects.Effects.Count == 0)
        {
            return SemanticCommittedChangePlan.NoChanges;
        }

        long started = Stopwatch.GetTimestamp();
        long allocated = GC.GetTotalAllocatedBytes();
        ReadOnlyMemory<byte> content = SemanticCommittedEffectsCodec.Encode(effects);
        _telemetry.RecordPlanningStage("effects_encoding", started, allocated);
        started = Stopwatch.GetTimestamp();
        allocated = GC.GetTotalAllocatedBytes();
        CommittedChangeRoutingSummary routing = SemanticChangeRoutingSummaryBuilder.Build(effects);
        _telemetry.RecordPlanningStage("change_routing", started, allocated);
        return new(CommittedChangePayload.BorrowOwnedContent(SemanticCommittedEffectsCodec.CodecId,
            SemanticCommittedEffectsCodec.CodecVersion, content, effects.Effects.Count, routing), effects);
    }

    private sealed record SemanticPlanningState(SemanticDatabaseState State,
        IReadOnlySet<SemanticEntityIdentity> ExistingRelationshipTargets);
}
