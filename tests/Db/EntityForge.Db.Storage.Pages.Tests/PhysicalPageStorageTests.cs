using System.Text;
using System.Diagnostics.Metrics;

namespace EntityForge.Db.Storage.Pages.Tests;

[TestClass]
public sealed class PhysicalPageStorageTests
{
    private string? _directory;

    [TestMethod]
    public void Eviction_recycles_only_exclusive_overwrite_images_after_pins_and_dirty_state_clear()
    {
        using var cache = new PhysicalPageFrameCache(1, 1);
        byte[] image = new byte[4096];
        PhysicalPageFrame frame = cache.Add(new(0), image, PageAccessClass.Normal, canRecycleImage: true);
        Assert.IsFalse(cache.TryEvict(PageAccessClass.Normal, out _));
        frame.PinCount--;
        frame.Dirty = true;
        Assert.IsFalse(cache.TryEvict(PageAccessClass.Normal, out _));
        frame.Dirty = false;
        Assert.IsTrue(cache.TryEvict(PageAccessClass.Normal, out byte[]? recycled));
        Assert.AreSame(image, recycled);
        frame = cache.Add(new(1), image, PageAccessClass.Normal);
        frame.PinCount--;
        Assert.IsTrue(cache.TryEvict(PageAccessClass.Normal, out recycled));
        Assert.IsNull(recycled, "Coalesced load waiters may still hold a loaded image.");
    }

    [TestMethod]
    public async Task Overwrite_eviction_reuses_storage_without_aliasing_input_or_losing_wal_protection()
    {
        const int pageSize = PhysicalPageFileOptions.DefaultPageSize;
        await using PhysicalPageFile file = await PhysicalPageFile.OpenAsync(NewPath());
        await using var pool = new PhysicalPageBufferPool(file, pageSize * 2L, scanFrames: 1);
        byte[] first = new SlottedDataPage(pageSize, new(0)).Encode();
        await using (var lease = await pool.AcquireForOverwriteWithDirtyFlushAsync(new(0), first, 10))
        {
            lease.MarkDirty(10);
            first[^1] ^= 1;
            SlottedDataPage.Validate(lease.Page.Span, new(0));
        }
        byte[] second = new SlottedDataPage(pageSize, new(1)).Encode();
        await Assert.ThrowsExceptionAsync<PhysicalPageWalDependencyException>(async () =>
            await pool.AcquireForOverwriteWithDirtyFlushAsync(new(1), second, 9));
        await using (var lease = await pool.AcquireForOverwriteWithDirtyFlushAsync(new(1), second, 10))
        {
            CollectionAssert.AreEqual(second, lease.Page.ToArray());
            lease.MarkDirty(11);
            second[^1] ^= 1;
            SlottedDataPage.Validate(lease.Page.Span, new(1));
        }
        await pool.FlushDirtyAsync(11);
        SlottedDataPage.Validate((await file.ReadAsync(new(0))).Span, new(0));
        SlottedDataPage.Validate((await file.ReadAsync(new(1))).Span, new(1));
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (_directory is not null && Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }

    [TestMethod]
    public async Task Data_flush_preserves_exact_content_and_length_after_growth_and_truncation()
    {
        const int originalPages = 3;
        const int retainedPages = 2;
        string path = NewPath();
        byte[] content = new byte[PhysicalPageFileOptions.DefaultPageSize];
        new Random(1701).NextBytes(content);
        await using (var file = await PhysicalPageFile.OpenAsync(path))
        {
            await file.EnsureCapacityAsync(originalPages);
            await file.WriteAsync(new(1), content);
            await file.FlushAsync();
        }
        await using (var reopened = await PhysicalPageFile.OpenAsync(path))
        {
            Assert.AreEqual((long)originalPages, await reopened.GetPageCountAsync());
            CollectionAssert.AreEqual(content, (await reopened.ReadAsync(new(1))).ToArray());
            await reopened.TruncateAsync(retainedPages);
            await reopened.FlushAsync();
        }
        await using var final = await PhysicalPageFile.OpenAsync(path);
        Assert.AreEqual((long)retainedPages, await final.GetPageCountAsync());
        CollectionAssert.AreEqual(content, (await final.ReadAsync(new(1))).ToArray());
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(async () => await final.FlushAsync(canceled.Token));
        await final.FlushAsync();
    }

    [TestMethod]
    public async Task Physical_io_telemetry_attributes_workload_target_layer_and_durable_flush()
    {
        var measurements = new List<(string Name, IReadOnlyDictionary<string, object?> Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == PhysicalPageTelemetry.MeterName)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
            measurements.Add((instrument.Name, tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value))));
        listener.SetMeasurementEventCallback<double>((instrument, _, tags, _) =>
            measurements.Add((instrument.Name, tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value))));
        listener.Start();

        string path = NewPath();
        await using PhysicalPageFile file = await PhysicalPageFile.OpenAsync(path);
        using (PhysicalPageWorkload.EnterMaintenance("ordered"))
        {
            await file.WriteAsync(new(0), new byte[file.PageSize]);
            await file.FlushAsync();
            await file.ReadAsync(new(0));
        }

        Assert.IsTrue(measurements.Any(measurement => measurement.Name == "entityforge.db.pages.flushes"));
        Assert.IsTrue(measurements.Where(measurement => measurement.Name is
                "entityforge.db.pages.reads" or "entityforge.db.pages.writes" or "entityforge.db.pages.flushes")
            .All(measurement =>
                Equals(measurement.Tags[PhysicalPageTelemetry.WorkloadTag], "maintenance") &&
                Equals(measurement.Tags[PhysicalPageTelemetry.MaintenanceTargetTag], "ordered") &&
                Equals(measurement.Tags[PhysicalPageTelemetry.StorageLayerTag], "device")));
    }

    [TestMethod]
    public async Task Positional_page_file_round_trips_and_durably_flushes_exact_pages()
    {
        const int pageSize = PhysicalPageFileOptions.DefaultPageSize;
        string path = NewPath();
        var id = new PhysicalPageId(3);
        var page = new SlottedDataPage(pageSize, id);
        Assert.IsTrue(page.TryInsert(Encoding.UTF8.GetBytes("Acuity"), 7, out _));
        await using (PhysicalPageFile file = await PhysicalPageFile.OpenAsync(path))
        {
            await file.WriteAsync(id, page.Encode());
            await file.FlushAsync();
        }
        await using PhysicalPageFile reopened = await PhysicalPageFile.OpenAsync(path);
        ReadOnlyMemory<byte> restored = await reopened.ReadAsync(id);

        SlottedDataPage.Validate(restored.Span, id);
        Assert.AreEqual(pageSize, restored.Length);
    }

    [TestMethod]
    public void Slotted_page_preserves_slots_compacts_and_fails_closed_on_corruption()
    {
        var page = new SlottedDataPage(PhysicalPageFileOptions.SixteenKiB, new(4));
        Assert.IsTrue(page.TryInsert(Encoding.UTF8.GetBytes("first"), 1, out ushort first));
        Assert.IsTrue(page.TryInsert(Encoding.UTF8.GetBytes("second"), 2, out ushort second));
        page.Delete(first, 3);
        page.Compact(4);
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("second"), page.Read(second).ToArray());
        byte[] encoded = page.Encode();
        encoded[^1] ^= 1;

        Assert.ThrowsException<PhysicalPageCorruptionException>(() =>
            SlottedDataPage.Validate(encoded, new(4)));
    }

    [TestMethod]
    public void Public_slotted_page_images_are_detached_from_page_owned_storage()
    {
        var page = new SlottedDataPage(PhysicalPageFileOptions.DefaultPageSize, new(5));
        Assert.IsTrue(page.TryInsert(Encoding.UTF8.GetBytes("stable"), 1, out _));
        byte[] first = page.Encode();
        byte[] second = page.Encode();

        first[^1] ^= 1;

        Assert.AreNotSame(first, second);
        SlottedDataPage.Validate(second, new(5));
        CollectionAssert.AreEqual(second, page.Encode());
    }

    [TestMethod]
    public async Task Buffer_coalesces_reads_is_bounded_and_enforces_wal_before_dirty_page_flush()
    {
        string path = NewPath();
        const int pageSize = PhysicalPageFileOptions.DefaultPageSize;
        await using PhysicalPageFile file = await PhysicalPageFile.OpenAsync(path);
        for (int index = 0; index < 4; index++)
        {
            await file.WriteAsync(new(index), new SlottedDataPage(pageSize, new(index)).Encode());
        }

        await file.FlushAsync();
        await using var pool = new PhysicalPageBufferPool(file, pageSize * 3L, scanFrames: 1);
        await using (PhysicalPageLease lease = await pool.AcquireAsync(new(0)))
        {
            lease.MarkDirty(10);
        }

        await Assert.ThrowsExceptionAsync<PhysicalPageWalDependencyException>(async () =>
            await pool.FlushDirtyAsync(9));
        await pool.FlushDirtyAsync(10);
        await using (PhysicalPageLease first = await pool.AcquireAsync(new(0)))
        {
            Assert.IsTrue(first.WasCacheHit);
        }

        await using (PhysicalPageLease scan = await pool.AcquireAsync(new(1), PageAccessClass.SequentialScan))
        {
        }
        await using (PhysicalPageLease scan = await pool.AcquireAsync(new(2), PageAccessClass.SequentialScan))
        {
        }

        PhysicalPageBufferPoolSnapshot snapshot = pool.Snapshot();
        Assert.IsTrue(snapshot.ResidentBytes <= pageSize * 3L);
        Assert.AreEqual(0, snapshot.DirtyPages);
        Assert.IsTrue(snapshot.Evictions > 0);
    }

    [TestMethod]
    public async Task Access_classes_share_one_coherent_frame_per_page()
    {
        string path = NewPath();
        const int pageSize = PhysicalPageFileOptions.DefaultPageSize;
        await using PhysicalPageFile file = await PhysicalPageFile.OpenAsync(path);
        await file.WriteAsync(new(0), new SlottedDataPage(pageSize, new(0)).Encode());
        await file.FlushAsync();
        await using var pool = new PhysicalPageBufferPool(file, pageSize * 3L, scanFrames: 1);
        await using (PhysicalPageLease scan = await pool.AcquireAsync(new(0), PageAccessClass.SequentialScan))
        {
        }

        await using (PhysicalPageLease normal = await pool.AcquireAsync(new(0), PageAccessClass.Normal))
        {
            Assert.IsTrue(normal.WasCacheHit);
        }

        Assert.AreEqual(1, pool.Snapshot().ResidentPages);
    }

    [TestMethod]
    public async Task Write_acquisition_flushes_wal_safe_dirty_frames_when_the_budget_is_full()
    {
        string path = NewPath();
        const int pageSize = PhysicalPageFileOptions.DefaultPageSize;
        await using PhysicalPageFile file = await PhysicalPageFile.OpenAsync(path);
        for (int index = 0; index < 2; index++)
        {
            await file.WriteAsync(new(index), new SlottedDataPage(pageSize, new(index)).Encode());
        }

        await file.FlushAsync();
        await using var pool = new PhysicalPageBufferPool(file, pageSize * 2L, scanFrames: 1);
        await using (PhysicalPageLease first = await pool.AcquireWithDirtyFlushAsync(new(0), 10))
        {
            first.MarkDirty(10);
        }

        await using (PhysicalPageLease second = await pool.AcquireWithDirtyFlushAsync(new(1), 10))
        {
            second.MarkDirty(10);
        }

        await pool.FlushDirtyAsync(10);

        PhysicalPageBufferPoolSnapshot snapshot = pool.Snapshot();
        Assert.AreEqual(0, snapshot.DirtyPages);
        Assert.IsTrue(snapshot.Evictions > 0);
    }

    [TestMethod]
    public async Task Eviction_writeback_flushes_a_bounded_dirty_batch()
    {
        string path = NewPath();
        const int pageSize = PhysicalPageFileOptions.DefaultPageSize;
        const int residentFrames = 10;
        await using PhysicalPageFile file = await PhysicalPageFile.OpenAsync(path);
        for (int index = 0; index <= residentFrames; index++)
        {
            await file.WriteAsync(new(index), new SlottedDataPage(pageSize, new(index)).Encode());
        }

        await file.FlushAsync();
        await using var pool = new PhysicalPageBufferPool(file, pageSize * (residentFrames + 1L), scanFrames: 1);
        for (int index = 0; index < residentFrames; index++)
        {
            await using (PhysicalPageLease lease = await pool.AcquireAsync(new(index)))
            {
                lease.MarkDirty(1);
            }
        }

        await using (PhysicalPageLease lease = await pool.AcquireWithDirtyFlushAsync(new(residentFrames), 1))
        {
        }

        Assert.AreEqual(2, pool.Snapshot().DirtyPages);
    }

    [TestMethod]
    public async Task Buffered_read_flushes_safe_dirty_frames_when_normal_cache_is_full()
    {
        string path = NewPath();
        const int pageSize = PhysicalPageFileOptions.DefaultPageSize;
        await using BufferedPhysicalPageStore pages = await BufferedPhysicalPageStore.OpenLocalAsync(
            path, pageSize * 3L);
        await pages.EnsureCapacityAsync(3);
        for (int index = 0; index < 3; index++)
        {
            await pages.WriteAsync(new(index), new SlottedDataPage(pageSize, new(index)).Encode());
        }

        await pages.FlushAsync();
        await pages.WriteAsync(new(0), new SlottedDataPage(pageSize, new(0)).Encode());
        await pages.WriteAsync(new(1), new SlottedDataPage(pageSize, new(1)).Encode());

        ReadOnlyMemory<byte> third = await pages.ReadAsync(new(2));

        SlottedDataPage.Validate(third.Span, new(2));
    }

    [TestMethod]
    public async Task Buffered_maintenance_scans_do_not_evict_foreground_pages()
    {
        string path = NewPath();
        const int pageSize = PhysicalPageFileOptions.DefaultPageSize;
        await using BufferedPhysicalPageStore pages = await BufferedPhysicalPageStore.OpenLocalAsync(
            path, pageSize * 3L);
        await pages.EnsureCapacityAsync(4);
        for (int index = 0; index < 4; index++)
        {
            await pages.WriteAsync(new(index), new SlottedDataPage(pageSize, new(index)).Encode());
        }

        await pages.FlushAsync();
        _ = await pages.ReadAsync(new(0));

        using (PhysicalPageWorkload.EnterMaintenance("test"))
        {
            _ = await pages.ReadAsync(new(1));
            _ = await pages.ReadAsync(new(2));
            _ = await pages.ReadAsync(new(3));
        }
        _ = await pages.ReadAsync(new(0));

        PhysicalPageBufferPoolSnapshot snapshot = pages.BufferSnapshot();
        Assert.AreEqual(1L, snapshot.Hits);
        Assert.AreEqual(4L, snapshot.Misses);
        Assert.AreEqual(1L, snapshot.Evictions);
    }

    [TestMethod]
    public async Task Buffered_shrink_invalidates_frames_before_page_ids_are_reused()
    {
        string path = NewPath();
        const int pageSize = PhysicalPageFileOptions.DefaultPageSize;
        var oldPage = new SlottedDataPage(pageSize, new(2));
        Assert.IsTrue(oldPage.TryInsert(Encoding.UTF8.GetBytes("old"), 1, out _));
        var replacement = new SlottedDataPage(pageSize, new(2));
        Assert.IsTrue(replacement.TryInsert(Encoding.UTF8.GetBytes("replacement"), 2, out _));
        byte[] expected = replacement.Encode();
        await using BufferedPhysicalPageStore pages = await BufferedPhysicalPageStore.OpenLocalAsync(
            path, pageSize * 3L);
        await pages.EnsureCapacityAsync(3);
        await pages.WriteAsync(new(2), oldPage.Encode());
        await pages.FlushAsync();
        _ = await pages.ReadAsync(new(2));

        await pages.TruncateAsync(2);
        await pages.EnsureCapacityAsync(3);
        await pages.WriteAsync(new(2), expected);
        await pages.FlushAsync();

        ReadOnlyMemory<byte> restored = await pages.ReadAsync(new(2));
        CollectionAssert.AreEqual(expected, restored.ToArray());
    }

    [TestMethod]
    public async Task Buffered_overwrite_initializes_a_proven_free_interior_page_without_reading_old_bytes()
    {
        string path = NewPath();
        const int pageSize = PhysicalPageFileOptions.DefaultPageSize;
        await using (PhysicalPageFile file = await PhysicalPageFile.OpenAsync(path))
        {
            await file.EnsureCapacityAsync(3);
            await file.FlushAsync();
        }
        byte[] expected = new SlottedDataPage(pageSize, new(1)).Encode();

        await using (BufferedPhysicalPageStore pages = await BufferedPhysicalPageStore.OpenLocalAsync(
                         path, pageSize * 3L))
        {
            await pages.WriteAsync(new(1), expected);
            await pages.FlushAsync();
        }

        await using BufferedPhysicalPageStore reopened = await BufferedPhysicalPageStore.OpenLocalAsync(
            path, pageSize * 3L);
        CollectionAssert.AreEqual(expected, (await reopened.ReadAsync(new(1))).ToArray());
    }

    [TestMethod]
    public void Selected_chunked_overflow_exposes_bounded_allocation_without_changing_logical_value()
    {
        const int valueBytes = 20_000;
        OverflowAllocation chunked = new ChunkedOverflowPolicy(2_048, 4_096).Plan(valueBytes);

        Assert.AreEqual(2_048, chunked.InlineBytes);
        Assert.AreEqual(20_480L, chunked.AllocatedBytes);
        Assert.ThrowsException<PhysicalPageCapacityException>(() =>
            new ChunkedOverflowPolicy(2_048, 4_096).Plan(int.MaxValue));
    }

    [TestMethod]
    public async Task Buffer_pool_rejects_disposal_until_active_leases_are_released()
    {
        string path = NewPath();
        const int pageSize = PhysicalPageFileOptions.DefaultPageSize;
        PhysicalPageFile file = await PhysicalPageFile.OpenAsync(path);
        await file.WriteAsync(new(0), new SlottedDataPage(pageSize, new(0)).Encode());
        var pool = new PhysicalPageBufferPool(file, pageSize * 2L, scanFrames: 1);
        PhysicalPageLease lease = await pool.AcquireAsync(new(0));

        await Assert.ThrowsExceptionAsync<PhysicalPageLifecycleException>(async () =>
            await pool.DisposeAsync());
        await lease.DisposeAsync();
        await pool.DisposeAsync();
        await pool.DisposeAsync();
    }

    [TestMethod]
    public async Task Buffer_pool_disposal_durably_flushes_unpinned_dirty_pages()
    {
        string path = NewPath();
        const int pageSize = PhysicalPageFileOptions.DefaultPageSize;
        var expectedPage = new SlottedDataPage(pageSize, new(0));
        Assert.IsTrue(expectedPage.TryInsert(Encoding.UTF8.GetBytes("durable-on-close"), 7, out _));
        byte[] expected = expectedPage.Encode();
        PhysicalPageFile file = await PhysicalPageFile.OpenAsync(path);
        await file.WriteAsync(new(0), new SlottedDataPage(pageSize, new(0)).Encode());
        await file.FlushAsync();
        var pool = new PhysicalPageBufferPool(file, pageSize * 2L, scanFrames: 1);
        await using (PhysicalPageLease lease = await pool.AcquireAsync(new(0)))
        {
            expected.CopyTo(lease.Page);
            lease.MarkDirty(0);
        }

        await pool.DisposeAsync();

        await using PhysicalPageFile reopened = await PhysicalPageFile.OpenAsync(path);
        CollectionAssert.AreEqual(expected, (await reopened.ReadAsync(new(0))).ToArray());
    }

    [TestMethod]
    public async Task Cancelled_dirty_flush_does_not_release_a_frame_latch_it_did_not_acquire()
    {
        string path = NewPath();
        const int pageSize = PhysicalPageFileOptions.DefaultPageSize;
        await using PhysicalPageFile file = await PhysicalPageFile.OpenAsync(path);
        await file.WriteAsync(new(0), new SlottedDataPage(pageSize, new(0)).Encode());
        await file.FlushAsync();
        await using var pool = new PhysicalPageBufferPool(file, pageSize * 2L, scanFrames: 1);
        PhysicalPageLease lease = await pool.AcquireAsync(new(0));
        lease.MarkDirty(0);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await pool.FlushDirtyAsync(0, cancellation.Token));

        await lease.DisposeAsync();
        Assert.AreEqual(0, pool.Snapshot().PinnedPages);
    }

    [TestMethod]
    public async Task Failed_multi_frame_flush_releases_every_flush_owned_pin()
    {
        string path = NewPath();
        const int pageSize = PhysicalPageFileOptions.DefaultPageSize;
        await using PhysicalPageFile file = await PhysicalPageFile.OpenAsync(path);
        for (int index = 0; index < 3; index++)
        {
            await file.WriteAsync(new(index), new SlottedDataPage(pageSize, new(index)).Encode());
        }

        await file.FlushAsync();
        await using var pool = new PhysicalPageBufferPool(file, pageSize * 4L, scanFrames: 1);
        for (int index = 0; index < 3; index++)
        {
            await using (PhysicalPageLease lease = await pool.AcquireAsync(new(index)))
            {
                lease.MarkDirty(index == 0 ? 10 : 0);
            }
        }

        await Assert.ThrowsExceptionAsync<PhysicalPageWalDependencyException>(async () =>
            await pool.FlushDirtyAsync(9));
        Assert.AreEqual(0, pool.Snapshot().PinnedPages);
        await pool.FlushDirtyAsync(10);
        Assert.AreEqual(0, pool.Snapshot().DirtyPages);
    }

    [TestMethod]
    public async Task Disposed_page_lease_rejects_further_dirty_marking()
    {
        string path = NewPath();
        const int pageSize = PhysicalPageFileOptions.DefaultPageSize;
        await using PhysicalPageFile file = await PhysicalPageFile.OpenAsync(path);
        await file.WriteAsync(new(0), new SlottedDataPage(pageSize, new(0)).Encode());
        await file.FlushAsync();
        await using var pool = new PhysicalPageBufferPool(file, pageSize * 2L, scanFrames: 1);
        PhysicalPageLease lease = await pool.AcquireAsync(new(0));
        await lease.DisposeAsync();

        Assert.ThrowsException<ObjectDisposedException>(() => lease.MarkDirty(0));
    }

    [TestMethod]
    public async Task Cancelled_waiter_does_not_cancel_or_duplicate_shared_page_load()
    {
        const int pageSize = PhysicalPageFileOptions.DefaultPageSize;
        var source = new ControlledPageStore(new SlottedDataPage(pageSize, new(0)).Encode());
        await using var pool = new PhysicalPageBufferPool(source, pageSize * 2L, scanFrames: 1);
        using var cancellation = new CancellationTokenSource();
        ValueTask<PhysicalPageLease> cancelled = pool.AcquireAsync(new(0), cancellationToken: cancellation.Token);
        ValueTask<PhysicalPageLease> successful = pool.AcquireAsync(new(0));
        cancellation.Cancel();
        source.Complete();

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () => await cancelled);
        await using PhysicalPageLease lease = await successful;
        Assert.AreEqual(1, source.ReadCount);
    }

    [TestMethod]
    public async Task Buffer_pool_disposal_drains_an_underlying_load_abandoned_by_a_cancelled_waiter()
    {
        const int pageSize = PhysicalPageFileOptions.DefaultPageSize;
        var source = new ControlledPageStore(new SlottedDataPage(pageSize, new(0)).Encode());
        var pool = new PhysicalPageBufferPool(source, pageSize * 2L, scanFrames: 1);
        using var cancellation = new CancellationTokenSource();
        ValueTask<PhysicalPageLease> acquire = pool.AcquireAsync(new(0),
            cancellationToken: cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () => await acquire);

        Task disposal = pool.DisposeAsync().AsTask();
        Assert.IsFalse(disposal.IsCompleted);
        source.Complete();
        await disposal;
    }

    [TestMethod]
    public async Task Closing_a_tail_range_blocks_new_loads_and_rejects_an_inflight_tail_load()
    {
        const int pageSize = PhysicalPageFileOptions.DefaultPageSize;
        var source = new ControlledPageStore(new SlottedDataPage(pageSize, new(1)).Encode());
        await using var pool = new PhysicalPageBufferPool(source, pageSize * 2L, scanFrames: 1);
        pool.SetAvailablePageCount(2);
        ValueTask<PhysicalPageLease> inflight = pool.AcquireAsync(new(1));
        await source.ReadStarted;

        Task closing = pool.CloseRangeAsync(1, CancellationToken.None).AsTask();
        await Assert.ThrowsExceptionAsync<PhysicalPageLifecycleException>(async () =>
            await pool.AcquireAsync(new(1)));
        source.Complete();
        await Assert.ThrowsExceptionAsync<PhysicalPageLifecycleException>(async () => await inflight);
        await closing;
        Assert.AreEqual(0, pool.Snapshot().ResidentPages);
    }

    [TestMethod]
    public async Task Scan_eviction_does_not_remove_same_identifier_from_normal_clock()
    {
        string path = NewPath();
        const int pageSize = PhysicalPageFileOptions.DefaultPageSize;
        const int totalPages = 16;
        const int bufferFrames = 6;
        const int scanFrames = 2;
        await using PhysicalPageFile file = await PhysicalPageFile.OpenAsync(path);
        for (int index = 0; index < totalPages; index++)
        {
            await file.WriteAsync(new(index), new SlottedDataPage(pageSize, new(index)).Encode());
        }

        await file.FlushAsync();
        await using var pool = new PhysicalPageBufferPool(file, pageSize * bufferFrames, scanFrames);

        for (int index = 0; index < bufferFrames - scanFrames; index++)
        {
            await using (PhysicalPageLease lease = await pool.AcquireAsync(new(index)))
            {
            }
        }

        for (int index = 0; index < totalPages; index++)
        {
            await using (PhysicalPageLease lease = await pool.AcquireAsync(new(index), PageAccessClass.SequentialScan))
            {
            }
        }

        await using PhysicalPageLease replacement = await pool.AcquireAsync(new(totalPages - 1));

        Assert.IsTrue(pool.Snapshot().ResidentBytes <= (long)pageSize * bufferFrames);
    }

    [TestMethod]
    public async Task Concurrent_capacity_growth_is_monotonic_and_preserves_written_tail_pages()
    {
        string path = NewPath();
        const int pageCount = 128;
        await using PhysicalPageFile pages = await PhysicalPageFile.OpenAsync(path);

        await Task.WhenAll(Enumerable.Range(0, pageCount).Select(async index =>
        {
            await pages.EnsureCapacityAsync(index + 1);
            await pages.WriteAsync(new(index),
                new SlottedDataPage(pages.PageSize, new(index), generation: index + 1).Encode());
        }));
        await pages.EnsureCapacityAsync(pageCount / 2);
        await pages.FlushAsync();

        Assert.AreEqual(pageCount, await pages.GetPageCountAsync());
        for (int index = 0; index < pageCount; index++)
        {
            SlottedDataPage.Validate((await pages.ReadAsync(new(index))).Span, new(index));
        }
    }

    [TestMethod]
    public async Task Truncate_waits_for_an_admitted_tail_write_and_rejects_later_tail_writes()
    {
        const int pageSize = PhysicalPageFileOptions.DefaultPageSize;
        var source = new ControlledResizablePageStore(pageSize, initialPageCount: 1);
        await using var pages = new BufferedPhysicalPageStore(source, pageSize * 2L,
            new PhysicalPageTelemetry("buffer-test"), 1);
        await pages.EnsureCapacityAsync(2);
        source.BlockWrites();
        Task write = pages.WriteAsync(new(1), new SlottedDataPage(pageSize, new(1)).Encode()).AsTask();
        await source.WriteStarted;

        Task truncate = pages.TruncateAsync(1).AsTask();
        Assert.IsFalse(truncate.IsCompleted);
        source.ReleaseWrites();
        await write;
        await truncate;

        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(async () =>
            await pages.WriteAsync(new(1), new SlottedDataPage(pageSize, new(1)).Encode()));
    }

    [TestMethod]
    public async Task Failed_truncate_reconciles_the_actual_length_and_reopens_the_preserved_tail()
    {
        const int pageSize = PhysicalPageFileOptions.DefaultPageSize;
        var source = new ControlledResizablePageStore(pageSize, initialPageCount: 2);
        byte[] tail = new SlottedDataPage(pageSize, new(1), generation: 7).Encode();
        await source.WriteAsync(new(1), tail);
        await using var pages = new BufferedPhysicalPageStore(source, pageSize * 2L,
            new PhysicalPageTelemetry("buffer-test"), 2);
        _ = await pages.ReadAsync(new(1));
        source.FailNextTruncate = true;

        await Assert.ThrowsExactlyAsync<IOException>(async () => await pages.TruncateAsync(1));

        CollectionAssert.AreEqual(tail, (await pages.ReadAsync(new(1))).ToArray());
        await pages.WriteAsync(new(1), tail);
    }

    [TestMethod]
    public async Task Failed_after_mutation_truncate_reconciles_to_the_shorter_physical_length()
    {
        const int pageSize = PhysicalPageFileOptions.DefaultPageSize;
        var source = new ControlledResizablePageStore(pageSize, initialPageCount: 2)
        {
            FailNextTruncateAfterMutation = true
        };
        await using var pages = new BufferedPhysicalPageStore(source, pageSize * 2L,
            new PhysicalPageTelemetry("buffer-test"), 2);

        await Assert.ThrowsExactlyAsync<IOException>(async () => await pages.TruncateAsync(1));

        Assert.AreEqual(1L, await pages.GetPageCountAsync());
        await Assert.ThrowsExactlyAsync<PhysicalPageLifecycleException>(async () =>
            await pages.ReadAsync(new(1)));
        await pages.EnsureCapacityAsync(2);
        await pages.WriteAsync(new(1), new SlottedDataPage(pageSize, new(1)).Encode());
    }

    private string NewPath()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"entityforge-pages-{Guid.NewGuid():N}");
        return Path.Combine(_directory, "pages.dat");
    }

    [TestMethod]
    public async Task Page_load_and_contended_latch_measurements_are_distinct_and_cancellation_releases_pin()
    {
        const int pageSize = PhysicalPageFileOptions.DefaultPageSize;
        using var telemetry = new PhysicalPageTelemetry("buffer");
        var observations = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, owner) =>
        {
            if (instrument.Meter.Name == PhysicalPageTelemetry.MeterName &&
                instrument.Name is "entityforge.db.pages.read.duration" or "entityforge.db.pages.cache.latch_wait.duration")
            {
                owner.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, state) =>
        {
            Assert.IsTrue(double.IsFinite(value) && value >= 0);
            observations.Enqueue(instrument.Name);
        });
        listener.Start();
        var source = new ControlledPageStore(new SlottedDataPage(pageSize, new(0)).Encode());
        await using var pool = new PhysicalPageBufferPool(source, pageSize * 2L, scanFrames: 1, telemetry: telemetry);
        Task<PhysicalPageLease> loading = pool.AcquireAsync(new(0)).AsTask();
        await source.ReadStarted;
        Assert.IsFalse(loading.IsCompleted);
        Assert.AreEqual(0, observations.Count);
        source.Complete();
        var held = await loading;
        try
        {
            Assert.AreEqual(1, observations.Count(name => name == "entityforge.db.pages.read.duration"));
            using var cancelled = new CancellationTokenSource();
            Task<PhysicalPageLease> waiting = pool.AcquireAsync(new(0), cancellationToken: cancelled.Token).AsTask();
            Assert.IsFalse(waiting.IsCompleted);
            cancelled.Cancel();
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await waiting);
            Assert.AreEqual(1, observations.Count(name => name == "entityforge.db.pages.cache.latch_wait.duration"));
        }
        finally
        {
            await held.DisposeAsync();
        }
        var blocking = await pool.AcquireAsync(new(0));
        Task<PhysicalPageLease> successfulWait = pool.AcquireAsync(new(0)).AsTask();
        Assert.IsFalse(successfulWait.IsCompleted);
        await blocking.DisposeAsync();
        await using var later = await successfulWait;
        Assert.AreEqual(2, observations.Count(name => name == "entityforge.db.pages.cache.latch_wait.duration"));
        Assert.AreEqual(1, source.ReadCount);
        Assert.AreEqual(1, observations.Count(name => name == "entityforge.db.pages.read.duration"));
        CollectionAssert.AreEqual(new SlottedDataPage(pageSize, new(0)).Encode(), later.Page.ToArray());
    }

    private sealed class ControlledPageStore(byte[] page) : IPhysicalPageStore
    {
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ReadCount
        {
            get; private set;
        }

        public Task ReadStarted => _started.Task;

        public int PageSize => page.Length;

        public void Complete() => _ready.TrySetResult();

        public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(PhysicalPageId pageId, CancellationToken cancellationToken = default)
        {
            ReadCount++;
            _started.TrySetResult();
            await _ready.Task.WaitAsync(cancellationToken);
            return page;
        }

        public ValueTask WriteAsync(PhysicalPageId pageId, ReadOnlyMemory<byte> value, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask FlushAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ControlledResizablePageStore : IResizablePhysicalPageStore
    {
        private readonly Dictionary<long, byte[]> _pages = [];
        private TaskCompletionSource? _writeRelease;
        private readonly TaskCompletionSource _writeStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private long _pageCount;

        public ControlledResizablePageStore(int pageSize, long initialPageCount)
        {
            PageSize = pageSize;
            _pageCount = initialPageCount;
        }

        public int PageSize
        {
            get;
        }

        public bool FailNextTruncate
        {
            get; set;
        }

        public bool FailNextTruncateAfterMutation
        {
            get; set;
        }

        public Task WriteStarted => _writeStarted.Task;

        public void BlockWrites() => _writeRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseWrites() => _writeRelease?.TrySetResult();

        public ValueTask<long> GetPageCountAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_pageCount);

        public ValueTask EnsureCapacityAsync(long pageCount, CancellationToken cancellationToken = default)
        {
            _pageCount = Math.Max(_pageCount, pageCount);
            return ValueTask.CompletedTask;
        }

        public ValueTask<ReadOnlyMemory<byte>> ReadAsync(PhysicalPageId pageId,
            CancellationToken cancellationToken = default)
        {
            if (pageId.Value >= _pageCount)
            {
                throw new ArgumentOutOfRangeException(nameof(pageId));
            }

            return ValueTask.FromResult<ReadOnlyMemory<byte>>(_pages.GetValueOrDefault(pageId.Value)
                ?? new byte[PageSize]);
        }

        public async ValueTask WriteAsync(PhysicalPageId pageId, ReadOnlyMemory<byte> page,
            CancellationToken cancellationToken = default)
        {
            if (pageId.Value >= _pageCount)
            {
                throw new ArgumentOutOfRangeException(nameof(pageId));
            }

            if (_writeRelease is not null)
            {
                _writeStarted.TrySetResult();
                await _writeRelease.Task.WaitAsync(cancellationToken);
            }
            _pages[pageId.Value] = page.ToArray();
        }

        public ValueTask FlushAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask TruncateAsync(long pageCount, CancellationToken cancellationToken = default)
        {
            if (FailNextTruncate)
            {
                FailNextTruncate = false;
                throw new IOException("simulated truncate failure");
            }
            _pageCount = pageCount;
            foreach (long key in _pages.Keys.Where(key => key >= pageCount).ToArray())
            {
                _pages.Remove(key);
            }

            if (FailNextTruncateAfterMutation)
            {
                FailNextTruncateAfterMutation = false;
                throw new IOException("simulated post-mutation failure");
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
