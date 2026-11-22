using EntityForge.Db.Mapping;
using EntityForge.Db.Graph.Contracts;

namespace EntityForge.Db.Graph;

/// <summary>Owns a frozen native Graph transaction and its required mapping contract.</summary>
public sealed class GraphEntityWrite : IAsyncDisposable
{
    /// <summary>Maximum native mutations in one record operation.</summary>
    public const int MaximumMutations = 1_000;

    /// <summary>Maximum encoded native transaction size.</summary>
    public const int MaximumBytes = 64 * 1024 * 1024;
    private readonly byte[]? _payload;
    private readonly Guid _operationId;
    private readonly EntityRecordDeliveryCodec.Metadata? _delivery;
    private readonly EntityPreparedLifetime<DatabaseCommitReceipt>? _lifetime;

    internal GraphEntityWrite(EntityMappingManifest manifest, Guid operationId, object owner,
        Func<CancellationToken, ValueTask<DatabaseCommitReceipt>> deliver, Func<ValueTask> release)
    {
        Manifest = manifest;
        _operationId = operationId;
        _lifetime = new(owner, deliver, release);
    }

    /// <summary>Whether this write carries an exportable native payload rather than receiver-owned resources.</summary>
    public bool IsPortable => _lifetime is null;

    internal ValueTask<DatabaseCommitReceipt> DeliverRetainedAsync(object owner, CancellationToken token) =>
        (_lifetime ?? throw new InvalidOperationException("This is a portable native write.")).DeliverAsync(owner, token);

    /// <summary>Releases retained preparation resources. This does not abort the logical operation.</summary>
    public ValueTask DisposeAsync() => _lifetime?.DisposeAsync() ?? ValueTask.CompletedTask;

    internal GraphEntityWrite(EntityMappingManifest binding, GraphTransaction transaction,
        Guid databaseIncarnation, string? producerFingerprint, bool delete, int batchOperations = 0)
    {
        Manifest = binding;
        _operationId = transaction.TransactionId;
        _delivery = EntityRecordDeliveryCodec.Create(GraphTransactionCodec.Version, databaseIncarnation,
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

        _payload = GraphTransactionCodec.Encode(transaction);
        if (_payload.Length > MaximumBytes)
        {
            throw new InvalidOperationException("The native record exceeds its byte limit.");
        }

        _ = GraphTransactionCodec.DecodeRecord(_operationId, _payload, MaximumMutations * Math.Max(1, batchOperations));
    }

    /// <summary>Gets the immutable mapping contract admitted before the native transaction.</summary>
    public EntityMappingManifest Manifest
    {
        get;
    }

    /// <summary>Exports a bounded, checksummed command for durable retention before acknowledging input.</summary>
    public byte[] Export() => _payload is not null ? EntityRecordDeliveryCodec.Encode(_delivery!, _payload)
        : throw new InvalidOperationException("This preparation owns retained resources. Retain the captured request for portable delivery.");

    /// <summary>Restores a frozen command without CLR mappings or source evaluation. Delivery requires the original database incarnation.</summary>
    public static GraphEntityWrite Restore(ReadOnlySpan<byte> envelope)
    {
        var (metadata, payload) = EntityRecordDeliveryCodec.Decode(envelope,
            DatabaseProfileId.PropertyGraph, GraphTransactionCodec.Version);
        _ = RestoreTransaction(metadata, payload);
        return new(metadata, payload);
    }

    private GraphEntityWrite(EntityRecordDeliveryCodec.Metadata metadata, byte[] ownedPayload)
    {
        Manifest = metadata.Binding;
        _operationId = metadata.OperationId;
        _delivery = metadata;
        _payload = ownedPayload;
    }

    internal static GraphTransaction RestoreTransaction(EntityRecordDeliveryCodec.Metadata metadata, ReadOnlySpan<byte> payload)
    {
        EntityRecordDeliveryCodec.Validate(metadata, DatabaseProfileId.PropertyGraph, GraphTransactionCodec.Version);
        if (metadata.NativeProfile is not null)
        {
            throw new InvalidDataException("A native change operation is not a mapped entity write.");
        }

        GraphTransaction transaction = GraphTransactionCodec.DecodeRecord(metadata.OperationId, payload, MaximumMutations * Math.Max(1, metadata.BatchOperations));
        if (transaction.InterpretationFingerprint != EntityRecordInterpretation.Fingerprint(
                metadata.Binding, metadata.ProducerFingerprint, metadata.Delete, metadata.BatchOperations))
        {
            throw new InvalidDataException("The native command differs from its mapping interpretation.");
        }

        if (!GraphTransactionCodec.Encode(transaction).AsSpan().SequenceEqual(payload))
        {
            throw new InvalidDataException("The native command is not in canonical delivery format.");
        }

        return transaction;
    }

    /// <summary>Gets a detached native transaction for inspection or durable delivery.</summary>
    public GraphTransaction Transaction => _payload is not null ? GraphTransactionCodec.Decode(_operationId, _payload)
        : throw new InvalidOperationException("A distributed preparation does not expose one local native transaction.");
}
