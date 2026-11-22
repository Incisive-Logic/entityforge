using EntityForge.Db.Mapping;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Rdf;

/// <summary>Owns a frozen native RDF transaction and its required mapping contract.</summary>
public sealed class RdfEntityWrite : IAsyncDisposable
{
    /// <summary>Maximum native mutations in one record operation.</summary>
    public const int MaximumMutations = 1_000;

    /// <summary>Maximum encoded transaction bytes.</summary>
    public const int MaximumBytes = 64 * 1024 * 1024;
    private readonly byte[]? _payload;
    private readonly Guid _operationId;
    private readonly EntityRecordDeliveryCodec.Metadata? _delivery;
    private readonly EntityPreparedLifetime<DatabaseCommitReceipt>? _lifetime;

    internal RdfEntityWrite(EntityMappingManifest manifest, Guid operationId, object owner,
        Func<CancellationToken, ValueTask<DatabaseCommitReceipt>> deliver, Func<ValueTask> release)
    {
        Manifest = manifest;
        _operationId = operationId;
        _lifetime = new(owner, deliver, release);
    }

    /// <summary>Whether this write owns an exportable native payload rather than retained resources.</summary>
    public bool IsPortable => _lifetime is null;

    internal ValueTask<DatabaseCommitReceipt> DeliverRetainedAsync(object owner, CancellationToken token) =>
        (_lifetime ?? throw new InvalidOperationException("This is a portable native write.")).DeliverAsync(owner, token);

    /// <summary>Releases retained resources without aborting the operation.</summary>
    public ValueTask DisposeAsync() => _lifetime?.DisposeAsync() ?? ValueTask.CompletedTask;

    internal RdfEntityWrite(EntityMappingManifest binding, RdfTransaction transaction,
        Guid databaseIncarnation, string? producerFingerprint, bool delete, int batchOperations = 0)
    {
        Manifest = binding;
        _operationId = transaction.TransactionId;
        _delivery = EntityRecordDeliveryCodec.Create(RdfTransactionCodec.Version, databaseIncarnation,
            _operationId, binding, producerFingerprint, delete) with
        {
            BatchOperations = batchOperations
        };
        if (batchOperations is < 0 or > EntityRecordDeliveryCodec.MaximumBatchOperations)
        {
            throw new InvalidOperationException("Invalid mapped batch operation count.");
        }

        if (transaction.Mutations.Count > MaximumMutations * Math.Max(1, batchOperations))
        {
            throw new InvalidOperationException("The native record exceeds its mutation limit.");
        }

        _payload = RdfTransactionCodec.Encode(transaction);
        if (_payload.Length > MaximumBytes)
        {
            throw new InvalidOperationException("The native record exceeds its byte limit.");
        }
    }

    /// <summary>Gets the exact mapping contract required by this write.</summary>
    public EntityMappingManifest Manifest
    {
        get;
    }

    /// <summary>Exports a bounded, checksummed command for durable retention before acknowledging input.</summary>
    public byte[] Export() => _payload is not null ? EntityRecordDeliveryCodec.Encode(_delivery!, _payload)
        : throw new InvalidOperationException("Retain the captured request for portable delivery of a retained preparation.");

    /// <summary>Restores a frozen command without CLR mappings or source evaluation. Delivery requires the original database incarnation.</summary>
    public static RdfEntityWrite Restore(ReadOnlySpan<byte> envelope)
    {
        var (metadata, payload) = EntityRecordDeliveryCodec.Decode(envelope,
            DatabaseProfileId.Rdf, RdfTransactionCodec.Version);
        _ = RestoreTransaction(metadata, payload);
        return new(metadata, payload);
    }

    private RdfEntityWrite(EntityRecordDeliveryCodec.Metadata metadata, byte[] ownedPayload)
    {
        Manifest = metadata.Binding;
        _operationId = metadata.OperationId;
        _delivery = metadata;
        _payload = ownedPayload;
    }

    internal static RdfTransaction RestoreTransaction(EntityRecordDeliveryCodec.Metadata metadata,
        ReadOnlySpan<byte> payload)
    {
        EntityRecordDeliveryCodec.Validate(metadata, DatabaseProfileId.Rdf, RdfTransactionCodec.Version);
        if (metadata.NativeProfile is not null)
        {
            throw new InvalidDataException("A native change operation is not a mapped entity write.");
        }

        if (payload.Length > MaximumBytes)
        {
            throw new InvalidOperationException("The native record exceeds its byte limit.");
        }

        RdfTransaction transaction = RdfTransactionCodec.DecodeTransaction(metadata.OperationId, payload);
        if (transaction.Mutations.Count > MaximumMutations * Math.Max(1, metadata.BatchOperations))
        {
            throw new InvalidOperationException("The native record exceeds its mutation limit.");
        }

        if (transaction.InterpretationFingerprint != EntityRecordInterpretation.Fingerprint(
                metadata.Binding, metadata.ProducerFingerprint, metadata.Delete, metadata.BatchOperations))
        {
            throw new InvalidDataException("The native command differs from its mapping interpretation.");
        }

        if (!RdfTransactionCodec.Encode(transaction).AsSpan().SequenceEqual(payload))
        {
            throw new InvalidDataException("The native command is not in canonical delivery format.");
        }

        return transaction;
    }

    /// <summary>Gets a detached native transaction for inspection or durable delivery.</summary>
    public RdfTransaction Transaction => _payload is not null ? RdfTransactionCodec.DecodeTransaction(_operationId, _payload)
        : throw new InvalidOperationException("A retained preparation does not expose one local native transaction.");
}
