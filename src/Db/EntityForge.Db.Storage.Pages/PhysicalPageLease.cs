namespace EntityForge.Db.Storage.Pages;

/// <summary>Captures buffer-pool occupancy and cumulative cache activity at one observation.</summary>
/// <param name="ResidentPages">Number of resident page frames.</param>
/// <param name="PinnedPages">Number of resident frames with at least one active pin.</param>
/// <param name="DirtyPages">Number of resident frames awaiting writeback.</param>
/// <param name="ResidentBytes">Resident frame bytes, excluding other managed and operating-system cache memory.</param>
/// <param name="Hits">Cumulative cache hits for this pool.</param>
/// <param name="Misses">Cumulative cache misses for this pool.</param>
/// <param name="Evictions">Cumulative frame evictions for this pool.</param>
public sealed record PhysicalPageBufferPoolSnapshot(int ResidentPages, int PinnedPages, int DirtyPages,
    long ResidentBytes, long Hits, long Misses, long Evictions);

internal sealed class PhysicalPageLease : IAsyncDisposable
{
    private Func<ValueTask>? _release;
    private readonly Action<long> _markDirty;

    internal PhysicalPageLease(Memory<byte> page, bool cacheHit, Action<long> markDirty, Func<ValueTask> release)
    {
        Page = page;
        WasCacheHit = cacheHit;
        _markDirty = markDirty;
        _release = release;
    }

    public Memory<byte> Page
    {
        get;
    }

    public bool WasCacheHit
    {
        get;
    }

    public void MarkDirty(long requiredWalPosition)
    {
        if (requiredWalPosition < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(requiredWalPosition));
        }

        ObjectDisposedException.ThrowIf(Volatile.Read(ref _release) is null, this);
        _markDirty(requiredWalPosition);
    }

    public ValueTask DisposeAsync() =>
        Interlocked.Exchange(ref _release, null)?.Invoke() ?? ValueTask.CompletedTask;
}
