using EntityForge.Db.Durability;
using EntityForge.Db.Storage;

namespace EntityForge.Db.Storage.Pages.Tests;

[TestClass]
public sealed class InitializedRedoStreamTests
{
    public TestContext TestContext { get; set; } = null!;

    private string _directory = null!;

    private string PathName => Path.Combine(_directory, "candidate.wal");

    [TestMethod]
    public async Task Reclamation_marker_scan_has_bounded_allocation_and_validates_every_checksum()
    {
        byte[] one = RedoWalCodec.GroupMarker(1, 0);
        byte[] many = Enumerable.Range(1, 10000).SelectMany(i => RedoWalCodec.GroupMarker(i, 0)).ToArray();
        long Scan(byte[] bytes)
        {
            using var source = new MemoryStream(bytes, false);
            using var target = new MemoryStream();
            long before = GC.GetAllocatedBytesForCurrentThread();
            var result = RedoWalRecovery.RewriteAfterAsync(source, target, new(0), default).GetAwaiter().GetResult();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.AreEqual(bytes.Length / one.Length, result.DurableLsn);
            CollectionAssert.AreEqual(RedoWalCodec.GroupMarker(result.DurableLsn, 0), target.ToArray());
            return allocated;
        }
        Scan(one);
        Scan(many);
        Assert.IsTrue(Scan(many) - Scan(one) < 1024, "Marker allocations must not grow with scanned group count.");
        many[many.Length / 2 + RedoWalCodec.HeaderSize] ^= 1;
        await Assert.ThrowsAsync<RedoWalCorruptionException>(async () =>
            await RedoWalRecovery.RewriteAfterAsync(new MemoryStream(many), new MemoryStream(), new(0), default));
        await Assert.ThrowsAsync<RedoWalCorruptionException>(async () =>
            await RedoWalRecovery.RewriteAfterAsync(new MemoryStream(one[..^1]), new MemoryStream(), new(0), default));
    }

    [TestMethod]
    public async Task Captured_range_read_ahead_preserves_seeks_and_excludes_concurrent_appends()
    {
        byte[] original = new byte[150000];
        new Random(812).NextBytes(original);
        await using var writer = await InitializedRedoStream.OpenAsync(PathName);
        await writer.WriteAsync(original);
        await writer.CommitAsync();
        await using var range = writer.OpenReadRange(123, 100123);
        byte[] first = new byte[31];
        await range.ReadExactlyAsync(first);
        CollectionAssert.AreEqual(original[123..154], first);
        writer.Position = writer.Length;
        await writer.WriteAsync(new byte[70000]);
        await writer.CommitAsync();
        range.Seek(65500, SeekOrigin.Begin);
        byte[] crossing = new byte[100];
        await range.ReadExactlyAsync(crossing);
        CollectionAssert.AreEqual(original[(123 + 65500)..(123 + 65600)], crossing);
        range.Seek(-30, SeekOrigin.Current);
        Assert.AreEqual(65570L, range.Position);
        range.Position = 0;
        byte[] all = new byte[100000];
        await range.ReadExactlyAsync(all);
        CollectionAssert.AreEqual(original[123..100123], all);
        Assert.AreEqual(0, await range.ReadAsync(new byte[20]));
        Assert.AreEqual(100000L, range.Length);
        Assert.ThrowsException<IOException>(() => range.Seek(1, SeekOrigin.End));
        Assert.ThrowsException<IOException>(() => range.Position = -1);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        range.Position = 0;
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            int read = await range.ReadAsync(first, cancelled.Token);
            Assert.Fail($"Expected cancellation, but the range read returned {read} bytes.");
        });
        Assert.AreEqual(0L, range.Position);
    }

    [TestMethod]
    public async Task Native_capacity_reservation_preserves_prefix_and_requires_exact_eof()
    {
        Directory.CreateDirectory(_directory);
        byte[] prefix = Enumerable.Range(0, 4096).Select(i => (byte)(i % 251)).ToArray();
        await File.WriteAllBytesAsync(PathName, prefix);
        using var handle = File.OpenHandle(PathName, FileMode.Open, FileAccess.ReadWrite);
        Assert.IsFalse(DurableFile.TryAllocateZeroedTail(handle, 0, 4096, CancellationToken.None));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsException<OperationCanceledException>(() =>
            DurableFile.TryAllocateZeroedTail(handle, prefix.Length, 8192, cancelled.Token));
        Assert.AreEqual((long)prefix.Length, RandomAccess.GetLength(handle));
        bool allocated = DurableFile.TryAllocateZeroedTail(handle, prefix.Length, 8192, CancellationToken.None);
        if (!OperatingSystem.IsLinux())
        {
            Assert.IsFalse(allocated);
            return;
        }
        Assert.IsTrue(allocated, "The Linux qualification filesystem must support allocation.");
        DurableFile.SyncData(handle);
        Assert.AreEqual(12288L, RandomAccess.GetLength(handle));
        byte[] actual = new byte[12288];
        Assert.AreEqual(actual.Length, RandomAccess.Read(handle, actual, 0));
        CollectionAssert.AreEqual(prefix, actual[..prefix.Length]);
        Assert.IsTrue(actual[prefix.Length..].All(b => b == 0));
        using var readOnly = File.OpenHandle(PathName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        Assert.ThrowsException<IOException>(() =>
            DurableFile.TryAllocateZeroedTail(readOnly, actual.Length, 4096, CancellationToken.None));
    }

    [TestInitialize] public void Initialize() => _directory = Path.Combine(Path.GetTempPath(), $"ef-initialized-{Guid.NewGuid():N}");

    [TestCleanup]
    public void Cleanup()
    {
        DurableDirectory.BeforeSync.Value = null;
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }

    [TestMethod]
    public async Task Sibling_open_reuses_only_live_directory_proof_and_retains_publication_sync()
    {
        var synchronized = new List<string>();
        DurableDirectory.BeforeSync.Value = synchronized.Add;
        await using (var source = await InitializedRedoStream.OpenAsync(PathName, InitializedRedoStream.BlockBytes))
        {
            CollectionAssert.Contains(synchronized, Path.GetDirectoryName(_directory)!);
            synchronized.Clear();
            string sibling = Path.Combine(_directory, "replacement.wal");
            await using (var replacement = await source.OpenSiblingAsync(sibling))
            {
                CollectionAssert.AreEqual(new[] { _directory }, synchronized);
                await replacement.WriteAsync(new byte[] { 3, 7, 11 });
                await replacement.CommitAsync();
            }
            synchronized.Clear();
            await using var reopened = await source.OpenSiblingAsync(sibling);
            byte[] bytes = new byte[3];
            await reopened.ReadExactlyAsync(bytes);
            CollectionAssert.AreEqual(new byte[] { 3, 7, 11 }, bytes);
            CollectionAssert.AreEqual(new[] { _directory }, synchronized);
            await Assert.ThrowsExceptionAsync<ArgumentException>(async () =>
                await source.OpenSiblingAsync(Path.Combine(_directory, "foreign", "wrong.wal")));
            Assert.IsFalse(Directory.Exists(Path.Combine(_directory, "foreign")));
        }
        synchronized.Clear();
        await using var recovery = await InitializedRedoStream.OpenAsync(PathName, InitializedRedoStream.BlockBytes);
        CollectionAssert.Contains(synchronized, Path.GetDirectoryName(_directory)!);
    }

    [TestMethod]
    public async Task Failed_sibling_publication_invalidates_directory_proof_before_retry()
    {
        await using var source = await InitializedRedoStream.OpenAsync(PathName, InitializedRedoStream.BlockBytes);
        string sibling = Path.Combine(_directory, "replacement.wal");
        DurableDirectory.BeforeSync.Value = _ => throw new IOException("Injected publication barrier failure.");
        await Assert.ThrowsExceptionAsync<IOException>(async () => await source.OpenSiblingAsync(sibling));
        Assert.IsTrue(File.Exists(sibling), "Failure must occur after rename.");
        var synchronized = new List<string>();
        DurableDirectory.BeforeSync.Value = synchronized.Add;
        await using var retry = await source.OpenSiblingAsync(sibling);
        CollectionAssert.Contains(synchronized, Path.GetDirectoryName(_directory)!);
        Assert.AreEqual(0L, retry.Length);
    }

    [TestMethod]
    public async Task Missing_sibling_directory_requires_full_reconciliation_again()
    {
        var source = await InitializedRedoStream.OpenAsync(PathName, InitializedRedoStream.BlockBytes);
        await source.DisposeAsync();
        File.Delete(PathName);
        Directory.Delete(_directory);
        var synchronized = new List<string>();
        DurableDirectory.BeforeSync.Value = synchronized.Add;
        await using var replacement = await source.OpenSiblingAsync(Path.Combine(_directory, "replacement.wal"));
        CollectionAssert.Contains(synchronized, Path.GetDirectoryName(_directory)!);
    }

    [TestMethod]
    public async Task Initialized_capacity_is_not_a_record_and_growth_preserves_the_committed_prefix()
    {
        byte[] first = new byte[3000], second = new byte[9000];
        new Random(1).NextBytes(first);
        new Random(2).NextBytes(second);
        await using (var stream = await InitializedRedoStream.OpenAsync(PathName, 4096))
        {
            Assert.AreEqual(0L, stream.Length);
            Assert.AreEqual(InitializedRedoStream.HeaderBytes + 4096, new FileInfo(PathName).Length);
            await stream.WriteAsync(first);
            await stream.CommitAsync();
            await stream.WriteAsync(second);
            await stream.CommitAsync();
        }
        await using var reopened = await InitializedRedoStream.OpenAsync(PathName, 4096);
        byte[] actual = new byte[first.Length + second.Length];
        await reopened.ReadExactlyAsync(actual);
        CollectionAssert.AreEqual(first.Concat(second).ToArray(), actual);
        Assert.AreEqual(0, await reopened.ReadAsync(new byte[1]));
    }

    [TestMethod]
    public async Task Interrupted_unpublished_initialization_is_replaced_before_publication()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllBytesAsync(PathName + ".initializing", new byte[] { 1, 2, 3 });
        await using var opened = await InitializedRedoStream.OpenAsync(PathName, 4096);
        Assert.AreEqual(0L, opened.Length);
        Assert.IsFalse(File.Exists(PathName + ".initializing"));
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(32)]
    [DataRow(4096)]
    public async Task Truncated_published_initialization_is_never_reinitialized(int length)
    {
        await using (await InitializedRedoStream.OpenAsync(PathName, 4096))
        {
        }
        await using (var file = File.OpenWrite(PathName))
        {
            file.SetLength(length);
        }

        for (int attempt = 0; attempt < 2; attempt++)
        {
            await Assert.ThrowsAsync<IOException>(async () =>
            {
                await using var ignored = await InitializedRedoStream.OpenAsync(PathName, 4096);
            });
            Assert.AreEqual((long)length, new FileInfo(PathName).Length);
        }
    }

    [TestMethod]
    public async Task Unpublished_data_and_capacity_growth_are_not_recovered_as_committed_data()
    {
        await using (var stream = await InitializedRedoStream.OpenAsync(PathName, 4096))
        {
            await stream.WriteAsync(new byte[] { 1, 2, 3 });
            await stream.CommitAsync();
            await stream.WriteAsync(new byte[9000]);
            await stream.FlushAsync();
        }
        await using (var reopened = await InitializedRedoStream.OpenAsync(PathName, 4096))
        {
            Assert.AreEqual(3L, reopened.Length);
            reopened.Seek(0, SeekOrigin.End);
            await reopened.WriteAsync(new byte[] { 4 });
            await reopened.CommitAsync();
        }
        await using var final = await InitializedRedoStream.OpenAsync(PathName, 4096);
        byte[] contents = new byte[4];
        await final.ReadExactlyAsync(contents);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, contents);
    }

    [TestMethod]
    public async Task One_damaged_latest_mirror_is_repaired_without_losing_the_latest_commit()
    {
        await CreateGroupAsync();
        byte[] image = await File.ReadAllBytesAsync(PathName);
        image[InitializedRedoStream.SlotOffset(2) + 24] ^= 1;
        await File.WriteAllBytesAsync(PathName, image);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            await using var reopened = await InitializedRedoStream.OpenAsync(PathName, 4096);
            Assert.AreEqual(1L, (await RedoWalRecovery.ReadAsync(reopened, false, default)).DurableLsn);
        }
    }

    [TestMethod]
    public async Task Both_damaged_latest_mirrors_never_fall_back_to_the_previous_commit()
    {
        await CreateGroupAsync();
        byte[] image = await File.ReadAllBytesAsync(PathName);
        image[InitializedRedoStream.SlotOffset(2) + 24] ^= 1;
        image[InitializedRedoStream.SlotOffset(3) + 24] ^= 1;
        await File.WriteAllBytesAsync(PathName, image);
        await Assert.ThrowsAsync<RedoWalCorruptionException>(async () =>
        {
            await using var ignored = await InitializedRedoStream.OpenAsync(PathName, 4096);
        });
    }

    [TestMethod]
    public async Task All_prefix_tears_of_one_latest_copy_keep_the_independent_latest_evidence()
    {
        await CreateGroupAsync();
        byte[] original = await File.ReadAllBytesAsync(PathName);
        int destination = (int)InitializedRedoStream.SlotOffset(2);
        int old = (int)InitializedRedoStream.SlotOffset(0);
        for (int cut = 0; cut <= InitializedRedoStream.RecordBytes; cut++)
        {
            byte[] image = (byte[])original.Clone();
            original.AsSpan(old + cut, InitializedRedoStream.RecordBytes - cut)
                .CopyTo(image.AsSpan(destination + cut));
            await File.WriteAllBytesAsync(PathName, image);
            await using var reopened = await InitializedRedoStream.OpenAsync(PathName, 4096);
            Assert.AreEqual(1L, (await RedoWalRecovery.ReadAsync(reopened, false, default)).DurableLsn);
        }
    }

    [TestMethod]
    public async Task Valid_end_with_missing_or_corrupted_committed_data_fails_closed()
    {
        await CreateGroupAsync();
        byte[] image = await File.ReadAllBytesAsync(PathName);
        image[InitializedRedoStream.HeaderBytes + RedoWalCodec.HeaderSize + 1] ^= 1;
        await File.WriteAllBytesAsync(PathName, image);
        await using var reopened = await InitializedRedoStream.OpenAsync(PathName, 4096);
        await Assert.ThrowsAsync<RedoWalCorruptionException>(async () =>
            await RedoWalRecovery.ReadAsync(reopened, false, default));
    }

    [TestMethod]
    public async Task Reclamation_retains_lsn_floor_and_future_appends_across_reopen()
    {
        await CreateGroupAsync();
        string replacement = PathName + ".replacement";
        await using (var source = await InitializedRedoStream.OpenAsync(PathName, 4096))
        {
            await using (var target = await InitializedRedoStream.OpenAsync(replacement, 4096))
            {
                var result = await RedoWalRecovery.RewriteAfterAsync(source, target, new(1), default);
                Assert.AreEqual(1, result.RemovedTransactions);
                await target.CommitAsync();
            }
        }
        await using var reopened = await InitializedRedoStream.OpenAsync(replacement, 4096);
        var recovery = await RedoWalRecovery.ReadAsync(reopened, false, default);
        Assert.AreEqual(1L, recovery.DurableLsn);
        Assert.AreEqual(0, recovery.Transactions.Count);
        reopened.Seek(0, SeekOrigin.End);
        await RedoWalCodec.WriteTransactionAsync(reopened, 2, Transaction(2), [], default);
        await reopened.WriteAsync(RedoWalCodec.GroupMarker(2, 1));
        await reopened.CommitAsync();
        recovery = await RedoWalRecovery.ReadAsync(reopened, false, default);
        Assert.AreEqual(2L, recovery.DurableLsn);
        Assert.AreEqual(1, recovery.Transactions.Count);
    }

    [TestMethod]
    public void Ambiguous_newer_bank_is_not_hidden_by_an_older_valid_mirror()
    {
        var old = new InitializedRedoStream.Tail(1, 100, 4096);
        var oldest = new InitializedRedoStream.Tail(0, 0, 4096);
        Assert.Throws<RedoWalCorruptionException>(() => InitializedRedoStream.Select([null, oldest, old, old]));
    }

    [DataTestMethod]
    [DataRow(RedoDurabilityStage.BeforeCapacityInitialization)]
    [DataRow(RedoDurabilityStage.AfterCapacityInitialization)]
    public async Task Interrupted_growth_preserves_the_selected_prefix_and_can_be_retried(RedoDurabilityStage stage)
    {
        await using (var initial = await InitializedRedoStream.OpenAsync(PathName, 4096))
        {
            await initial.WriteAsync(new byte[] { 1, 2, 3 });
            await initial.CommitAsync();
        }
        await using (var failing = await InitializedRedoStream.OpenAsync(PathName, 4096, observer: new FailAt(stage)))
        {
            failing.Seek(0, SeekOrigin.End);
            await Assert.ThrowsAsync<IOException>(async () => await failing.WriteAsync(new byte[9000]));
        }
        await using (var retry = await InitializedRedoStream.OpenAsync(PathName, 4096))
        {
            Assert.AreEqual(3L, retry.Length);
            retry.Seek(0, SeekOrigin.End);
            await retry.WriteAsync(new byte[9000]);
            await retry.CommitAsync();
        }
        await using var final = await InitializedRedoStream.OpenAsync(PathName, 4096);
        Assert.AreEqual(9003L, final.Length);
        byte[] prefix = new byte[3];
        await final.ReadExactlyAsync(prefix);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, prefix);
    }

    private sealed class FailAt(RedoDurabilityStage stage) : IRedoDurabilityObserver
    {
        public void Reached(RedoDurabilityStage reached)
        {
            if (reached == stage)
            {
                throw new IOException("Injected initialization interruption.");
            }
        }
    }

    private static readonly Func<FileStream, long, long, CancellationToken, Task> Fill =
        typeof(InitializedRedoStream).GetMethod("FillAsync", System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Static)!.CreateDelegate<Func<FileStream, long, long, CancellationToken, Task>>();

    [TestMethod]
    public async Task Initialization_fill_matches_private_zero_oracle_and_cancels_without_touching_other_files()
    {
        const int bufferBytes = 1024 * 1024;
        Directory.CreateDirectory(_directory);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(async index =>
        {
            int start = 13 + index, end = start + 2 * bufferBytes + 19;
            byte[] dirty = Enumerable.Repeat((byte)(index + 1), end + 31).ToArray();
            string path = Path.Combine(_directory, $"fill-{index}");
            await File.WriteAllBytesAsync(path, dirty);
            await using (var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite,
                FileShare.None, 4096, FileOptions.Asynchronous))
            {
                using var cancelled = new CancellationTokenSource();
                cancelled.Cancel();
                await Assert.ThrowsAsync<OperationCanceledException>(() => Fill(file, start, end, cancelled.Token));
                file.Position = 0;
                byte[] unchanged = new byte[dirty.Length];
                await file.ReadExactlyAsync(unchanged);
                CollectionAssert.AreEqual(dirty, unchanged);
                await Fill(file, start, end, default);
            }
            Array.Clear(dirty, start, end - start);
            CollectionAssert.AreEqual(dirty, await File.ReadAllBytesAsync(path));
        }));
        // A later fill must still contain only zeros after concurrent callers wrote different files.
        await using var fresh = new FileStream(Path.Combine(_directory, "fresh"), FileMode.Create,
            FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.Asynchronous);
        await Fill(fresh, 0, bufferBytes, default);
        fresh.Position = 0;
        byte[] zeros = new byte[bufferBytes];
        await fresh.ReadExactlyAsync(zeros);
        Assert.IsTrue(zeros.All(value => value == 0));
    }

    [TestMethod]
    public async Task Repeated_initialization_fill_avoids_per_call_megabyte_allocation()
    {
        const int bufferBytes = 1024 * 1024, repetitions = 4;
        Directory.CreateDirectory(_directory);
        await using var file = new FileStream(PathName, FileMode.Create, FileAccess.ReadWrite,
            FileShare.None, 4096, FileOptions.Asynchronous);
        await Fill(file, 0, bufferBytes, default); // Include one-time initialization outside the measured window.
        long before = GC.GetTotalAllocatedBytes(true);
        for (int i = 0; i < repetitions; i++)
        {
            await Fill(file, 0, bufferBytes, default);
        }

        long current = GC.GetTotalAllocatedBytes(true) - before;
        before = GC.GetTotalAllocatedBytes(true);
        for (int i = 0; i < repetitions; i++)
        {
            byte[] privateZeros = new byte[bufferBytes];
            file.Position = 0;
            await file.WriteAsync(privateZeros);
        }
        long previous = GC.GetTotalAllocatedBytes(true) - before;
        TestContext.WriteLine($"Four warmed 1 MiB fills: private buffers={previous} allocated bytes; shared zeros={current} allocated bytes.");
        Assert.IsTrue(previous - current > 3 * bufferBytes,
            $"Expected removal of repeated zero buffers; previous={previous}, current={current}.");
    }

    private async Task CreateGroupAsync()
    {
        await using var stream = await InitializedRedoStream.OpenAsync(PathName, 4096);
        await RedoWalCodec.WriteTransactionAsync(stream, 1, Transaction(1), [], default);
        await stream.WriteAsync(RedoWalCodec.GroupMarker(1, 1));
        await stream.CommitAsync();
    }

    private static DurableTransactionEnvelope Transaction(int version) => new(Guid.NewGuid(), new(version),
        Guid.NewGuid(), new(DatabaseProfileId.PropertyGraph, "test", 1, new byte[] { 1, 2, 3 }),
        DurableCommittedChangeKind.System, null);
}
