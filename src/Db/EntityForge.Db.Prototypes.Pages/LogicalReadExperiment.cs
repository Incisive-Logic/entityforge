using System.Text.Json;

namespace EntityForge.Db.Prototypes.Pages;

public sealed record LogicalReadExperimentOptions(
    int Seed,
    IReadOnlyList<int> PageSizes,
    int RecordCount,
    int QueryCount,
    int HotRecordPercentage,
    int HotQueryPercentage,
    int AbsoluteInlineThreshold,
    long CacheByteBudget,
    int SequentialScanFrameCount)
{
    public static LogicalReadExperimentOptions Default
    {
        get;
    }
    = new(
        Seed: 0x0E17F04,
        PageSizes: [8 * 1024, 16 * 1024, 32 * 1024],
        RecordCount: 50_000,
        QueryCount: 100_000,
        HotRecordPercentage: 20,
        HotQueryPercentage: 80,
        AbsoluteInlineThreshold: 4 * 1024,
        CacheByteBudget: 8L * 1024 * 1024,
        SequentialScanFrameCount: 8);
}

public sealed record LogicalReadPageSizeResult(
    int PageSize,
    int PrimaryPageCount,
    int OverflowPageCount,
    int CacheFrameCount,
    int QueryCount,
    long SourcePageReads,
    long PhysicalBytesRead,
    double PageReadsPerLogicalQuery,
    double PhysicalBytesPerLogicalQuery,
    double CacheHitRate,
    long Evictions);

public sealed record LogicalReadExperimentResult(int SchemaVersion, int Seed, int AbsoluteInlineThreshold,
    IReadOnlyList<LogicalReadPageSizeResult> Results)
{
    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
}

public sealed class LogicalReadExperiment
{
    public const int ResultSchemaVersion = 1;
    private const int OverflowReferenceSize = 16;
    private const int OverflowPageHeaderSize = 32;

    public async Task<LogicalReadExperimentResult> RunAsync(LogicalReadExperimentOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= LogicalReadExperimentOptions.Default;
        Validate(options);
        int[] lengths = RecordLengths(options);
        int[] queries = Queries(options);
        var results = new List<LogicalReadPageSizeResult>();
        foreach (int pageSize in options.PageSizes)
        {
            results.Add(await RunPageSizeAsync(options, pageSize, lengths, queries, cancellationToken)
                .ConfigureAwait(false));
        }

        return new(ResultSchemaVersion, options.Seed, options.AbsoluteInlineThreshold, results);
    }

    private static async Task<LogicalReadPageSizeResult> RunPageSizeAsync(LogicalReadExperimentOptions options,
        int pageSize, IReadOnlyList<int> lengths, IReadOnlyList<int> queries,
        CancellationToken cancellationToken)
    {
        PackedRecord[] records = Pack(pageSize, options.AbsoluteInlineThreshold, lengths,
            out int primaryPageCount, out int overflowPageCount);
        var source = new DeterministicPrototypePageSource();
        var pool = new PrototypePageBufferPool(source, pageSize, options.CacheByteBudget,
            options.SequentialScanFrameCount);
        foreach (int recordIndex in queries)
        {
            PackedRecord record = records[recordIndex];
            using PrototypePageLease primary = await pool.AcquireAsync(record.PrimaryPageId,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            for (int overflowIndex = 0; overflowIndex < record.OverflowPageCount; overflowIndex++)
            {
                using PrototypePageLease overflow = await pool.AcquireAsync(
                    primaryPageCount + record.FirstOverflowPage + overflowIndex,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }
        PrototypePageBufferPoolSnapshot snapshot = pool.Snapshot();
        long totalAccesses = snapshot.Hits + snapshot.Misses;
        return new(pageSize, primaryPageCount, overflowPageCount,
            checked((int)(options.CacheByteBudget / pageSize)), options.QueryCount, source.ReadCount,
            source.ReadCount * pageSize, (double)source.ReadCount / options.QueryCount,
            (double)(source.ReadCount * pageSize) / options.QueryCount,
            totalAccesses == 0 ? 0 : (double)snapshot.Hits / totalAccesses, snapshot.Evictions);
    }

    private static PackedRecord[] Pack(int pageSize, int inlineThreshold, IReadOnlyList<int> lengths,
        out int primaryPageCount, out int overflowPageCount)
    {
        var records = new PackedRecord[lengths.Count];
        var pages = new List<SlottedPrototypePage>();
        int nextOverflowPage = 0;
        int overflowPayload = pageSize - OverflowPageHeaderSize;
        for (int index = 0; index < lengths.Count; index++)
        {
            int logicalLength = lengths[index];
            bool isOverflow = logicalLength > inlineThreshold;
            int inlineLength = isOverflow ? OverflowReferenceSize : logicalLength;
            byte[] payload = new byte[inlineLength];
            SlottedPrototypePage? page = pages.LastOrDefault(candidate =>
                candidate.ContiguousFreeBytes >= inlineLength + SlottedPrototypePage.SlotSize);
            if (page is null)
            {
                page = new SlottedPrototypePage(pageSize, pages.Count);
                pages.Add(page);
            }
            if (!page.TryInsert(payload, out _))
            {
                throw new PrototypePageCapacityException($"Record {index} cannot fit page size {pageSize}.");
            }

            int overflowCount = isOverflow
                ? checked((int)((logicalLength + (long)overflowPayload - 1) / overflowPayload)) : 0;
            records[index] = new(page.PageId, nextOverflowPage, overflowCount);
            nextOverflowPage += overflowCount;
        }
        primaryPageCount = pages.Count;
        overflowPageCount = nextOverflowPage;
        return records;
    }

    private static int[] RecordLengths(LogicalReadExperimentOptions options)
    {
        var random = new Random(options.Seed);
        var lengths = new int[options.RecordCount];
        for (int index = 0; index < lengths.Length; index++)
        {
            lengths[index] = index % 100 == 0 ? random.Next(32 * 1024, (128 * 1024) + 1)
                : index % 10 == 0 ? random.Next(1024, 4096) : random.Next(24, 384);
        }

        return lengths;
    }

    private static int[] Queries(LogicalReadExperimentOptions options)
    {
        var random = new Random(options.Seed ^ options.QueryCount);
        int hotRecordCount = options.RecordCount * options.HotRecordPercentage / 100;
        var queries = new int[options.QueryCount];
        for (int index = 0; index < queries.Length; index++)
        {
            bool hot = random.Next(100) < options.HotQueryPercentage;
            queries[index] = hot ? random.Next(hotRecordCount) : random.Next(hotRecordCount, options.RecordCount);
        }
        return queries;
    }

    private static void Validate(LogicalReadExperimentOptions options)
    {
        if (options.PageSizes.Count == 0 || options.PageSizes.Any(size => size <= options.AbsoluteInlineThreshold))
        {
            throw new ArgumentOutOfRangeException(nameof(options.PageSizes));
        }

        if (options.RecordCount <= 0 || options.QueryCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options.RecordCount));
        }

        if (options.HotRecordPercentage is <= 0 or >= 100 || options.HotQueryPercentage is <= 0 or >= 100)
        {
            throw new ArgumentOutOfRangeException(nameof(options.HotRecordPercentage));
        }

        if (options.AbsoluteInlineThreshold <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options.AbsoluteInlineThreshold));
        }
    }

    private readonly record struct PackedRecord(long PrimaryPageId, int FirstOverflowPage,
        int OverflowPageCount);
}
