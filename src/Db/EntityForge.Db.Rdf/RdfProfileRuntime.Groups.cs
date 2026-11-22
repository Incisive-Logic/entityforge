using EntityForge.Db.Kernel;
using EntityForge.Db.Mapping;
using EntityForge.Db.Rdf.Contracts;
using EntityForge.Db.Storage;

namespace EntityForge.Db.Rdf;

internal sealed partial class RdfProfileRuntime
{
    private readonly HashSet<Guid> _orderedPreparations = [];

    public bool SupportsAtomicGroupPublication => true;

    public async ValueTask StagePreparedStateAsync(DatabaseVersion version, Guid id,
        ReadOnlyMemory<byte> payload, CancellationToken token = default)
    {
        if (EntityOperationCodec.IsCommand(payload.Span))
        {
            // Catalog publication must wait for durability, just like the native state.
            lock (_gate)
            {
                var prior = _pendingStates.Values.MaxBy(state => state.Version.Value);
                _pendingStates.Add(id, new(version, prior?.View ?? _view,
                    prior?.CommandReplays ?? _commandReplays));
                _orderedPreparations.Add(id);
            }
            return;
        }
        if (IsMetadata(payload) || Decode(id, payload.Span).IsInitialBulkLoad)
        {
            throw new NotSupportedException("RDF groups require ordinary native transactions.");
        }

        await ApplyAsync(version, id, payload, token).ConfigureAwait(false);
        lock (_gate)
        {
            _orderedPreparations.Add(id);
        }
    }

    public async ValueTask<IReadOnlyDictionary<Guid, IReadOnlyList<DatabasePhysicalPageMutation>>>
        PreparePhysicalMutationGroupAsync(IReadOnlyList<DatabaseProfilePhysicalPreparation> preparations,
            CancellationToken token = default)
    {
        Guid owner = preparations[^1].TransactionId;
        var members = preparations.Where(item => !IsMetadata(item.Payload))
            .Select(item => (item.Version.Value, _logicalEffects.TryGet(item.TransactionId, item.Payload.Span, out var compiled)
                ? compiled.Indexes : RdfPhysicalAccessPath.Compile(PreparedTransaction(item.TransactionId, item.Payload)))).ToArray();
        var stage = await _physical.PrepareGroupAsync(owner, members, token).ConfigureAwait(false);
        if (stage is null)
        {
            return new Dictionary<Guid, IReadOnlyList<DatabasePhysicalPageMutation>>();
        }

        _stagedPhysical.Add(owner);
        // Complete after-images belong to the final member. Readers cannot enter between members.
        return new Dictionary<Guid, IReadOnlyList<DatabasePhysicalPageMutation>> { [owner] = stage.PhysicalMutations };
    }

    public ValueTask BeginPreparedPublicationAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public ValueTask AbandonPreparedStatesAsync(IReadOnlyList<Guid> ids, CancellationToken token = default)
    {
        lock (_gate)
        {
            foreach (Guid id in ids)
            {
                _orderedPreparations.Remove(id);
                _pendingStates.Remove(id);
                _preparedTransactions.Remove(id);
            }
        }

        return ValueTask.CompletedTask;
    }
}
