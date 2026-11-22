namespace EntityForge.Db.Distributed.Sharding;

// Only callers queued before an observation is dispatched may share its result.
// In particular a late completion check never joins an already-running read.
internal sealed class FreshObservationQueue<TKey, TValue>(Func<TKey, CancellationToken, ValueTask<TValue>> observe)
    : IAsyncDisposable where TKey : notnull
{
    private const int MaximumWaiting = 256;
    private const int MaximumCohort = 32;
    private readonly object _sync = new();
    private readonly Queue<Request> _waiting = new();
    private readonly CancellationTokenSource _stop = new();
    private TaskCompletionSource? _drained;
    private bool _closed;

    private sealed record Request(TKey Key, CancellationToken Token, TaskCompletionSource<TValue> Result);

    internal ValueTask<TValue> ReadAsync(TKey key, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var result = new TaskCompletionSource<TValue>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool start;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (_waiting.Count >= MaximumWaiting)
            {
                throw new InvalidOperationException("Placement observation capacity is exhausted.");
            }

            _waiting.Enqueue(new(key, token, result));
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
            Request[] cohort;
            lock (_sync)
            {
                if (_waiting.Count == 0)
                {
                    var completed = _drained!;
                    _drained = null;
                    completed.TrySetResult();
                    return;
                }
                cohort = new Request[Math.Min(MaximumCohort, _waiting.Count)];
                for (int i = 0; i < cohort.Length; i++)
                {
                    cohort[i] = _waiting.Dequeue();
                }
            }
            // Selection is complete before any read begins. Groups have distinct keys.
            await Task.WhenAll(cohort.GroupBy(r => r.Key).Select(async group =>
            {
                var members = group.Where(r => !r.Token.IsCancellationRequested).ToArray();
                foreach (var cancelled in group.Where(r => r.Token.IsCancellationRequested))
                {
                    cancelled.Result.TrySetCanceled(cancelled.Token);
                }

                if (members.Length == 0)
                {
                    return;
                }

                try
                {
                    var value = await observe(group.Key, _stop.Token).ConfigureAwait(false);
                    foreach (var member in members)
                    {
                        member.Result.TrySetResult(value);
                    }
                }
                catch (Exception error)
                {
                    foreach (var member in members)
                    {
                        member.Result.TrySetException(error);
                    }
                }
            })).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? drain;
        lock (_sync)
        {
            _closed = true;
            drain = _drained?.Task;
        }
        _stop.Cancel();
        if (drain is not null)
        {
            await drain.ConfigureAwait(false);
        }
        // Keep the source valid for idempotent concurrent disposal.
    }
}
