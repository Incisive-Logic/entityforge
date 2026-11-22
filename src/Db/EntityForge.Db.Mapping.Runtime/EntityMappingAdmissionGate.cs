namespace EntityForge.Db.Mapping;

/// <summary>Allows concurrent readers while giving a waiting writer priority over new admission.</summary>
internal sealed class EntityMappingAdmissionGate : IDisposable
{
    private readonly SemaphoreSlim _turnstile = new(1, 1);
    private readonly object _readerState = new();
    private readonly SemaphoreSlim _resource = new(1, 1);
    private int _readers;

    public async ValueTask<IDisposable> EnterReadAsync(CancellationToken cancellationToken)
    {
        await _turnstile.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Keep entry serialized until the reader is registered. Exit only holds a
            // synchronous bookkeeping lock; it never blocks on an async continuation.
            lock (_readerState)
            {
                if (_readers != 0)
                {
                    _readers++;
                    return new Lease(ExitRead);
                }
            }

            await _resource.WaitAsync(cancellationToken).ConfigureAwait(false);
            lock (_readerState)
            {
                _readers++;
            }
            return new Lease(ExitRead);
        }
        finally
        {
            _turnstile.Release();
        }
    }

    public async ValueTask<IDisposable> EnterWriteAsync(CancellationToken cancellationToken, Action? turnstileAcquired = null)
    {
        await _turnstile.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            turnstileAcquired?.Invoke();
            await _resource.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new Lease(ExitWrite);
        }
        catch
        {
            _turnstile.Release();
            throw;
        }
    }

    private void ExitRead()
    {
        lock (_readerState)
        {
            _readers--;
            if (_readers == 0)
            {
                _resource.Release();
            }
        }
    }

    private void ExitWrite()
    {
        _resource.Release();
        _turnstile.Release();
    }

    public void Dispose()
    {
        _turnstile.Dispose();
        _resource.Dispose();
    }

    private sealed class Lease(Action release) : IDisposable
    {
        private Action? _release = release;

        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
