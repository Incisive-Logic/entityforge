namespace EntityForge.Db.Storage.Pages;

internal sealed class PhysicalPageLoadCoordinator(int maximumLoads)
{
    private readonly object _gate = new();
    private readonly Dictionary<PhysicalPageId, Task<byte[]>> _loads = [];

    private TaskCompletionSource _capacityChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool HasActiveLoads
    {
        get
        {
            lock (_gate)
            {
                return _loads.Count != 0;
            }
        }
    }

    public async ValueTask DrainAsync()
    {
        Task<byte[]>[] active;
        lock (_gate)
        {
            active = _loads.Values.ToArray();
        }

        if (active.Length == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(active).ConfigureAwait(false);
        }
        catch
        {
            // Load failures are delivered to their acquire callers. Disposal only needs every
            // underlying store operation to have reached a terminal state before closing it.
        }
    }

    public bool TryGetOrAdd(PhysicalPageId pageId, Func<Task<byte[]>> loadFactory,
        out Task<byte[]>? load, out Task capacityChanged)
    {
        lock (_gate)
        {
            capacityChanged = _capacityChanged.Task;
            if (_loads.TryGetValue(pageId, out load))
            {
                return true;
            }

            if (_loads.Count >= maximumLoads)
            {
                return false;
            }

            load = loadFactory();
            _loads.Add(pageId, load);
        }
        // Register after releasing the gate: an already-completed task may run this inline.
        Task<byte[]> started = load;
        _ = started.ContinueWith(_ => RemoveCompleted(pageId, started), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return true;
    }

    private void RemoveCompleted(PhysicalPageId pageId, Task<byte[]> load)
    {
        lock (_gate)
        {
            if (!_loads.TryGetValue(pageId, out Task<byte[]>? current) || !ReferenceEquals(current, load))
            {
                return;
            }

            _loads.Remove(pageId);
            // Waiters keep the old signal. Install its replacement before waking them so
            // a caller that still finds admission full can wait for the next completion.
            TaskCompletionSource availableCapacity = _capacityChanged;
            _capacityChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
            availableCapacity.TrySetResult();
        }
    }
}
