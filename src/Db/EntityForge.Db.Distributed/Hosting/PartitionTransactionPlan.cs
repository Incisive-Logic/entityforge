using System.Security.Cryptography;
using EntityForge.Db.Durability;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Distributed.Hosting;

internal sealed class PartitionTransactionMember(PartitionExecutionDomain domain, DatabaseVersion readVersion,
    ReadOnlyMemory<byte> payload)
{
    internal PartitionExecutionDomain Domain { get; } = domain;

    internal DatabaseVersion ReadVersion { get; } = readVersion;

    internal ReadOnlyMemory<byte> Payload { get; } = payload.ToArray();
}

internal sealed class PartitionTransactionPlan
{
    internal const int MaximumMembers = 64;

    internal Guid Operation
    {
        get;
    }

    internal ShardMapVersion MapVersion
    {
        get;
    }

    internal IReadOnlyList<PartitionTransactionMember> Members
    {
        get;
    }

    internal string Descriptor
    {
        get;
    }

    internal PartitionTransactionPlan(Guid operation, ShardMapVersion mapVersion, IReadOnlyList<PartitionTransactionMember> members)
    {
        PartitionOwnerOperation.RequireCoordinatedNamespace(operation);
        if (operation == Guid.Empty || mapVersion.Value <= 0 || members.Count < 1 || members.Count > MaximumMembers ||
            members.Any(m => m is null || m.Domain is null || m.ReadVersion.Value < 0 || m.Payload.IsEmpty) ||
            members.Sum(m => (long)m.Payload.Length) > DurablePartitionTransaction.MaximumPayloadBytes)
        {
            throw new ArgumentException("Invalid bounded partition plan.");
        }

        var first = members[0].Domain;
        if (members.Select(m => m.Domain.Partition).Distinct().Count() != members.Count ||
            members.Any(m => m.Domain.Generation != first.Generation || m.Domain.Profile != first.Profile ||
                m.Domain.RoutingFingerprint != first.RoutingFingerprint))
        {
            throw new InvalidDataException("Partition plan domains differ.");
        }

        Operation = operation;
        MapVersion = mapVersion;
        Members = Array.AsReadOnly(members.OrderBy(m => m.Domain.Partition.Value, StringComparer.Ordinal)
            .Select(m => new PartitionTransactionMember(m.Domain, m.ReadVersion, m.Payload)).ToArray());
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer);
        writer.Write("entityforge.partition-transaction.v1");
        writer.Write(operation.ToByteArray());
        writer.Write(mapVersion.Value);
        writer.Write(Members.Count);
        foreach (var member in Members)
        {
            writer.Write(member.Domain.Generation.ToByteArray());
            writer.Write(member.Domain.Partition.Value);
            writer.Write(member.Domain.RoutingFingerprint);
            writer.Write((byte)member.Domain.Profile);
            writer.Write(member.Domain.Incarnation.ToByteArray());
            writer.Write(member.ReadVersion.Value);
            writer.Write(member.Payload.Length);
            writer.Write(SHA256.HashData(member.Payload.Span));
        }
        Descriptor = Convert.ToHexString(SHA256.HashData(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length))));
    }
}

internal sealed record PartitionTransactionDecision(Guid Operation, string Descriptor, bool Commit, bool ApplicationOnly = false,
    DurableTimestampState? Timestamp = null);

internal interface IPartitionTransactionAuthority
{
    ValueTask<PartitionTransactionDecision> DecideTimestampedAsync(Guid operation, string descriptor, DurableTimestampState timestamp,
        CancellationToken token = default) => throw new NotSupportedException("This authority cannot persist timestamp decisions.");

    string Identity
    {
        get;
    }

    ValueTask RequireUnenrolledAsync(PartitionAuthorityEnrollment partition, CancellationToken token = default);

    ValueTask EnrollAsync(PartitionExecutionDomain domain, bool create, CancellationToken token = default);

    ValueTask BindApplicationAsync(PartitionApplicationBinding binding, CancellationToken token = default);

    ValueTask<PartitionApplicationBinding?> ReadApplicationAsync(Guid operation, CancellationToken token = default);

    ValueTask<PartitionTransactionDecision> AbortApplicationAsync(Guid operation, string applicationDescriptor, CancellationToken token = default);

    ValueTask BindAsync(PartitionTransactionPlan plan, CancellationToken token = default);

    ValueTask<PartitionTransactionPlan?> ReadPlanAsync(Guid operation, CancellationToken token = default);

    ValueTask<PartitionTransactionDecision?> ReadDecisionAsync(Guid operation, CancellationToken token = default);

    async ValueTask<IReadOnlyList<PartitionTerminalObservation>> ReadTerminalAsync(IReadOnlyList<Guid> operations,
        CancellationToken token = default, IReadOnlyDictionary<Guid, string>? selected = null)
    {
        PartitionTerminalObservation.RequireBatch(operations);
        // Custom authorities retain their ordinary complete observations. The caller
        // still checks any compact decision against its exact native descriptor.
        _ = PartitionTerminalObservation.CopySelections(operations, selected);
        var results = new List<PartitionTerminalObservation>(operations.Count);
        foreach (var operation in operations)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                results.Add(new(operation, await ReadApplicationAsync(operation, token),
                    await ReadPlanAsync(operation, token), await ReadDecisionAsync(operation, token)));
            }
            catch (Exception failure)
            {
                results.Add(new(operation, null, null, null, failure));
            }
        }
        return results;
    }

    ValueTask<PartitionTransactionDecision> DecideAsync(Guid operation, string descriptor, bool commit, CancellationToken token = default);

    // Called only after every enrolled owner durably installs this cutoff while
    // its receiver is drained. Deletion never precedes the native acknowledgements.
    ValueTask RetainAsync(long cutoffMilliseconds, CancellationToken token = default);
}

internal sealed record PartitionAuthorityEnrollment(Guid Generation, string Partition, string Routing,
    DatabaseProfileId Profile, Guid Incarnation)
{
    internal static PartitionAuthorityEnrollment From(PartitionExecutionDomain domain) =>
        new(domain.Generation, domain.Partition.Value, domain.RoutingFingerprint, domain.Profile, domain.Incarnation);
}
