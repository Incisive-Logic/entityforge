using System.Security.Cryptography;

namespace EntityForge.Db.Mapping;

// Implemented by the kernel without granting profiles access to kernel internals.
internal interface IEntityOperationCompactionAuthority
{
    ValueTask<EntityOperationBinding?> ReadOperationAsync(Guid operationId, CancellationToken cancellationToken);

    ValueTask CompactCommittedAsync(Guid operationId, CancellationToken cancellationToken);

    ValueTask<EntityOperationBinding?> PrepareCommittedCompactionAsync(Guid operationId, CancellationToken cancellationToken);

    // This projection is recoverable from the durable binding and native receipt; it needs no new logical commit.
    ValueTask CompleteCommittedCompactionAsync(EntityOperationBinding compact, CancellationToken cancellationToken);

    ValueTask VerifyReadableAsync(EntityOperationCommitted committed, CancellationToken cancellationToken);
}

// A local receiver may atomically persist an initial binding and its admitted native commit.
// Native rejection still persists the binding alone; execution is never published before durability.
internal sealed class EntityOperationBoundCommit
{
    internal EntityOperationBoundCommit(EntityOperationBinding binding)
    {
        if (binding.Aborted || binding.Committed is not null || binding.AbortedPlanDigest is not null)
        {
            throw new ArgumentException("Only an initial binding can accompany a native commit.", nameof(binding));
        }
        Binding = binding with
        {
            Plan = binding.Plan.ToArray()
        };
        var decoded = EntityRecordDeliveryCodec.Decode<EntityRecordDeliveryCodec.Metadata>(Binding.Plan);
        Metadata = decoded.Metadata;
        Payload = decoded.Payload;
        if (Metadata.OperationId != Binding.OperationId)
        {
            throw new EntityForgeDbIdempotencyException(Binding.OperationId);
        }
        BindingRecord = EntityOperationCodec.Encode(Binding);
        BindingTransactionId = EntityOperationCodec.TransactionId(BindingRecord.Span);
        ReservedBytes = BindingRecord.Length + EntityOperationCatalog.TerminalReservationBytes;
        EnvelopeDigest = EntityOperationCommitted.Digest(Binding.Plan);
        PreparedFingerprint = EntityOperationCommitted.Digest(EntityRecordDeliveryCodec.PreparedIdentity(Metadata, decoded.Payload));
        NativeHash = EntityOperationCommitted.Digest(decoded.Payload);
    }

    internal EntityOperationBoundCommit(EntityOperationBinding binding, ReadOnlyMemory<byte> payload) : this(binding)
    {
        if (!Payload.Span.SequenceEqual(payload.Span))
        {
            throw new EntityForgeDbIdempotencyException(binding.OperationId);
        }
    }

    internal EntityOperationBinding Binding
    {
        get;
    }

    internal EntityRecordDeliveryCodec.Metadata Metadata
    {
        get;
    }

    internal ReadOnlyMemory<byte> Payload
    {
        get;
    }

    internal ReadOnlyMemory<byte> BindingRecord
    {
        get;
    }

    internal Guid BindingTransactionId
    {
        get;
    }

    internal long ReservedBytes
    {
        get;
    }

    private string EnvelopeDigest
    {
        get;
    }

    private string PreparedFingerprint
    {
        get;
    }

    private string NativeHash
    {
        get;
    }

    internal EntityOperationCommitted Describe(string codec, int version, DatabaseCommitReceipt receipt) => new(EntityOperationCommitted.CurrentFormat, EnvelopeDigest, PreparedFingerprint, codec, version, NativeHash, receipt, []);
}

internal interface IEntityOperationAtomicCommitAuthority
{
    ValueTask<IReadOnlyList<DatabaseCommitReceipt>> CommitBoundGroupAsync(IReadOnlyList<EntityOperationBoundCommit> requests, CancellationToken cancellationToken);

    ValueTask<DatabaseCommitReceipt> CommitBoundAsync(EntityOperationBoundCommit request, CancellationToken cancellationToken);
}

internal interface IEntityOperationAtomicCommitProfile : IEntityOperationCompactionProfile
{
    // The kernel has verified the envelope, destination, operation and exact native payload.
    void ValidateBoundPayload(EntityRecordDeliveryCodec.Metadata metadata, ReadOnlyMemory<byte> nativePayload);

    EntityOperationCommitted DescribeBoundCommitted(EntityOperationBoundCommit request, DatabaseCommitReceipt receipt) => DescribeCommitted(request.Binding, receipt);
}

// Profile-owned interpretation, invoked under kernel serialization. Receipt input is kernel-owned.
internal interface IEntityOperationCompactionProfile
{
    EntityOperationCatalog OperationCatalog
    {
        get;
    }

    EntityOperationBinding? ReadOperationTransition(ReadOnlyMemory<byte> payload);

    EntityOperationCommitted DescribeCommitted(EntityOperationBinding binding, DatabaseCommitReceipt receipt);
}
