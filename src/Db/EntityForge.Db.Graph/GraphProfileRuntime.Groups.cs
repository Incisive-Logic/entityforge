using EntityForge.Db.Mapping;
using EntityForge.Db.Graph.Contracts;

namespace EntityForge.Db.Graph;

internal sealed partial class GraphProfileRuntime
{
    internal static bool CanGroup(GraphTransaction transaction)
    {
        bool eligible = !transaction.IsInitialBulkLoad;
        if (!DatabaseProfileWorkTelemetry.IsEnabled)
        {
            return eligible;
        }

        string shape = transaction.IsInitialBulkLoad ? "bulk" : transaction.Mutations.All(m =>
            m is SetVertexProperty or RemoveVertexProperty or SetEdgeProperty or RemoveEdgeProperty) ? "property" : "topology";
        DatabaseProfileWorkTelemetry.Record("graph", eligible ? "group-eligible-check" : "group-ineligible-check", shape, 1);
        return eligible;
    }

    public async ValueTask StagePreparedStateAsync(DatabaseVersion version, Guid transactionId,
        ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        if (EntityOperationCodec.IsCommand(payload.Span))
        {
            // Stage the state at this binding's position without publishing its catalog entry.
            lock (_stateGate)
            {
                var prior = _pendingStates.Values.MaxBy(item => item.Version.Value);
                _pendingStates[transactionId] = new(version, prior?.Vertices ?? _vertices,
                    prior?.Edges ?? _edges, prior?.View ?? _currentView, new([]),
                    (prior?.Statistics ?? _queryStatistics) with
                    {
                        Version = version
                    }, []);
                _orderedPreparations.Add(transactionId);
            }
            return;
        }
        if (IsMetadata(payload) || !CanGroup(DecodeTransaction(transactionId, payload.Span)))
        {
            throw new NotSupportedException("Graph commit groups require ordinary native transactions.");
        }

        await ApplyAsync(version, transactionId, payload, cancellationToken).ConfigureAwait(false);
        lock (_stateGate)
        {
            _orderedPreparations.Add(transactionId);
            foreach (var key in _pendingStates[transactionId].ConflictKeys)
            {
                _orderedWrites.Set(key, version);
            }
        }
    }

    public ValueTask BeginPreparedPublicationAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // ApplyAsync already built the invisible predecessor chain. Publication consumes it in order.
        return ValueTask.CompletedTask;
    }

    public ValueTask AbandonPreparedStatesAsync(IReadOnlyList<Guid> transactionIds,
        CancellationToken cancellationToken = default)
    {
        lock (_stateGate)
        {
            foreach (Guid id in transactionIds)
            {
                _orderedPreparations.Remove(id);
                _pendingStates.Remove(id);
                _preparedPlans.Remove(id);
                _preparedTransactions.Remove(id);
            }
            _orderedWrites.Clear();
        }
        return ValueTask.CompletedTask;
    }
}
