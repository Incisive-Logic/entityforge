namespace EntityForge.Db.Prototypes.Pages;

public enum PrototypePageAccessKind
{
    Normal = 0,
    SequentialScan = 1
}

public interface IPrototypePageSource
{
    ValueTask<ReadOnlyMemory<byte>> ReadAsync(long pageId, int pageSize, CancellationToken cancellationToken);
}

public sealed record PrototypePageBufferPoolSnapshot(
    long Hits,
    long Misses,
    long Loads,
    long Evictions,
    long RejectedPinnedVictims,
    int ResidentPages,
    int PinnedPages,
    long ResidentBytes);

public sealed class PrototypePageLease : IDisposable
{
    private Action? _release;

    internal PrototypePageLease(ReadOnlyMemory<byte> page, bool wasCacheHit, Action release)
    {
        Page = page;
        WasCacheHit = wasCacheHit;
        _release = release;
    }

    public ReadOnlyMemory<byte> Page
    {
        get;
    }

    public bool WasCacheHit
    {
        get;
    }

    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}

public sealed class PrototypePageBufferPool
{
    private const byte MaximumUsageCount = 5;
    private const int MinimumFrameCount = 2;
    private readonly object _sync = new();
    private readonly IPrototypePageSource _source;
    private readonly int _pageSize;
    private readonly int _normalCapacity;
    private readonly int _scanCapacity;
    private readonly Dictionary<long, Frame> _normal = [];
    private readonly Dictionary<long, Frame> _scan = [];
    private readonly Dictionary<PageKey, Task<ReadOnlyMemory<byte>>> _inflight = [];
    private readonly List<long> _normalClock = [];
    private readonly Queue<long> _scanOrder = [];
    private int _clockHand;
    private long _hits;
    private long _misses;
    private long _loads;
    private long _evictions;
    private long _rejectedPinnedVictims;

    public PrototypePageBufferPool(IPrototypePageSource source, int pageSize, long byteBudget,
        int sequentialScanFrameCount)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        if (pageSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }

        if (byteBudget < (long)pageSize * MinimumFrameCount)
        {
            throw new ArgumentOutOfRangeException(nameof(byteBudget));
        }

        if (sequentialScanFrameCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequentialScanFrameCount));
        }

        int totalFrames = checked((int)(byteBudget / pageSize));
        _scanCapacity = Math.Min(sequentialScanFrameCount, totalFrames - 1);
        _normalCapacity = totalFrames - _scanCapacity;
        _pageSize = pageSize;
    }

    public async ValueTask<PrototypePageLease> AcquireAsync(long pageId,
        PrototypePageAccessKind accessKind = PrototypePageAccessKind.Normal,
        CancellationToken cancellationToken = default)
    {
        if (pageId < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageId));
        }

        Dictionary<long, Frame> cache = Cache(accessKind);
        Task<ReadOnlyMemory<byte>> load;
        var key = new PageKey(pageId, accessKind);
        lock (_sync)
        {
            if (cache.TryGetValue(pageId, out Frame? frame))
            {
                frame.PinCount++;
                frame.UsageCount = MaximumUsageCount;
                _hits++;
                return Lease(frame, accessKind, wasCacheHit: true);
            }
            _misses++;
            if (!_inflight.TryGetValue(key, out load!))
            {
                load = LoadAsync(pageId);
                _inflight.Add(key, load);
            }
        }

        ReadOnlyMemory<byte> page;
        try
        {
            page = await load.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lock (_sync)
            {
                if (_inflight.TryGetValue(key, out Task<ReadOnlyMemory<byte>>? current) && current == load)
                {
                    _inflight.Remove(key);
                }
            }

            throw;
        }

        lock (_sync)
        {
            _inflight.Remove(key);
            if (cache.TryGetValue(pageId, out Frame? winner))
            {
                winner.PinCount++;
                winner.UsageCount = MaximumUsageCount;
                return Lease(winner, accessKind, wasCacheHit: false);
            }
            EnsureCapacity(accessKind);
            var frame = new Frame(page, pinCount: 1, usageCount: MaximumUsageCount);
            cache.Add(pageId, frame);
            if (accessKind == PrototypePageAccessKind.Normal)
            {
                _normalClock.Add(pageId);
            }
            else
            {
                _scanOrder.Enqueue(pageId);
            }

            return Lease(frame, accessKind, wasCacheHit: false);
        }
    }

    public PrototypePageBufferPoolSnapshot Snapshot()
    {
        lock (_sync)
        {
            int resident = _normal.Count + _scan.Count;
            int pinned = _normal.Values.Concat(_scan.Values).Count(frame => frame.PinCount > 0);
            return new(_hits, _misses, _loads, _evictions, _rejectedPinnedVictims,
                resident, pinned, (long)resident * _pageSize);
        }
    }

    private async Task<ReadOnlyMemory<byte>> LoadAsync(long pageId)
    {
        ReadOnlyMemory<byte> page = await _source.ReadAsync(pageId, _pageSize, CancellationToken.None)
            .ConfigureAwait(false);
        if (page.Length != _pageSize)
        {
            throw new PrototypePageCorruptionException(
                $"Page {pageId} has length {page.Length}; expected {_pageSize}.");
        }

        lock (_sync)
        {
            _loads++;
        }

        return page;
    }

    private void EnsureCapacity(PrototypePageAccessKind accessKind)
    {
        if (accessKind == PrototypePageAccessKind.SequentialScan)
        {
            while (_scan.Count >= _scanCapacity)
            {
                long candidate = _scanOrder.Dequeue();
                if (!_scan.TryGetValue(candidate, out Frame? frame))
                {
                    continue;
                }

                if (frame.PinCount != 0)
                {
                    _rejectedPinnedVictims++;
                    _scanOrder.Enqueue(candidate);
                    if (_scan.Values.All(item => item.PinCount != 0))
                    {
                        throw new PrototypePageCapacityException("Every sequential-scan frame is pinned.");
                    }

                    continue;
                }
                _scan.Remove(candidate);
                _evictions++;
            }
            return;
        }

        int examined = 0;
        int maximumExaminations = Math.Max(_normalClock.Count * (MaximumUsageCount + 1), 1);
        while (_normal.Count >= _normalCapacity && examined++ < maximumExaminations)
        {
            if (_normalClock.Count == 0)
            {
                break;
            }

            if (_clockHand >= _normalClock.Count)
            {
                _clockHand = 0;
            }

            long candidate = _normalClock[_clockHand];
            if (!_normal.TryGetValue(candidate, out Frame? frame))
            {
                _normalClock.RemoveAt(_clockHand);
                continue;
            }
            if (frame.PinCount != 0)
            {
                _rejectedPinnedVictims++;
                _clockHand++;
                continue;
            }
            if (frame.UsageCount > 0)
            {
                frame.UsageCount--;
                _clockHand++;
                continue;
            }
            _normal.Remove(candidate);
            _normalClock.RemoveAt(_clockHand);
            _evictions++;
        }
        if (_normal.Count >= _normalCapacity)
        {
            throw new PrototypePageCapacityException("No unpinned normal buffer frame became evictable.");
        }
    }

    private PrototypePageLease Lease(Frame frame, PrototypePageAccessKind accessKind, bool wasCacheHit) =>
        new(frame.Page, wasCacheHit, () =>
        {
            lock (_sync)
            {
                if (frame.PinCount <= 0)
                {
                    throw new InvalidOperationException("A page lease was released twice.");
                }

                frame.PinCount--;
            }
        });

    private Dictionary<long, Frame> Cache(PrototypePageAccessKind accessKind) =>
        accessKind == PrototypePageAccessKind.Normal ? _normal : _scan;

    private sealed class Frame(ReadOnlyMemory<byte> page, int pinCount, byte usageCount)
    {
        public ReadOnlyMemory<byte> Page { get; } = page;

        public int PinCount { get; set; } = pinCount;

        public byte UsageCount { get; set; } = usageCount;
    }

    private readonly record struct PageKey(long PageId, PrototypePageAccessKind AccessKind);
}
