using EntityForge.Db.Storage;
using System.Diagnostics.Metrics;

namespace EntityForge.Db.Storage.Pages.Tests;

[TestClass]
public sealed class PhysicalImageCommitmentTests
{
    [TestMethod]
    public async Task Prepared_image_reuses_only_unchanged_blocks_and_never_certifies_active_mutations()
    {
        await using var file = await PhysicalPageFile.OpenAsync(Image);
        await file.EnsureCapacityAsync(8 * PhysicalImageCommitment.BlockBytes / file.PageSize);
        await file.FlushAsync();
        await file.ImageCommitment.WarmAsync();
        var prepared = await file.ImageCommitment.CaptureAsync();
        Assert.AreEqual(0L, prepared.BytesRead);
        Assert.AreEqual((await new PhysicalImageCommitment(Image).CaptureAsync()).Sha256, prepared.Sha256);
        byte[] page = new byte[file.PageSize];
        page[0] = 73;
        await file.WriteAsync(new(3), page);
        await file.FlushAsync();
        var changed = await file.ImageCommitment.CaptureAsync();
        Assert.AreEqual((long)PhysicalImageCommitment.BlockBytes, changed.BytesRead);
        Assert.AreEqual((await new PhysicalImageCommitment(Image).CaptureAsync()).Sha256, changed.Sha256);
        var cold = new PhysicalImageCommitment(Image);
        using (cold.BeginWrite(0, file.PageSize))
        {
            await cold.WarmAsync();
            await Assert.ThrowsExceptionAsync<InvalidDataException>(() => cold.CaptureAsync().AsTask());
        }
        Assert.AreEqual(prepared.Bytes, (await cold.CaptureAsync()).BytesRead);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsExceptionAsync<TaskCanceledException>(() => cold.WarmAsync(cancelled.Token).AsTask());
        Assert.AreEqual(changed.Sha256, (await cold.CaptureAsync()).Sha256);
    }

    [TestMethod]
    public async Task Concurrent_preparation_cannot_cache_bytes_across_attempted_writes_or_resize()
    {
        await using var file = await PhysicalPageFile.OpenAsync(Image);
        int pages = 16 * PhysicalImageCommitment.BlockBytes / file.PageSize;
        await file.EnsureCapacityAsync(pages);
        var warming = file.ImageCommitment.WarmAsync().AsTask();
        byte[] page = new byte[file.PageSize];
        for (int i = 0; i < 32; i++)
        {
            page[0] = (byte)i;
            await file.WriteAsync(new(i * 17 % pages), page);
        }
        await file.EnsureCapacityAsync(pages + 3);
        await warming;
        await file.FlushAsync();
        var captured = await file.ImageCommitment.CaptureAsync();
        Assert.AreEqual((await new PhysicalImageCommitment(Image).CaptureAsync()).Sha256, captured.Sha256);
    }

    [TestMethod]
    public async Task Parallel_block_capture_matches_independent_ordered_digest_for_partial_tail()
    {
        byte[] data = new byte[PhysicalImageCommitment.BlockBytes * 3 + 317];
        new Random(731).NextBytes(data);
        await File.WriteAllBytesAsync(Image, data);
        using var expected = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.SHA256);
        expected.AppendData("EntityForge.PhysicalImage.BlockSha256.v1"u8);
        byte[] header = new byte[20];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(header, data.Length);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), PhysicalImageCommitment.BlockBytes);
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(12), 4);
        expected.AppendData(header);
        for (int offset = 0; offset < data.Length; offset += PhysicalImageCommitment.BlockBytes)
        {
            expected.AppendData(System.Security.Cryptography.SHA256.HashData(data.AsSpan(offset,
                Math.Min(PhysicalImageCommitment.BlockBytes, data.Length - offset))));
        }

        string digest = Convert.ToHexString(expected.GetHashAndReset());
        var image = new PhysicalImageCommitment(Image);
        var cold = await image.CaptureAsync();
        var warm = await image.CaptureAsync();
        Assert.AreEqual(digest, cold.Sha256);
        Assert.AreEqual(digest, warm.Sha256);
        Assert.AreEqual((long)data.Length, cold.BytesRead);
        Assert.AreEqual(0L, warm.BytesRead);
    }

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ef-image-" + Guid.NewGuid().ToString("N"));

    private string Image => Path.Combine(_directory, "pages");

    [TestInitialize] public void Create() => Directory.CreateDirectory(_directory);

    [TestCleanup] public void Cleanup() => Directory.Delete(_directory, true);

    [TestMethod]
    public async Task Opt_in_capture_phases_preserve_digest_and_warm_read_elision()
    {
        await File.WriteAllBytesAsync(Image, new byte[PhysicalImageCommitment.BlockBytes]);
        var independent = await new PhysicalImageCommitment(Image).CaptureAsync();
        var phases = new List<string>();
        var bytesRead = new List<long>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, owner) =>
        {
            if (instrument.Meter.Name == "EntityForge.Db.Storage.ImageCommitment")
            {
                owner.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((_, value, tags, _) =>
        {
            Assert.IsTrue(double.IsFinite(value) && value >= 0);
            foreach (var tag in tags)
            {
                if (tag.Key == "entityforge.db.phase")
                {
                    phases.Add((string)tag.Value!);
                }
            }
        });
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
        {
            if (instrument.Name.EndsWith(".read.bytes", StringComparison.Ordinal))
            {
                bytesRead.Add(value);
            }
        });
        listener.Start();
        var owned = new PhysicalImageCommitment(Image);
        var cold = await owned.CaptureAsync();
        var warm = await owned.CaptureAsync();
        Assert.AreEqual(independent.Sha256, cold.Sha256);
        Assert.AreEqual(cold.Sha256, warm.Sha256);
        CollectionAssert.AreEqual(new long[] { PhysicalImageCommitment.BlockBytes, 0 }, bytesRead);
        foreach (string phase in new[] { "cache_snapshot", "read_hash", "read", "hash", "digest_fold", "total" })
        {
            Assert.AreEqual(2, phases.Count(item => item == phase));
        }
    }

    [TestMethod]
    public async Task Owned_page_writes_rehash_only_changed_blocks_and_reopen_does_not_trust_old_cache()
    {
        PhysicalImageCommitment? closed = null;
        await using (var file = await PhysicalPageFile.OpenAsync(Image))
        {
            closed = file.ImageCommitment;
            const int Blocks = 8;
            await file.EnsureCapacityAsync(Blocks * PhysicalImageCommitment.BlockBytes / file.PageSize);
            await file.WriteAsync(new(3), new byte[file.PageSize]);
            await file.FlushAsync();
            var first = await file.ImageCommitment.CaptureAsync();
            Assert.AreEqual((long)Blocks * PhysicalImageCommitment.BlockBytes, first.BytesRead);
            var unchanged = await file.ImageCommitment.CaptureAsync();
            Assert.AreEqual(0L, unchanged.BytesRead);
            Assert.AreEqual(first.Sha256, unchanged.Sha256);
            byte[] changed = new byte[file.PageSize];
            changed[0] = 19;
            await file.WriteAsync(new(3), changed);
            await file.FlushAsync();
            var next = await file.ImageCommitment.CaptureAsync();
            Assert.AreEqual((long)PhysicalImageCommitment.BlockBytes, next.BytesRead);
            Assert.AreNotEqual(first.Sha256, next.Sha256);
            Assert.AreEqual((await new PhysicalImageCommitment(Image).CaptureAsync()).Sha256, next.Sha256);
        }
        await Assert.ThrowsExceptionAsync<ObjectDisposedException>(() => closed!.CaptureAsync().AsTask());
        await using var reopened = await PhysicalPageFile.OpenAsync(Image);
        var cold = await reopened.ImageCommitment.CaptureAsync();
        Assert.AreEqual(cold.Bytes, cold.BytesRead);
        Assert.AreEqual((await new PhysicalImageCommitment(Image).CaptureAsync()).Sha256, cold.Sha256);
    }

    [TestMethod]
    public async Task Sparse_growth_partial_blocks_truncation_and_regrowth_preserve_exact_image_association()
    {
        await using var file = await PhysicalPageFile.OpenAsync(Image);
        var empty = await file.ImageCommitment.CaptureAsync();
        Assert.AreEqual(0L, empty.Bytes);
        int blockPages = PhysicalImageCommitment.BlockBytes / file.PageSize;
        await file.EnsureCapacityAsync(blockPages + 1);
        byte[] value = new byte[file.PageSize];
        value[^1] = 77;
        await file.WriteAsync(new(blockPages), value);
        var grown = await file.ImageCommitment.CaptureAsync();
        Assert.AreNotEqual(empty.Sha256, grown.Sha256);
        await file.EnsureCapacityAsync(3 * blockPages);
        var sparse = await file.ImageCommitment.CaptureAsync();
        Assert.AreEqual(2L * PhysicalImageCommitment.BlockBytes, sparse.BytesRead);
        Assert.AreEqual((await new PhysicalImageCommitment(Image).CaptureAsync()).Sha256, sparse.Sha256);
        await file.TruncateAsync(blockPages);
        var truncated = await file.ImageCommitment.CaptureAsync();
        Assert.AreNotEqual(sparse.Sha256, truncated.Sha256);
        await file.EnsureCapacityAsync(3 * blockPages);
        var regrown = await file.ImageCommitment.CaptureAsync();
        Assert.AreNotEqual(sparse.Sha256, regrown.Sha256); // Truncation removed the former nonzero tail.
        Assert.AreEqual((await new PhysicalImageCommitment(Image).CaptureAsync()).Sha256, regrown.Sha256);
    }

    [TestMethod]
    public async Task Ambiguous_write_invalidates_before_attempt_and_capture_requires_drained_writers()
    {
        await File.WriteAllBytesAsync(Image, new byte[PhysicalImageCommitment.BlockBytes * 2]);
        var image = new PhysicalImageCommitment(Image);
        var before = await image.CaptureAsync();
        using (image.BeginWrite(0, 1))
        {
            await Assert.ThrowsExceptionAsync<InvalidDataException>(() => image.CaptureAsync().AsTask());
            // A durable prefix followed by an error must not leave the old cached digest usable.
            await using var stream = new FileStream(Image, FileMode.Open, FileAccess.Write);
            await stream.WriteAsync(new byte[] { 63 });
            stream.Flush(true);
        }
        var after = await image.CaptureAsync();
        Assert.AreEqual((long)PhysicalImageCommitment.BlockBytes, after.BytesRead);
        Assert.AreNotEqual(before.Sha256, after.Sha256);
        Assert.AreEqual((await new PhysicalImageCommitment(Image).CaptureAsync()).Sha256, after.Sha256);
        using (image.BeginWrite(0, 1))
        { /* failed before any bytes changed */
        }
        var failedBeforeWriting = await image.CaptureAsync();
        Assert.AreEqual((long)PhysicalImageCommitment.BlockBytes, failedBeforeWriting.BytesRead);
        Assert.AreEqual(after.Sha256, failedBeforeWriting.Sha256);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsExceptionAsync<TaskCanceledException>(() => image.CaptureAsync(cancelled.Token).AsTask());
        Assert.AreEqual(after.Sha256, (await image.CaptureAsync()).Sha256);
    }

    [TestMethod]
    public async Task Buffered_flush_and_raw_redo_writes_reach_the_same_invalidation_boundary()
    {
        await using var store = await BufferedPhysicalPageStore.OpenLocalAsync(Image);
        await store.EnsureCapacityAsync(100);
        var page = new SlottedDataPage(store.PageSize, new(2));
        Assert.IsTrue(page.TryInsert(new byte[] { 1 }, 1, out _));
        await store.WriteAsync(new(2), page.Encode());
        await store.FlushAsync();
        var before = await store.ImageCommitment.CaptureAsync();
        var replacement = new SlottedDataPage(store.PageSize, new(2));
        Assert.IsTrue(replacement.TryInsert(new byte[] { 2 }, 2, out _));
        await ((IPhysicalPageRecoveryStore)store).WriteRawAsync(new(2), replacement.Encode(), default);
        await ((IPhysicalPageRecoveryStore)store).FlushRawAsync(default);
        var after = await store.ImageCommitment.CaptureAsync();
        Assert.AreNotEqual(before.Sha256, after.Sha256);
        Assert.AreEqual((await new PhysicalImageCommitment(Image).CaptureAsync()).Sha256, after.Sha256);
    }
}
