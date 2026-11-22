namespace EntityForge.Db.Kernel;

public sealed partial class DatabaseKernel
{
    // One short-lived preview, including before-images. Larger preparations keep
    // the existing validated discard/reprepare path rather than retaining more memory.
    internal const long MaximumRetainedPhysicalPreparationBytes = 8L * 1024 * 1024;
    private PreparedKernelCommit? _retainedPhysicalPreparation;

    internal long RetainedPhysicalPreparationHits
    {
        get; private set;
    }

    internal bool HasRetainedPhysicalPreparation => _retainedPhysicalPreparation is not null;

    private bool RetainPhysicalPreparation(PreparedKernelCommit prepared, bool singleton,
        DatabaseTransactionFootprint footprint)
    {
        if (!singleton || !footprint.IsScopedPreparation ||
            _profile is not IDatabaseProfileRetainedPhysicalPreparation || prepared.PhysicalMutations.Count == 0)
        {
            return false;
        }

        long bytes = prepared.Transaction.Payload.Content.Length + (long)(prepared.ChangePlan.Changes?.Content.Length ?? 0);
        foreach (var mutation in prepared.PhysicalMutations)
        {
            bytes += (long)mutation.Bytes.Length + mutation.BeforeImage.Length;
            if (bytes > MaximumRetainedPhysicalPreparationBytes)
            {
                return false;
            }
        }
        if (_retainedPhysicalPreparation is not null)
        {
            throw new InvalidOperationException("A previous physical preparation must be abandoned first.");
        }

        _retainedPhysicalPreparation = prepared;
        return true;
    }

    private PreparedKernelCommit? TakePhysicalPreparation(Guid operation, ReadOnlyMemory<byte> payload,
        DatabaseVersion version, bool partitionAdmissionOwned)
    {
        var prepared = _retainedPhysicalPreparation;
        if (!partitionAdmissionOwned || prepared is null || prepared.Version != version ||
            prepared.Transaction.TransactionId != operation ||
            !prepared.Transaction.Payload.Content.Span.SequenceEqual(payload.Span))
        {
            return null;
        }

        _retainedPhysicalPreparation = null; // Transfer cleanup ownership to the native commit.
        return prepared;
    }

    private async ValueTask DiscardPhysicalPreparationAsync(Guid? operation = null)
    {
        var prepared = _retainedPhysicalPreparation;
        if (prepared is null || operation is { } id && prepared.Transaction.TransactionId != id)
        {
            return;
        }

        _retainedPhysicalPreparation = null;
        try
        {
            await prepared.PhysicalPlanner!.AbandonUnsubmittedPhysicalMutationsAsync(
                prepared.Transaction.TransactionId, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            RequireRecovery("retained_physical_preparation_cleanup");
            throw;
        }
    }
}
