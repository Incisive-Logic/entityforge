namespace EntityForge.Db.Kernel;

public sealed partial class DatabaseKernel
{
    private readonly CancellationTokenSource _privateCompactionStop = new();
    private Task? _privateCompactionTask;
    private Exception? _privateCompactionFailure;
    private DatabaseVersion _privateCompactionCheckpointVersion;

    private async Task StopPrivateCheckpointCompactionAsync()
    {
        Task? work;
        await _checkpointPublication.WaitAsync().ConfigureAwait(false);
        try
        {
            _privateCompactionStop.Cancel();
            work = _privateCompactionTask;
        }
        finally
        {
            _checkpointPublication.Release();
        }
        if (work is not null)
        {
            await work.ConfigureAwait(false);
        }
    }

    // Called with checkpoint publication exclusion. No roots are published by
    // this worker: it stops at the explicit base-ready barrier, before catch-up.
    private void StartPrivateCheckpointCompaction(DatabaseVersion checkpointVersion)
    {
        _privateCompactionCheckpointVersion = checkpointVersion;
        if (_privateCompactionStop.IsCancellationRequested ||
            _privateCompactionTask is { IsCompleted: false } ||
            _profile is not IDatabaseProfileIncrementalCheckpointCompaction { CanAdvancePrivateCheckpointCompaction: true })
        {
            return;
        }

        using (ExecutionContext.SuppressFlow())
        {
            _privateCompactionTask = Task.Run(AdvancePrivateCheckpointCompactionAsync);
        }
    }

    private async Task AdvancePrivateCheckpointCompactionAsync()
    {
        var token = _privateCompactionStop.Token;
        try
        {
            while (true)
            {
                await Task.Delay(100, token).ConfigureAwait(false);
                await _checkpointPublication.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    await _commitCoordinator.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        if (_state != DatabaseKernelState.Ready ||
                            _profile is not IDatabaseProfileIncrementalCheckpointCompaction { CanAdvancePrivateCheckpointCompaction: true } work)
                        {
                            return;
                        }
                        // Never wait for reader admission while holding commit
                        // coordination: a reader may itself be awaiting a commit.
                        using var admission = work.TryEnterPrivateCheckpointCompaction();
                        if (admission is null || _partitionReservations.Count != 0)
                        {
                            continue;
                        }

                        await DiscardPhysicalPreparationAsync().ConfigureAwait(false);
                        // Shutdown cancels admission waits, not a durable batch.
                        // Drain this slice before releasing any source pins/logs.
                        await AdvanceCheckpointCompactionAsync(work, CurrentVersion, CancellationToken.None,
                            _privateCompactionCheckpointVersion).ConfigureAwait(false);
                    }
                    finally
                    {
                        _commitCoordinator.Release();
                    }
                }
                finally
                {
                    _checkpointPublication.Release();
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {

        }
        catch (Exception failure)
        {
            _privateCompactionFailure = failure;
            RequireRecovery("private_checkpoint_compaction");
        }
    }
}
