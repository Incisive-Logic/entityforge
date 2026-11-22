using EntityForge.Db.Durability;

namespace EntityForge.Db.Kernel;

public sealed partial class DatabaseKernel
{
    private readonly object _snapshotProgressSync = new();
    private TaskCompletionSource? _snapshotProgress;

    internal async ValueTask WaitForSnapshotProgressAsync(IReadOnlyList<Guid> operations, CancellationToken token)
    {
        RequirePartitionTransactions();
        ArgumentNullException.ThrowIfNull(operations);
        using var trace = _telemetry.Start("db.snapshot.progress_wait", Profile);
        if (operations.Count is < 1 or > DurablePartitionTransaction.MaximumTransactions)
        {
            throw new ArgumentException("A bounded nonempty preparation set is required.");
        }

        var owned = operations.ToArray();
        DatabasePartitionSnapshotSummary.RequireProbe(owned);
        int waits = 0;
        trace?.SetTag("entityforge.progress_waits", waits);
        while (true)
        {
            // Subscribe before inspecting state: a completion between inspection
            // and awaiting this task must not become a lost wakeup.
            Task changed;
            lock (_snapshotProgressSync)
            {
                changed = (_snapshotProgress ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            }

            await _commitCoordinator.WaitAsync(token).ConfigureAwait(false);
            try
            {
                EnsureReady();
                if (!owned.Any(id => _partitionTransactions.TryGetValue(id, out var entry) &&
                    entry.State == DurablePartitionTransactionState.Prepared && !_committedTransactions.ContainsKey(id)))
                {
                    return;
                }
            }
            finally
            {
                _commitCoordinator.Release();
            }
            // Neither native admission nor a write exclusion is held while waiting.
            trace?.SetTag("entityforge.progress_waits", ++waits);
            await changed.WaitAsync(token).ConfigureAwait(false);
        }
    }

    private void NotifySnapshotProgress()
    {
        if (Volatile.Read(ref _snapshotProgress) is null)
        {
            return;
        }

        TaskCompletionSource? changed;
        lock (_snapshotProgressSync)
        {
            changed = _snapshotProgress;
            _snapshotProgress = null;
        }
        changed?.TrySetResult();
    }
}
