using System.Text.Json;

namespace EntityForge.Db.Prototypes.Pages;

public sealed record BufferPoolExperimentOptions(
    IReadOnlyList<int> PageSizes,
    long CacheByteBudget,
    int SequentialScanFrameCount,
    int DatasetPageCount,
    int HotPageCount,
    int WarmupPassCount,
    int MeasurementPassCount)
{
    public static BufferPoolExperimentOptions Default
    {
        get;
    }
    = new(
        PageSizes: [8 * 1024, 16 * 1024, 32 * 1024],
        CacheByteBudget: 8L * 1024 * 1024,
        SequentialScanFrameCount: 8,
        DatasetPageCount: 4_096,
        HotPageCount: 384,
        WarmupPassCount: 2,
        MeasurementPassCount: 4);
}

public sealed record BufferPoolPageSizeResult(
    int PageSize,
    int CacheFrameCount,
    int HotPageCount,
    double PreScanHotHitRate,
    double PostScanHotHitRate,
    long SourceReads,
    long CacheHits,
    long CacheMisses,
    long Loads,
    long Evictions,
    long ResidentBytes);

public sealed record BufferPoolExperimentResult(int SchemaVersion,
    IReadOnlyList<BufferPoolPageSizeResult> Results)
{
    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
}

public sealed class BufferPoolExperiment
{
    public const int ResultSchemaVersion = 1;

    public async Task<BufferPoolExperimentResult> RunAsync(BufferPoolExperimentOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= BufferPoolExperimentOptions.Default;
        Validate(options);
        var results = new List<BufferPoolPageSizeResult>();
        foreach (int pageSize in options.PageSizes)
        {
            results.Add(await RunPageSizeAsync(options, pageSize, cancellationToken).ConfigureAwait(false));
        }

        return new(ResultSchemaVersion, results);
    }

    private static async Task<BufferPoolPageSizeResult> RunPageSizeAsync(BufferPoolExperimentOptions options,
        int pageSize, CancellationToken cancellationToken)
    {
        var source = new DeterministicPrototypePageSource();
        var pool = new PrototypePageBufferPool(source, pageSize, options.CacheByteBudget,
            options.SequentialScanFrameCount);
        for (int pass = 0; pass < options.WarmupPassCount; pass++)
        {
            await ReadRangeAsync(pool, options.HotPageCount, PrototypePageAccessKind.Normal, cancellationToken)
                .ConfigureAwait(false);
        }

        double preScan = await HitRateAsync(pool, options.HotPageCount, options.MeasurementPassCount,
            cancellationToken).ConfigureAwait(false);
        await ReadRangeAsync(pool, options.DatasetPageCount, PrototypePageAccessKind.SequentialScan,
            cancellationToken).ConfigureAwait(false);
        double postScan = await HitRateAsync(pool, options.HotPageCount, options.MeasurementPassCount,
            cancellationToken).ConfigureAwait(false);
        PrototypePageBufferPoolSnapshot snapshot = pool.Snapshot();
        return new(pageSize, checked((int)(options.CacheByteBudget / pageSize)), options.HotPageCount,
            preScan, postScan, source.ReadCount, snapshot.Hits, snapshot.Misses, snapshot.Loads,
            snapshot.Evictions, snapshot.ResidentBytes);
    }

    private static async Task<double> HitRateAsync(PrototypePageBufferPool pool, int pageCount, int passCount,
        CancellationToken cancellationToken)
    {
        int hits = 0;
        int reads = 0;
        for (int pass = 0; pass < passCount; pass++)
        {
            for (int pageId = 0; pageId < pageCount; pageId++)
            {
                using PrototypePageLease lease = await pool.AcquireAsync(pageId,
                    PrototypePageAccessKind.Normal, cancellationToken).ConfigureAwait(false);
                if (lease.WasCacheHit)
                {
                    hits++;
                }

                reads++;
            }
        }

        return reads == 0 ? 0 : (double)hits / reads;
    }

    private static async Task ReadRangeAsync(PrototypePageBufferPool pool, int pageCount,
        PrototypePageAccessKind accessKind, CancellationToken cancellationToken)
    {
        for (int pageId = 0; pageId < pageCount; pageId++)
        {
            using PrototypePageLease lease = await pool.AcquireAsync(pageId, accessKind, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static void Validate(BufferPoolExperimentOptions options)
    {
        if (options.PageSizes.Count == 0 || options.PageSizes.Any(size => size <= 0))
        {
            throw new ArgumentOutOfRangeException(nameof(options.PageSizes));
        }

        if (options.CacheByteBudget <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options.CacheByteBudget));
        }

        if (options.SequentialScanFrameCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options.SequentialScanFrameCount));
        }

        if (options.DatasetPageCount <= options.HotPageCount || options.HotPageCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options.DatasetPageCount));
        }

        if (options.WarmupPassCount <= 0 || options.MeasurementPassCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options.WarmupPassCount));
        }
    }
}

public sealed class DeterministicPrototypePageSource : IPrototypePageSource
{
    private long _readCount;

    public long ReadCount => Interlocked.Read(ref _readCount);

    public ValueTask<ReadOnlyMemory<byte>> ReadAsync(long pageId, int pageSize,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _readCount);
        var page = new byte[pageSize];
        page.AsSpan().Fill(checked((byte)(pageId % (byte.MaxValue + 1))));
        return ValueTask.FromResult<ReadOnlyMemory<byte>>(page);
    }
}
