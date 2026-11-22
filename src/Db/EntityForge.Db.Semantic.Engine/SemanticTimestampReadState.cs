using System.Collections.Immutable;
using EntityForge.Db.Semantic.Core;
using EntityForge.Db.Semantic;
using EntityForge.Semantics;

namespace EntityForge.Db.Semantic.Engine;

internal sealed partial class SemanticProfileStateCoordinator
{
    // Native publication order is deliberately separate from timestamp order. Equal
    // maximum versions alone cannot prove that a timestamp view is a native predecessor.
    private SemanticPlanningEvidence? _planningEvidence;
    // Private ordered-preparation basis; never published to timestamp readers.
    private SemanticPlanningEvidence? _stagedPlanningEvidence;

    internal void PublishPlanningEvidence(SemanticTimestampEffects effects)
    {
        var prior = _planningEvidence;
        _planningEvidence = prior is not null && prior.Version == effects.Before.Version
            ? prior.Apply(effects) : null;
    }

    private SemanticEvidenceSlice? ReadPlanningEvidence(DatabaseVersion predecessor,
        IReadOnlyList<SemanticEntityIdentity> subjects, CancellationToken token)
    {
        var state = _stagedPlanningEvidence ?? _planningEvidence;
        return state is not null && state.Version == predecessor
            ? state.Read(subjects, token) : null;
    }

    internal SemanticSessionEnvelope? TimestampSession
    {
        get; private set;
    }

    internal IReadOnlyList<SemanticSessionEnvelope>? TimestampSessions
    {
        get; private set;
    }

    internal SemanticDatabaseState TimestampMetadata => _state.Working;

    internal IDisposable EnterTimestampRead(SemanticDatabaseState state) =>
        (_state as SemanticDatabaseStateStore ?? throw new NotSupportedException("Timestamp reads need an owned state store."))
        .EnterTimestampRead(state);

    internal async ValueTask<SemanticDatabaseState> CaptureTimestampBaseAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        const int SubjectsPerBatch = 256;
        var state = _state.Working;
        using var snapshot = _timestampCandidates.CaptureCandidateSnapshot(state.Version);
        var fields = new Dictionary<Guid, SemanticFieldEvidence>();
        var relationships = new Dictionary<Guid, SemanticRelationshipEvidence>();
        var ownedSubjects = state.SourceMemberships.Entries.Select(pair => pair.Key.Subject).ToHashSet();
        if (snapshot is EntityForge.Db.Semantic.Durability.ISemanticRecoverySnapshot recovery)
        {
            var evidence = await recovery.ReadRecoveryEvidenceAsync(ownedSubjects, token).ConfigureAwait(false);
            foreach (var field in evidence.Fields.Where(e => e.BeginVersion.Value <= state.Version.Value &&
                (e.EndVersion is null || e.EndVersion.Value.Value > state.Version.Value)))
            {
                fields.Add(field.AssertionId, field);
            }

            foreach (var edge in evidence.Relationships.Where(e => e.BeginVersion.Value <= state.Version.Value &&
                (e.EndVersion is null || e.EndVersion.Value.Value > state.Version.Value)))
            {
                relationships.Add(edge.AssertionId, edge);
            }
        }
        else
        {
            foreach (var subjects in ownedSubjects.Chunk(SubjectsPerBatch))
            {
                var evidence = await snapshot.ReadOwnedEntityEvidenceAsync(subjects, token).ConfigureAwait(false);
                foreach (var field in evidence.Fields.Where(e => e.BeginVersion.Value <= state.Version.Value &&
                    (e.EndVersion is null || e.EndVersion.Value.Value > state.Version.Value)))
                {
                    fields[field.AssertionId] = field;
                }

                foreach (var edge in evidence.Relationships.Where(e => e.BeginVersion.Value <= state.Version.Value &&
                (e.EndVersion is null || e.EndVersion.Value.Value > state.Version.Value)))
                {
                    relationships[edge.AssertionId] = edge;
                }
            }
        }

        if (fields.Count != state.AssertionStatistics.FieldCount || relationships.Count != state.AssertionStatistics.RelationshipCount)
        {
            throw new InvalidDataException("Timestamp recovery did not reconstruct the complete Semantic evidence set.");
        }

        var complete = state with
        {
            Fields = new SemanticAssertionCollection<SemanticFieldEvidence>(fields.ToImmutableDictionary()),
            Relationships = new SemanticAssertionCollection<SemanticRelationshipEvidence>(relationships.ToImmutableDictionary()),
            // Replay protection belongs to native write state, not the recovered read basis.
            AppliedSessions = ImmutableDictionary<Guid, AppliedSession>.Empty
        };
        _planningEvidence = SemanticPlanningEvidence.Create(complete);
        return complete;
    }
}

internal sealed record SemanticTimestampEffects(SemanticDatabaseState Before,
    SemanticDatabaseState After, SemanticCommittedEffects Effects, SemanticSessionEnvelope? Session = null,
    IReadOnlyList<SemanticSessionEnvelope>? TransactionSessions = null)
{
    private static readonly DatabaseVersion StatisticsRetractionBegin = default;

    internal SemanticDatabaseState Apply(SemanticDatabaseState prior)
    {
        var fields = SemanticAssertionCollection<SemanticFieldEvidence>.Index(prior.Fields, e => e.AssertionId);
        var relationships = SemanticAssertionCollection<SemanticRelationshipEvidence>.Index(prior.Relationships, e => e.AssertionId);
        var version = new DatabaseVersion(Math.Max(prior.Version.Value, After.Version.Value));
        var fieldDelta = new List<SemanticFieldEvidence>();
        var relationshipDelta = new List<SemanticRelationshipEvidence>();
        foreach (var effect in Effects.Effects)
        {
            switch (effect)
            {
                case SemanticFieldAssertionAdded e:
                    if (fields.TryGetValue(e.Assertion.AssertionId, out var oldField))
                    {
                        fieldDelta.Add(oldField with
                        {
                            BeginVersion = StatisticsRetractionBegin,
                            EndVersion = version
                        });
                    }

                    fields = fields.SetItem(e.Assertion.AssertionId, e.Assertion);
                    fieldDelta.Add(e.Assertion with
                    {
                        BeginVersion = version,
                        EndVersion = null
                    });
                    break;
                case SemanticFieldAssertionRetracted e:
                    if (fields.TryGetValue(e.Assertion.AssertionId, out var removedField))
                    {
                        fieldDelta.Add(removedField with
                        {
                            BeginVersion = StatisticsRetractionBegin,
                            EndVersion = version
                        });
                    }

                    fields = fields.Remove(e.Assertion.AssertionId);
                    break;
                case SemanticRelationshipAssertionAdded e:
                    if (relationships.TryGetValue(e.Assertion.AssertionId, out var oldEdge))
                    {
                        relationshipDelta.Add(oldEdge with
                        {
                            BeginVersion = StatisticsRetractionBegin,
                            EndVersion = version
                        });
                    }

                    relationships = relationships.SetItem(e.Assertion.AssertionId, e.Assertion);
                    relationshipDelta.Add(e.Assertion with
                    {
                        BeginVersion = version,
                        EndVersion = null
                    });
                    break;
                case SemanticRelationshipAssertionRetracted e:
                    if (relationships.TryGetValue(e.Assertion.AssertionId, out var removedEdge))
                    {
                        relationshipDelta.Add(removedEdge with
                        {
                            BeginVersion = StatisticsRetractionBegin,
                            EndVersion = version
                        });
                    }

                    relationships = relationships.Remove(e.Assertion.AssertionId);
                    break;
            }
        }

        var activeFields = new SemanticAssertionCollection<SemanticFieldEvidence>(fields);
        var activeRelationships = new SemanticAssertionCollection<SemanticRelationshipEvidence>(relationships);
        var stats = prior.QueryStatistics.Apply(new(fieldDelta, relationshipDelta), version);
        var memberships = prior.SourceMemberships;
        var sessions = TransactionSessions ?? (Session is null ? null : new[] { Session });
        var membershipOwners = sessions?.SelectMany(session =>
            Before.SourceMemberships.GetPartition(session.Start.Source.SourceSystem, session.Start.Source.SourcePartition).Keys
                .Concat(After.SourceMemberships.GetPartition(session.Start.Source.SourceSystem, session.Start.Source.SourcePartition).Keys)
                .Concat(session.Pages.SelectMany(page => page.Changes).Select(change =>
                    new SourceOwner(change.Subject, change.Provenance.SourceSystem, change.Provenance.SourceRecord))))
                .Distinct().ToArray();
        var beforeMemberships = ReferenceEquals(Before.SourceMemberships, After.SourceMemberships)
            ? new Dictionary<SourceOwner, SourceMembership>() : Memberships(Before.SourceMemberships, membershipOwners);
        var afterMemberships = ReferenceEquals(Before.SourceMemberships, After.SourceMemberships)
            ? beforeMemberships : Memberships(After.SourceMemberships, membershipOwners);
        foreach (var key in beforeMemberships.Keys.Except(afterMemberships.Keys))
        {
            memberships = memberships.Remove(key);
        }

        foreach (var pair in afterMemberships)
        {
            if (!beforeMemberships.TryGetValue(pair.Key, out var old) || old != pair.Value)
            {
                memberships = memberships.SetItem(pair.Key, pair.Value.SourcePartition, pair.Value.Provenance);
            }
        }

        var result = prior with
        {
            Version = version,
            Fields = activeFields,
            Relationships = activeRelationships,
            AssertionStatistics = new(activeFields.Count, activeRelationships.Count),
            QueryStatistics = stats,
            PublicationDelta = SemanticStatePublicationDelta.Empty,
            Catalog = new(PatchSet(prior.Catalog.Models, Before.Catalog.Models, After.Catalog.Models),
                PatchSet(prior.Catalog.Terms, Before.Catalog.Terms, After.Catalog.Terms),
                PatchSet(prior.Catalog.IdentitySchemes, Before.Catalog.IdentitySchemes, After.Catalog.IdentitySchemes),
                new(Before.Catalog.VocabularyVersion == After.Catalog.VocabularyVersion ? prior.Catalog.VocabularyVersion.Value :
                    Math.Max(prior.Catalog.VocabularyVersion.Value, After.Catalog.VocabularyVersion.Value)),
                PatchSet(prior.Catalog.Vocabularies, Before.Catalog.Vocabularies, After.Catalog.Vocabularies)),
            Governance = new(new(Before.Governance.Version == After.Governance.Version ? prior.Governance.Version.Value :
                Math.Max(prior.Governance.Version.Value, After.Governance.Version.Value)),
                Patch(prior.Governance.Rules, Before.Governance.Rules, After.Governance.Rules)),
            PropertyStorage = new(new(Before.PropertyStorage.Version == After.PropertyStorage.Version ? prior.PropertyStorage.Version.Value :
                Math.Max(prior.PropertyStorage.Version.Value, After.PropertyStorage.Version.Value)),
                Patch(prior.PropertyStorage.Storage, Before.PropertyStorage.Storage, After.PropertyStorage.Storage)),
            SourceMemberships = memberships,
            SourceProgress = Patch(prior.SourceProgress, Before.SourceProgress, After.SourceProgress, sessions?.Select(s => new SourcePartitionKey(s.Start.Source.SourceSystem, s.Start.Source.SourcePartition)).Distinct()),
            // Native retention can retire replay records without a timestamp publication.
            // Carrying them here would keep retired records alive in the trimmed read basis.
            AppliedSessions = ImmutableDictionary<Guid, AppliedSession>.Empty,
            ResumableWrites = Patch(prior.ResumableWrites, Before.ResumableWrites, After.ResumableWrites),
            SnapshotLeases = new(Patch(prior.SnapshotLeases.Active, Before.SnapshotLeases.Active, After.SnapshotLeases.Active))
        };
        SemanticTimestampIndex.Carry(prior, result, Effects);
        return result;
    }

    private static Dictionary<SourceOwner, SourceMembership> Memberships(SourceMembershipIndex index, SourceOwner[]? owners)
    {
        if (owners is null)
        {
            return index.Entries.ToDictionary();
        }

        var result = new Dictionary<SourceOwner, SourceMembership>();
        foreach (var owner in owners)
        {
            if (index.TryGetMembership(owner, out var membership))
            {
                result.Add(owner, membership!);
            }
        }

        return result;
    }

    private static IReadOnlyList<T> PatchSet<T>(IReadOnlyList<T> prior, IReadOnlyList<T> before, IReadOnlyList<T> after) =>
        ReferenceEquals(before, after) ? prior : prior.Except(before.Except(after)).Union(after.Except(before)).ToImmutableArray();

    private static IReadOnlyDictionary<TKey, TValue> Patch<TKey, TValue>(IReadOnlyDictionary<TKey, TValue> prior,
        IReadOnlyDictionary<TKey, TValue> before, IReadOnlyDictionary<TKey, TValue> after, IEnumerable<TKey>? keys = null) where TKey : notnull
    {
        if (ReferenceEquals(before, after))
        {
            return prior;
        }

        var result = prior.ToImmutableDictionary();
        foreach (var key in keys ?? before.Keys.Concat(after.Keys).Distinct())
        {
            if (!after.TryGetValue(key, out var value))
            {
                result = result.Remove(key);
            }
            else if (!before.TryGetValue(key, out var old) || !EqualityComparer<TValue>.Default.Equals(old, value))
            {
                result = result.SetItem(key, value);
            }
        }
        return result;
    }
}
