namespace EntityForge.Db.Distributed.Hosting;

// Already-waiting root lookups share discovery AND first-page execution. There
// is no collection delay, polling, or late join to an earlier timestamp.
internal sealed class PartitionRootQueryQueue(Func<PartitionRootIndexDemand[], CancellationToken,
    ValueTask<PartitionRootIndexRound>> execute) : IAsyncDisposable
{
    private const int MaximumWaiting = 256, MaximumCohort = 32;
    private readonly object _sync = new();
    private readonly Queue<Request> _waiting = new();
    private readonly CancellationTokenSource _stop = new();
    private TaskCompletionSource? _drained;
    private bool _closed;

    private sealed record Request(PartitionRootIndexDemand Demand, CancellationToken Token, TaskCompletionSource<PartitionRootIndexRound> Result);

    internal ValueTask<PartitionRootIndexRound> ReadAsync(PartitionRootIndexDemand demand, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var result = new TaskCompletionSource<PartitionRootIndexRound>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool start;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (_waiting.Count >= MaximumWaiting)
            {
                throw new InvalidOperationException("Root lookup admission is full.");
            }

            _waiting.Enqueue(new(demand, token, result));
            start = _drained is null;
            if (start)
            {
                _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
        if (start)
        {
            _ = DrainAsync();
        }

        return new(result.Task.WaitAsync(token));
    }

    private async Task DrainAsync()
    {
        while (true)
        {
            Request[] requests;
            lock (_sync)
            {
                if (_waiting.Count == 0)
                {
                    var drained = _drained!;
                    _drained = null;
                    drained.TrySetResult();
                    return;
                }
                requests = new Request[Math.Min(MaximumCohort, _waiting.Count)];
                for (int i = 0; i < requests.Length; i++)
                {
                    requests[i] = _waiting.Dequeue();
                }
            }
            await Task.WhenAll(requests.GroupBy(request => request.Demand.Column).Select(async group =>
            {
                var active = group.Where(request => !request.Token.IsCancellationRequested).ToArray();
                foreach (var cancelled in group.Where(request => request.Token.IsCancellationRequested))
                {
                    cancelled.Result.TrySetCanceled(cancelled.Token);
                }

                if (active.Length == 0)
                {
                    return;
                }

                try
                {
                    var result = await execute(active.Select(request => request.Demand).ToArray(), _stop.Token);
                    foreach (var request in active)
                    {
                        request.Result.TrySetResult(result);
                    }
                }
                catch (Exception error)
                {
                    foreach (var request in active)
                    {
                        request.Result.TrySetException(error);
                    }
                }
            }));
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? drained;
        lock (_sync)
        {
            _closed = true;
            drained = _drained?.Task;
        }
        _stop.Cancel();
        if (drained is not null)
        {
            await drained;
        }
    }
}
