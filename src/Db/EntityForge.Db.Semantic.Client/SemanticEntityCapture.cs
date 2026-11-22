using EntityForge.Db.Mapping;
using EntityForge.Semantics;

namespace EntityForge.Db.Semantic;

public sealed partial class SemanticEntityStore<T> where T : class
{
    /// <summary>Looks up an application operation in this database without writing, planning or admitting mappings.
    /// NotFound and PlanBound do not establish abort. Prepared writes are included.</summary>
    public ValueTask<EntityOperationResolution<SessionCommitResult>> ResolveAsync(Guid operationId,
        CancellationToken cancellationToken = default) => _client.ResolveAsync(operationId, cancellationToken);

    /// <summary>Requests a durable abort; the existing committed outcome takes precedence.</summary>
    public ValueTask<EntityOperationResolution<SessionCommitResult>> AbortAsync(Guid operationId,
        CancellationToken cancellationToken = default) => _client.AbortAsync(operationId, cancellationToken);

    /// <summary>Freezes evaluated values and target policy without planning or writing database state.
    /// Omitted guard is unguarded, as with SaveAsync and PrepareSave.</summary>
    public SemanticEntityRequest CaptureSave(T entity, SemanticEntityWriteOperation operation,
        SemanticSourceProgressPrecondition? guard = null) => _plan.CaptureSaveCore(_client.DatabaseIncarnation, entity, operation, guard?.Predecessor, guard is not null);

    /// <summary>Freezes a root contribution deletion without planning or writing state.</summary>
    public SemanticEntityRequest CaptureDelete(object identity, SemanticEntityWriteOperation operation,
        SemanticSourceProgressPrecondition? guard = null) => _plan.CaptureDeleteCore(_client.DatabaseIncarnation, identity, operation, guard?.Predecessor, guard is not null);


}

/// <summary>Immutable captured Semantic values and interpretation, before native planning.
/// This is distinct from a prepared native write and does not reserve a transaction.</summary>
public sealed class SemanticEntityRequest
{
    private const int FormatVersion = 3;
    internal readonly Metadata _metadata;
    internal readonly byte[] _payload;
    private readonly Lazy<byte[]> _compilationIdentity;

    internal SemanticEntityRequest(Guid database, SemanticEntityDefinition definition, SemanticApplicationIdentity application,
        SemanticEntityStoreLimits limits, SemanticTargetBinding? rootBinding, object value,
        SemanticEntityWriteOperation operation, SemanticSourcePosition? predecessor, bool guarded, bool delete)
        : this(new(FormatVersion, database, definition, application, limits, rootBinding is null ? null : BindingMetadata.From(rootBinding), operation, predecessor, guarded, delete),
            EntityValueCapture.Encode(value))
    {
    }

    private SemanticEntityRequest(Metadata metadata, byte[] payload, Lazy<byte[]>? compilationIdentity = null)
    {
        if (metadata.Version != FormatVersion || metadata.DatabaseIncarnation == Guid.Empty || metadata.Definition is null ||
            metadata.Application is null || metadata.Limits is null || metadata.Operation is null)
        {
            throw new InvalidDataException("Invalid captured Semantic operation metadata.");
        }

        var value = EntityValueCapture.Decode(payload);
        if (value is null || !metadata.Delete && value is not Entity)
        {
            throw new InvalidDataException("A captured operation requires an identity or Entity values.");
        }

        if (!metadata.Guarded && metadata.Predecessor is not null)
        {
            throw new InvalidDataException("An unguarded request cannot specify a predecessor.");
        }

        _metadata = metadata;
        _payload = payload.ToArray();
        _compilationIdentity = compilationIdentity ?? new(() => EntityRecordDeliveryCodec.Encode(
            _metadata with
            {
                DatabaseIncarnation = Guid.Empty
            }, _payload));
    }

    /// <summary>Gets the stable identity of this captured operation.</summary>
    public Guid OperationId => _metadata.Operation.OperationId;

    internal SemanticEntityRequest ForDatabase(Guid database) => new(_metadata with { DatabaseIncarnation = database }, _payload, _compilationIdentity);

    internal SemanticEntityRequest ForOperation(Guid operation) => new(_metadata with { Operation = new(operation, _metadata.Operation.ObservedAt) }, _payload);

    /// <summary>Exports the immutable values and complete target policy for an existing client journal.</summary>
    public byte[] Export() => EntityRecordDeliveryCodec.Encode(_metadata, _payload);

    internal ReadOnlyMemory<byte> CompilationIdentity() => _compilationIdentity.Value;

    internal byte[] CanonicalIdentity() => EntityRecordDeliveryCodec.EncodeIdentity("semantic-capture/v3", w =>
    {
        w.Write(_metadata.Version);
        DeliveryBinary.Guid(w, _metadata.DatabaseIncarnation);
        w.Write(_metadata.Definition.Fingerprint);
        SemanticDeliveryMetadata.WriteApplication(w, _metadata.Application);
        SemanticDeliveryMetadata.WriteLimits(w, _metadata.Limits);
        DeliveryBinary.Optional(w, _metadata.RootBinding?.ToBinding().Fingerprint);
        SemanticDeliveryMetadata.WriteOperation(w, _metadata.Operation);
        SemanticDeliveryMetadata.WritePosition(w, _metadata.Predecessor);
        w.Write(_metadata.Guarded);
        w.Write(_metadata.Delete);
    }, _payload);

    /// <summary>Restores captured values without producer code or expression evaluation.</summary>
    public static SemanticEntityRequest Restore(ReadOnlySpan<byte> envelope)
    {
        var (metadata, payload) = EntityRecordDeliveryCodec.Decode<Metadata>(envelope, out var description);
        // This profile's authenticated description begins with type, version and
        // destination. Preserve every other byte, including diagnostic metadata.
        const int DatabaseOffset = 2 * sizeof(int);
        const int GuidBytes = 16;
        description.AsSpan(DatabaseOffset, GuidBytes).Clear();
        return new(metadata, payload, new(() => EntityRecordDeliveryCodec.EncodeDescription(description, payload)));
    }

    internal ValueTask<SessionCommitResult> DeliverAsync(ISemanticEntityClient client, CancellationToken cancellationToken) =>
        client.DeliverAsync(this, cancellationToken);

    internal sealed record BindingMetadata(Uri Identifier, string Version, SemanticModelReference TargetVocabulary,
        SemanticTerm Concept, SemanticIdentityScheme IdentityScheme, IReadOnlyList<SemanticTargetMemberBinding> Members)
    {
        internal SemanticTargetBinding ToBinding() => new(Identifier, Version, TargetVocabulary, Concept, IdentityScheme, Members);

        internal static BindingMetadata From(SemanticTargetBinding value) => new(value.Identifier, value.Version,
            value.TargetVocabulary, value.Concept, value.IdentityScheme, value.Members);
    }

    internal sealed record Metadata(int Version, Guid DatabaseIncarnation, SemanticEntityDefinition Definition,
        SemanticApplicationIdentity Application, SemanticEntityStoreLimits Limits, BindingMetadata? RootBinding,
        SemanticEntityWriteOperation Operation, SemanticSourcePosition? Predecessor, bool Guarded, bool Delete) : IDeliveryMetadata<Metadata>
    {
        public static int TypeTag => 5;

        public void Write(BinaryWriter w)
        {
            w.Write(Version);
            DeliveryBinary.Guid(w, DatabaseIncarnation);
            SemanticDeliveryMetadata.WritePlan(w, Definition, Application, Limits, RootBinding);
            SemanticDeliveryMetadata.WriteOperation(w, Operation);
            SemanticDeliveryMetadata.WritePosition(w, Predecessor);
            w.Write(Guarded);
            w.Write(Delete);
        }

        public static Metadata Read(BinaryReader r) => new(r.ReadInt32(), DeliveryBinary.Guid(r), SemanticDeliveryMetadata.ReadDefinition(r),
            SemanticDeliveryMetadata.ReadApplication(r), SemanticDeliveryMetadata.ReadLimits(r), SemanticDeliveryMetadata.ReadBinding(r),
            SemanticDeliveryMetadata.ReadOperation(r), SemanticDeliveryMetadata.ReadPosition(r), DeliveryBinary.Flag(r), DeliveryBinary.Flag(r));
    }
}
