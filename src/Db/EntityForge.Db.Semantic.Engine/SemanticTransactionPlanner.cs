using System.Diagnostics;
using EntityForge.Db.Semantic;
using EntityForge.Db.Semantic.Durability;
using System.Security.Cryptography;
using System.Text;
using EntityForge.Db.Semantic.Core;
using EntityForge.Db.Telemetry;
using EntityForge.Semantics;

namespace EntityForge.Db.Semantic.Engine;

internal sealed record SemanticTransactionPlan(
    SemanticDatabaseState State,
    SemanticCommittedEffects CommittedEffects,
    IReadOnlyList<SemanticWriteDependency> OwnershipWrites,
    IReadOnlyList<SemanticPredicateDependency> PredicateWrites);

internal sealed class SemanticTransactionPlanner
{
    private readonly SemanticStateProjector _projector;
    private readonly SourceSnapshotReconciler _sourceSnapshots;
    private ActiveEvidenceIndex? _evidence;
    private bool _evidenceComplete;
    private SemanticCatalogRegistry? _catalog;
    private SemanticDatabaseState? _indexedState;
    private readonly EntityForgeDbTelemetry? _telemetry;

    public SemanticTransactionPlanner(SemanticStateProjector projector,
        EntityForgeDbTelemetry? telemetry = null, SourceSnapshotReconciler? sourceSnapshots = null)
    {
        _projector = projector ?? throw new ArgumentNullException(nameof(projector));
        _telemetry = telemetry;
        _sourceSnapshots = sourceSnapshots ?? new SourceSnapshotReconciler();
    }

    internal void InvalidatePreparedIndexes()
    {
        _indexedState = null;
        _evidence = null;
        _evidenceComplete = false;
        _catalog = null;
    }

    public SemanticTransactionPlan Plan(SemanticDatabaseState state,
        SemanticSessionEnvelope session,
        IReadOnlyList<SemanticEntityChange> effectiveChanges,
        DatabaseVersion version,
        IReadOnlySet<SemanticEntityIdentity>? existingRelationshipTargets = null)
    {
        return Plan(state, [(session, effectiveChanges)], version, existingRelationshipTargets);
    }

    internal SemanticTransactionPlan Plan(SemanticDatabaseState state,
        IReadOnlyList<(SemanticSessionEnvelope Session, IReadOnlyList<SemanticEntityChange> Changes)> contributions,
        DatabaseVersion version, IReadOnlySet<SemanticEntityIdentity>? existingRelationshipTargets = null)
    {
        var session = contributions[0].Session;
        IReadOnlyList<SemanticEntityChange> effectiveChanges = contributions.SelectMany(c => c.Changes).ToArray();
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(effectiveChanges);
        long started = Stopwatch.GetTimestamp();
        long allocated = GC.GetTotalAllocatedBytes();
        Action<string, long, long>? recordStage = _telemetry is null
            ? null : _telemetry.RecordPlanningStage;
        SemanticDatabaseState projected = _projector.Apply(state, effectiveChanges,
            session.Start.SessionId, ReplayHash(contributions.Select(c => c.Session).ToArray()), version,
            retainProjectedEvidence: false, recordStage);
        _telemetry?.RecordPlanningStage("semantic_state_projection", started, allocated);
        started = Stopwatch.GetTimestamp();
        allocated = GC.GetTotalAllocatedBytes();
        projected = _sourceSnapshots.UpdateMembership(projected, contributions, version);
        _telemetry?.RecordPlanningStage("source_membership_projection", started, allocated);
        started = Stopwatch.GetTimestamp();
        allocated = GC.GetTotalAllocatedBytes();
        SemanticCommittedEffects effects = Effects(state, projected, version,
            existingRelationshipTargets ?? new HashSet<SemanticEntityIdentity>());
        _telemetry?.RecordPlanningStage("committed_effect_projection", started, allocated);
        projected = projected with
        {
            Catalog = _catalog!.Snapshot()
        };
        return new(projected, effects, new SemanticDependencyTracker().Capture(effectiveChanges),
            SemanticPredicateDependency.WritesFrom(state, effectiveChanges));
    }

    internal static string ReplayHash(IReadOnlyList<SemanticSessionEnvelope> sessions)
    {
        if (sessions.Count == 1)
        {
            return sessions[0].Close.ReplayHash;
        }

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write(sessions.Count);
        foreach (var session in sessions)
        {
            writer.Write(session.Close.ReplayHash);
        }

        writer.Flush();
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }

    public bool TryFindExistingIdentities(SemanticDatabaseState state,
        IReadOnlyList<SemanticEntityIdentity> identities,
        out IReadOnlySet<SemanticEntityIdentity> existing)
    {
        if (_evidenceComplete && _evidence is not null && _indexedState?.Version == state.Version)
        {
            existing = identities.Where(_evidence.Contains).ToHashSet();
            return true;
        }
        existing = new HashSet<SemanticEntityIdentity>();
        return false;
    }

    private SemanticCommittedEffects Effects(SemanticDatabaseState before,
        SemanticDatabaseState after, DatabaseVersion version,
        IReadOnlySet<SemanticEntityIdentity> existingRelationshipTargets)
    {
        var effects = new List<SemanticCommittedEffect>(checked(
            after.PublicationDelta.Fields.Count * 2 + after.PublicationDelta.Relationships.Count * 2));
        EnsureIndexes(before);
        ActiveEvidenceIndex beforeIndex = _evidence!;
        SemanticCatalogRegistry beforeCatalog = _catalog!;
        var priorMembership = new Dictionary<SemanticEntityIdentity, bool>();
        foreach (SemanticFieldEvidence field in after.PublicationDelta.Fields)
        {
            priorMembership.TryAdd(field.Subject, beforeIndex.Contains(field.Subject));
        }

        foreach (SemanticRelationshipEvidence relationship in after.PublicationDelta.Relationships)
        {
            priorMembership.TryAdd(relationship.Subject, beforeIndex.Contains(relationship.Subject));
            priorMembership.TryAdd(relationship.Target, beforeIndex.Contains(relationship.Target) ||
                existingRelationshipTargets.Contains(relationship.Target));
        }
        var priorFieldCounts = new Dictionary<(SemanticEntityIdentity Subject, SemanticTerm Field), int>();
        foreach (SemanticFieldEvidence field in after.PublicationDelta.Fields)
        {
            priorFieldCounts.TryAdd((field.Subject, field.Field),
                beforeIndex.FieldCount(field.Subject, field.Field));
        }

        var priorRelationshipCounts =
            new Dictionary<(SemanticEntityIdentity Subject, SemanticTerm Relationship), int>();
        foreach (SemanticRelationshipEvidence relationship in after.PublicationDelta.Relationships)
        {
            priorRelationshipCounts.TryAdd((relationship.Subject, relationship.Relationship),
                beforeIndex.RelationshipCount(relationship.Subject, relationship.Relationship));
        }

        beforeIndex.Apply(after.PublicationDelta, version);
        foreach (SemanticFieldEvidence field in after.PublicationDelta.Fields)
        {
            if (field.BeginVersion == version && field.EndVersion is null)
            {
                effects.Add(new SemanticFieldAssertionAdded(field));
            }

            if (field.EndVersion == version)
            {
                effects.Add(new SemanticFieldAssertionRetracted(field));
            }
        }
        foreach (SemanticRelationshipEvidence relationship in after.PublicationDelta.Relationships)
        {
            if (relationship.BeginVersion == version && relationship.EndVersion is null)
            {
                effects.Add(new SemanticRelationshipAssertionAdded(relationship));
            }

            if (relationship.EndVersion == version)
            {
                effects.Add(new SemanticRelationshipAssertionRetracted(relationship));
            }
        }

        foreach ((SemanticEntityIdentity subject, bool wasMember) in priorMembership)
        {
            // The planning slice hydrates changed owners, not every referenced entity's
            // independent contribution. Losing a local incoming edge cannot erase that
            // independent entity's concept membership.
            bool isMember = beforeIndex.Contains(subject) || after.SourceMemberships.ContainsSubject(subject);
            if (wasMember != isMember)
            {
                effects.Add(new SemanticConceptMembershipChanged(subject, wasMember, isMember));
            }
        }

        foreach (((SemanticEntityIdentity subject, SemanticTerm field), int beforeCount) in priorFieldCounts)
        {
            int afterCount = beforeIndex.FieldCount(subject, field);
            if (beforeCount != afterCount)
            {
                effects.Add(new SemanticFieldEvidenceChanged(subject, field, beforeCount, afterCount));
            }
        }
        foreach (((SemanticEntityIdentity subject, SemanticTerm relationship), int beforeCount) in
                 priorRelationshipCounts)
        {
            int afterCount = beforeIndex.RelationshipCount(subject, relationship);
            if (beforeCount != afterCount)
            {
                effects.Add(new SemanticRelationshipEvidenceChanged(subject,
                    relationship, beforeCount, afterCount));
            }
        }

        SemanticCatalogRegistry afterCatalog = beforeCatalog.Apply(after);
        AddCatalogRegistrations(effects, beforeCatalog, afterCatalog);
        _catalog = afterCatalog;
        _indexedState = after;
        return new(effects);
    }

    private void EnsureIndexes(SemanticDatabaseState state)
    {
        // A hydrated owner slice is not the full database. Consecutive group members
        // can hydrate different subjects at this same predecessor version.
        if (_evidenceComplete && _indexedState?.Version == state.Version)
        {
            return;
        }

        _evidence = ActiveEvidenceIndex.Create(state);
        _evidenceComplete = state.Fields.Count == state.AssertionStatistics.FieldCount &&
                            state.Relationships.Count == state.AssertionStatistics.RelationshipCount;
        _catalog = new SemanticCatalogRegistry(state);
        _indexedState = state;
    }

    private sealed class ActiveEvidenceIndex(
        Dictionary<SemanticEntityIdentity, int> entities,
        Dictionary<(SemanticEntityIdentity Subject, SemanticTerm Field), int> fields,
        Dictionary<(SemanticEntityIdentity Subject, SemanticTerm Relationship), int> relationships)
    {
        public bool Contains(SemanticEntityIdentity subject) => entities.GetValueOrDefault(subject) > 0;

        public int FieldCount(SemanticEntityIdentity subject, SemanticTerm field) =>
            fields.GetValueOrDefault((subject, field));

        public int RelationshipCount(SemanticEntityIdentity subject, SemanticTerm relationship) =>
            relationships.GetValueOrDefault((subject, relationship));

        public static ActiveEvidenceIndex Create(SemanticDatabaseState state)
        {
            var entities = new Dictionary<SemanticEntityIdentity, int>();
            var fields = new Dictionary<(SemanticEntityIdentity Subject, SemanticTerm Field), int>();
            var relationships =
                new Dictionary<(SemanticEntityIdentity Subject, SemanticTerm Relationship), int>();
            foreach (SemanticFieldEvidence field in state.Fields)
            {
                if (field.EndVersion is not null)
                {
                    continue;
                }

                Adjust(fields, (field.Subject, field.Field), 1);
                Adjust(entities, field.Subject, 1);
            }
            foreach (SemanticRelationshipEvidence relationship in state.Relationships)
            {
                if (relationship.EndVersion is not null)
                {
                    continue;
                }

                Adjust(relationships, (relationship.Subject, relationship.Relationship), 1);
                Adjust(entities, relationship.Subject, 1);
                Adjust(entities, relationship.Target, 1);
            }
            return new(entities, fields, relationships);
        }

        public void Apply(SemanticStatePublicationDelta delta, DatabaseVersion version)
        {
            foreach (SemanticFieldEvidence field in delta.Fields)
            {
                int change = Change(field.BeginVersion, field.EndVersion, version);
                Adjust(fields, (field.Subject, field.Field), change);
                Adjust(entities, field.Subject, change);
            }
            foreach (SemanticRelationshipEvidence relationship in delta.Relationships)
            {
                int change = Change(relationship.BeginVersion, relationship.EndVersion, version);
                Adjust(relationships, (relationship.Subject, relationship.Relationship), change);
                Adjust(entities, relationship.Subject, change);
                Adjust(entities, relationship.Target, change);
            }
        }

        private static int Change(DatabaseVersion begin, DatabaseVersion? end,
            DatabaseVersion version) => begin == version ? end is null ? 1 : 0 : end == version ? -1 : 0;

        private static void Adjust<TKey>(Dictionary<TKey, int> counts, TKey key, int change)
            where TKey : notnull
        {
            if (change == 0)
            {
                return;
            }

            int value = counts.GetValueOrDefault(key) + change;
            if (value == 0)
            {
                counts.Remove(key);
            }
            else
            {
                counts[key] = value;
            }
        }
    }

    private static void AddCatalogRegistrations(List<SemanticCommittedEffect> effects,
        SemanticCatalogRegistry oldCatalog, SemanticCatalogRegistry newCatalog)
    {
        var oldModels = oldCatalog.Models.ToHashSet();
        var oldTerms = oldCatalog.Terms.ToHashSet();
        var oldSchemes = oldCatalog.IdentitySchemes.ToHashSet();
        effects.AddRange(newCatalog.Models.Where(item => !oldModels.Contains(item))
            .Select(item => new SemanticCatalogModelRegistered(item)));
        effects.AddRange(newCatalog.Terms.Where(item => !oldTerms.Contains(item))
            .Select(item => new SemanticCatalogTermRegistered(item)));
        effects.AddRange(newCatalog.IdentitySchemes.Where(item => !oldSchemes.Contains(item))
            .Select(item => new SemanticCatalogIdentitySchemeRegistered(item)));
    }
}
