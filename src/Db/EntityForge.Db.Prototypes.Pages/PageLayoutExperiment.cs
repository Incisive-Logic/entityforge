using System.Text.Json;

namespace EntityForge.Db.Prototypes.Pages;

public sealed record PageLayoutExperimentOptions(
    int Seed,
    int RecordCount,
    IReadOnlyList<int> PageSizes,
    int DeletionInterval,
    int MaximumInlineValueDivisor)
{
    public static PageLayoutExperimentOptions Default
    {
        get;
    }
    = new(
        Seed: 0x0E17F04,
        RecordCount: 10_000,
        PageSizes: [8 * 1024, 16 * 1024, 32 * 1024],
        DeletionInterval: 7,
        MaximumInlineValueDivisor: 4);
}

public sealed record PageLayoutResult(
    int PageSize,
    int RecordCount,
    int PageCount,
    long AllocatedPageBytes,
    long OverflowAllocatedBytes,
    long TotalAllocatedBytes,
    long LogicalPayloadBytes,
    long InlinePayloadBytes,
    long OverflowPayloadBytes,
    int OverflowRecordCount,
    long FragmentedBytesBeforeCompaction,
    long FragmentedBytesAfterCompaction,
    double IngestStorageAmplification,
    double PostDeletionInlinePageUtilization,
    double AverageLiveRecordsPerPage);

public sealed record PageLayoutExperimentResult(
    int SchemaVersion,
    int Seed,
    IReadOnlyList<PageLayoutResult> Results)
{
    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
}

public sealed class PageLayoutExperiment
{
    public const int ResultSchemaVersion = 1;
    private const int OverflowReferenceSize = 16;
    private const int OverflowPageHeaderSize = 32;
    private const int MinimumRecordSize = 24;
    private const int TypicalRecordMaximumExclusive = 384;
    private const int MediumRecordMinimum = 1024;
    private const int MediumRecordMaximumExclusive = 4096;
    private const int LargeRecordMinimum = 32 * 1024;
    private const int LargeRecordMaximumExclusive = (128 * 1024) + 1;
    private const int MediumRecordFrequency = 10;
    private const int LargeRecordFrequency = 100;

    public PageLayoutExperimentResult Run(PageLayoutExperimentOptions? options = null)
    {
        options ??= PageLayoutExperimentOptions.Default;
        Validate(options);
        var results = options.PageSizes.Select(pageSize => RunPageSize(options, pageSize)).ToArray();
        return new(ResultSchemaVersion, options.Seed, results);
    }

    private static PageLayoutResult RunPageSize(PageLayoutExperimentOptions options, int pageSize)
    {
        var random = new Random(options.Seed);
        var pages = new List<SlottedPrototypePage>();
        long inlineBytes = 0;
        long overflowBytes = 0;
        long overflowAllocatedBytes = 0;
        long logicalPayloadBytes = 0;
        int overflowRecords = 0;
        int maximumInlineValue = pageSize / options.MaximumInlineValueDivisor;

        for (int recordIndex = 0; recordIndex < options.RecordCount; recordIndex++)
        {
            int logicalLength = RecordLength(random, recordIndex);
            logicalPayloadBytes += logicalLength;
            bool overflow = logicalLength > maximumInlineValue;
            int inlineLength = overflow ? OverflowReferenceSize : logicalLength;
            byte[] payload = Payload(inlineLength, recordIndex);
            SlottedPrototypePage page = pages.LastOrDefault(candidate =>
                candidate.ContiguousFreeBytes >= inlineLength + SlottedPrototypePage.SlotSize)
                ?? NewPage(pages, pageSize);
            if (!page.TryInsert(payload, out _))
            {
                page = NewPage(pages, pageSize);
                if (!page.TryInsert(payload, out _))
                {
                    throw new PrototypePageCapacityException($"Record {recordIndex} cannot fit page size {pageSize}.");
                }
            }
            inlineBytes += inlineLength;
            if (overflow)
            {
                overflowRecords++;
                overflowBytes += logicalLength;
                int overflowPagePayload = pageSize - OverflowPageHeaderSize;
                long overflowPageCount = (logicalLength + (long)overflowPagePayload - 1) / overflowPagePayload;
                overflowAllocatedBytes += overflowPageCount * pageSize;
            }
        }

        foreach (SlottedPrototypePage page in pages)
        {
            ushort[] deletedSlots = Enumerable.Range(0, page.SlotCount)
                .Where(index => index % options.DeletionInterval == 0)
                .Select(index => checked((ushort)index)).ToArray();
            foreach (ushort slot in deletedSlots)
            {
                page.Delete(slot);
            }
        }
        long fragmentationBefore = pages.Sum(page => (long)page.FragmentedBytes);
        foreach (SlottedPrototypePage page in pages)
        {
            page.Compact();
            SlottedPrototypePage.Validate(page.Encode());
        }
        long fragmentationAfter = pages.Sum(page => (long)page.FragmentedBytes);
        long allocatedBytes = (long)pages.Count * pageSize;
        long totalAllocatedBytes = allocatedBytes + overflowAllocatedBytes;
        long livePayloadBytes = pages.Sum(page => (long)page.LivePayloadBytes);
        return new(pageSize, options.RecordCount, pages.Count, allocatedBytes, overflowAllocatedBytes,
            totalAllocatedBytes, logicalPayloadBytes, inlineBytes, overflowBytes,
            overflowRecords, fragmentationBefore, fragmentationAfter,
            logicalPayloadBytes == 0 ? 0 : (double)totalAllocatedBytes / logicalPayloadBytes,
            allocatedBytes == 0 ? 0 : (double)livePayloadBytes / allocatedBytes,
            pages.Count == 0 ? 0 : pages.Average(page => page.LiveRecordCount));
    }

    private static SlottedPrototypePage NewPage(ICollection<SlottedPrototypePage> pages, int pageSize)
    {
        var page = new SlottedPrototypePage(pageSize, pages.Count);
        pages.Add(page);
        return page;
    }

    private static int RecordLength(Random random, int recordIndex)
    {
        if (recordIndex % LargeRecordFrequency == 0)
        {
            return random.Next(LargeRecordMinimum, LargeRecordMaximumExclusive);
        }

        if (recordIndex % MediumRecordFrequency == 0)
        {
            return random.Next(MediumRecordMinimum, MediumRecordMaximumExclusive);
        }

        return random.Next(MinimumRecordSize, TypicalRecordMaximumExclusive);
    }

    private static byte[] Payload(int length, int recordIndex)
    {
        var bytes = new byte[length];
        byte value = checked((byte)(recordIndex % (byte.MaxValue + 1)));
        Array.Fill(bytes, value);
        return bytes;
    }

    private static void Validate(PageLayoutExperimentOptions options)
    {
        if (options.RecordCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options.RecordCount));
        }

        if (options.PageSizes.Count == 0)
        {
            throw new ArgumentException("At least one page size is required.");
        }

        if (options.PageSizes.Any(size => size <= SlottedPrototypePage.HeaderSize + SlottedPrototypePage.SlotSize))
        {
            throw new ArgumentOutOfRangeException(nameof(options.PageSizes));
        }

        if (options.DeletionInterval <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options.DeletionInterval));
        }

        if (options.MaximumInlineValueDivisor <= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options.MaximumInlineValueDivisor));
        }
    }
}
