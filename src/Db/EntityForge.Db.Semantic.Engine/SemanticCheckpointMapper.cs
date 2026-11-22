using System.Collections.Immutable;
using EntityForge.Db.Semantic.Core;
using EntityForge.Db.Semantic;
using EntityForge.Db.Semantic.Durability;
using EntityForge.Semantics;

namespace EntityForge.Db.Semantic.Engine;

internal static class SemanticCheckpointMapper
{
    public static SemanticCheckpoint Capture(SemanticDatabaseState state, bool includeSourceMetadata = true) => new(
        state.Version, state.AssertionStatistics.FieldCount, state.AssertionStatistics.RelationshipCount,
        state.Catalog,
        new(state.PropertyStorage.Version, state.PropertyStorage.Storage.Values.ToArray()),
        new(state.Governance.Version, state.Governance.Rules.Values.ToArray()),
        state.SnapshotLeases.Active.Values.ToArray(),
        state.AppliedSessions.Select(item =>
            new CheckpointAppliedSession(item.Key, item.Value.ContentHash, item.Value.Version)).ToArray(),
        includeSourceMetadata ? state.SourceMemberships.Entries.Select(item => new CheckpointSourceMembership(item.Key.Subject,
            item.Key.SourceSystem, item.Key.SourceRecord, item.Value.SourcePartition, item.Value.Provenance)).ToArray() : [],
        includeSourceMetadata ? state.SourceProgress.Select(item => new CheckpointSourcePartitionProgress(
            item.Key.SourceSystem, item.Key.SourcePartition, item.Value.GenerationId,
            item.Value.SourceVersion, item.Value.SourcePosition, item.Value.Version, item.Value.Coverage)).ToArray() : [],
        new(state.QueryStatistics.Version, Cardinalities(state.QueryStatistics.ConceptCardinalities),
            Cardinalities(state.QueryStatistics.PropertyCardinalities),
            Cardinalities(state.QueryStatistics.RelationshipCardinalities),
            state.QueryStatistics.PropertyValueFrequencies.OrderBy(item =>
                    item.Key.Identifier.AbsoluteUri, StringComparer.Ordinal)
                .Select(item => new SemanticValueFrequencyCheckpoint(item.Key,
                    item.Value.TrackedValues.OrderBy(value => value.Key, StringComparer.Ordinal)
                        .Select(value => new SemanticValueCardinality(value.Key, value.Value)).ToArray(),
                    item.Value.OverflowOccurrences)).ToArray()))
    {
        SourceMetadataReference = includeSourceMetadata ? null : new(Guid.Empty, 0, state.Version,
        state.SourceMemberships.Count, state.SourceProgress.Count)
    };

    public static SemanticDatabaseState Restore(SemanticCheckpoint checkpoint)
    {
        SourceMembershipMutation[] membershipMutations = checkpoint.SourceMemberships.Select(item =>
            SourceMembershipMutation.Set(new(item.Subject, item.SourceSystem, item.SourceRecord),
                item.SourcePartition, item.Provenance)).ToArray();
        SourceMembershipIndex memberships = SourceMembershipIndex.Empty.Apply(membershipMutations);
        return new(checkpoint.Version, [], [],
            new(checkpoint.PropertyStorage.Version,
                checkpoint.PropertyStorage.Storage.ToDictionary(storage => storage.Property)),
            new(checkpoint.Governance.Version,
                checkpoint.Governance.Rules.ToDictionary(rule => rule.Identifier)),
            new(checkpoint.SnapshotLeases.ToDictionary(lease => lease.LeaseId)),
            ImmutableDictionary<Guid, ResumableWriteTransaction>.Empty,
            checkpoint.AppliedSessions.ToImmutableDictionary(item => item.SessionId,
                item => new AppliedSession(item.ContentHash, item.Version)), memberships,
            checkpoint.SourceProgress.ToImmutableDictionary(item =>
                new SourcePartitionKey(item.SourceSystem, item.SourcePartition),
                item => new SourcePartitionProgress(item.GenerationId, item.SourceVersion,
                    item.SourcePosition, item.Version, item.Coverage)))
        {
            AssertionStatistics = new(checkpoint.FieldCount, checkpoint.RelationshipCount),
            Catalog = checkpoint.Catalog,
            QueryStatistics = RestoreStatistics(checkpoint)
        };
    }

    private static IReadOnlyList<SemanticTermCardinality> Cardinalities(
        IReadOnlyDictionary<SemanticTerm, long> values) => values
        .OrderBy(item => item.Key.Identifier.AbsoluteUri, StringComparer.Ordinal)
        .Select(item => new SemanticTermCardinality(item.Key, item.Value)).ToArray();

    private static SemanticQueryStatistics RestoreStatistics(SemanticCheckpoint checkpoint)
    {
        SemanticQueryStatisticsCheckpoint? statistics = checkpoint.QueryStatistics;
        return statistics is null ? SemanticQueryStatistics.Empty : new(statistics.Version,
            statistics.Concepts.ToDictionary(item => item.Term, item => item.Cardinality),
            statistics.Properties.ToDictionary(item => item.Term, item => item.Cardinality),
            statistics.Relationships.ToDictionary(item => item.Term, item => item.Cardinality),
            statistics.PropertyValues.ToDictionary(item => item.Property,
                item => new SemanticValueFrequency(
                    item.Values.ToDictionary(value => value.Fingerprint, value => value.Cardinality,
                        StringComparer.Ordinal), item.OverflowOccurrences)));
    }
}
