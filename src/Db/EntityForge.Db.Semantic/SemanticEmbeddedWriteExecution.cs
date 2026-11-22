using EntityForge.Db.Mapping;
using EntityForge.Semantics;
using EntityForge.Semantics.Protobuf;

namespace EntityForge.Db.Semantic;

internal sealed class SemanticEmbeddedWriteExecution
{
    private readonly ISemanticDatabase Database;
    private readonly Func<SemanticSessionEnvelope, Action<IReadOnlyList<SemanticEntityChange>>, CancellationToken, ValueTask<SessionCommitResult>>? _apply;
    private readonly EntityMappingManifest[] _manifests;
    private readonly SemanticSessionEnvelope _session;
    private readonly SemanticEntityStoreLimits _limits;
    private readonly Guid _databaseIncarnation;
    private readonly byte[] _envelope;
    private readonly SemanticEntityDeliveryCodec.Metadata _metadata;
    private readonly byte[] _payload;
    private readonly byte[] _preparedIdentity;

    internal SemanticEmbeddedWriteExecution(ISemanticDatabase database, SemanticEntityWrite write,
        Func<SemanticSessionEnvelope, Action<IReadOnlyList<SemanticEntityChange>>, CancellationToken, ValueTask<SessionCommitResult>>? apply = null)
    {
        Database = database;
        _apply = apply;
        var validated = SemanticEntityDeliveryCodec.RestoreValidated(write.Export());
        _envelope = validated.Write.Export();
        _metadata = validated.Metadata;
        if (_metadata.LocalOwnerCoverage is not null && apply is null)
        {
            throw new NotSupportedException("Owner-local prepared writes require owner admission.");
        }

        _payload = validated.Payload;
        var metadata = _metadata;
        _preparedIdentity = SemanticEntityDeliveryCodec.PreparedIdentity(metadata, _payload);
        _databaseIncarnation = metadata.DatabaseIncarnation;
        if (((ISemanticMappedExecution)database).DatabaseIncarnation != _databaseIncarnation)
        {
            throw new InvalidOperationException("The prepared write belongs to another database incarnation.");
        }

        _manifests = metadata.Manifests;
        _session = validated.Session;
        _limits = new(metadata.Entities, metadata.Depth, metadata.Bytes);
    }

    private byte[] Export() => _envelope;

    internal ValueTask<SessionCommitResult> DeliverCoreAsync(CancellationToken cancellationToken) =>
        ((IEntityOperationHost)Database).Operations.ExecutePreparedAsync(_session.Start.SessionId, MatchesBoundPlan,
            () => CommitWithMappingAsync(cancellationToken), cancellationToken, Export(), _preparedIdentity, SemanticEntityDeliveryCodec.Replay, receipt => receipt.Receipt, _databaseIncarnation);

    private bool MatchesBoundPlan(byte[] retained)
    {
        var (expected, expectedPayload) = EntityRecordDeliveryCodec.Decode<SemanticEntityDeliveryCodec.Metadata>(retained);
        var actual = _metadata;
        return expectedPayload.AsSpan().SequenceEqual(_payload) &&
            expected.DatabaseIncarnation == actual.DatabaseIncarnation && expected.OperationId == actual.OperationId &&
            expected.Entities == actual.Entities && expected.Depth == actual.Depth && expected.Bytes == actual.Bytes && Equals(expected.LocalOwnerCoverage, actual.LocalOwnerCoverage) &&
            expected.Manifests.Select(m => m.Fingerprint).SequenceEqual(actual.Manifests.Select(m => m.Fingerprint));
    }

    private ValueTask<SessionCommitResult> CommitWithMappingAsync(CancellationToken cancellationToken) =>
        Database.Mappings().ExecuteAsync(_manifests, () => CommitCoreAsync(cancellationToken), cancellationToken,
            async () =>
            {
                SessionCommitResult? receipt = await ((ISemanticMappedExecution)Database)
                    .ResolveMappedReplayAsync(_session, cancellationToken).ConfigureAwait(false);
                return (receipt is not null, receipt!);
            });

    private ValueTask<SessionCommitResult> CommitCoreAsync(CancellationToken cancellationToken) =>
        _apply is null ? ((ISemanticMappedExecution)Database)
            .ApplyMappedSessionAsync(_session, ValidateCompleteChanges, cancellationToken)
            : _apply(_session, ValidateCompleteChanges, cancellationToken);

    private void ValidateCompleteChanges(IReadOnlyList<SemanticEntityChange> complete) =>
        SemanticEntityDeliveryCodec.ValidateCompleteChanges(_session, _limits, complete);

}
