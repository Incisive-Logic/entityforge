namespace EntityForge.Db.Storage.Pages;

/// <summary>Keeps snapshot ownership alive until every already-admitted asynchronous read drains.</summary>
internal sealed class SnapshotOperationGate(Action release)
{
    private readonly object _gate = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _active;
    private bool _closing;
    private bool _released;

    public IDisposable Enter(object owner)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closing, owner);
            _active++;
            return new Operation(this);
        }
    }

    public void Close()
    {
        bool releaseNow;
        lock (_gate)
        {
            _closing = true;
            releaseNow = _active == 0 && !_released;
            if (releaseNow)
            {
                _released = true;
            }
        }
        if (releaseNow)
        {
            CompleteRelease();
        }
    }

    public async ValueTask CloseAsync()
    {
        Close();
        await _drained.Task.ConfigureAwait(false);
    }

    private void Exit()
    {
        bool releaseNow;
        lock (_gate)
        {
            _active--;
            releaseNow = _closing && _active == 0 && !_released;
            if (releaseNow)
            {
                _released = true;
            }
        }
        if (releaseNow)
        {
            CompleteRelease();
        }
    }

    private void CompleteRelease()
    {
        try
        {
            release();
            _drained.TrySetResult();
        }
        catch (Exception exception)
        {
            _drained.TrySetException(exception);
            throw;
        }
    }

    private sealed class Operation(SnapshotOperationGate owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Exit();
            }
        }
    }
}
