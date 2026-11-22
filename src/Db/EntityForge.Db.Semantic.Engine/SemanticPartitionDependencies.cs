using System.Security.Cryptography;
using EntityForge.Semantics.Protobuf;
using EntityForge.Db.Semantic.Core;
using EntityForge.Db.Kernel;
using EntityForge.Db.Semantic.Durability;
using EntityForge.Semantics;
using EntityForge.Db.Mapping;

namespace EntityForge.Db.Semantic.Engine;

internal static class SemanticPartitionDependencies
{
    internal static SemanticEntityIdentity[] Entities(SemanticSessionEnvelope session,
        IEnumerable<SemanticRelationshipEvidence>? prior = null) =>
        session.Pages.SelectMany(p => p.Changes).SelectMany(change =>
            new[] { change.Subject }.Concat(change.Relationships.Select(r => r.Target)
                .OfType<SemanticEntityIdentity>())).Concat((prior ?? []).Select(r => r.Target))
                .Distinct().OrderBy(Key, StringComparer.Ordinal).ToArray();

    internal static string Key(SemanticEntityIdentity entity) => Convert.ToHexString(new SemanticChangeSessionTransportCodec().SerializeIdentity(entity));

    internal static SemanticPartitionReadEvidence Capture(SemanticDatabaseState state, SemanticSessionEnvelope session,
        IEnumerable<SemanticRelationshipEvidence> prior, EntityMappingManifest[] mappings,
        IEnumerable<SemanticEntityIdentity>? additionalEntities = null)
    {
        var entities = Entities(session, prior).Concat(additionalEntities ?? []).Distinct().OrderBy(Key, StringComparer.Ordinal).ToArray();
        return new(state.Version, entities, Sources(state, session, entities), Fingerprint(state, session, entities), mappings);
    }

    internal static void Validate(SemanticDatabaseState state, DurableDatabaseCommit commit)
    {
        // The hydrated slice is transaction-scoped. Index its visible relationships
        // once instead of rescanning the complete slice for each contribution.
        ILookup<SemanticEntityIdentity, SemanticRelationshipEvidence>? relationships = null;
        foreach (var contribution in commit.Contributions)
        {
            if (contribution.LocalOwnerCoverage is { } local)
            {
                var source = contribution.Session.Start.Source;
                var prior = state.SourceProgress.GetValueOrDefault(new(source.SourceSystem, source.SourcePartition));
                if (contribution.Read is not null || local.Owners.Count != 1 || state.Governance.Rules.Count != 0 ||
                    state.AssertionStatistics.RelationshipCount != 0 ||
                    contribution.Session.Pages.SelectMany(page => page.Changes).Any(change => change.Relationships.Count != 0) ||
                    prior is not null && !Equals(prior.Coverage, local))
                {
                    throw new DatabasePartitionConflictException();
                }
            }
            if (contribution.Read is null)
            {
                continue;
            }

            relationships ??= state.Relationships.Where(r => r.BeginVersion.Value <= state.Version.Value &&
                (r.EndVersion is null || r.EndVersion.Value.Value > state.Version.Value)).ToLookup(r => r.Subject);
            Validate(state, contribution.Session, contribution.Read, relationships);
        }
    }

    private static void Validate(SemanticDatabaseState state, SemanticSessionEnvelope session,
        SemanticPartitionReadEvidence? evidence,
        ILookup<SemanticEntityIdentity, SemanticRelationshipEvidence> relationships)
    {
        if (evidence is not { } read)
        {
            return;
        }

        if (read.OwnerCoverage is { } coverage && state.SourceProgress.GetValueOrDefault(
                new(session.Start.Source.SourceSystem, session.Start.Source.SourcePartition))?.Coverage is { } priorCoverage &&
            (coverage.Routing != priorCoverage.Routing || priorCoverage.Owners.Except(coverage.Owners).Any()))
        {
            throw new DatabasePartitionConflictException();
        }

        var subjects = session.Pages.SelectMany(p => p.Changes).Select(c => c.Subject).ToHashSet();
        var entities = Entities(session, subjects.SelectMany(subject => relationships[subject]));
        if (read.Entities is null || read.Sources is null || entities.Except(read.Entities).Any() ||
            !read.Entities.SequenceEqual(read.Entities.Distinct().OrderBy(Key, StringComparer.Ordinal)) ||
            !Sources(state, session, read.Entities).SequenceEqual(read.Sources) ||
            read.ReadVersion.Value > state.Version.Value ||
            read.Fingerprint != Fingerprint(state, session, read.Entities))
        {
            throw new DatabasePartitionConflictException();
        }
    }

    private static SemanticPartitionSource[] Sources(SemanticDatabaseState state, SemanticSessionEnvelope session,
        SemanticEntityIdentity[] entities)
    {
        var subjects = entities.ToHashSet();
        return state.SourceMemberships.GetSubjects(subjects)
            .Select(pair => new SemanticPartitionSource(pair.Key.SourceSystem, pair.Value.SourcePartition))
            .Append(new(session.Start.Source.SourceSystem, session.Start.Source.SourcePartition)).Distinct()
            .OrderBy(value => value.SourceSystem.AbsoluteUri, StringComparer.Ordinal).ThenBy(value => value.SourcePartition.AbsoluteUri, StringComparer.Ordinal).ToArray();
    }

    internal static string Fingerprint(SemanticDatabaseState state, SemanticSessionEnvelope session,
        SemanticEntityIdentity[] entities)
    {
        var source = session.Start.Source;
        var subjects = entities.ToHashSet();
        // Membership is maintained for every native entity change. Contributor progress
        // includes its native version, so even equal source-position text cannot conceal
        // changed evidence. Empty membership and new contributors are covered as well.
        var memberships = state.SourceMemberships.GetSubjects(subjects)
            .Concat(state.SourceMemberships.GetPartition(source.SourceSystem, source.SourcePartition).Keys
                .Select(owner =>
                {
                    state.SourceMemberships.TryGetMembership(owner, out var membership);
                    return new KeyValuePair<SourceOwner, SourceMembership>(owner, membership!);
                }))
            .DistinctBy(pair => pair.Key)
            .OrderBy(pair => Key(pair.Key.Subject), StringComparer.Ordinal)
            .ThenBy(pair => pair.Key.SourceSystem.AbsoluteUri, StringComparer.Ordinal)
            .ThenBy(pair => pair.Key.SourceRecord, StringComparer.Ordinal).ToArray();
        using var hash = new DatabaseBinaryFingerprint("semantic-partition-dependencies/v3");
        var codec = new SemanticChangeSessionTransportCodec();
        void Progress(SourcePartitionProgress? value)
        {
            hash.Long(value is null ? 0 : 1);
            if (value is null)
            {
                return;
            }

            hash.Guid(value.GenerationId);
            hash.Text(value.SourceVersion);
            hash.Text(value.SourcePosition);
            hash.Long(value.Version.Value);
            hash.Long(value.Coverage is null ? 0 : 1);
            if (value.Coverage is { } coverage)
            {
                hash.Text(coverage.Routing);
                hash.Long(coverage.Owners.Count);
                foreach (var owner in coverage.Owners)
                {
                    hash.Text(owner);
                }
            }
        }
        hash.Long(memberships.Length);
        foreach (var pair in memberships)
        {
            hash.BytesValue(codec.SerializeIdentity(pair.Key.Subject));
            hash.Text(pair.Key.SourceSystem.AbsoluteUri);
            hash.Text(pair.Key.SourceRecord);
            hash.Text(pair.Value.SourcePartition.AbsoluteUri);
            hash.BytesValue(codec.SerializeProvenance(pair.Value.Provenance));
            Progress(state.SourceProgress.GetValueOrDefault(new(pair.Key.SourceSystem, pair.Value.SourcePartition)));
        }
        Progress(state.SourceProgress.GetValueOrDefault(new(source.SourceSystem, source.SourcePartition)));
        hash.Long(state.PropertyStorage.Version.Value);
        hash.Long(state.Governance.Version.Value);
        hash.Long(state.Catalog.VocabularyVersion.Value);
        return hash.Finish();
    }
}
