using EntityForge.Db.Semantic.Engine;
using EntityForge.Db.Graph;
using EntityForge.Db.Mapping;
using EntityForge.Db.Semantic;
using EntityForge.Db.Semantic.Durability;
using EntityForge.Db.Distributed.Sharding;
using EntityForge.Semantics.Protobuf;
using EntityForge.Semantics;

namespace EntityForge.Db.Distributed.Hosting;

internal sealed record PartitionSemanticSource(SemanticChangeSessionStart Start, SemanticSourcePosition? LocalPredecessor, bool HadMembership);

internal sealed record PartitionSemanticMutationEnvelope(PartitionSemanticSource[] Sources, byte[] Expanded,
    long LocalVersion, DistributedRoutingBinding Routing, LogicalShardId Shard, bool HasGovernance = false, string[]? PolicyOwners = null,
    int[]? ContributionIndices = null);

internal static class PartitionSemanticCodec
{
    internal sealed record ValidatedExpansion(
        IReadOnlyList<(SemanticEntityDeliveryCodec.Metadata Metadata, SemanticSessionEnvelope Session)> Originals,
        PartitionSemanticSource[] Sources, DurableDatabaseCommit Commit, bool HasGovernance, string[] PolicyOwners, int[] ContributionIndices)
    {
        internal byte[] Encode(long localVersion, DistributedRoutingBinding routing, LogicalShardId shard) =>
            PartitionSemanticEnvelopeCodec.Encode(new PartitionSemanticMutationEnvelope(Sources,
                new SemanticCommitCodec().Encode(Commit), localVersion, routing, shard, HasGovernance, PolicyOwners, ContributionIndices));
    }

    internal const string Codec = "entityforge.distributed.semantic.retained";
    internal const int Version = 10;
    internal const int MaximumChanges = 1_000;
    internal const int MaximumDependencyEntities = MaximumChanges * 2;

    internal static (SemanticEntityDeliveryCodec.Metadata Metadata, SemanticSessionEnvelope Session) Original(byte[] bytes) => OriginalCore(bytes);

    private static (SemanticEntityDeliveryCodec.Metadata Metadata, SemanticSessionEnvelope Session) OriginalCore(byte[] bytes)
    {
        var (metadata, payload) = EntityRecordDeliveryCodec.Decode<SemanticEntityDeliveryCodec.Metadata>(bytes);
        var session = metadata.Manifests is { Length: > 0 }
            ? SemanticEntityDeliveryCodec.RestoreValidated(metadata, payload).Session
            : SemanticEntityDeliveryCodec.Session(metadata, payload);
        if (metadata.DatabaseIncarnation == Guid.Empty || metadata.OperationId != session.Start.SessionId || metadata.Profile != DatabaseProfileId.Semantic || metadata.Manifests is null)
        {
            throw new InvalidDataException("The Semantic operation differs from its destination framing.");
        }

        var codec = new SemanticChangeSessionTransportCodec();
        bool validShape;
        if (metadata.Manifests.Length != 0)
        {
            validShape = session.Pages.SelectMany(p => p.Changes).All(change =>
                change.Kind is SemanticEntityChangeKind.ReplaceSourceState or SemanticEntityChangeKind.RetractSourceEntity);
        }
        else
        {
            validShape = new SemanticChangeSessionValidator().Validate(session.Start, session.Pages, session.Close).IsValid;
        }
        // Mapped restoration above already validates both hashes and canonical bytes.
        if (!validShape)
        {
            throw new InvalidDataException("The Semantic session has inconsistent hashes.");
        }

        if (metadata.Manifests.Length == 0)
        {
            var hashes = session.Close.OperationHash is null
                ? (ContentHash: codec.ComputeContentHash(session.Pages), OperationHash: (string?)null)
                : codec.ComputeHashes(session.Start, session.Pages);
            if (hashes.ContentHash != session.Close.ContentHash ||
                session.Close.OperationHash is not null && hashes.OperationHash != session.Close.OperationHash)
            {
                throw new InvalidDataException("The Semantic session has inconsistent hashes.");
            }
        }
        return (metadata, session);
    }

    internal static ValueTask<byte[]> ExpandAsync(byte[] original, SemanticDistributedState retained,
        DistributedRoutingBinding routing, LogicalShardId shard, CancellationToken token,
        SemanticEntityIdentity[]? dependencies = null) => ExpandAsync(new[] { original }, retained, routing, shard, token, dependencies);

    internal static async ValueTask<byte[]> ExpandAsync(IReadOnlyList<byte[]> originals, SemanticDistributedState retained,
        DistributedRoutingBinding routing, LogicalShardId shard, CancellationToken token,
        SemanticEntityIdentity[]? dependencies = null, IReadOnlyList<SemanticEntityIdentity[]>? contributionDependencies = null)
    {
        var expanded = await ExpandValidatedAsync(originals, retained, routing, shard, token,
            dependencies, contributionDependencies);
        return expanded.Encode(retained.State.Version.Value, routing, shard);
    }

    // Owned invocation-local state. Never reuse this across a retained cut or receiver admission.
    internal static async ValueTask<ValidatedExpansion> ExpandValidatedAsync(IReadOnlyList<byte[]> originals,
        SemanticDistributedState retained, DistributedRoutingBinding routing, LogicalShardId shard, CancellationToken token,
        SemanticEntityIdentity[]? dependencies = null, IReadOnlyList<SemanticEntityIdentity[]>? contributionDependencies = null)
    {
        token.ThrowIfCancellationRequested();
        if (originals.Count is < 1 or > DurableDatabaseCommit.MaximumTransactionContributions)
        {
            throw new InvalidDataException("Invalid Semantic transaction contribution count.");
        }

        var decoded = new (SemanticEntityDeliveryCodec.Metadata Metadata, SemanticSessionEnvelope Session)[originals.Count];
        for (int index = 0; index < originals.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            decoded[index] = Original(originals[index]);
        }
        return await ExpandTypedAsync(decoded, retained, routing, shard, token, dependencies, contributionDependencies);
    }

    internal static async ValueTask<ValidatedExpansion> ExpandTypedAsync(
        IReadOnlyList<(SemanticEntityDeliveryCodec.Metadata Metadata, SemanticSessionEnvelope Session)> originals,
        SemanticDistributedState retained, DistributedRoutingBinding routing, LogicalShardId shard, CancellationToken token,
        SemanticEntityIdentity[]? dependencies = null, IReadOnlyList<SemanticEntityIdentity[]>? contributionDependencies = null)
    {
        if (originals.Count is < 1 or > DurableDatabaseCommit.MaximumTransactionContributions)
        {
            throw new InvalidDataException("Invalid Semantic transaction contribution count.");
        }

        if (contributionDependencies is not null && contributionDependencies.Count != originals.Count)
        {
            throw new InvalidDataException("Semantic dependency contributions differ from the captured transaction.");
        }

        var validatedOriginals = new List<(SemanticEntityDeliveryCodec.Metadata, SemanticSessionEnvelope)>(originals.Count);
        var captures = new List<(SemanticSessionEnvelope Session, EntityMappingManifest[] Mappings,
            SemanticEntityIdentity[]? AdditionalEntities)>();
        var sources = new List<PartitionSemanticSource>();
        var indices = new List<int>();
        var coverages = new List<SemanticOwnerCoverage?>();
        long changes = 0, assertions = 0;
        foreach (var (metadata, session) in originals)
        {
            token.ThrowIfCancellationRequested();
            validatedOriginals.Add((metadata, session));
            var complete = retained.Expand(session).Where(change => routing.Route(DistributedDatabaseProfile.Semantic,
                ProfilePartitionRouting.SemanticOwner(change.Subject)) == shard).ToArray();
            changes += complete.Length;
            assertions += complete.Sum(c => (long)c.Fields.Count + c.Relationships.Count);
            if (changes > MaximumChanges || assertions > MaximumChanges)
            {
                throw new InvalidOperationException("The Semantic transaction exceeds its retained change bound.");
            }

            if (metadata.Manifests.Length != 0)
            {
                SemanticEntityDeliveryCodec.ValidateCompleteChanges(session, new(metadata.Entities, metadata.Depth, metadata.Bytes), complete);
            }

            int originalIndex = validatedOriginals.Count - 1;
            var predecessor = retained.Progress(session.Start.Source.SourceSystem, session.Start.Source.SourcePartition);
            bool hadMembership = retained.State.SourceMemberships.GetPartition(session.Start.Source.SourceSystem, session.Start.Source.SourcePartition).Count != 0;
            sources.Add(new(session.Start, predecessor, hadMembership));
            var coverage = Coverage(session);
            var additional = contributionDependencies is null ? (captures.Count == 0 ? dependencies : null) : contributionDependencies[originalIndex];
            // Keep meaningful source progress and all governance participation. A
            // single empty contribution remains a native fence on a dependency-
            // only/discovery owner; unrelated sources do not acquire local progress.
            bool required = complete.Length != 0 || coverage is not null || predecessor is not null || hadMembership ||
                retained.State.Governance.Rules.Count != 0 || contributionDependencies is not null && additional is { Length: > 0 };
            if (!required && (originalIndex != originals.Count - 1 || captures.Count != 0))
            {
                continue;
            }

            var start = new SemanticChangeSessionStart(session.Start.SessionId, SemanticChangeSessionKind.Incremental,
                session.Start.Source, session.Start.ProducerActivityId, session.Start.StartedAt, session.Start.CorrelationId);
            SemanticChangeSessionPage[] pages = complete.Length == 0 ? [] : [new(start.SessionId, 0, complete)];
            var codec = new SemanticChangeSessionTransportCodec();
            var hashes = codec.ComputeHashes(start, pages);
            var close = new SemanticChangeSessionClose(start.SessionId, pages.Length, complete.Length, hashes.ContentHash,
                session.Close.ClosedAt, hashes.OperationHash);
            var expanded = new SemanticSessionEnvelope(start, pages, close);
            captures.Add((expanded, metadata.Manifests, additional));
            indices.Add(originalIndex);
            coverages.Add(coverage);
        }
        var reads = await retained.CapturePartitionDependenciesAsync(captures, token);
        var commit = DurableDatabaseCommit.ForSemanticTransaction(captures.Select((capture, index) =>
            new SemanticTransactionContribution(capture.Session, reads[index] with
            {
                OwnerCoverage = coverages[index]
            })).ToArray());
        return new(validatedOriginals, sources.ToArray(), commit, retained.State.Governance.Rules.Count != 0,
            PartitionSemanticGovernance.Owners(retained.State.Governance.Rules.Keys, routing)
                .Concat(PartitionSemanticGovernance.HasRules(retained.State.Governance.Rules.Keys) ? [shard.Value] : Array.Empty<string>())
                .Distinct(StringComparer.Ordinal).OrderBy(owner => owner, StringComparer.Ordinal).ToArray(), indices.ToArray());

        SemanticOwnerCoverage? Coverage(SemanticSessionEnvelope original)
        {
            var source = original.Start.Source;
            if (ProfilePartitionRouting.SemanticHome(routing, source.SourceSystem, source.SourcePartition) != shard)
            {
                return null;
            }

            var prior = retained.State.SourceProgress.GetValueOrDefault(new(source.SourceSystem, source.SourcePartition));
            var previous = prior is null ? [] : prior.Coverage is { } known && known.Routing == routing.Fingerprint
                ? known.Owners : routing.Shards.Select(owner => owner.Value).ToArray();
            return new(routing.Fingerprint, previous.Concat(original.Pages.SelectMany(page => page.Changes)
                .Select(change => routing.Route(DistributedDatabaseProfile.Semantic, ProfilePartitionRouting.SemanticOwner(change.Subject)).Value))
                .Distinct(StringComparer.Ordinal).OrderBy(owner => owner, StringComparer.Ordinal));
        }
    }

    internal static IReadOnlyList<SemanticTransactionContribution> Expanded(PartitionSemanticMutationEnvelope value, DistributedTransactionId id)
    {
        var commit = ((ISemanticCommitDecoder)new SemanticCommitCodec()).Decode(DatabaseVersion.Initial, value.Expanded);
        if (commit.Kind != DurableDatabaseCommitKind.SemanticTransaction || commit.TransactionId != id.Value ||
            value.Sources is null || value.ContributionIndices is null || value.ContributionIndices.Length != commit.Contributions.Count ||
            value.ContributionIndices.Any(index => index < 0 || index >= value.Sources.Length) ||
            !value.ContributionIndices.SequenceEqual(value.ContributionIndices.Distinct().Order()) ||
            commit.Contributions.Where((c, index) => c.Session.Start.Source != value.Sources[value.ContributionIndices[index]].Start.Source).Any())
        {
            throw new InvalidDataException("Expanded transaction changed its identity or sources.");
        }

        return commit.Contributions;
    }

    internal static void ValidateProgress(SemanticSessionEnvelope original, SemanticSourcePosition? predecessor)
        => ValidateProgress(original.Start, predecessor);

    internal static void ValidateProgress(SemanticChangeSessionStart original, SemanticSourcePosition? predecessor)
    {
        if (original.ProgressPrecondition is { } supplied &&
            (supplied.Expectation == SemanticSourceProgressExpectation.NoPriorProgress ? predecessor is not null : supplied.Predecessor != predecessor))
        {
            throw new InvalidOperationException("The retained Semantic contribution guard differs from the requested predecessor.");
        }
    }
}
