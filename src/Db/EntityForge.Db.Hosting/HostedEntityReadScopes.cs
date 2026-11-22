namespace EntityForge.Db.Hosting;

// Handles are ephemeral: restart/expiry means unavailable, never a new snapshot or an absent entity.
internal sealed class HostedEntityReadScopes : IAsyncDisposable
{
    internal const int MaximumScopes = 64;
    internal static readonly TimeSpan MaximumDuration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(1);
    private readonly object sync = new();
    private readonly Dictionary<Guid, Entry> entries = [];
    private readonly CancellationTokenSource stopping = new();
    private readonly Task sweeper;
    private Task? disposal;
    private readonly HashSet<Task> closingTasks = [];
    private bool closed;

    internal Func<Task>? BeforeCloseObserved
    {
        get; set;
    }

    internal HostedEntityReadScopes() => sweeper = SweepAsync();

    internal async ValueTask<Guid> OpenAsync(string owner, TimeSpan duration,
        Func<CancellationToken, ValueTask<IAsyncDisposable>> acquire, CancellationToken cancellation)
    {
        if (duration <= TimeSpan.Zero || duration > MaximumDuration)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        Entry entry;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(closed, this);
            if (entries.Count == MaximumScopes)
            {
                throw new ReadScopeCapacityException();
            }

            entry = new(owner, duration);
            entries.Add(entry.Id, entry);
        }
        try
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellation, entry.Lifetime.Token);
            var scope = await acquire(attempt.Token).ConfigureAwait(false);
            entry.Ready.TrySetResult(scope);
            attempt.Token.ThrowIfCancellationRequested();
            lock (sync)
            {
                if (closed || entry.Expired)
                {
                    throw new ReadScopeUnavailableException();
                }
            }

            return entry.Id;
        }
        catch (Exception primary)
        {
            entry.Ready.TrySetResult(null);
            try
            {
                await CloseAsync(entry).ConfigureAwait(false);
            }
            catch (Exception release)
            {
                primary.Data["ReadScopeReleaseFailure"] = release;
            }
            throw;
        }
    }

    internal async ValueTask<T> UseAsync<T>(Guid id, string owner,
        Func<IAsyncDisposable, CancellationToken, ValueTask<T>> read, CancellationToken cancellation)
    {
        Entry entry;
        lock (sync)
        {
            if (closed || !entries.TryGetValue(id, out entry!) || entry.Owner != owner || entry.Retired)
            {
                throw new ReadScopeUnavailableException();
            }
        }
        if (entry.Expired)
        {
            _ = CloseAsync(entry);
            throw new ReadScopeUnavailableException();
        }
        bool entered = false;
        try
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellation, entry.Lifetime.Token);
            await entry.Gate.WaitAsync(attempt.Token).ConfigureAwait(false);
            try
            {
                if (entry.Expired || entry.Retired || entry.Lifetime.IsCancellationRequested)
                {
                    throw new ReadScopeUnavailableException();
                }

                entered = true;
                var scope = await entry.Ready.Task.WaitAsync(attempt.Token).ConfigureAwait(false)
                    ?? throw new ReadScopeUnavailableException();
                var value = await read(scope, attempt.Token).ConfigureAwait(false);
                attempt.Token.ThrowIfCancellationRequested();
                if (entry.Expired)
                {
                    throw new ReadScopeUnavailableException();
                }

                return value;
            }
            finally
            {
                entry.Gate.Release();
            }
        }
        catch (Exception primary)
        {
            // Rejected/queued readers do not join native cleanup. The catalog owns and observes it.
            var close = CloseAsync(entry);
            try
            {
                if (entered)
                {
                    await close.ConfigureAwait(false);
                }
            }
            catch (Exception release)
            {
                primary.Data["ReadScopeReleaseFailure"] = release;
            }
            throw;
        }
    }

    internal async ValueTask ReleaseAsync(Guid id, string owner)
    {
        Entry? entry;
        lock (sync)
        {
            if (!entries.TryGetValue(id, out entry))
            {
                return; // retry after lost release reply
            }

            if (entry.Owner != owner)
            {
                throw new ReadScopeUnavailableException();
            }
        }
        await CloseAsync(entry).ConfigureAwait(false);
    }

    private Task CloseAsync(Entry entry)
    {
        TaskCompletionSource completion;
        lock (entry)
        {
            entry.Retired = true;
            if (entry.Closing is { IsFaulted: false, IsCanceled: false })
            {
                return entry.Closing;
            }

            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            entry.Closing = completion.Task;
        }
        // Start outside the entry lock: completion removes the entry under the catalog lock.
        lock (sync)
        {
            closingTasks.Add(completion.Task);
        }
        // Native DisposeAsync may block before returning its ValueTask. Isolate that synchronous prefix.
        _ = Task.Run(() => CompleteCloseAsync(entry, completion));
        _ = ObserveCloseAsync(completion.Task);
        return completion.Task;
    }

    private async Task CompleteCloseAsync(Entry entry, TaskCompletionSource completion)
    {
        try
        {
            try
            {
                await entry.Lifetime.CancelAsync().ConfigureAwait(false);
            }
            finally
            {
                await entry.Gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    var scope = await entry.Ready.Task.ConfigureAwait(false);
                    if (scope is not null)
                    {
                        await scope.DisposeAsync().ConfigureAwait(false);
                    }

                    lock (sync)
                    {
                        entries.Remove(entry.Id);
                    }
                }
                finally
                {
                    entry.Gate.Release();
                }
            }
            completion.TrySetResult();
        }
        catch (Exception failure)
        {
            completion.TrySetException(failure);
        }
    }

    private async Task ObserveCloseAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
            /* Fault remains on the entry, charged to capacity and retryable. */
        }
        finally
        {
            if (BeforeCloseObserved is { } before)
            {
                await before().ConfigureAwait(false);
            }

            lock (sync)
            {
                closingTasks.Remove(task);
            }
        }
    }

    private async Task SweepAsync()
    {
        using var timer = new PeriodicTimer(SweepInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stopping.Token).ConfigureAwait(false))
            {
                Entry[] expired;
                lock (sync)
                {
                    expired = entries.Values.Where(e => e.Expired || e.Retired).ToArray();
                }
                // Failed disposal remains charged to capacity and is observed again during host shutdown.
                foreach (var entry in expired)
                {
                    _ = CloseAsync(entry);
                }
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {

        }
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        Entry[] remaining;
        lock (sync)
        {
            if (disposal is { IsFaulted: false, IsCanceled: false })
            {
                return new(disposal);
            }

            closed = true;
            remaining = entries.Values.ToArray();
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            disposal = completion.Task;
        }
        _ = CompleteDisposeAsync(remaining, completion);
        return new(completion.Task);
    }

    private async Task CompleteDisposeAsync(Entry[] remaining, TaskCompletionSource completion)
    {
        try
        {
            await DisposeCoreAsync(remaining).ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception failure)
        {
            completion.TrySetException(failure);
        }
    }

    private async Task DisposeCoreAsync(Entry[] remaining)
    {
        Task[] drains = remaining.Select(CloseAsync).ToArray();
        await stopping.CancelAsync().ConfigureAwait(false);
        Task[] pending;
        // A completed failed close can still await observer removal. Its entry is retried by drains;
        // awaiting that stale task again would make a successful shutdown retry fail nondeterministically.
        lock (sync)
        {
            pending = closingTasks.Where(task => !task.IsCompleted).ToArray();
        }

        await Task.WhenAll(drains.Concat(pending).Append(sweeper)).ConfigureAwait(false);
        stopping.Dispose();
    }

    private sealed class Entry(string owner, TimeSpan duration)
    {
        private readonly long started = System.Diagnostics.Stopwatch.GetTimestamp();

        internal Guid Id { get; } = Guid.NewGuid();

        internal string Owner { get; } = owner;

        internal bool Expired => System.Diagnostics.Stopwatch.GetElapsedTime(started) >= duration;

        internal CancellationTokenSource Lifetime { get; } = new();

        internal SemaphoreSlim Gate { get; } = new(1, 1);

        internal TaskCompletionSource<IAsyncDisposable?> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task? Closing;
        internal volatile bool Retired;
    }
}

internal sealed class ReadScopeUnavailableException : Exception;

internal sealed class ReadScopeCapacityException : Exception;
