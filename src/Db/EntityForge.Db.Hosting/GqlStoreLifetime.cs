namespace EntityForge.Db.Hosting;

// A routing lease protects the store's lifetime, not its data snapshot.
internal sealed class GqlStoreLifetime
{
    private readonly object _gate = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _readers;
    private bool _closing;

    internal IDisposable Acquire()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            _readers++;
            return new Lease(this);
        }
    }

    internal Task CloseAsync()
    {
        lock (_gate)
        {
            _closing = true;
            if (_readers == 0)
            {
                _drained.TrySetResult();
            }
            return _drained.Task;
        }
    }

    private void Release()
    {
        lock (_gate)
        {
            if (--_readers == 0 && _closing)
            {
                _drained.TrySetResult();
            }
        }
    }

    private sealed class Lease(GqlStoreLifetime owner) : IDisposable
    {
        private GqlStoreLifetime? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }
}
