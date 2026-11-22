using System.Text;
using System.Text.Json;
using EntityForge.Db.Semantic.Engine;
using EntityForge.Db.Semantic;
using EntityForge.Db.Semantic.Durability;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Distributed.Hosting;

// Policy publication fences every enrolled owner in the same durable transaction.
// Ordinary writes validate their local catalogue, including the absence of policy.
// The target retains the actual rule; other owners retain its durable directory entry.
internal static class PartitionSemanticGovernance
{
    private static ReadOnlySpan<byte> Magic => "EFSG0001"u8;

    private const long RequestCatalogVersion = 1;
    private const long CatalogVersionIncrement = 1;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly Uri Epoch = new("urn:entityforge:partition:policy-publication-epoch");

    private static bool Reserved(Uri identifier) => identifier == Epoch || identifier.AbsoluteUri.StartsWith(Epoch.AbsoluteUri + "/", StringComparison.Ordinal);

    internal static string[] Owners(IEnumerable<Uri> rules, DistributedRoutingBinding routing)
    {
        var identifiers = rules.ToArray();
        // An older epoch did not record the destination. Unknown coverage stays conservative.
        if (identifiers.Contains(Epoch))
        {
            return routing.Shards.Select(owner => owner.Value).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        }

        var owners = identifiers.Where(Reserved).Select(id => Uri.UnescapeDataString(id.AbsoluteUri[(Epoch.AbsoluteUri.Length + 1)..])).Distinct().OrderBy(value => value, StringComparer.Ordinal).ToArray();
        if (owners.Any(owner => !routing.Shards.Any(shard => shard.Value == owner)))
        {
            throw new InvalidDataException("Policy directory contains an unenrolled owner.");
        }

        return owners;
    }

    internal static bool HasRules(IEnumerable<Uri> rules) => rules.Any(id => !Reserved(id));

    internal static LogicalShardId Home(DistributedRoutingBinding routing) =>
        routing.Shards.OrderBy(s => s.Value, StringComparer.Ordinal).First();

    internal static bool Is(PartitionApplicationBinding binding) => binding.Requests.Count == 1 && binding.Requests[0].Span.StartsWith(Magic);

    internal static byte[] Encode(LogicalShardId target, SemanticGovernanceRuleDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (Reserved(definition.Identifier))
        {
            throw new ArgumentException("Reserved policy identity.", nameof(definition));
        }

        var change = new SemanticGovernanceRuleChange(definition.Identifier, definition.Kind, definition.DefinitionReference,
            definition.Revision, definition.Enabled, new(RequestCatalogVersion), definition.Scope);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Utf8);
        writer.Write(Magic);
        writer.Write(target.Value);
        writer.Write(new SemanticCommitCodec().Encode(DurableDatabaseCommit.ForGovernanceRule(change)));
        if (stream.Length > PartitionApplicationBinding.MaximumRequestBytes)
        {
            throw new InvalidDataException("Oversized policy request.");
        }

        return stream.ToArray();
    }

    internal static (LogicalShardId Target, SemanticGovernanceRuleChange Change) Decode(ReadOnlySpan<byte> bytes)
    {
        if (!bytes.StartsWith(Magic) || bytes.Length > PartitionApplicationBinding.MaximumRequestBytes)
        {
            throw new InvalidDataException("Invalid policy request.");
        }

        using var stream = new MemoryStream(bytes.ToArray(), false);
        using var reader = new BinaryReader(stream, Utf8);
        stream.Position = Magic.Length;
        var target = new LogicalShardId(reader.ReadString());
        var commit = ((ISemanticCommitDecoder)new SemanticCommitCodec()).Decode(default, reader.ReadBytes(checked((int)(stream.Length - stream.Position))));
        if (commit.Kind != DurableDatabaseCommitKind.GovernanceRule || Reserved(commit.GovernanceRule!.Identifier) || commit.GovernanceRule.CatalogVersion.Value != RequestCatalogVersion)
        {
            throw new InvalidDataException("Invalid policy publication.");
        }

        return (target, commit.GovernanceRule);
    }

    internal static byte[] Payload(ReadOnlySpan<byte> request, Guid operation, LogicalShardId owner,
        DistributedRoutingBinding routing, long catalogVersion)
    {
        var (target, change) = Decode(request);
        if (!routing.Shards.Contains(target) || !routing.Shards.Contains(owner))
        {
            throw new InvalidDataException("Wrong policy owner.");
        }

        if (owner != target)
        {
            change = new(new Uri(Epoch.AbsoluteUri + "/" + Uri.EscapeDataString(target.Value)), SemanticGovernanceRuleKind.Policy, Epoch, operation.ToString("N"), false, new(RequestCatalogVersion));
        }

        return new SemanticCommitCodec().Encode(DurableDatabaseCommit.ForGovernanceRule(change with
        {
            CatalogVersion = new(checked(catalogVersion + CatalogVersionIncrement))
        }));
    }

    internal static PartitionTransactionPlan Plan(PartitionApplicationBinding application, DistributedRoutingBinding routing)
    {
        if (!Is(application) || application.NativeDescriptor is not null)
        {
            throw new InvalidDataException("Not a policy publication.");
        }

        var plan = PartitionSelectionCodec.DecodePlan(application.PlanningEvidence.Span);
        application.RequireCoordinatedPlan(plan);
        var (target, _) = Decode(application.Requests[0].Span);
        if (plan.MapVersion != routing.MapVersion || !plan.Members.Select(m => m.Domain.Partition).ToHashSet().SetEquals(routing.Shards))
        {
            throw new InvalidDataException("Policy publication omitted an enrolled conflict owner.");
        }

        foreach (var member in plan.Members)
        {
            if (member.Domain.Profile != DatabaseProfileId.Semantic || member.Domain.RoutingFingerprint != routing.Fingerprint)
            {
                throw new InvalidDataException("Wrong policy domain.");
            }

            var commit = ((ISemanticCommitDecoder)new SemanticCommitCodec()).Decode(default, member.Payload.Span);
            if (commit.Kind != DurableDatabaseCommitKind.GovernanceRule || commit.GovernanceRule!.CatalogVersion.Value < CatalogVersionIncrement ||
                !Payload(application.Requests[0].Span, application.Operation, member.Domain.Partition, routing,
                    commit.GovernanceRule.CatalogVersion.Value - CatalogVersionIncrement).AsSpan().SequenceEqual(member.Payload.Span))
            {
                throw new InvalidDataException("Policy member differs from the complete request.");
            }
        }
        return plan;
    }
}

internal sealed partial class PartitionOwnedNode
{
    private async ValueTask<byte[]> ReadSemanticGovernanceAsync(DatabaseVersion version, ReadOnlyMemory<byte> request, Guid operation, CancellationToken token)
    {
        if (_native.Database is not SemanticDatabaseEngine semantic)
        {
            throw new InvalidDataException("Not a Semantic owner.");
        }

        using var retained = await semantic.AcquireDistributedStateAsync(version, token);
        return PartitionSemanticGovernance.Payload(request.Span, operation, Domain.Partition, Routing, retained.State.Governance.Version.Value);
    }

    private PartitionTransactionMember BuildSemanticGovernanceMember(PartitionApplicationBinding binding) =>
        PartitionSemanticGovernance.Plan(binding, Routing).Members.Single(m => m.Domain == Domain);
}

internal sealed partial class PartitionApplicationClient
{
    internal async ValueTask<DatabaseDistributedCommitReceipt> PublishSemanticGovernanceAsync(Guid operation, LogicalShardId target,
        SemanticGovernanceRuleDefinition definition, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (Profile != DatabaseProfileId.Semantic || !_owners.ContainsKey(target))
        {
            throw new InvalidDataException("Wrong policy destination.");
        }

        if (_authority is PartitionNativeHomeAuthority)
        {
            throw new NotSupportedException("Semantic policy publication requires the Semantic decision authority.");
        }

        var request = PartitionSemanticGovernance.Encode(target, definition);
        var selected = await _authority.ReadApplicationAsync(operation, token);
        if (selected is null)
        {
            var participants = _routing.Shards.Select(id => _owners[id]).ToArray();
            var cut = await PartitionRemoteRead.ExecuteAsync(_owners.Values.Cast<IPartitionReadParticipant>().ToArray(), _routing,
                async (read, cancellation) =>
                {
                    var members = new List<PartitionTransactionMember>();
                    foreach (var owner in participants)
                    {
                        var bytes = await read.ReadAsync(owner.Domain.Partition.Value,
                            new("semantic-governance", Identity: operation, Subject: Convert.ToBase64String(request)), cancellation);
                        // The HTTP projection wraps an opaque binary payload in a JSON string.
                        if (bytes.Length > 0 && bytes[0] == (byte)'"')
                        {
                            bytes = JsonSerializer.Deserialize<byte[]>(bytes) ?? throw new InvalidDataException("Missing policy proposal.");
                        }

                        members.Add(new(owner.Domain, read.Position.Positions.Single(p => p.Identity == owner.Domain.Identity).Version, bytes));
                    }
                    return new PartitionTransactionPlan(operation, _routing.MapVersion, members);
                }, token, useSnapshots: true);
            var proposal = new PartitionApplicationBinding(operation, participants.Select(p => p.Domain.Identity).ToArray(),
                [request], planningEvidence: PartitionSelectionCodec.EncodePlan(cut.Value));
            _ = PartitionSemanticGovernance.Plan(proposal, _routing);
            await _authority.BindApplicationAsync(proposal, token);
            selected = await _authority.ReadApplicationAsync(operation, token) ?? throw new InvalidDataException("Policy selection disappeared.");
        }
        if (!PartitionSemanticGovernance.Is(selected) || !selected.Requests[0].Span.SequenceEqual(request))
        {
            throw new EntityForgeDbIdempotencyException(operation);
        }

        var plan = PartitionSemanticGovernance.Plan(selected, _routing);
        var result = await new PartitionTransactionCoordinator(_authority).ExecuteAsync(plan,
            plan.Members.Select(m => _owners[m.Domain.Partition]).ToArray(), token);
        if (!result.Decision.Commit)
        {
            throw new EntityForge.Db.Mapping.EntityOperationAbortedException(operation, result.PreparationFailure);
        }

        return result.Completion;
    }
}
