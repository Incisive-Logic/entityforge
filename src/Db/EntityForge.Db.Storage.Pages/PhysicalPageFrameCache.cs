namespace EntityForge.Db.Storage.Pages;

internal sealed class PhysicalPageFrame : IDisposable
{
    public PhysicalPageFrame(PhysicalPageId id, byte[] image, bool canRecycleImage)
    {
        Id = id;
        Image = image;
        CanRecycleImage = canRecycleImage;
    }

    public PhysicalPageId Id
    {
        get;
    }

    public byte[] Image
    {
        get;
    }

    public bool CanRecycleImage
    {
        get;
    }

    public SemaphoreSlim Latch { get; } = new(1, 1);

    public int PinCount
    {
        get; set;
    }

    public byte Usage
    {
        get; set;
    }

    public bool Dirty
    {
        get; set;
    }

    public long RequiredWalPosition
    {
        get; set;
    }

    public LinkedListNode<PhysicalPageFrame>? ClockNode
    {
        get; set;
    }

    public void Dispose() => Latch.Dispose();
}

internal sealed class PhysicalPageFrameCache : IDisposable
{
    private const byte MaximumUsage = 5;
    private const int VictimSearchPasses = MaximumUsage + 2;
    private readonly int _normalCapacity;
    private readonly int _scanCapacity;
    private readonly Dictionary<PhysicalPageId, PhysicalPageFrame> _normal = [];
    private readonly Dictionary<PhysicalPageId, PhysicalPageFrame> _scan = [];
    private readonly LinkedList<PhysicalPageFrame> _clock = new();
    private readonly Queue<PhysicalPageId> _scanOrder = [];
    private LinkedListNode<PhysicalPageFrame>? _clockHand;

    public PhysicalPageFrameCache(int normalCapacity, int scanCapacity)
    {
        _normalCapacity = normalCapacity;
        _scanCapacity = scanCapacity;
    }

    public IEnumerable<PhysicalPageFrame> Frames => _normal.Values.Concat(_scan.Values);

    public bool TryGet(PhysicalPageId id, PageAccessClass access, out PhysicalPageFrame? frame)
    {
        if (Cache(access).TryGetValue(id, out frame))
        {
            return true;
        }

        return Cache(access is PageAccessClass.Normal ? PageAccessClass.SequentialScan : PageAccessClass.Normal)
            .TryGetValue(id, out frame);
    }

    public void Pin(PhysicalPageFrame frame)
    {
        frame.PinCount++;
        frame.Usage = MaximumUsage;
    }

    public PhysicalPageFrame Add(PhysicalPageId id, byte[] image, PageAccessClass access,
        bool canRecycleImage = false)
    {
        PhysicalPageFrame frame = new(id, image, canRecycleImage);
        Cache(access).Add(id, frame);
        if (access is PageAccessClass.Normal)
        {
            frame.ClockNode = _clock.AddLast(frame);
        }
        else
        {
            _scanOrder.Enqueue(id);
        }

        Pin(frame);
        return frame;
    }

    public bool TryEvict(PageAccessClass access, out byte[]? reusableImage)
    {
        reusableImage = null;
        Dictionary<PhysicalPageId, PhysicalPageFrame> cache = Cache(access);
        PhysicalPageFrame? victim = access is PageAccessClass.Normal ? NormalVictim() : ScanVictim();
        if (victim is null)
        {
            return false;
        }

        cache.Remove(victim.Id);
        if (access is PageAccessClass.Normal)
        {
            RemoveClockFrame(victim);
        }

        victim.Dispose();
        // Loaded images can still be referenced by coalesced load waiters. Only
        // overwrite-owned images become exclusively ours when the last pin leaves.
        if (victim.CanRecycleImage)
        {
            reusableImage = victim.Image;
        }

        return true;
    }

    public bool IsAtCapacity(PageAccessClass access) =>
        Cache(access).Count >= (access is PageAccessClass.Normal ? _normalCapacity : _scanCapacity);

    public void InvalidateFrom(long firstInvalidPageId)
    {
        PhysicalPageId[] normalIds = _normal.Keys.Where(id => id.Value >= firstInvalidPageId).ToArray();
        PhysicalPageId[] scanIds = _scan.Keys.Where(id => id.Value >= firstInvalidPageId).ToArray();
        foreach (PhysicalPageId id in normalIds)
        {
            _normal.Remove(id, out PhysicalPageFrame? frame);
            if (frame is not null)
            {
                RemoveClockFrame(frame);
            }

            frame?.Dispose();
        }
        foreach (PhysicalPageId id in scanIds)
        {
            _scan.Remove(id, out PhysicalPageFrame? frame);
            frame?.Dispose();
        }
        if (scanIds.Length != 0)
        {
            PhysicalPageId[] retained = _scanOrder.Where(id => id.Value < firstInvalidPageId).ToArray();
            _scanOrder.Clear();
            foreach (PhysicalPageId id in retained)
            {
                _scanOrder.Enqueue(id);
            }
        }
    }

    private void RemoveClockFrame(PhysicalPageFrame frame)
    {
        LinkedListNode<PhysicalPageFrame> node = frame.ClockNode!;
        if (_clockHand == node)
        {
            _clockHand = node.Next;
        }

        _clock.Remove(node);
        frame.ClockNode = null;
    }

    private PhysicalPageFrame? ScanVictim()
    {
        int examined = _scanOrder.Count;
        while (examined-- > 0)
        {
            PhysicalPageId id = _scanOrder.Dequeue();
            PhysicalPageFrame frame = _scan[id];
            if (frame.PinCount == 0 && !frame.Dirty)
            {
                return frame;
            }

            _scanOrder.Enqueue(id);
        }
        return null;
    }

    private PhysicalPageFrame? NormalVictim()
    {
        int examined = Math.Max(_clock.Count * VictimSearchPasses, 1);
        while (examined-- > 0 && _clock.Count != 0)
        {
            _clockHand ??= _clock.First;
            PhysicalPageFrame frame = _clockHand!.Value;
            if (frame.PinCount != 0 || frame.Dirty)
            {
                _clockHand = _clockHand.Next;
                continue;
            }
            if (frame.Usage != 0)
            {
                frame.Usage--;
                _clockHand = _clockHand.Next;
                continue;
            }
            return frame;
        }
        return null;
    }

    private Dictionary<PhysicalPageId, PhysicalPageFrame> Cache(PageAccessClass access) =>
        access is PageAccessClass.Normal ? _normal : _scan;

    public void Dispose()
    {
        foreach (PhysicalPageFrame frame in Frames)
        {
            frame.Dispose();
        }

        _normal.Clear();
        _scan.Clear();
        _clock.Clear();
        _clockHand = null;
        _scanOrder.Clear();
    }
}
