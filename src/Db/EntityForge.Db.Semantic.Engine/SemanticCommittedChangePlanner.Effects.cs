using System.Collections.Immutable;
using EntityForge.Semantics.Protobuf;
using EntityForge.Semantics;
using EntityForge.Db.Semantic.Core;
using EntityForge.Db.Semantic;
using EntityForge.Db.Semantic.Durability;

namespace EntityForge.Db.Semantic.Engine;

internal sealed partial class SemanticCommittedChangePlanner
{
    private sealed record RetainedDependency(SemanticSessionEnvelope Session, SemanticEntityIdentity[] Entities, string Fingerprint);

    private sealed record RetainedEffects(SemanticDatabaseState Before, SemanticTransactionPlan Plan,
        IReadOnlyList<SemanticEntityChange> Changes, Lazy<RetainedDependency[]> Dependencies);

    private readonly Dictionary<Guid, DurableDatabaseCommit> _effectCommits = [];
    private readonly Dictionary<Guid, SemanticDatabaseState> _effectPredecessors = [];
    private readonly Dictionary<Guid, IReadOnlyList<SemanticEntityChange>> _effectChanges = [];
    private readonly Dictionary<Guid, RetainedEffects> _retainedEffects = [];

    internal void RetainEffects(Guid operation)
    {
        if (_prepared.TryGetValue(operation, out var plan) && _effectPredecessors.TryGetValue(operation, out var before))
        {
            var commit = _effectCommits[operation];
            var dependencies = new Lazy<RetainedDependency[]>(() => commit.Contributions.Select(c =>
            {
                var entities = SemanticPartitionDependencies.Entities(c.Session,
                    plan.State.PublicationDelta.Relationships).Concat(c.Read?.Entities ?? []).Distinct().ToArray();
                return new RetainedDependency(c.Session, entities,
                    SemanticPartitionDependencies.Fingerprint(before, c.Session, entities));
            }).ToArray());
            _retainedEffects.TryAdd(operation, new(before, plan, _effectChanges[operation], dependencies));
        }
    }

    internal void ReleaseEffects(Guid? operation)
    {
        if (operation is { } id)
        {
            _retainedEffects.Remove(id);
            _effectPredecessors.Remove(id);
            _effectChanges.Remove(id);
            _effectCommits.Remove(id);
        }
        else
        {
            _retainedEffects.Clear();
            _effectPredecessors.Clear();
            _effectChanges.Clear();
            _effectCommits.Clear();
        }
    }

    private bool CanRebaseEffects(DurableDatabaseCommit commit, RetainedEffects retained, CancellationToken token)
    {
        var current = _state.Working;
        var before = retained.Before;
        if (ReferenceEquals(current, before))
        {
            return true;
        }

        if (commit.IsInitialBulkLoad || commit.UsesAuthoritativeVersion || current.Version.Value < before.Version.Value ||
            current.AppliedSessions.ContainsKey(commit.TransactionId) ||
            current.PropertyStorage != before.PropertyStorage || current.Governance != before.Governance ||
            current.Catalog.VocabularyVersion != before.Catalog.VocabularyVersion ||
            !current.Catalog.Models.SequenceEqual(before.Catalog.Models) ||
            !current.Catalog.Terms.SequenceEqual(before.Catalog.Terms) ||
            !current.Catalog.IdentitySchemes.SequenceEqual(before.Catalog.IdentitySchemes) ||
            !current.Catalog.Vocabularies.SequenceEqual(before.Catalog.Vocabularies))
        {
            return false;
        }
        // Membership/progress protect field owners, including absent owners. Inbound
        // relationship existence also affects concept membership; until that dependency
        // has its own revision, retain the conservative global guard for relationship state.
        if ((before.AssertionStatistics.RelationshipCount != 0 || current.AssertionStatistics.RelationshipCount != 0 ||
             retained.Plan.State.PublicationDelta.Relationships.Count != 0) &&
            (!ReferenceEquals(current.SourceMemberships, before.SourceMemberships) ||
             !ReferenceEquals(current.SourceProgress, before.SourceProgress)))
        {
            return false;
        }

        foreach (var dependency in retained.Dependencies.Value)
        {
            token.ThrowIfCancellationRequested();
            if (SemanticPartitionDependencies.Fingerprint(current, dependency.Session, dependency.Entities) != dependency.Fingerprint)
            {
                return false;
            }
        }
        return true;
    }

    private SemanticCommittedChangePlan RebaseEffects(DurableDatabaseCommit commit, RetainedEffects retained)
    {
        var current = _state.Working;
        var plan = retained.Plan;
        var old = plan.State.Version;
        var version = commit.Version;
        DatabaseVersion Move(DatabaseVersion value) => value == old ? version : value;
        SemanticFieldEvidence Field(SemanticFieldEvidence value) => value with
        {
            BeginVersion = Move(value.BeginVersion),
            EndVersion = value.EndVersion is { } end ? Move(end) : null
        };
        SemanticRelationshipEvidence Relationship(SemanticRelationshipEvidence value) => value with
        {
            BeginVersion = Move(value.BeginVersion),
            EndVersion = value.EndVersion is { } end ? Move(end) : null
        };
        var delta = new SemanticStatePublicationDelta(plan.State.PublicationDelta.Fields.Select(Field).ToArray(),
            plan.State.PublicationDelta.Relationships.Select(Relationship).ToArray());
        var effects = new SemanticCommittedEffects(plan.CommittedEffects.Effects.Select(effect => effect switch
        {
            SemanticFieldAssertionAdded e => e with { Assertion = Field(e.Assertion) },
            SemanticFieldAssertionRetracted e => e with { Assertion = Field(e.Assertion) },
            SemanticRelationshipAssertionAdded e => e with { Assertion = Relationship(e.Assertion) },
            SemanticRelationshipAssertionRetracted e => e with { Assertion = Relationship(e.Assertion) },
            _ => effect
        }).ToArray());
        SemanticEffectRelocation.Register(plan.CommittedEffects, effects, old, version);
        var sessions = (current.AppliedSessions as ImmutableDictionary<Guid, AppliedSession> ??
            current.AppliedSessions.ToImmutableDictionary()).ToBuilder();
        var id = commit.TransactionId;
        sessions[id] = plan.State.AppliedSessions[id] with
        {
            Version = version
        };
        var progress = (current.SourceProgress as ImmutableDictionary<SourcePartitionKey, SourcePartitionProgress>
            ?? current.SourceProgress.ToImmutableDictionary()).ToBuilder();
        foreach (var change in plan.State.SourceMetadataChanges.Progress)
        {
            var identity = change.After ?? change.Before!;
            var key = new SourcePartitionKey(identity.SourceSystem, identity.SourcePartition);
            if (change.After is { } after)
            {
                progress[key] = new(after.GenerationId, after.SourceVersion, after.SourcePosition, Move(after.Version), after.Coverage);
            }
            else
            {
                progress.Remove(key);
            }
        }
        bool samePredecessor = ReferenceEquals(current, retained.Before);
        var memberships = samePredecessor ? plan.State.SourceMemberships : current.SourceMemberships;
        foreach (var change in samePredecessor ? [] : plan.State.SourceMetadataChanges.Memberships)
        {
            var identity = change.After ?? change.Before!;
            var owner = new SourceOwner(identity.Subject, identity.SourceSystem, identity.SourceRecord);
            memberships = change.After is { } after
                ? memberships.SetItem(owner, after.SourcePartition, after.Provenance) : memberships.Remove(owner);
        }
        var state = current with
        {
            Version = version,
            Fields = [],
            Relationships = [],
            PublicationDelta = delta,
            AppliedSessions = sessions.ToImmutable(),
            QueryStatistics = samePredecessor ? plan.State.QueryStatistics with
            {
                Version = version
            } : current.QueryStatistics.Apply(delta, version),
            AssertionStatistics = samePredecessor ? plan.State.AssertionStatistics : current.AssertionStatistics.Apply(delta, version),
            SourceMemberships = memberships,
            SourceProgress = progress.ToImmutable(),
            Catalog = plan.State.Catalog
        };
        state = state with
        {
            SourceMetadataChanges = plan.State.SourceMetadataChanges with
            {
                Progress = plan.State.SourceMetadataChanges.Progress.Select(change => change with
                {
                    After = change.After is { } after ? after with
                    {
                        Version = Move(after.Version)
                    } : null
                }).ToArray()
            }
        };
        _prepared[id] = plan with
        {
            State = state,
            CommittedEffects = effects
        };
        return Encode(effects) with
        {
            SourceMetadata = state.SourceMetadataChanges,
            CompleteChanges = retained.Changes,
            PropertyStorage = new(state.PropertyStorage.Version, state.PropertyStorage.Storage.Values.ToArray())
        };
    }
}
