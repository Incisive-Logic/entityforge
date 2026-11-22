using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Distributed.Hosting;

internal sealed class PartitionReadEpochChangedException : Exception;

// Reconstructible read acceleration, never a durable transaction authority. A
// generation owns protected history; each request selects one fresh timestamp
// and independently admits that timestamp on every required owner.
internal sealed class PartitionReadEpoch(IPartitionReadParticipant[] owners, DistributedRoutingBinding routing,
    EntityForge.Db.Kernel.IDatabaseBoundedClock? clock = null) : IAsyncDisposable
{
    private readonly EntityForge.Db.Kernel.IDatabaseBoundedClock _clock = PartitionTimestampClock.Require(clock);
    private static readonly TimeSpan RefreshAfter = TimeSpan.FromSeconds(5);
    private const int MaximumRetiredEpochs = 16;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<Task> _retired = [];
    private Epoch? _current;
    private Epoch? _refresh;
    private static readonly TimeSpan BeginRefreshAfter = TimeSpan.FromSeconds(4);
    private bool _closed;

    internal async ValueTask<(T Value, DatabasePositionVector Position)> ExecuteAsync<T>(
        Func<PartitionRemoteRead, CancellationToken, ValueTask<T>> projection,
        DatabasePositionVector? minimum, CancellationToken token, bool indexProgress = false)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(PartitionReadCuts.MaximumDuration);
        while (true)
        {
            Epoch? epoch = null;
            Task<PartitionRemoteRead>? ready = null;
            await _gate.WaitAsync(deadline.Token);
            try
            {
                ObjectDisposedException.ThrowIf(_closed, this);
                foreach (var completed in _retired.Where(t => t.IsCompleted).ToArray())
                {
                    // Report a failed retirement once. Keeping it in the cache
                    // would reject every later read and maintenance drain.
                    _retired.Remove(completed);
                    await completed;
                }
                if (_refresh is { } replacement && replacement.Ready.IsCompleted)
                {
                    if (!replacement.Ready.IsCompletedSuccessfully)
                    {
                        replacement.Retire();
                        _retired.Add(replacement.Completion);
                        _refresh = null;
                        await replacement.Ready; // Preserve acquisition/cleanup failures.
                    }
                    RetireCurrent();
                    _current = replacement;
                    _refresh = null;
                }
                if (_current is { } prior && prior.Invalid)
                {
                    RetireCurrent();
                }

                bool mustRefresh = _current is null || minimum is not null && !_current.Read.Position.Covers(minimum);
                if (mustRefresh || DateTimeOffset.UtcNow - _current!.Created >= BeginRefreshAfter)
                {
                    if (_refresh is null)
                    {
                        if (_retired.Count >= MaximumRetiredEpochs)
                        {
                            throw new InvalidOperationException("Read protections are waiting for drainage.");
                        }

                        _refresh = new Epoch(owners, routing, minimum);
                    }
                    if (mustRefresh)
                    {
                        ready = _refresh.Ready;
                    }
                }
                if (ready is null && _current is { } usable && usable.TryBorrow())
                {
                    epoch = usable;
                }
            }
            finally
            {
                _gate.Release();
            }
            // All callers share acquisition, but none waits for it under the
            // borrowing gate. An existing healthy generation can serve fresh
            // timestamps during bounded proactive replacement.
            if (ready is not null)
            {
                await ready.WaitAsync(deadline.Token);
                continue;
            }
            if (epoch is null)
            {
                continue;
            }

            try
            {
                using var query = DistributedReadTelemetry.Start(indexProgress ? "partition.read.epoch.index" : "partition.read.epoch.query");
                var value = await projection(epoch.Read.ForTimestampQuery(_clock), deadline.Token);
                if (epoch.Completion.IsCompleted)
                {
                    await epoch.Completion;
                }

                return (value, epoch.Read.Position);
            }
            finally
            {
                epoch.Return();
            }
        }
    }

    private void RetireCurrent()
    {
        if (_current is not { } previous)
        {
            return;
        }

        previous.Retire();
        _retired.Add(previous.Completion);
        _current = null;
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _closed = true;
            RetireCurrent();
            if (_refresh is { } pending)
            {
                pending.Retire();
                _retired.Add(pending.Completion);
                _refresh = null;
            }
            await Task.WhenAll(_retired);
            _retired.Clear();
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed class Epoch
    {
        private readonly object _sync = new();
        private readonly TaskCompletionSource<PartitionRemoteRead> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _users;
        private bool _retired;

        internal DateTimeOffset Created
        {
            get; private set;
        }

        internal bool Invalid => Volatile.Read(ref _retired) || Completion.IsCompleted;

        internal Task<PartitionRemoteRead> Ready => _ready.Task;

        internal PartitionRemoteRead Read => Ready.Result;

        internal Task Completion
        {
            get;
        }

        internal Epoch(IPartitionReadParticipant[] owners, DistributedRoutingBinding routing, DatabasePositionVector? minimum)
        {
            Completion = RunAsync(owners, routing, minimum);
            _ = Completion.ContinueWith(t =>
{
    _ = t.Exception;
}, TaskContinuationOptions.OnlyOnFaulted);
        }

        private async Task RunAsync(IPartitionReadParticipant[] owners, DistributedRoutingBinding routing, DatabasePositionVector? minimum)
        {
            try
            {
                await PartitionRemoteRead.ExecuteProtectedAsync(owners, routing, async (read, token) =>
                {
                    Created = DateTimeOffset.UtcNow;
                    read.UseAsEpoch();
                    _ready.TrySetResult(read);
                    if (await Task.WhenAny(_release.Task, Task.Delay(RefreshAfter, token)) != _release.Task)
                    {
                        Retire();
                    }

                    await _release.Task.WaitAsync(token);
                    return true;
                }, minimum: minimum);
            }
            catch (Exception failure)
            {
                _ready.TrySetException(failure);
                throw;
            }
        }

        internal bool TryBorrow()
        {
            lock (_sync)
            {
                if (_retired)
                {
                    return false;
                }

                _users++;
                return true;
            }
        }

        internal void Return()
        {
            lock (_sync)
            {
                _users--;
                if (_users == 0 && _retired)
                {
                    _release.TrySetResult();
                }
            }
        }

        internal void Retire()
        {
            lock (_sync)
            {
                _retired = true;
                if (_users == 0)
                {
                    _release.TrySetResult();
                }
            }
        }
    }
}

internal sealed partial class PartitionApplicationClient
{
    private PartitionReadEpoch? _readEpoch;
    private readonly object _readEpochSync = new();

    private ValueTask<(T Value, DatabasePositionVector Position)> ExecuteReadEpochAsync<T>(
        Func<PartitionRemoteRead, CancellationToken, ValueTask<T>> projection, DatabasePositionVector? minimum, CancellationToken token, bool indexProgress = false)
    {
        PartitionReadEpoch epoch;
        lock (_readEpochSync)
        {
            epoch = _readEpoch ??= new(_owners.Values.Cast<IPartitionReadParticipant>().ToArray(), _routing, _timestampClock);
        }

        return epoch.ExecuteAsync(projection, minimum, token, indexProgress);
    }

    internal async ValueTask CloseReadEpochAsync()
    {
        await CloseRootDirectoryAsync();
        await CloseIndexedServingAsync();
        PartitionReadEpoch? epoch;
        lock (_readEpochSync)
        {
            epoch = _readEpoch;
            _readEpoch = null;
        }
        if (epoch is not null)
        {
            await epoch.DisposeAsync();
        }
    }
}
