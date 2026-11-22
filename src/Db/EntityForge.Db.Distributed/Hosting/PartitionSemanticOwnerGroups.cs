using EntityForge.Db.Distributed.Sharding;
using EntityForge.Db.Semantic.Engine;
using EntityForge.Db.Semantic;
using EntityForge.Semantics;

namespace EntityForge.Db.Distributed.Hosting;

internal static partial class PartitionSemanticPlanning
{
    private sealed record OwnerGroupProposal(Expansion Expansion, DecodedExpansion Decoded);

    // Independent source groups still form ONE selected plan and ONE atomic decision.
    // No group is selected or prepared until every local closure proof has succeeded.
    private static async ValueTask<PartitionApplicationBinding?> TrySelectOwnerGroupsAsync(Guid operation,
        DistributedRoutingBinding routing, IReadOnlyList<IPartitionTransactionParticipant> owners,
        IReadOnlyList<ReadOnlyMemory<byte>> requests, PlanningCapture capture, CancellationToken token,
        PartitionSemanticMappingAdmission? mappings)
    {
        if (capture.LocalGroups is not { Count: > 1 } groups || groups.Count != owners.Count ||
            owners.Any(owner => !groups.ContainsKey(owner.Domain.Partition.Value)))
        {
            return null;
        }

        using var measured = DistributedReadTelemetry.Measure("partition.semantic-planning.owner-groups");
        var parts = await PartitionParticipantWork.RunAsync(owners, async owner =>
        {
            foreach (var request in capture.Mappings)
            {
                if (mappings is null)
                {
                    await owner.InitializeApplicationMappingAsync(request, token);
                }
                else
                {
                    await mappings.InitializeAsync(owner, request, token);
                }
            }

            var indices = groups[owner.Domain.Partition.Value];
            var localRequests = indices.Select(index => requests[index]).ToArray();
            var query = Query(localRequests);
            if (owner.SupportsSemanticProposal)
            {
                var proposal = await owner.ProposeSemanticAsync(query.Captured!,
                    PartitionReadCuts.MaximumDuration, token);
                if (proposal.Position.Identity != owner.Domain.Identity ||
                    proposal.Envelope.Length is 0 or > PartitionApplicationBinding.MaximumRequestBytes)
                {
                    throw new InvalidDataException("Semantic owner proposal association differs.");
                }

                var expansion = new Expansion(owner.Domain.Identity, proposal.Envelope.ToArray(), indices.ToArray());
                var decoded = DecodeExpansions(new(new([proposal.Position]), [expansion]), operation).Single();
                if (decoded.Envelope.LocalVersion != proposal.Position.Version.Value)
                {
                    throw new InvalidDataException("Semantic owner proposal position differs.");
                }

                return IsIndependent(decoded, routing) ? new OwnerGroupProposal(expansion, decoded) : null;
            }
            var cut = await PartitionRemoteRead.ExecuteSemanticOwnerAsync<OwnerGroupProposal?>((IPartitionReadParticipant)owner,
                routing, async (read, cancellation) =>
                {
                    var expansion = new Expansion(owner.Domain.Identity,
                        (await read.ReadAsync(owner.Domain.Partition.Value, query, cancellation)).ToArray(), indices.ToArray());
                    if (expansion.Envelope.Length > PartitionApplicationBinding.MaximumRequestBytes)
                    {
                        throw new InvalidOperationException("Semantic expansion evidence exceeds its bound.");
                    }

                    var decoded = DecodeExpansions(new(read.Position, [expansion]), operation).Single();
                    if (decoded.Envelope.LocalVersion != read.Position.Positions.Single().Version.Value)
                    {
                        throw new InvalidDataException("Owner group native position differs from its cut.");
                    }

                    return IsIndependent(decoded, routing) ? new OwnerGroupProposal(expansion, decoded) : null;
                }, token, initial: query);
            return cut.Value;
        });
        if (parts.Any(part => part is null))
        {
            return null;
        }

        var expansions = parts.Select(part => part!.Expansion).ToArray();
        var position = new DatabasePositionVector(parts.Select(part => new DatabasePartitionPosition(part!.Expansion.Owner,
            new(part.Decoded.Envelope.LocalVersion))).ToArray());
        var binding = new PartitionApplicationBinding(operation, expansions.Select(part => part.Owner).ToArray(), requests,
            planningEvidence: PartitionSelectionCodec.EncodeEvidence(new(position, expansions)));
        var evidence = new Evidence(position, expansions);
        RequireGroupDirectory(binding, routing, evidence);
        var validated = new ValidatedEvidence(evidence, parts.Select(part =>
            ValidateOwnerGroup(binding, routing, evidence, part!.Expansion, part.Decoded)).ToArray());
        Interpretation.Value?.Remember(binding, routing, validated);
        DistributedReadTelemetry.Work("partition.semantic-planning.owner-group-transactions", 1);
        return binding;
    }

    private static void RequireGroupDirectory(PartitionApplicationBinding binding, DistributedRoutingBinding routing, Evidence evidence)
    {
        if (evidence.Expansions.Length != binding.Owners.Count ||
            !evidence.Expansions.Select(part => part.Owner).ToHashSet().SetEquals(binding.Owners) ||
            evidence.Position.Positions.Count != evidence.Expansions.Length ||
            !evidence.Position.Positions.Select(position => position.Identity).ToHashSet().SetEquals(binding.Owners))
        {
            throw new InvalidDataException("Owner group directory differs from selected owners.");
        }

        var indices = new List<int>();
        foreach (var part in evidence.Expansions)
        {
            if (part.Owner.Profile != DatabaseProfileId.Semantic || part.Owner.Routing != routing.Fingerprint ||
                !routing.Shards.Any(shard => shard.Value == part.Owner.Partition) ||
                part.RequestIndices is not { Length: > 0 } local ||
                !local.SequenceEqual(local.Distinct().Order()) || local.Any(index => index < 0 || index >= binding.Requests.Count))
            {
                throw new InvalidDataException("Invalid owner group request directory.");
            }

            indices.AddRange(local);
        }
        if (!indices.Order().SequenceEqual(Enumerable.Range(0, binding.Requests.Count)))
        {
            throw new InvalidDataException("Owner groups must cover every request exactly once.");
        }
    }

    private static DecodedExpansion DecodeOwnerGroup(PartitionApplicationBinding binding, DistributedRoutingBinding routing,
        Evidence evidence, Expansion part)
    {
        var envelope = PartitionSemanticEnvelopeCodec.Decode(part.Envelope);
        var decoded = new DecodedExpansion(part.Owner, envelope, PartitionSemanticCodec.Expanded(envelope, new(binding.Operation)));
        return ValidateOwnerGroup(binding, routing, evidence, part, decoded);
    }

    private static DecodedExpansion ValidateOwnerGroup(PartitionApplicationBinding binding, DistributedRoutingBinding routing,
        Evidence evidence, Expansion part, DecodedExpansion decoded)
    {
        var envelope = decoded.Envelope;
        if (decoded.Owner != part.Owner || envelope.Routing.Fingerprint != routing.Fingerprint || envelope.Shard.Value != part.Owner.Partition ||
            envelope.LocalVersion != evidence.Position.Positions.Single(position => position.Identity == part.Owner).Version.Value ||
            envelope.Sources.Length != part.RequestIndices!.Length || !IsIndependent(decoded, routing))
        {
            throw new InvalidDataException("Owner group does not prove independent source closure.");
        }

        for (int index = 0; index < envelope.Sources.Length; index++)
        {
            PartitionSemanticCodec.ValidateProgress(envelope.Sources[index].Start, envelope.Sources[index].LocalPredecessor);
        }

        for (int index = 0; index < decoded.Contributions.Count; index++)
        {
            if (decoded.Contributions[index].Session.Start.Source != envelope.Sources[envelope.ContributionIndices![index]].Start.Source ||
                decoded.Contributions[index].Session.Pages.SelectMany(page => page.Changes).Any(change =>
                    routing.Route(DistributedDatabaseProfile.Semantic, ProfilePartitionRouting.SemanticOwner(change.Subject)).Value != part.Owner.Partition))
            {
                throw new InvalidDataException("Owner group contribution ownership differs.");
            }
        }

        DistributedReadTelemetry.Work("partition.semantic-owner-group.decoded-contributions", decoded.Contributions.Count);
        DistributedReadTelemetry.Work("partition.semantic-owner-group.decoded-sources", envelope.Sources.Length);
        return decoded;
    }

    private static bool IsIndependent(DecodedExpansion part, DistributedRoutingBinding routing) =>
        !part.Envelope.HasGovernance && part.Envelope.PolicyOwners is { Length: 0 } &&
        part.Envelope.Sources.All(source => ProfilePartitionRouting.SemanticHome(routing,
            source.Start.Source.SourceSystem, source.Start.Source.SourcePartition).Value == part.Owner.Partition) &&
        DirectoryOwners([part], routing).All(owner => owner == part.Owner.Partition) &&
        Closure([part]).All(entity => routing.Route(DistributedDatabaseProfile.Semantic,
            ProfilePartitionRouting.SemanticOwner(entity)).Value == part.Owner.Partition);

    private static ValidatedEvidence ReadOwnerGroups(PartitionApplicationBinding binding, DistributedRoutingBinding routing, Evidence evidence)
    {
        RequireGroupDirectory(binding, routing, evidence);
        return new(evidence, evidence.Expansions.Select(part => DecodeOwnerGroup(binding, routing, evidence, part)).ToArray());
    }

    internal static ValidatedEvidence ReadForOwner(PartitionApplicationBinding binding, DistributedRoutingBinding routing,
        DatabasePartitionIdentity owner)
    {
        var evidence = PartitionSelectionCodec.DecodeEvidence(binding.PlanningEvidence.Span);
        if (!evidence.Expansions.Any(part => part.RequestIndices is not null))
        {
            return ReadValidated(binding, routing);
        }

        RequireGroupDirectory(binding, routing, evidence);
        // Every selected owner validates its own request slice before the common
        // coordinator can decide commit. The directory proves total, disjoint coverage;
        // immutable application/plan descriptors bind all slices to that one decision.
        return new(evidence, [DecodeOwnerGroup(binding, routing, evidence, evidence.Expansions.Single(part => part.Owner == owner))]);
    }

    internal static IReadOnlyList<ReadOnlyMemory<byte>> OwnerRequests(PartitionApplicationBinding binding, Evidence evidence,
        DatabasePartitionIdentity owner) => evidence.Expansions.Single(part => part.Owner == owner).RequestIndices is { } indices
            ? indices.Select(index => binding.Requests[index]).ToArray() : binding.Requests;

    internal static void RequireFreshOwnerGroup(PartitionSemanticCodec.ValidatedExpansion actual,
        DistributedRoutingBinding routing, LogicalShardId shard)
    {
        if (actual.HasGovernance || actual.PolicyOwners.Length != 0 || actual.Originals.Any(original =>
            ProfilePartitionRouting.SemanticHome(routing, original.Session.Start.Source.SourceSystem,
                original.Session.Start.Source.SourcePartition) != shard ||
            original.Session.Pages.SelectMany(page => page.Changes).Any(change =>
                routing.Route(DistributedDatabaseProfile.Semantic, ProfilePartitionRouting.SemanticOwner(change.Subject)) != shard ||
                change.Relationships.Select(relationship => relationship.Target).OfType<SemanticEntityIdentity>().Any(target =>
                    routing.Route(DistributedDatabaseProfile.Semantic, ProfilePartitionRouting.SemanticOwner(target)) != shard))) ||
            actual.Commit.Contributions.Any(contribution => contribution.Read?.OwnerCoverage is not { } coverage ||
                coverage.Routing != routing.Fingerprint || coverage.Owners.Any(owner => owner != shard.Value) ||
                contribution.Read.Entities.Any(entity => routing.Route(DistributedDatabaseProfile.Semantic,
                    ProfilePartitionRouting.SemanticOwner(entity)) != shard)))
        {
            throw new EntityForge.Db.Kernel.DatabasePartitionConflictException();
        }
    }
}
