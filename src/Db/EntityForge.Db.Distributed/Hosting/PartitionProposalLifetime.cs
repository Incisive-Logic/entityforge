namespace EntityForge.Db.Distributed.Hosting;

// Node lifecycle exclusion owns admission and every drain. Completing work uses
// a separate lock, so an exclusive owner can await drainage without blocking exits.
internal sealed class PartitionProposalLifetime
{
    private readonly object _sync = new();
    private int _active;
    private long _bytes;
    private TaskCompletionSource? _drained;

    internal IDisposable Enter(long bytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (bytes <= 0 || _active >= PartitionOwnedExecution.DefaultMaximumJobs ||
                bytes > PartitionOwnedExecution.DefaultMaximumBytes - _bytes)
            {
                throw new InvalidOperationException("Partition proposal capacity is exhausted.");
            }

            if (_active++ == 0)
            {
                _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            _bytes += bytes;
            return new Lease(this, bytes);
        }
    }

    internal Task DrainAsync()
    {
        lock (_sync)
        {
            return _active == 0 ? Task.CompletedTask : _drained!.Task;
        }
    }

    private void Exit(long bytes)
    {
        lock (_sync)
        {
            _bytes -= bytes;
            if (--_active == 0)
            {
                _drained!.TrySetResult();
            }
        }
    }

    private sealed class Lease(PartitionProposalLifetime owner, long bytes) : IDisposable
    {
        private PartitionProposalLifetime? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Exit(bytes);
    }
}
