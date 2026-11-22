using EntityForge.Db.Mapping;
using EntityForge.Semantics;
using EntityForge.Semantics.Protobuf;

namespace EntityForge.Db.Semantic;

/// <summary>Owns an immutable prepared native session, so retrying cannot re-read a changed CLR object.</summary>
public sealed class SemanticEntityWrite : IAsyncDisposable
{
    private readonly byte[]? _envelope;

    // Available only for the pure compiler's newly owned, frozen session. Restored
    // untrusted writes must still traverse the complete transport validator.
    internal (SemanticEntityDeliveryCodec.Metadata Metadata, SemanticSessionEnvelope Session)? Compiled
    {
        get;
    }

    private readonly Guid _operationId;
    private readonly SemanticSourcePosition _position;
    private readonly EntityPreparedLifetime<SessionCommitResult>? _lifetime;

    internal SemanticEntityWrite(Guid operation, SemanticSourcePosition position, object owner,
        Func<CancellationToken, ValueTask<SessionCommitResult>> deliver, Func<ValueTask> release)
    {
        _operationId = operation;
        _position = position;
        _lifetime = new(owner, deliver, release);
    }

    /// <summary>Whether this write owns an exportable native session rather than retained resources.</summary>
    public bool IsPortable => _lifetime is null;

    internal ValueTask<SessionCommitResult> DeliverRetainedAsync(object owner, CancellationToken token) =>
        (_lifetime ?? throw new InvalidOperationException("This is a portable native session.")).DeliverAsync(owner, token);

    /// <summary>Releases retained resources without aborting the operation.</summary>
    public ValueTask DisposeAsync() => _lifetime?.DisposeAsync() ?? ValueTask.CompletedTask;

    internal SemanticEntityWrite(Guid incarnation, EntityMappingManifest[] manifests, SemanticSessionEnvelope session, SemanticEntityStoreLimits limits, bool retainCompiled = false)
    {
        _envelope = SemanticEntityDeliveryCodec.Export(incarnation, manifests, session, limits, out _, out var metadata);
        if (retainCompiled)
        {
            Compiled = (metadata, session);
        }

        _operationId = session.Start.SessionId;
        _position = session.Start.Source.Position;
    }

    // The compiler supplies its freshly serialized parts and the private session
    // frozen from those exact bytes. No caller-owned buffer is retained here.
    internal SemanticEntityWrite(Guid incarnation, EntityMappingManifest[] manifests, SemanticSessionEnvelope session,
        SemanticEntityStoreLimits limits, byte[][] parts, bool retainCompiled)
    {
        if (retainCompiled)
        {
            var metadata = SemanticEntityDeliveryCodec.Describe(incarnation, manifests, session, limits, parts[0].Length, parts[1].Length);
            EntityRecordDeliveryCodec.ValidateBounds(metadata, parts.Sum(part => (long)part.Length));
            Compiled = (metadata, session);
        }
        else
        {
            _envelope = SemanticEntityDeliveryCodec.Export(incarnation, manifests, session, limits, parts, out _, out _);
        }

        _operationId = session.Start.SessionId;
        _position = session.Start.Source.Position;
    }

    private SemanticEntityWrite(byte[] ownedEnvelope, Guid operation, SemanticSourcePosition position)
    {
        _envelope = ownedEnvelope;
        _operationId = operation;
        _position = position;
    }

    // Only the codec transfers freshly encoded bytes after validating the incoming session.
    internal static SemanticEntityWrite FromValidatedEnvelope(byte[] ownedEnvelope, Guid operation,
        SemanticSourcePosition position) => new(ownedEnvelope, operation, position);

    /// <summary>Gets the operation identity carried by this frozen session.</summary>
    public Guid OperationId => _operationId;

    /// <summary>Restores a detached prepared write without requiring an embedded database.</summary>
    public static SemanticEntityWrite Restore(ReadOnlySpan<byte> envelope) => SemanticEntityDeliveryCodec.Restore(envelope);

    /// <summary>Exports the frozen session, exact mapping closure, destination and complete-write limits for durable retention.</summary>
    public byte[] Export() => _envelope is not null ? _envelope.ToArray()
        : Compiled is { } compiled ? SemanticEntityDeliveryCodec.Export(compiled.Metadata.DatabaseIncarnation,
            compiled.Metadata.Manifests, compiled.Session, new(compiled.Metadata.Entities, compiled.Metadata.Depth, compiled.Metadata.Bytes))
        : throw new InvalidOperationException("Retain the captured request for portable delivery of a retained preparation.");

    /// <summary>Gets the position this write establishes on success, for sequencing durably retained successor writes.
    /// This is not evidence that the write has committed.</summary>
    public SemanticSourcePosition ResultingPosition => _position;

    /// <summary>Delivers this detached write through an explicitly supplied application client.</summary>
    public ValueTask<SessionCommitResult> CommitAsync(ISemanticEntityClient client, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        return client.DeliverAsync(this, cancellationToken);
    }
}
