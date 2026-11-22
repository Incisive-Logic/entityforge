namespace EntityForge.Db.Distributed.Hosting;

/// <summary>One bounded execution lifetime per enrolled owner's terminal operation.</summary>
/// <remarks>The supplied executor owns placement admission and fresh decision validation.
/// This inbox neither caches outcomes nor grants authority from a notification.</remarks>
internal sealed class PartitionTerminalInbox(Func<Guid, CancellationToken, Task<DatabaseCommitReceipt?>> execute) : IAsyncDisposable
{
    internal const int MaximumJobs = 256;
    internal const int MaximumWaiters = 1024;
    private readonly object _sync = new();
    private readonly Dictionary<Guid, Job> _jobs = [];
    private int _waiters;
    private bool _closed;

    private sealed class Job(Guid operation)
    {
        internal readonly Guid Operation = operation;
        internal readonly CancellationTokenSource Cancellation = new();
        internal readonly TaskCompletionSource<DatabaseCommitReceipt?> Completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int Waiters;
        internal bool Finished;
        internal Task CancellationCompletion = Task.CompletedTask;
    }

    internal Task<DatabaseCommitReceipt?> JoinAsync(Guid operation, CancellationToken token = default)
    {
        if (operation == Guid.Empty)
        {
            throw new ArgumentException("Missing terminal operation identity.", nameof(operation));
        }

        token.ThrowIfCancellationRequested();
        Job job;
        bool start = false;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (_waiters == MaximumWaiters)
            {
                throw new InvalidOperationException("Terminal waiter capacity is exhausted.");
            }

            if (!_jobs.TryGetValue(operation, out job!))
            {
                if (_jobs.Count == MaximumJobs)
                {
                    throw new InvalidOperationException("Terminal job capacity is exhausted.");
                }

                job = new(operation);
                _jobs.Add(operation, job);
                start = true;
            }
            job.Waiters++;
            _waiters++;
        }
        if (start)
        {
            _ = RunAsync(job);
        }

        return WaitAsync(job, token);
    }

    private async Task<DatabaseCommitReceipt?> WaitAsync(Job job, CancellationToken token)
    {
        try
        {
            return await job.Completion.Task.WaitAsync(token).ConfigureAwait(false);
        }
        finally
        {
            lock (_sync)
            {
                _waiters--;
                job.Waiters--;
                // Cancellation can withdraw queued work, but the executor must retain
                // ownership once native completion has been selected. Another waiter
                // (including a notification owner) must never inherit this cancellation.
                if (job.Waiters == 0 && !job.Finished)
                {
                    job.CancellationCompletion = job.Cancellation.CancelAsync();
                }
            }
        }
    }

    private async Task RunAsync(Job job)
    {
        DatabaseCommitReceipt? receipt = null;
        Exception? failure = null;
        try
        {
            receipt = await execute(job.Operation, job.Cancellation.Token).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            failure = error;
        }
        Task cancellation;
        lock (_sync)
        {
            job.Finished = true;
            cancellation = job.CancellationCompletion;
        }
        try
        {
            await cancellation.ConfigureAwait(false);
        }
        catch (Exception error)
        {
            failure ??= error;
        }
        lock (_sync)
        {
            _jobs.Remove(job.Operation);
            job.Cancellation.Dispose();
            if (failure is OperationCanceledException cancelled)
            {
                job.Completion.TrySetCanceled(cancelled.CancellationToken);
            }
            else if (failure is not null)
            {
                job.Completion.TrySetException(failure);
                // All waiters may have cancelled; durable recovery owns any remaining
                // obligation. Observe the task fault even in that case.
                _ = job.Completion.Task.Exception;
            }
            else
            {
                job.Completion.TrySetResult(receipt);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task[] pending;
        lock (_sync)
        {
            _closed = true;
            pending = _jobs.Values.Select(job => (Task)job.Completion.Task).ToArray();
        }
        // Failures belong to the exact operation's waiters, not to unrelated shutdown.
        // Join every owned executor before its native/fence resources can be disposed.
        try
        {
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
        catch
        {

        }
    }
}
