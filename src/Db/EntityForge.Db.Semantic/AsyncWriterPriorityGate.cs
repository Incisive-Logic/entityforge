namespace EntityForge.Db.Semantic;

/// <summary>Allows concurrent readers while giving a waiting writer priority over new admission.</summary>
internal sealed class AsyncWriterPriorityGate : IDisposable
{
    private readonly SemaphoreSlim _turnstile = new(1, 1);
    private readonly SemaphoreSlim _readerState = new(1, 1);
    private readonly SemaphoreSlim _resource = new(1, 1);
    private int _readers;

    public async ValueTask<IDisposable> EnterReadAsync(CancellationToken cancellationToken)
    {
        await _turnstile.WaitAsync(cancellationToken).ConfigureAwait(false);
        _turnstile.Release();
        await _readerState.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_readers == 0)
            {
                await _resource.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            _readers++;
            return new Lease(ExitRead);
        }
        finally
        {
            _readerState.Release();
        }
    }

    public async ValueTask<IDisposable> EnterWriteAsync(CancellationToken cancellationToken,
        Action? turnstileAcquired = null)
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

    // Never queue a writer behind retained readers. This is safe for checkpoint-only work
    // during enrollment, where a queued writer could obstruct those readers' own completion.
    public IDisposable? TryEnterWrite()
    {
        if (!_turnstile.Wait(0))
        {
            return null;
        }

        if (_resource.Wait(0))
        {
            return new Lease(ExitWrite);
        }

        _turnstile.Release();
        return null;
    }

    private void ExitRead()
    {
        _readerState.Wait();
        try
        {
            _readers--;
            if (_readers == 0)
            {
                _resource.Release();
            }
        }
        finally
        {
            _readerState.Release();
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
        _readerState.Dispose();
        _resource.Dispose();
    }

    private sealed class Lease(Action release) : IDisposable
    {
        private Action? _release = release;

        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
