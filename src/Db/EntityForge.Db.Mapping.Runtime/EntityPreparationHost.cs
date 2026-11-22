namespace EntityForge.Db.Mapping;

// Authenticated composition supplies owner identity. Handles are ephemeral capabilities, not operation decisions.
internal sealed class EntityPreparationHost<TPrepared, TResult> : IAsyncDisposable where TPrepared : class, IAsyncDisposable
{
    private const int MaximumOwnerCharacters = 1024;
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(1);
    private readonly object _sync = new();
    private readonly Func<TPrepared, CancellationToken, ValueTask<TResult>> _deliver;
    private readonly Func<TResult, DatabaseCommitReceipt>? _receipt;
    private readonly Guid _database;
    private readonly int _capacity;
    private readonly TimeSpan _maximumLifetime;
    private readonly TimeProvider _time;
    private readonly Dictionary<Guid, Entry> _entries = [];
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _scanner;
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _pending, _active, _retiring;
    private bool _closing;
    private readonly List<Exception> _cleanupFailures = [];
    private Task? _disposal;

    internal EntityPreparationHost(Guid database, int capacity, TimeSpan maximumLifetime, Func<TPrepared, CancellationToken, ValueTask<TResult>> deliver, TimeProvider? time = null, Func<TResult, DatabaseCommitReceipt>? receipt = null)
    {
        ArgumentNullException.ThrowIfNull(deliver);
        if (database == Guid.Empty)
        {
            throw new ArgumentException("A preparation destination is required.", nameof(database));
        }
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }
        if (maximumLifetime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumLifetime));
        }
        _database = database;
        _capacity = capacity;
        _maximumLifetime = maximumLifetime;
        _deliver = deliver;
        _receipt = receipt;
        _time = time ?? TimeProvider.System;
        _scanner = ScanAsync();
    }

    internal async ValueTask<EntityPreparedHandle> AcquireAsync(string owner, Guid operation, TimeSpan lifetime, Func<CancellationToken, ValueTask<TPrepared>> prepare, CancellationToken cancellationToken)
    {
        ValidateOwner(owner);
        if (lifetime <= TimeSpan.Zero || lifetime > _maximumLifetime)
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime));
        }
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            EnsureAvailable();
            if (_entries.Count + _pending + _retiring >= _capacity)
            {
                throw new InvalidOperationException("Preparation capacity reached.");
            }
            _pending++;
            _active++;
        }

        TPrepared? write = null;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
            write = await prepare(linked.Token).ConfigureAwait(false);
            var handle = new EntityPreparedHandle(Guid.NewGuid(), _database, operation, _time.GetUtcNow().Add(lifetime));
            lock (_sync)
            {
                EnsureAvailable();
                linked.Token.ThrowIfCancellationRequested();
                _entries.Add(handle.Id, new(handle, owner, write));
            }

            return handle;
        }
        catch (Exception failure)
        {
            if (failure is IEntityPreparationCleanupFailure)
            {
                RecordFailure(failure);
            }
            if (write is not null)
            {
                try
                {
                    await write.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception releaseFailure)
                {
                    RecordFailure(releaseFailure);
                    throw new AggregateException(failure, releaseFailure);
                }
            }

            throw;
        }
        finally
        {
            lock (_sync)
            {
                _pending--;
                EndActivity();
            }
        }
    }

    internal async ValueTask<TResult> DeliverAsync(string owner, EntityPreparedHandle handle, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Entry entry;
        lock (_sync)
        {
            EnsureAvailable();
            entry = Find(owner, handle);
            if (entry.Retiring || entry.Handle.ExpiresAtUtc <= _time.GetUtcNow())
            {
                throw new InvalidOperationException("The prepared handle has expired or was released.");
            }
            if (entry.Active)
            {
                throw new InvalidOperationException("The prepared handle is already in use.");
            }
            entry.Active = true;
            _active++;
        }

        TResult result;
        try
        {
            result = await _deliver(entry.Write, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            try
            {
                await FinishUseAsync(entry).ConfigureAwait(false);
            }
            catch (Exception releaseFailure)
            {
                EntityFailureDiagnostics.Attach(failure, releaseFailure);
            }

            throw;
        }

        try
        {
            await FinishUseAsync(entry).ConfigureAwait(false);
        }
        catch (Exception cleanup) when (_receipt is not null)
        {
            throw new EntityOperationCommittedException<TResult>(result, _receipt(result), cleanup);
        }

        return result;
    }

    private async ValueTask FinishUseAsync(Entry entry)
    {
        bool retire;
        lock (_sync)
        {
            entry.Active = false;
            retire = entry.Retiring || entry.Handle.ExpiresAtUtc <= _time.GetUtcNow();
            if (retire)
            {
                _entries.Remove(entry.Handle.Id);
                _retiring++;
            }
        }

        try
        {
            if (retire)
            {
                await CloseEntryAsync(entry).ConfigureAwait(false);
            }
        }
        finally
        {
            lock (_sync)
            {
                EndActivity();
            }
        }
    }

    internal async ValueTask ReleaseAsync(string owner, EntityPreparedHandle handle)
    {
        Entry entry;
        bool close;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            ValidateRoute(owner, handle);
            if (!_entries.ContainsKey(handle.Id))
            {
                return;
            }
            entry = Find(owner, handle);
            entry.Retiring = true;
            close = !entry.Active;
            if (close)
            {
                _entries.Remove(handle.Id);
                _active++;
                _retiring++;
            }
        }

        if (!close)
        {
            await entry.Released.Task.ConfigureAwait(false);
            return;
        }

        try
        {
            await CloseEntryAsync(entry).ConfigureAwait(false);
        }
        finally
        {
            lock (_sync)
            {
                EndActivity();
            }
        }
    }

    internal async ValueTask SweepAsync()
    {
        Entry[] expired;
        lock (_sync)
        {
            if (_closing)
            {
                return;
            }
            var candidates = _entries.Values.Where(e => e.Handle.ExpiresAtUtc <= _time.GetUtcNow()).ToArray();
            foreach (var entry in candidates)
            {
                entry.Retiring = true;
            }
            expired = candidates.Where(e => !e.Active).ToArray();
            foreach (var entry in expired)
            {
                _entries.Remove(entry.Handle.Id);
            }
            _active += expired.Length;
            _retiring += expired.Length;
        }

        var failures = new List<Exception>();
        foreach (var entry in expired)
        {
            try
            {
                await CloseEntryAsync(entry).ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                failures.Add(failure);
            }
            finally
            {
                lock (_sync)
                {
                    EndActivity();
                }
            }
        }

        if (failures.Count != 0)
        {
            throw new AggregateException(failures);
        }
    }

    private async Task ScanAsync()
    {
        using var timer = new PeriodicTimer(ScanInterval, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(_shutdown.Token).ConfigureAwait(false))
            {
                try
                {
                    await SweepAsync().ConfigureAwait(false);
                }
                // Close new admission, but retain automatic expiry ownership for unrelated handles.
                catch (Exception failure)
                {
                    RecordFailure(failure);
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
    }

    private Entry Find(string owner, EntityPreparedHandle handle)
    {
        ValidateRoute(owner, handle);
        if (!_entries.TryGetValue(handle.Id, out var entry) || entry.Owner != owner || entry.Handle != handle)
        {
            throw new InvalidOperationException("The prepared handle is unavailable for this owner and operation.");
        }
        return entry;
    }

    private static void ValidateOwner(string owner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        if (owner.Length > MaximumOwnerCharacters)
        {
            throw new ArgumentOutOfRangeException(nameof(owner));
        }
    }

    private void ValidateRoute(string owner, EntityPreparedHandle handle)
    {
        ValidateOwner(owner);
        ArgumentNullException.ThrowIfNull(handle);
        if (handle.Id == Guid.Empty || handle.DatabaseIncarnation != _database || handle.OperationId == Guid.Empty)
        {
            throw new InvalidOperationException("The prepared handle differs from its destination.");
        }
    }

    private void EnsureAvailable()
    {
        ObjectDisposedException.ThrowIf(_closing, this);
        if (_cleanupFailures.Count != 0)
        {
            throw new InvalidOperationException("Preparation cleanup failed; the host is closed to new work.", new AggregateException(_cleanupFailures));
        }
    }

    private void EndActivity()
    {
        if (--_active == 0 && _closing)
        {
            _drained.TrySetResult();
        }
    }

    private void RecordFailure(Exception failure)
    {
        IEnumerable<Exception> failures = failure is AggregateException aggregate ? aggregate.Flatten().InnerExceptions : [failure];
        lock (_sync)
        {
            foreach (var item in failures)
            {
                if (!_cleanupFailures.Contains(item))
                {
                    _cleanupFailures.Add(item);
                }
            }
        }
    }

    private async ValueTask CloseEntryAsync(Entry entry, bool charged = true)
    {
        Exception? failure = null;
        try
        {
            await entry.Write.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            failure = error;
            RecordFailure(error);
            throw;
        }
        finally
        {
            if (charged)
            {
                lock (_sync)
                {
                    _retiring--;
                }
            }
            if (failure is null)
            {
                entry.Released.TrySetResult();
            }
            else
            {
                entry.Released.TrySetException(failure);
                _ = entry.Released.Task.Exception;
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposal is not null)
            {
                return new(_disposal);
            }
            _closing = true;
            if (_active == 0)
            {
                _drained.TrySetResult();
            }
            return new(_disposal = CloseAsync());
        }
    }

    private async Task CloseAsync()
    {
        try
        {
            await _shutdown.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            RecordFailure(failure);
        }

        await _scanner.ConfigureAwait(false);
        await _drained.Task.ConfigureAwait(false);
        Entry[] remaining;
        lock (_sync)
        {
            remaining = _entries.Values.ToArray();
            _entries.Clear();
        }

        var failures = new List<Exception>();
        foreach (var entry in remaining)
        {
            try
            {
                await CloseEntryAsync(entry, false).ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                failures.Add(failure);
            }
        }
        _shutdown.Dispose();
        lock (_sync)
        {
            failures = _cleanupFailures.ToList();
        }
        if (failures.Count != 0)
        {
            throw new AggregateException(failures);
        }
    }

    private sealed class Entry(EntityPreparedHandle handle, string owner, TPrepared write)
    {
        internal EntityPreparedHandle Handle { get; } = handle;

        internal string Owner { get; } = owner;

        internal TPrepared Write { get; } = write;

        internal bool Active, Retiring;

        internal TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
