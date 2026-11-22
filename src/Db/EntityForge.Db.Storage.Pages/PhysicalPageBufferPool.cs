using System.Diagnostics;

namespace EntityForge.Db.Storage.Pages;

internal sealed class PhysicalPageBufferPool : IAsyncDisposable
{
    private const int MinimumFrames = 2;
    private const int EvictionWritebackBatchPages = 8;
    private readonly object _gate = new();
    private readonly IPhysicalPageStore _store;
    private readonly PhysicalPageTelemetry _telemetry;
    private readonly bool _ownsTelemetry;
    private readonly PhysicalPageFrameCache _frames;
    private readonly PhysicalPageLoadCoordinator _loads;
    private long _hits, _misses, _evictions;
    private long _firstUnavailablePageId = long.MaxValue;
    private bool _closing, _disposed;

    public PhysicalPageBufferPool(IPhysicalPageStore store, long byteBudget, int scanFrames = 8,
        PhysicalPageTelemetry? telemetry = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        if (byteBudget < (long)store.PageSize * MinimumFrames)
        {
            throw new ArgumentOutOfRangeException(nameof(byteBudget));
        }

        if (scanFrames <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(scanFrames));
        }

        int frames = checked((int)(byteBudget / store.PageSize));
        int scanCapacity = Math.Min(scanFrames, frames - 1);
        _frames = new(frames - scanCapacity, scanCapacity);
        _loads = new(Math.Min(frames, 32));
        _telemetry = telemetry ?? new();
        _ownsTelemetry = telemetry is null;
    }

    public async ValueTask<PhysicalPageLease> AcquireAsync(PhysicalPageId pageId,
        PageAccessClass access = PageAccessClass.Normal, CancellationToken cancellationToken = default)
    {
        Task<byte[]>? load = null;
        PhysicalPageFrame? cached = null;
        long admissionStarted = Stopwatch.GetTimestamp();
        bool waited = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task capacityChanged;
            lock (_gate)
            {
                ThrowIfUnavailable();
                ThrowIfOutsideAvailableRange(pageId);
                if (_frames.TryGet(pageId, access, out cached))
                {
                    _frames.Pin(cached!);
                    _hits++;
                    _telemetry.Hit();
                    break;
                }
                if (_loads.TryGetOrAdd(pageId, () => LoadAsync(pageId), out load, out capacityChanged))
                {
                    _misses++;
                    _telemetry.Miss();
                    break;
                }
            }
            waited = true;
            await capacityChanged.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        if (waited)
        {
            _telemetry.LoadAdmissionWait(Stopwatch.GetElapsedTime(admissionStarted));
        }

        if (cached is not null)
        {
            await AcquireLatchAsync(cached, cancellationToken).ConfigureAwait(false);
            return Lease(cached, true);
        }
        byte[] image = await load!.WaitAsync(cancellationToken).ConfigureAwait(false);
        PhysicalPageFrame frame;
        lock (_gate)
        {
            ThrowIfUnavailable();
            ThrowIfOutsideAvailableRange(pageId);
            // Another waiter may have installed the shared read while this caller resumed.
            if (_frames.TryGet(pageId, access, out frame!))
            {
                _frames.Pin(frame);
            }
            else
            {
                EnsureCapacity(access);
                frame = _frames.Add(pageId, image, access);
            }
        }
        await AcquireLatchAsync(frame, cancellationToken).ConfigureAwait(false);
        return Lease(frame, false);
    }

    /// <summary>
    /// Acquires a page frame, flushing WAL-safe dirty frames when the bounded pool has no
    /// clean victim available.
    /// </summary>
    public async ValueTask<PhysicalPageLease> AcquireWithDirtyFlushAsync(PhysicalPageId pageId,
        long durableWalPosition, PageAccessClass access = PageAccessClass.Normal,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await AcquireAsync(pageId, access, cancellationToken).ConfigureAwait(false);
        }
        catch (PhysicalPageCapacityException)
        {
            await FlushDirtyAsync(durableWalPosition, EvictionWritebackBatchPages, true,
                cancellationToken).ConfigureAwait(false);
            return await AcquireAsync(pageId, access, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Acquires a coherently cached frame for a complete replacement image without reading the page's
    /// former ownership. This is required when a proven-free interior page is assigned a new generation.
    /// </summary>
    public async ValueTask<PhysicalPageLease> AcquireForOverwriteWithDirtyFlushAsync(PhysicalPageId pageId,
        ReadOnlyMemory<byte> replacement, long durableWalPosition,
        CancellationToken cancellationToken = default)
    {
        if (replacement.Length != _store.PageSize)
        {
            throw new ArgumentException("A replacement must contain exactly one physical page.",
                nameof(replacement));
        }

        SlottedDataPage.Validate(replacement.Span, pageId);
        try
        {
            return await AcquireForOverwriteAsync(pageId, replacement, cancellationToken).ConfigureAwait(false);
        }
        catch (PhysicalPageCapacityException)
        {
            await FlushDirtyAsync(durableWalPosition, EvictionWritebackBatchPages, true,
                cancellationToken).ConfigureAwait(false);
            return await AcquireForOverwriteAsync(pageId, replacement, cancellationToken).ConfigureAwait(false);
        }
    }

    internal async ValueTask<PhysicalPageLease?> TryAcquireCachedForOverwriteAsync(
        PhysicalPageId pageId, ReadOnlyMemory<byte> replacement,
        CancellationToken cancellationToken = default)
    {
        PhysicalPageFrame? frame;
        lock (_gate)
        {
            ThrowIfUnavailable();
            ThrowIfOutsideAvailableRange(pageId);
            if (!_frames.TryGet(pageId, PageAccessClass.Normal, out frame))
            {
                return null;
            }

            _frames.Pin(frame!);
            _hits++;
            _telemetry.Hit();
        }
        await AcquireLatchAsync(frame!, cancellationToken).ConfigureAwait(false);
        PhysicalPageLease lease = Lease(frame!, true);
        replacement.CopyTo(lease.Page);
        return lease;
    }

    internal ValueTask WriteCheckpointPagesAsync(CancellationToken token) =>
        FlushDirtyAsync(long.MaxValue, int.MaxValue, false, token, durable: false);

    internal ValueTask WriteDirtyPagesAsync(int maximumPages, CancellationToken token) =>
        FlushDirtyAsync(long.MaxValue, maximumPages, false, token, durable: false);

    public async ValueTask FlushDirtyAsync(long durableWalPosition, CancellationToken cancellationToken = default)
        => await FlushDirtyAsync(durableWalPosition, int.MaxValue, false, cancellationToken)
            .ConfigureAwait(false);

    private async ValueTask FlushDirtyAsync(long durableWalPosition, int maximumPages,
        bool evictionTriggered, CancellationToken cancellationToken, bool durable = true)
    {
        PhysicalPageFrame[] dirty;
        lock (_gate)
        {
            ThrowIfDisposed();
            dirty = _frames.Frames.Where(frame => frame.Dirty).Take(maximumPages).ToArray();
        }
        // Keep the same bounded selection, but submit adjacent physical pages
        // together so advisory writeback can coalesce them into contiguous ranges.
        // Latches, pins and WAL dependencies are still checked for each write.
        Array.Sort(dirty, static (left, right) => left.Id.Value.CompareTo(right.Id.Value));
        long writebackStarted = Stopwatch.GetTimestamp();
        int written = 0;
        foreach (PhysicalPageFrame frame in dirty)
        {
            bool latchHeld = false;
            bool pinned = false;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    if (!frame.Dirty)
                    {
                        continue;
                    }

                    _frames.Pin(frame);
                    pinned = true;
                }
                await frame.Latch.WaitAsync(cancellationToken).ConfigureAwait(false);
                latchHeld = true;
                if (!frame.Dirty)
                {
                    continue;
                }

                if (frame.RequiredWalPosition > durableWalPosition)
                {
                    throw new PhysicalPageWalDependencyException(frame.RequiredWalPosition, durableWalPosition);
                }

                long started = Stopwatch.GetTimestamp();
                await _store.WriteAsync(frame.Id, frame.Image, cancellationToken).ConfigureAwait(false);
                _telemetry.Write(frame.Image.Length, Stopwatch.GetElapsedTime(started));
                frame.Dirty = false;
                written++;
            }
            finally
            {
                if (latchHeld)
                {
                    frame.Latch.Release();
                }

                if (pinned)
                {
                    lock (_gate)
                    {
                        frame.PinCount--;
                    }
                }
            }
        }
        if (durable)
        {
            await _store.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        if (evictionTriggered)
        {
            _telemetry.EvictionWriteback(written, Stopwatch.GetElapsedTime(writebackStarted));
        }
    }

    public PhysicalPageBufferPoolSnapshot Snapshot()
    {
        lock (_gate)
        {
            ThrowIfUnavailable();
            PhysicalPageFrame[] frames = _frames.Frames.ToArray();
            int pins = frames.Count(frame => frame.PinCount != 0), dirty = frames.Count(frame => frame.Dirty);
            _telemetry.Snapshot(pins, dirty);
            return new(frames.Length, pins, dirty, (long)frames.Length * _store.PageSize, _hits, _misses, _evictions);
        }
    }

    internal void InvalidateFrom(long firstInvalidPageId)
    {
        lock (_gate)
        {
            ThrowIfUnavailable();
            if (_loads.HasActiveLoads)
            {
                throw new PhysicalPageLifecycleException(
                    "The buffer pool cannot be invalidated while physical page loads are active.");
            }

            PhysicalPageFrame[] invalid = _frames.Frames
                .Where(frame => frame.Id.Value >= firstInvalidPageId).ToArray();
            if (invalid.Any(frame => frame.PinCount != 0 || frame.Dirty))
            {
                throw new PhysicalPageLifecycleException(
                    "The buffer pool cannot invalidate pinned or dirty page frames.");
            }

            _frames.InvalidateFrom(firstInvalidPageId);
        }
    }

    internal async ValueTask CloseRangeAsync(long firstInvalidPageId,
        CancellationToken cancellationToken)
    {
        long priorLimit;
        lock (_gate)
        {
            ThrowIfUnavailable();
            priorLimit = _firstUnavailablePageId;
            _firstUnavailablePageId = Math.Min(_firstUnavailablePageId, firstInvalidPageId);
        }
        try
        {
            await _loads.DrainAsync().AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);
            InvalidateFrom(firstInvalidPageId);
        }
        catch
        {
            lock (_gate)
            {
                _firstUnavailablePageId = priorLimit;
            }

            throw;
        }
    }

    internal void SetAvailablePageCount(long pageCount)
    {
        lock (_gate)
        {
            ThrowIfUnavailable();
            _firstUnavailablePageId = pageCount;
        }
    }

    private async Task<byte[]> LoadAsync(PhysicalPageId id)
    {
        _telemetry.LoadBytes(_store.PageSize);
        try
        {
            long started = Stopwatch.GetTimestamp();
            byte[] image = _store is IPhysicalPageOwnedReader owned
                ? await owned.ReadOwnedAsync(id, CancellationToken.None).ConfigureAwait(false)
                : (await _store.ReadAsync(id, CancellationToken.None).ConfigureAwait(false)).ToArray();
            SlottedDataPage.Validate(image, id);
            _telemetry.Read(image.Length, Stopwatch.GetElapsedTime(started));
            return image;
        }
        catch (PhysicalPageCorruptionException)
        {
            _telemetry.Corruption();
            throw;
        }
        finally
        {
            _telemetry.LoadBytes(-_store.PageSize);
        }
    }

    private async ValueTask<PhysicalPageLease> AcquireForOverwriteAsync(PhysicalPageId pageId,
        ReadOnlyMemory<byte> replacement, CancellationToken cancellationToken)
    {
        PhysicalPageFrame frame;
        bool cached;
        lock (_gate)
        {
            ThrowIfUnavailable();
            ThrowIfOutsideAvailableRange(pageId);
            cached = _frames.TryGet(pageId, PageAccessClass.Normal, out frame!);
            if (cached)
            {
                _frames.Pin(frame);
                _hits++;
                _telemetry.Hit();
            }
            else
            {
                byte[] image = EnsureCapacity(PageAccessClass.Normal)
                    ?? GC.AllocateUninitializedArray<byte>(_store.PageSize);
                replacement.CopyTo(image);
                frame = _frames.Add(pageId, image, PageAccessClass.Normal, canRecycleImage: true);
                _misses++;
                _telemetry.Miss();
            }
        }
        await AcquireLatchAsync(frame, cancellationToken).ConfigureAwait(false);
        PhysicalPageLease lease = Lease(frame, cached);
        if (cached)
        {
            replacement.CopyTo(lease.Page);
        }

        return lease;
    }

    private byte[]? EnsureCapacity(PageAccessClass access)
    {
        byte[]? reusableImage = null;
        while (_frames.IsAtCapacity(access))
        {
            if (!_frames.TryEvict(access, out reusableImage))
            {
                throw new PhysicalPageCapacityException("No clean unpinned page frame is evictable.");
            }

            _evictions++;
            _telemetry.Eviction();
        }
        return reusableImage;
    }

    private async ValueTask AcquireLatchAsync(PhysicalPageFrame frame, CancellationToken cancellationToken)
    {
        long started = _telemetry.ObserveLatchWait ? Stopwatch.GetTimestamp() : 0;
        bool contended = false;
        try
        {
            Task wait = frame.Latch.WaitAsync(cancellationToken);
            contended = !wait.IsCompleted;
            await wait.ConfigureAwait(false);
        }
        catch
        {
            lock (_gate)
            {
                frame.PinCount--;
            }

            throw;
        }
        finally
        {
            if (contended && started != 0)
            {
                _telemetry.LatchWait(started);
            }
        }
    }

    private PhysicalPageLease Lease(PhysicalPageFrame frame, bool hit) => new(frame.Image, hit,
        lsn =>
        {
            lock (_gate)
            {
                frame.Dirty = true;
                frame.RequiredWalPosition = Math.Max(frame.RequiredWalPosition, lsn);
            }
        },
        () =>
        {
            lock (_gate)
            {
                frame.PinCount--;
                frame.Latch.Release();
            }

            return ValueTask.CompletedTask;
        });

    private void ThrowIfUnavailable() => ObjectDisposedException.ThrowIf(_closing || _disposed, this);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private void ThrowIfOutsideAvailableRange(PhysicalPageId pageId)
    {
        if (pageId.Value >= _firstUnavailablePageId)
        {
            throw new PhysicalPageLifecycleException(
                $"Physical page {pageId.Value} is outside the currently available page range.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (_closing)
            {
                throw new PhysicalPageLifecycleException("The buffer pool is already closing.");
            }

            _closing = true;
        }
        try
        {
            await _loads.DrainAsync().ConfigureAwait(false);
            lock (_gate)
            {
                if (_frames.Frames.Any(frame => frame.PinCount != 0))
                {
                    throw new PhysicalPageLifecycleException(
                        "The buffer pool cannot be disposed while page leases are active.");
                }
            }

            await FlushDirtyAsync(long.MaxValue).ConfigureAwait(false);
            lock (_gate)
            {
                if (_loads.HasActiveLoads || _frames.Frames.Any(frame => frame.PinCount != 0 || frame.Dirty))
                {
                    throw new PhysicalPageLifecycleException(
                        "The buffer pool became active while it was closing.");
                }

                _disposed = true;
                _frames.Dispose();
            }
            await _store.DisposeAsync().ConfigureAwait(false);
            if (_ownsTelemetry)
            {
                _telemetry.Dispose();
            }
        }
        catch
        {
            lock (_gate)
            {
                if (!_disposed)
                {
                    _closing = false;
                }
            }

            throw;
        }
    }
}

internal interface IPhysicalPageOwnedReader
{
    ValueTask<byte[]> ReadOwnedAsync(PhysicalPageId pageId, CancellationToken cancellationToken);
}
