namespace EntityForge.Db.Storage.Pages;

/// <summary>
/// Owns a resizable page file and routes selected-format reads and writes through a bounded,
/// latch-protected buffer pool.
/// </summary>
public sealed class BufferedPhysicalPageStore : IResizablePhysicalPageStore, IPhysicalPageTelemetrySource,
    IPhysicalPageRecoveryStore
{
    /// <summary>Default resident page-frame budget: 64 MiB, excluding other process and operating-system memory.</summary>
    public const long DefaultByteBudget = 64L * 1024 * 1024;
    private readonly IResizablePhysicalPageStore _store;
    private readonly PhysicalPageBufferPool _pool;
    private readonly PhysicalPageTelemetry _telemetry;
    private readonly SemaphoreSlim _lengthGate = new(1, 1);
    private long _initializedPageCount;
    private long _resizedPageCount;
    private long _bufferedWriteBytes;

    internal BufferedPhysicalPageStore(IResizablePhysicalPageStore store, long byteBudget,
        PhysicalPageTelemetry telemetry, long pageCount)
    {
        _store = store;
        _telemetry = telemetry;
        _pool = new(store, byteBudget, telemetry: telemetry);
        _pool.SetAvailablePageCount(pageCount);
        _initializedPageCount = pageCount;
        _resizedPageCount = pageCount;
    }

    /// <summary>Gets the underlying store's fixed page size in bytes.</summary>
    public int PageSize => _store.PageSize;

    /// <summary>Gets the owned physical file's image commitment.</summary>
    /// <remarks>Dirty buffered pages must be written before capturing the corresponding file image.</remarks>
    /// <exception cref="InvalidOperationException">The underlying store is not a physical page file.</exception>
    public EntityForge.Db.Storage.PhysicalImageCommitment ImageCommitment =>
        (_store as PhysicalPageFile)?.ImageCommitment ??
        throw new InvalidOperationException("Image commitments require the owned physical file.");

    PhysicalPageTelemetry IPhysicalPageTelemetrySource.Telemetry => _telemetry;

    /// <summary>Opens or creates a local page file and wraps it in an owned buffer pool.</summary>
    /// <param name="path">Underlying file path.</param>
    /// <param name="byteBudget">Resident frame budget, at least two pages; rounded down to a whole frame count.</param>
    /// <param name="cancellationToken">Cancels opening and reading the initial file length.</param>
    /// <returns>A buffered store that owns the file and must be disposed by the caller.</returns>
    /// <remarks>Checks initial file alignment, but does not validate every existing page on open.</remarks>
    public static async ValueTask<BufferedPhysicalPageStore> OpenLocalAsync(string path,
        long byteBudget = DefaultByteBudget, CancellationToken cancellationToken = default)
    {
        PhysicalPageFile file = await PhysicalPageFile.OpenAsync(path, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        try
        {
            long pageCount = await file.GetPageCountAsync(cancellationToken).ConfigureAwait(false);
            return new(file, byteBudget, new PhysicalPageTelemetry("buffer"), pageCount);
        }
        catch
        {
            await file.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Reads a page through the cache and returns an independently owned copy.</summary>
    /// <param name="pageId">Page to read.</param>
    /// <param name="cancellationToken">Cancels waiting for cache admission or underlying I/O.</param>
    /// <returns>Page bytes that remain valid after the internal lease is released.</returns>
    /// <remarks>Maintenance-scoped reads use sequential-scan admission. Admission may write dirty pages to make room.</remarks>
    public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(PhysicalPageId pageId,
        CancellationToken cancellationToken = default)
    {
        PageAccessClass access = PhysicalPageTelemetryContext.IsMaintenance
            ? PageAccessClass.SequentialScan
            : PageAccessClass.Normal;
        await using PhysicalPageLease lease = await _pool.AcquireWithDirtyFlushAsync(pageId, long.MaxValue,
            access, cancellationToken).ConfigureAwait(false);
        return lease.Page.ToArray();
    }

    internal PhysicalPageBufferPoolSnapshot BufferSnapshot() => _pool.Snapshot();

    internal async ValueTask<T> DecodeAsync<T>(PhysicalPageId id, Func<ReadOnlyMemory<byte>, T> decode,
        CancellationToken token)
    {
        PageAccessClass access = PhysicalPageTelemetryContext.IsMaintenance
            ? PageAccessClass.SequentialScan : PageAccessClass.Normal;
        await using PhysicalPageLease lease = await _pool.AcquireWithDirtyFlushAsync(id,
            long.MaxValue, access, token).ConfigureAwait(false);
        return decode(lease.Page);
    }

    /// <summary>Writes a page within the admitted file length, buffering or writing through as appropriate.</summary>
    /// <param name="pageId">Existing page or a page admitted by a completed capacity increase.</param>
    /// <param name="page">Exactly one page, unchanged until this operation completes.</param>
    /// <param name="cancellationToken">Cancels lock acquisition, cache admission or underlying I/O.</param>
    /// <remarks>The caller must establish required WAL durability first. This method may write pages to the device
    /// but does not establish a durable flush barrier. Cancellation does not roll back writes.</remarks>
    /// <exception cref="ArgumentException">The input length is not exactly one page.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The page lies beyond the admitted file length.</exception>
    public async ValueTask WriteAsync(PhysicalPageId pageId, ReadOnlyMemory<byte> page,
        CancellationToken cancellationToken = default)
    {
        if (page.Length != PageSize)
        {
            throw new ArgumentException("A buffered physical write must contain exactly one page.", nameof(page));
        }

        await _lengthGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (pageId.Value >= Volatile.Read(ref _resizedPageCount))
            {
                throw new ArgumentOutOfRangeException(nameof(pageId),
                    "The physical page has not been admitted by the current store length.");
            }

            await WriteWithinAdmittedLengthAsync(pageId, page, cancellationToken).ConfigureAwait(false);
            // Physical publication already follows durable WAL admission (new
            // pages can be written directly above). Spread WAL-safe OS writes
            // before checkpoint capture, without introducing a durable flush.
            if (_bufferedWriteBytes >= PhysicalPageWriteback.BatchBytes)
            {
                await _pool.WriteDirtyPagesAsync(
                    Math.Max(1, PhysicalPageWriteback.BatchBytes / PageSize), cancellationToken)
                    .ConfigureAwait(false);
                _bufferedWriteBytes = 0;
            }
        }
        finally
        {
            _lengthGate.Release();
        }
    }

    private async ValueTask WriteWithinAdmittedLengthAsync(PhysicalPageId pageId,
        ReadOnlyMemory<byte> page, CancellationToken cancellationToken)
    {
        if (pageId.Value >= Volatile.Read(ref _initializedPageCount))
        {
            PhysicalPageLease? cached = await _pool.TryAcquireCachedForOverwriteAsync(
                pageId, page, cancellationToken).ConfigureAwait(false);
            if (cached is null)
            {
                await _store.WriteAsync(pageId, page, cancellationToken).ConfigureAwait(false);
                return;
            }
            await using PhysicalPageLease cachedLease = cached;
            cachedLease.MarkDirty(0);
            _bufferedWriteBytes += PageSize;
            return;
        }
        await using PhysicalPageLease lease = await _pool.AcquireForOverwriteWithDirtyFlushAsync(
            pageId, page, long.MaxValue, cancellationToken).ConfigureAwait(false);
        lease.MarkDirty(0);
        _bufferedWriteBytes += PageSize;
    }

    /// <summary>Writes dirty buffered pages and durably flushes the underlying store.</summary>
    /// <param name="cancellationToken">Cancels waiting and cancellable I/O.</param>
    /// <remarks>Required WAL durability must already be established. This is not an independent transaction commit.</remarks>
    public async ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        await _lengthGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _pool.FlushDirtyAsync(long.MaxValue, cancellationToken).ConfigureAwait(false);
            _bufferedWriteBytes = 0;
            Volatile.Write(ref _initializedPageCount, Volatile.Read(ref _resizedPageCount));
        }
        finally
        {
            _lengthGate.Release();
        }
    }

    // Capture runs under kernel exclusion. Only OS writeback is required here;
    // the durable barrier runs after a new full-page WAL epoch protects writers.
    /// <summary>Writes buffered checkpoint pages without performing the durable flush barrier.</summary>
    /// <param name="token">Cancels waiting and cancellable page writes.</param>
    /// <remarks>Used under kernel write exclusion during checkpoint capture. The coordinator establishes the next
    /// full-page WAL protection epoch before the separate durable checkpoint flush.</remarks>
    public async ValueTask WriteCheckpointPagesAsync(CancellationToken token = default)
    {
        await _lengthGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await _pool.WriteCheckpointPagesAsync(token).ConfigureAwait(false);
            _bufferedWriteBytes = 0;
            Volatile.Write(ref _initializedPageCount, Volatile.Read(ref _resizedPageCount));
        }
        finally
        {
            _lengthGate.Release();
        }
    }

    /// <summary>Runs the underlying store's durable flush on a worker task.</summary>
    /// <param name="token">Cancels scheduling or cancellable work; does not interrupt an in-progress synchronous durability call.</param>
    /// <remarks>Does not first write dirty buffer frames. Intended for the coordinator's checkpoint protocol after page writeback.</remarks>
    public async ValueTask FlushCheckpointPagesAsync(CancellationToken token = default) =>
        await Task.Run(async () => await _store.FlushAsync(token).ConfigureAwait(false), token).ConfigureAwait(false);

    /// <summary>Gets the underlying file's aligned page count.</summary>
    /// <param name="cancellationToken">Cancellation forwarded to the underlying store.</param>
    /// <returns>The file page count, not the number of cached or initialized pages.</returns>
    public ValueTask<long> GetPageCountAsync(CancellationToken cancellationToken = default) =>
        _store.GetPageCountAsync(cancellationToken);

    /// <summary>Grows the underlying file when necessary and admits the added page range to the buffer pool.</summary>
    /// <param name="pageCount">Nonnegative minimum page count.</param>
    /// <param name="cancellationToken">Cancels waiting and underlying resize operations.</param>
    /// <remarks>Does not shrink, initialize encoded pages or durably flush the resize.</remarks>
    public async ValueTask EnsureCapacityAsync(long pageCount, CancellationToken cancellationToken = default)
    {
        if (pageCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageCount));
        }

        await _lengthGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            long current = await _store.GetPageCountAsync(cancellationToken).ConfigureAwait(false);
            if (current >= pageCount)
            {
                return;
            }

            await _store.EnsureCapacityAsync(pageCount, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _resizedPageCount, pageCount);
            _pool.SetAvailablePageCount(pageCount);
        }
        finally
        {
            _lengthGate.Release();
        }
    }

    /// <summary>Shrinks the file after flushing dirty pages and closing the removed cache range.</summary>
    /// <param name="pageCount">Nonnegative target count; counts at or above the current size leave it unchanged.</param>
    /// <param name="cancellationToken">Cancels waiting, flush work or underlying truncation.</param>
    /// <remarks>Callers coordinate page ownership and recovery safety. A failed truncation attempts to reconcile
    /// the cache with the actual file length; failure does not imply that the old length was preserved.</remarks>
    public async ValueTask TruncateAsync(long pageCount, CancellationToken cancellationToken = default)
    {
        if (pageCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageCount));
        }

        await _lengthGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            long current = await _store.GetPageCountAsync(cancellationToken).ConfigureAwait(false);
            if (pageCount >= current)
            {
                return;
            }

            await _pool.FlushDirtyAsync(long.MaxValue, cancellationToken).ConfigureAwait(false);
            await _pool.CloseRangeAsync(pageCount, cancellationToken).ConfigureAwait(false);
            try
            {
                await _store.TruncateAsync(pageCount, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                await ReconcileLengthAfterFailedTruncateAsync(current, failure).ConfigureAwait(false);
                throw;
            }
            Volatile.Write(ref _initializedPageCount, pageCount);
            Volatile.Write(ref _resizedPageCount, pageCount);
        }
        finally
        {
            _lengthGate.Release();
        }
    }

    private async ValueTask ReconcileLengthAfterFailedTruncateAsync(long priorPageCount, Exception failure)
    {
        try
        {
            long actual = await _store.GetPageCountAsync(CancellationToken.None).ConfigureAwait(false);
            Volatile.Write(ref _resizedPageCount, actual);
            Volatile.Write(ref _initializedPageCount,
                Math.Min(Volatile.Read(ref _initializedPageCount), actual));
            _pool.SetAvailablePageCount(actual);
        }
        catch (Exception reconciliationFailure)
        {
            throw new AggregateException(
                $"Physical truncation failed and the store length could not be reconciled from its prior length of {priorPageCount} pages.",
                failure, reconciliationFailure);
        }
    }

    /// <summary>Drains loads, flushes dirty pages, and disposes the owned buffer pool, file and telemetry.</summary>
    /// <remarks>Callers must finish active page operations before disposal and establish required WAL durability.
    /// Shutdown can fail when active leases remain or when durable page flushing fails.</remarks>
    public async ValueTask DisposeAsync()
    {
        await _pool.DisposeAsync().ConfigureAwait(false);
        _lengthGate.Dispose();
        _telemetry.Dispose();
    }

    ValueTask<ReadOnlyMemory<byte>> IPhysicalPageRecoveryStore.ReadRawAsync(PhysicalPageId pageId,
        CancellationToken cancellationToken) => _store.ReadAsync(pageId, cancellationToken);

    ValueTask IPhysicalPageRecoveryStore.WriteRawAsync(PhysicalPageId pageId,
        ReadOnlyMemory<byte> page, CancellationToken cancellationToken) =>
        _store.WriteAsync(pageId, page, cancellationToken);

    ValueTask IPhysicalPageRecoveryStore.FlushRawAsync(CancellationToken cancellationToken) =>
        _store.FlushAsync(cancellationToken);
}

internal interface IPhysicalPageRecoveryStore
{
    ValueTask<ReadOnlyMemory<byte>> ReadRawAsync(PhysicalPageId pageId, CancellationToken cancellationToken);

    ValueTask WriteRawAsync(PhysicalPageId pageId, ReadOnlyMemory<byte> page,
        CancellationToken cancellationToken);

    ValueTask FlushRawAsync(CancellationToken cancellationToken);
}
