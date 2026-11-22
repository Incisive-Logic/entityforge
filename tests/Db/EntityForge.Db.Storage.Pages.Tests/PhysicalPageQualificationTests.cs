using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace EntityForge.Db.Storage.Pages.Tests;

[TestClass]
public sealed class PhysicalPageQualificationTests
{
    private const int SmallCorpusBytes = 32 * 1024 * 1024;
    private const int MediumCorpusBytes = 128 * 1024 * 1024;
    private const int SmallPointReadCount = 4_096;
    private const int MediumPointReadCount = 8_192;
    private const int SmallChurnPageCount = 256;
    private const int MediumChurnPageCount = 1_024;
    private const int ChurnFlushBatchSize = 64;
    private const int BufferFrameCount = 128;
    private const int ScanFrameCount = 16;
    private const int OverflowInlineBytes = 2_048;
    private const int OverflowChunkBytes = 4_096;
    private const int QualificationSeed = 14_778_116;
    private const int GrowthPageCount = 128;
    private const int ShrunkPageCount = 64;
    private static readonly int[] OverflowValueSizes = [3_000, 7_000, 20_000, 65_000, 130_000];

    public TestContext TestContext { get; set; } = null!;

    [DataTestMethod]
    [DataRow(SmallCorpusBytes, SmallPointReadCount, SmallChurnPageCount, "S")]
    [DataRow(MediumCorpusBytes, MediumPointReadCount, MediumChurnPageCount, "M")]
    public async Task Selected_page_format_runs_focused_real_io_and_recovery_workload(
        int corpusBytes, int pointReadCount, int churnPageCount, string scale)
    {
        string directory = Path.Combine(Path.GetTempPath(),
            $"entityforge-page-qualification-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            QualificationResult result = await RunAsync(Path.Combine(directory, "pages.dat"),
                corpusBytes, pointReadCount, churnPageCount, scale);

            Assert.AreEqual(PhysicalPageFileOptions.DefaultPageSize, result.PageSize);
            Assert.AreEqual(corpusBytes / result.PageSize, result.PageCount);
            Assert.IsTrue(result.ReopenValidated);
            Assert.IsTrue(result.ResidentBytes <=
                (long)result.PageSize * BufferFrameCount);
            Assert.AreEqual(0, result.DirtyPagesAfterFlush);
            Assert.AreEqual(result.ExpectedRecoveryDigest, result.ActualRecoveryDigest);
            TestContext.WriteLine(JsonSerializer.Serialize(result,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task Selected_page_file_grows_shrinks_and_reopens_at_exact_page_boundaries()
    {
        string directory = Path.Combine(Path.GetTempPath(),
            $"entityforge-page-resize-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "pages.dat");
        try
        {
            await using (PhysicalPageFile file = await PhysicalPageFile.OpenAsync(path))
            {
                await file.EnsureCapacityAsync(GrowthPageCount);
                await file.WriteAsync(new(GrowthPageCount - 1),
                    BuildPage(GrowthPageCount - 1, generation: 1));
                await file.FlushAsync();
                Assert.AreEqual(GrowthPageCount, await file.GetPageCountAsync());
                await file.TruncateAsync(ShrunkPageCount);
                await file.FlushAsync();
            }

            await using PhysicalPageFile reopened = await PhysicalPageFile.OpenAsync(path);
            Assert.AreEqual(ShrunkPageCount, await reopened.GetPageCountAsync());
            await Assert.ThrowsExceptionAsync<PhysicalPageCorruptionException>(async () =>
                await reopened.ReadAsync(new(GrowthPageCount - 1)));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static async Task<QualificationResult> RunAsync(string path, int corpusBytes,
        int pointReadCount, int churnPageCount, string scale)
    {
        int pageSize = PhysicalPageFileOptions.DefaultPageSize;
        int pageCount = corpusBytes / pageSize;
        var random = new Random(QualificationSeed);
        var watch = Stopwatch.StartNew();
        await using (PhysicalPageFile file = await PhysicalPageFile.OpenAsync(path))
        {
            await file.EnsureCapacityAsync(pageCount);
            for (int index = 0; index < pageCount; index++)
            {
                await file.WriteAsync(new(index), BuildPage(index, generation: 0));
            }

            await file.FlushAsync();
        }
        long createAndFlushMicroseconds = ElapsedMicroseconds(watch);

        watch.Restart();
        PhysicalPageBufferPoolSnapshot snapshot;
        long pointReadMicroseconds;
        long scanMicroseconds;
        long churnAndFlushMicroseconds;
        await using (var pool = new PhysicalPageBufferPool(
                         await PhysicalPageFile.OpenAsync(path),
                         (long)pageSize * BufferFrameCount, ScanFrameCount))
        {
            for (int index = 0; index < pointReadCount; index++)
            {
                int selected = random.Next(pageCount);
                await using PhysicalPageLease lease = await pool.AcquireAsync(new(selected));
            }
            pointReadMicroseconds = ElapsedMicroseconds(watch);

            watch.Restart();
            for (int index = 0; index < pageCount; index++)
            {
                await using (PhysicalPageLease lease = await pool.AcquireAsync(
                                 new(index), PageAccessClass.SequentialScan))
                {
                }
            }

            scanMicroseconds = ElapsedMicroseconds(watch);

            watch.Restart();
            for (int index = 0; index < churnPageCount; index++)
            {
                await using (PhysicalPageLease lease = await pool.AcquireAsync(new(index)))
                {
                    BuildPage(index, generation: 1).CopyTo(lease.Page);
                    lease.MarkDirty(index + 1L);
                }
                if ((index + 1) % ChurnFlushBatchSize == 0)
                {
                    await pool.FlushDirtyAsync(index + 1L);
                }
            }
            await pool.FlushDirtyAsync(churnPageCount);
            churnAndFlushMicroseconds = ElapsedMicroseconds(watch);
            snapshot = pool.Snapshot();
        }
        string expectedDigest = RecoveryDigest(churnPageCount, generation: 1);
        string actualDigest = await ReadRecoveryDigestAsync(path, churnPageCount);
        long overflowBytes = OverflowBytes();
        return new(scale, pageSize, pageCount, createAndFlushMicroseconds,
            pointReadMicroseconds, scanMicroseconds, churnAndFlushMicroseconds,
            snapshot.ResidentBytes, snapshot.Hits, snapshot.Misses, snapshot.Evictions,
            snapshot.DirtyPages, overflowBytes, expectedDigest, actualDigest,
            ReopenValidated: true);
    }

    private static async Task<string> ReadRecoveryDigestAsync(string path, int pageCount)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using PhysicalPageFile file = await PhysicalPageFile.OpenAsync(path);
        for (int index = 0; index < pageCount; index++)
        {
            ReadOnlyMemory<byte> image = await file.ReadAsync(new(index));
            SlottedDataPage.Validate(image.Span, new(index));
            hash.AppendData(image.Span);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string RecoveryDigest(int pageCount, long generation)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (int index = 0; index < pageCount; index++)
        {
            hash.AppendData(BuildPage(index, generation));
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static byte[] BuildPage(int index, long generation)
    {
        var page = new SlottedDataPage(PhysicalPageFileOptions.DefaultPageSize, new(index), generation);
        byte[] value = BitConverter.GetBytes(index);
        if (!page.TryInsert(value, index + 1L, out _))
        {
            Assert.Fail($"Qualification value did not fit page {index}.");
        }

        return page.Encode();
    }

    private static long OverflowBytes()
    {
        var chunked = new ChunkedOverflowPolicy(OverflowInlineBytes, OverflowChunkBytes);
        return OverflowValueSizes.Sum(size => chunked.Plan(size).AllocatedBytes);
    }

    private static long ElapsedMicroseconds(Stopwatch watch) =>
        watch.ElapsedTicks * 1_000_000L / Stopwatch.Frequency;

    private sealed record QualificationResult(
        string Scale,
        int PageSize,
        int PageCount,
        long CreateAndFlushMicroseconds,
        long PointReadMicroseconds,
        long ScanMicroseconds,
        long ChurnAndFlushMicroseconds,
        long ResidentBytes,
        long Hits,
        long Misses,
        long Evictions,
        int DirtyPagesAfterFlush,
        long ChunkedOverflowBytes,
        string ExpectedRecoveryDigest,
        string ActualRecoveryDigest,
        bool ReopenValidated);
}
