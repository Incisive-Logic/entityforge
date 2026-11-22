using System.Security.Cryptography;
using System.Text;
using System.Collections.Immutable;
using EntityForge.Db.Semantic;
using EntityForge.Db.Semantic.Durability;
using EntityForge.Semantics;

namespace EntityForge.Db.Semantic.Core;

/// <summary>Expands source snapshot changes to account for previously owned contributions that are now absent.</summary>
public sealed class SourceSnapshotReconciler
{
    internal Action? BeforeActiveOwnerCheck
    {
        get; set;
    }

    private const int GuidByteLength = 16;

    /// <summary>Adds reconciliation changes for source-owned evidence omitted by an authoritative snapshot.</summary>
    public IReadOnlyList<SemanticEntityChange> Expand(
        SemanticDatabaseState state,
        SemanticSessionEnvelope session,
        IReadOnlyList<SemanticEntityChange> changes)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(changes);
        Uri sourceSystem = session.Start.Source.SourceSystem;
        if (changes.Any(change => change.Provenance.SourceSystem != sourceSystem))
        {
            throw new SemanticSessionValidationException(
                "Every change in a semantic session must belong to its declared source system.");
        }
        // Incremental entity changes cannot retract omitted owners unless a complete
        // source record is replaced. Keep validation above this shortcut.
        if (session.Start.Kind is SemanticChangeSessionKind.Incremental &&
            !changes.Any(change => change.Kind is SemanticEntityChangeKind.ReplaceSourceState))
        {
            return changes.ToArray();
        }

        Uri partition = session.Start.Source.SourcePartition;
        HashSet<SourceOwner> observed = changes.Select(Owner).ToHashSet();
        var expanded = new List<SemanticEntityChange>(changes);
        HashSet<string> completeRecords = changes
            .Where(change => change.Kind is SemanticEntityChangeKind.ReplaceSourceState)
            .Select(change => change.Provenance.SourceRecord)
            .ToHashSet(StringComparer.Ordinal);
        foreach (KeyValuePair<SourceOwner, SemanticProvenance> membership in
            state.SourceMemberships.GetPartition(sourceSystem, partition))
        {
            SourceOwner owner = membership.Key;
            bool completeRecordReplaced = completeRecords.Contains(owner.SourceRecord);
            bool absentFromCompleteSnapshot = session.Start.Kind is SemanticChangeSessionKind.SourceSnapshot;
            if (!observed.Contains(owner) && (completeRecordReplaced || absentFromCompleteSnapshot))
            {
                expanded.Add(new SemanticEntityChange(ChangeId(session.Start.SessionId, owner),
                    SemanticEntityChangeKind.RetractSourceEntity, owner.Subject, membership.Value));
            }
        }
        return expanded;
    }

    /// <summary>Updates immutable source membership to reflect the accepted session changes.</summary>
    public SemanticDatabaseState UpdateMembership(
        SemanticDatabaseState projected,
        SemanticSessionEnvelope session,
        IReadOnlyList<SemanticEntityChange> effectiveChanges,
        DatabaseVersion version)
    {
        // The projected evidence belongs to this hydrated write scope. A cache indexed
        // by the published metadata state can be empty or belong to another write scope,
        // even at consecutive versions. Only removal-only changes need this observation.
        HashSet<SourceOwner> activeOwners = effectiveChanges.Any(RequiresActiveOwnerCheck)
            ? projected.Fields.Where(item => item.EndVersion is null)
                .Select(item => Owner(item.Subject, item.Provenance))
                .Concat(projected.Relationships.Where(item => item.EndVersion is null)
                    .Select(item => Owner(item.Subject, item.Provenance))).ToHashSet()
            : [];
        SourceMembershipIndex memberships = projected.SourceMemberships;
        Uri partition = session.Start.Source.SourcePartition;
        var membershipMutations = new List<SourceMembershipMutation>(effectiveChanges.Count);
        foreach (SemanticEntityChange change in effectiveChanges)
        {
            SourceOwner owner = Owner(change);
            if (change.Kind is SemanticEntityChangeKind.RetractSourceEntity)
            {
                membershipMutations.Add(SourceMembershipMutation.Remove(owner));
            }
            else
            {
                membershipMutations.Add(SourceMembershipMutation.Set(owner, partition, change.Provenance));
            }
        }

        BeforeActiveOwnerCheck?.Invoke();
        foreach (SemanticEntityChange change in effectiveChanges.Where(RequiresActiveOwnerCheck))
        {
            SourceOwner owner = Owner(change);
            bool active = activeOwners.Contains(owner);
            if (!active)
            {
                membershipMutations.Add(SourceMembershipMutation.Remove(owner));
            }
        }
        memberships = memberships.Apply(membershipMutations);

        // Record the actual reconciled result once per touched owner. In particular a
        // removal-only change can first set, then remove, the same owner above.
        // Do not enumerate the complete ownership index to discover these changes.
        var replacements = new List<SemanticMetadataReplacement<CheckpointSourceMembership>>();
        foreach (SourceOwner owner in membershipMutations.Select(item => item.Owner).Distinct())
        {
            projected.SourceMemberships.TryGetMembership(owner, out SourceMembership? before);
            memberships.TryGetMembership(owner, out SourceMembership? after);
            if (Equals(before, after))
            {
                continue;
            }

            CheckpointSourceMembership? Convert(SourceMembership? value) => value is null ? null :
                new(owner.Subject, owner.SourceSystem, owner.SourceRecord, value.SourcePartition, value.Provenance);
            replacements.Add(new(Convert(before), Convert(after)));
        }

        ImmutableDictionary<SourcePartitionKey, SourcePartitionProgress> progress =
            projected.SourceProgress as ImmutableDictionary<SourcePartitionKey, SourcePartitionProgress> ??
            projected.SourceProgress.ToImmutableDictionary();
        var progressKey = new SourcePartitionKey(session.Start.Source.SourceSystem, partition);
        progress.TryGetValue(progressKey, out SourcePartitionProgress? beforeProgress);
        var afterProgress = new SourcePartitionProgress(
            session.Start.Source.GenerationId, session.Start.Source.SourceVersion,
            session.Start.Source.SourcePosition, version);
        progress = progress.SetItem(progressKey, afterProgress);
        CheckpointSourcePartitionProgress? ConvertProgress(SourcePartitionProgress? value) => value is null ? null :
            new(progressKey.SourceSystem, progressKey.SourcePartition, value.GenerationId,
                value.SourceVersion, value.SourcePosition, value.Version, value.Coverage);
        SemanticDatabaseState result = projected with
        {
            SourceMemberships = memberships,
            SourceProgress = progress,
            SourceMetadataChanges = new(replacements.ToArray(),
                [new(ConvertProgress(beforeProgress), ConvertProgress(afterProgress))])
        };
        return result;
    }

    /// <summary>Updates immutable source membership to reflect the accepted session changes.</summary>
    public SemanticDatabaseState UpdateMembership(SemanticDatabaseState projected,
        IReadOnlyList<(SemanticSessionEnvelope Session, IReadOnlyList<SemanticEntityChange> Changes)> contributions,
        DatabaseVersion version)
    {
        // One mutable workspace for the transaction; publish the three membership
        // indexes and progress map once. Contribution order still determines the
        // final owner and progress values, including set-then-remove transitions.
        var mutations = new List<SourceMembershipMutation>();
        HashSet<SourceOwner> active = contributions.Any(c => c.Changes.Any(RequiresActiveOwnerCheck))
            ? projected.Fields.Where(e => e.EndVersion is null).Select(e => Owner(e.Subject, e.Provenance))
                .Concat(projected.Relationships.Where(e => e.EndVersion is null).Select(e => Owner(e.Subject, e.Provenance))).ToHashSet()
            : [];
        var originalProgress = projected.SourceProgress as ImmutableDictionary<SourcePartitionKey, SourcePartitionProgress>
            ?? projected.SourceProgress.ToImmutableDictionary();
        var progress = originalProgress.ToBuilder();
        var touchedProgress = new HashSet<SourcePartitionKey>();
        foreach (var contribution in contributions)
        {
            var source = contribution.Session.Start.Source;
            foreach (var change in contribution.Changes)
            {
                mutations.Add(change.Kind is SemanticEntityChangeKind.RetractSourceEntity
                    ? SourceMembershipMutation.Remove(Owner(change))
                    : SourceMembershipMutation.Set(Owner(change), source.SourcePartition, change.Provenance));
            }

            BeforeActiveOwnerCheck?.Invoke();
            foreach (var change in contribution.Changes.Where(RequiresActiveOwnerCheck))
            {
                if (!active.Contains(Owner(change)))
                {
                    mutations.Add(SourceMembershipMutation.Remove(Owner(change)));
                }
            }

            var key = new SourcePartitionKey(source.SourceSystem, source.SourcePartition);
            touchedProgress.Add(key);
            progress[key] = new(source.GenerationId, source.SourceVersion, source.SourcePosition, version);
        }
        var memberships = projected.SourceMemberships.Apply(mutations);
        var membershipChanges = new List<SemanticMetadataReplacement<CheckpointSourceMembership>>();
        foreach (var owner in mutations.Select(m => m.Owner).Distinct())
        {
            projected.SourceMemberships.TryGetMembership(owner, out var before);
            memberships.TryGetMembership(owner, out var after);
            if (Equals(before, after))
            {
                continue;
            }

            CheckpointSourceMembership? Convert(SourceMembership? value) => value is null ? null :
                new(owner.Subject, owner.SourceSystem, owner.SourceRecord, value.SourcePartition, value.Provenance);
            membershipChanges.Add(new(Convert(before), Convert(after)));
        }
        var progressChanges = new List<SemanticMetadataReplacement<CheckpointSourcePartitionProgress>>();
        foreach (var key in touchedProgress)
        {
            originalProgress.TryGetValue(key, out var before);
            var after = progress[key];
            if (Equals(before, after))
            {
                continue;
            }

            CheckpointSourcePartitionProgress? Convert(SourcePartitionProgress? value) => value is null ? null :
                new(key.SourceSystem, key.SourcePartition, value.GenerationId, value.SourceVersion, value.SourcePosition, value.Version, value.Coverage);
            progressChanges.Add(new(Convert(before), Convert(after)));
        }
        return projected with
        {
            SourceMemberships = memberships,
            SourceProgress = progress.ToImmutable(),
            SourceMetadataChanges = new(membershipChanges.ToArray(), progressChanges.ToArray())
        };
    }

    /// <summary>Applies explicit local-owner coverage when reconciling source membership.</summary>
    public SemanticDatabaseState ApplyOwnerCoverage(SemanticDatabaseState state, IReadOnlyList<SemanticTransactionContribution> contributions)
    {
        if (!contributions.Any(contribution => (contribution.Read?.OwnerCoverage ?? contribution.LocalOwnerCoverage) is not null))
        {
            return state;
        }

        var progress = state.SourceProgress.ToImmutableDictionary().ToBuilder();
        var updates = state.SourceMetadataChanges.Progress.ToArray();
        var addresses = updates.Select((change, index) => (change.After, Index: index)).Where(item => item.After is not null)
            .ToDictionary(item => new SourcePartitionKey(item.After!.SourceSystem, item.After.SourcePartition), item => item.Index);
        foreach (var contribution in contributions)
        {
            if ((contribution.Read?.OwnerCoverage ?? contribution.LocalOwnerCoverage) is not { } coverage)
            {
                continue;
            }

            var source = contribution.Session.Start.Source;
            var key = new SourcePartitionKey(source.SourceSystem, source.SourcePartition);
            progress[key] = progress[key] with
            {
                Coverage = coverage
            };
            if (!addresses.TryGetValue(key, out int index))
            {
                throw new InvalidDataException("Owner coverage has no source progress publication.");
            }

            updates[index] = updates[index] with
            {
                After = updates[index].After! with
                {
                    Coverage = coverage
                }
            };
        }
        return state with
        {
            SourceProgress = progress.ToImmutable(),
            SourceMetadataChanges = state.SourceMetadataChanges with
            {
                Progress = updates
            }
        };
    }

    private static bool RequiresActiveOwnerCheck(SemanticEntityChange change) =>
        (change.Kind is SemanticEntityChangeKind.ReplaceSourceState &&
            change.Fields.All(item => item.Kind is not SemanticFieldChangeKind.Set) &&
            change.Relationships.All(item => item.Kind is not SemanticRelationshipChangeKind.Add)) ||
        (change.Kind is SemanticEntityChangeKind.Upsert &&
            change.Fields.All(item => item.Kind is SemanticFieldChangeKind.Remove) &&
            change.Relationships.All(item => item.Kind is SemanticRelationshipChangeKind.Remove));

    private static SourceOwner Owner(SemanticEntityChange change) =>
        new(change.Subject, change.Provenance.SourceSystem, change.Provenance.SourceRecord);

    private static bool OwnedBy(SemanticFieldEvidence evidence, SourceOwner owner) =>
        evidence.Subject == owner.Subject && evidence.Provenance.SourceSystem == owner.SourceSystem &&
        evidence.Provenance.SourceRecord == owner.SourceRecord;

    private static bool OwnedBy(SemanticRelationshipEvidence evidence, SourceOwner owner) =>
        evidence.Subject == owner.Subject && evidence.Provenance.SourceSystem == owner.SourceSystem &&
        evidence.Provenance.SourceRecord == owner.SourceRecord;

    private static Guid ChangeId(Guid sessionId, SourceOwner owner)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', "source-snapshot-v1",
            sessionId.ToString("D"), owner.SourceSystem.AbsoluteUri, owner.SourceRecord,
            owner.Subject.Concept.Identifier.AbsoluteUri, owner.Subject.Scheme.Identifier.AbsoluteUri,
            owner.Subject.Value.ToString())));
        return new Guid(digest.AsSpan(0, GuidByteLength));
    }

    private static SourceOwner Owner(SemanticEntityIdentity subject, SemanticProvenance provenance) =>
        new(subject, provenance.SourceSystem, provenance.SourceRecord);
}
