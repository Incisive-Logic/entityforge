using EntityForge.Db.Durability;

namespace EntityForge.Db.Kernel;

/// <summary>A process-associated cursor. Reopening requires an explicit new baseline.</summary>
/// <param name="Source">The process-associated serving source identity; reopening requires a new baseline.</param>
/// <param name="Sequence">The last consumed serving sequence within that source.</param>
public sealed record DatabaseTimestampServingCursor(Guid Source, long Sequence);

/// <summary>Exact, durably committed profile evidence; native sequence is not logical order.</summary>
/// <param name="Sequence">The serving feed sequence, which is distinct from logical timestamp order.</param>
/// <param name="Operation">The committed operation identifier.</param>
/// <param name="Position">The durable native version of the operation.</param>
/// <param name="Time">The logical timestamp evidence associated with the operation.</param>
/// <param name="Mutation">The exact encoded profile mutation.</param>
/// <param name="Effects">Optional committed-change evidence for reconstructing serving state.</param>
public sealed record DatabaseTimestampServingChange(long Sequence, Guid Operation, DatabaseVersion Position,
    DurableTimestampState Time, byte[] Mutation, CommittedChangePayload? Effects);

/// <summary>Closed progress and immutable native basis owned by the supplied history protection.</summary>
/// <param name="Cursor">The serving cursor after this batch.</param>
/// <param name="Position">The captured native database version.</param>
/// <param name="CompleteThrough">The logical timestamp through which this batch certifies completeness.</param>
/// <param name="Baseline">Whether the batch establishes a new immutable baseline.</param>
/// <param name="View">The native view owned by the caller's history protection.</param>
/// <param name="Changes">The bounded changes following the prior cursor.</param>
public sealed record DatabaseTimestampServingBatch(DatabaseTimestampServingCursor Cursor,
    DatabaseVersion Position, DurableTimestampState CompleteThrough, bool Baseline,
    object View, DatabaseTimestampServingChange[] Changes)
{
    /// <summary>The maximum number of changes retained in the serving feed, 512.</summary>
    public const int MaximumChanges = 512;
}

public sealed partial class DatabaseKernel
{
    // Reconstructible acceleration only. Overflow cannot block a native commit,
    // and cannot silently turn a consumer's missing prefix into a complete feed.
    private const long MaximumServingChangeBytes = 4L * 1024 * 1024;
    private readonly Guid _servingSource = Guid.NewGuid();
    private readonly Queue<DatabaseTimestampServingChange> _servingChanges = new();
    private long _servingSequence, _servingBytes;
    private bool _servingExportEnabled;
    private DurableTimestampState? _servingDroppedThrough;

    private void PublishServingChange(PreparedKernelCommit prepared)
    {
        if (!_servingExportEnabled)
        {
            return;
        }

        var effects = TimestampProfile.CaptureServingEffects(prepared.ChangePlan.Changes);
        if (effects is not null)
        {
            effects = new(effects.CodecId, effects.CodecVersion, effects.Content, effects.EffectCount,
                effects.Routing, effects.Kind);
        }

        var change = new DatabaseTimestampServingChange(checked(++_servingSequence),
            prepared.Transaction.TransactionId, prepared.Version, prepared.Transaction.Timestamp!,
            prepared.Transaction.Payload.Content.ToArray(), effects);
        _servingChanges.Enqueue(change);
        _servingBytes = checked(_servingBytes + change.Mutation.Length + (effects?.Content.Length ?? 0));
        while (_servingChanges.Count > DatabaseTimestampServingBatch.MaximumChanges || _servingBytes > MaximumServingChangeBytes)
        {
            var removed = _servingChanges.Dequeue();
            _servingBytes -= removed.Mutation.Length + (removed.Effects?.Content.Length ?? 0);
            if (_servingDroppedThrough is null || removed.Time.CompareTo(_servingDroppedThrough) > 0)
            {
                _servingDroppedThrough = removed.Time;
            }
        }
    }

    internal async ValueTask<DatabaseTimestampServingBatch> CaptureTimestampServingAsync(
        IDatabaseTimestampProtection protection, DurableTimestampState time,
        DatabaseTimestampServingCursor? cursor, CancellationToken token)
    {
        await _commitCoordinator.WaitAsync(token).ConfigureAwait(false);
        try
        {
            EnsureReady();
            _servingExportEnabled = true;
        }
        finally
        {
            _commitCoordinator.Release();
        }
        // This admission establishes the read promise and joins ALL lower intents.
        // Afterwards no new operation may acquire a timestamp <= this promise.
        object view = await CaptureTimestampProjectionAsync(protection, time, null, token).ConfigureAwait(false);
        await _commitCoordinator.WaitAsync(token).ConfigureAwait(false);
        try
        {
            EnsureReady();
            lock (_timestampReadersSync)
            {
                if (protection.IsDisposed || !_timestampReaders.ContainsKey(protection))
                {
                    throw new InvalidOperationException("Serving history protection is no longer owned.");
                }
            }

            if (!TimestampCostOptions!.Healthy())
            {
                throw new InvalidOperationException("Serving clock is unhealthy.");
            }

            if (cursor is not null && (cursor.Source != _servingSource || cursor.Sequence < 0 ||
                cursor.Sequence > _servingSequence || cursor.Sequence < (_servingChanges.TryPeek(out var first)
                    ? first.Sequence - 1 : _servingSequence)))
            {
                throw new InvalidOperationException("Serving cursor lost its source generation or retained prefix; acquire a baseline.");
            }

            if (cursor is null && _servingDroppedThrough is { } dropped && dropped.CompareTo(time) > 0)
            {
                throw new InvalidOperationException("Serving baseline precedes an unavailable future-effect prefix.");
            }
            // A concurrent publication above T may already exist. Include it in
            // the baseline tail: advancing the cursor without it would lose it.
            var changes = _servingChanges.Where(c => cursor is null
                ? c.Time.CompareTo(time) > 0 : c.Sequence > cursor.Sequence).Select(c => c with
                {
                    Mutation = c.Mutation.ToArray(),
                    Effects = c.Effects is not { } e ? null :
                    new(e.CodecId, e.CodecVersion, e.Content, e.EffectCount, e.Routing, e.Kind)
                }).ToArray();
            return new(new(_servingSource, _servingSequence), CurrentVersion, time,
                cursor is null, view, changes);
        }
        finally
        {
            _commitCoordinator.Release();
        }
    }
}
