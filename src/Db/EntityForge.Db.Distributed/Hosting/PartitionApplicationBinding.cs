namespace EntityForge.Db.Distributed.Hosting;

// One immutable application selection precedes either execution protocol. Its
// identity is shared across owners; a local retry cannot race a coordinated plan.
internal sealed class PartitionApplicationBinding
{
    private const int StoredFormat = 4;
    internal const int MaximumRequestBytes = 512 * 1024;
    internal const int MaximumRequests = 256;

    internal Guid Operation
    {
        get;
    }

    internal IReadOnlyList<DatabasePartitionIdentity> Owners
    {
        get;
    }

    internal IReadOnlyList<ReadOnlyMemory<byte>> Requests
    {
        get;
    }

    internal string Descriptor
    {
        get;
    }

    internal string? NativeDescriptor
    {
        get;
    }

    internal ReadOnlyMemory<byte> PlanningEvidence
    {
        get;
    }

    // Format 4 makes the sole selected owner the decision authority, including
    // evidence-backed plans. The external authority still owns immutable selection.
    internal bool Local => Owners.Count == 1 && (PlanningEvidence.IsEmpty || NativeLocal);

    internal bool NativeLocal
    {
        get;
    }

    internal PartitionApplicationBinding(Guid operation, IReadOnlyList<DatabasePartitionIdentity> owners,
        IReadOnlyList<ReadOnlyMemory<byte>> requests, string? nativeDescriptor = null, ReadOnlyMemory<byte> planningEvidence = default,
        bool nativeLocal = false)
    {
        PartitionOwnerOperation.RequireCoordinatedNamespace(operation);
        if (operation == Guid.Empty || owners is null || owners.Count is < 1 or > DatabasePositionVector.MaximumPartitions ||
            owners.Any(owner => owner is null) || requests is null || requests.Count is < 1 or > MaximumRequests ||
            requests.Any(request => request.IsEmpty) || requests.Sum(request => (long)request.Length) > MaximumRequestBytes)
        {
            throw new InvalidDataException("Invalid bounded application selection.");
        }

        _ = new DatabasePositionVector(owners.Select(owner => new DatabasePartitionPosition(owner, new(0))).ToArray());
        Operation = operation;
        if (nativeDescriptor is not null && (nativeDescriptor.Length != 64 ||
            !nativeDescriptor.All(Uri.IsHexDigit) || owners.Count < 2))
        {
            throw new InvalidDataException("Invalid native application descriptor.");
        }

        NativeDescriptor = nativeDescriptor;
        if (planningEvidence.Length > MaximumRequestBytes || nativeDescriptor is not null && !planningEvidence.IsEmpty)
        {
            throw new InvalidDataException("Invalid bounded planning evidence.");
        }

        PlanningEvidence = planningEvidence.ToArray();
        if (nativeLocal && (owners.Count != 1 || planningEvidence.IsEmpty || nativeDescriptor is not null))
        {
            throw new InvalidDataException("Native local execution requires one evidence-backed owner.");
        }

        NativeLocal = nativeLocal;
        Owners = Array.AsReadOnly(owners.OrderBy(owner => owner.Partition, StringComparer.Ordinal).ToArray());
        Requests = Array.AsReadOnly(requests.Select(request => (ReadOnlyMemory<byte>)request.ToArray()).ToArray());
        Descriptor = PartitionSelectionCodec.ApplicationDescriptor(this);
    }

    internal Stored Export() => new(StoredFormat, Operation, Owners.ToArray(), Requests.Select(request => request.ToArray()).ToArray(), NativeDescriptor, PlanningEvidence.ToArray(), NativeLocal);

    internal PartitionApplicationBinding SelectNativeLocal() => Owners.Count == 1 && !PlanningEvidence.IsEmpty
        ? new(Operation, Owners, Requests, NativeDescriptor, PlanningEvidence, nativeLocal: true) : this;

    internal static PartitionApplicationBinding Native(PartitionTransactionPlan plan) => new(plan.Operation,
        plan.Members.Select(member => member.Domain.Identity).ToArray(),
        [(ReadOnlyMemory<byte>)Convert.FromHexString(plan.Descriptor)], plan.Descriptor);

    internal void RequireCoordinatedPlan(PartitionTransactionPlan plan)
    {
        if (Local || plan.Operation != Operation || plan.Members.Count != Owners.Count ||
            plan.Members.Any(member => !Owners.Contains(member.Domain.Identity)) ||
            NativeDescriptor is not null && NativeDescriptor != plan.Descriptor)
        {
            throw new EntityForgeDbIdempotencyException(plan.Operation);
        }
    }

    internal sealed record Stored(int Version, Guid Operation, DatabasePartitionIdentity[] Owners, byte[][] Requests, string? NativeDescriptor = null, byte[]? PlanningEvidence = null, bool NativeLocal = false)
    {
        internal PartitionApplicationBinding Restore()
        {
            if (Version != StoredFormat || PlanningEvidence is null || Requests is null || Requests.Length is < 1 or > MaximumRequests ||
                Requests.Any(request => request is null || request.Length == 0) ||
                Requests.Sum(request => (long)request.Length) > MaximumRequestBytes)
            {
                throw new InvalidDataException("Invalid retained application request framing.");
            }

            return new(Operation, Owners, Requests.Select(request => (ReadOnlyMemory<byte>)request).ToArray(), NativeDescriptor, PlanningEvidence, NativeLocal);
        }
    }
}
