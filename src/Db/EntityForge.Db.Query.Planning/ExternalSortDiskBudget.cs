namespace EntityForge.Db.Query.Planning;

internal sealed class ExternalSortDiskBudget(long maximumBytes)
{
    private readonly object _gate = new();
    private long _used;

    internal long BytesInUse
    {
        get
        {
            lock (_gate)
            {
                return _used;
            }
        }
    }

    internal void Reserve(long bytes)
    {
        lock (_gate)
        {
            if (bytes < 0 || maximumBytes < 1 || bytes > maximumBytes - _used)
            {
                throw new QuerySortBudgetException("Sort operators exceed their shared temporary-disk byte budget.");
            }
            _used += bytes;
        }
    }

    internal void Release(long bytes)
    {
        lock (_gate)
        {
            if (bytes < 0 || bytes > _used)
            {
                throw new InvalidOperationException("Invalid sort reservation release.");
            }
            _used -= bytes;
        }
    }
}
