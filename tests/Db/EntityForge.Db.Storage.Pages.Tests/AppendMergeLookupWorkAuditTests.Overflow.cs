using EntityForge.Db.AccessPaths;

namespace EntityForge.Db.Storage.Pages.Tests;

public sealed partial class AppendMergeLookupWorkAuditTests
{
    [TestMethod]
    [DataRow("sync")]
    [DataRow("async")]
    [DataRow("stream")]
    public async Task Overflow_values_roundtrip_all_writer_and_lookup_paths(string mode)
    {
        var codec = new AppendMergeRunCodec<int, string>(new IntCodec(), new ValidatingStringCodec());
        var writer = new AppendMergeRunWriter<int, string>(codec, new());
        var expected = Enumerable.Range(0, 18).Select(key => new AppendMergeRunEntry<int, string>(
            AppendMergeMutationKind.Add, 3, key, key == 17 ? new string('z', 4 * 1024 * 1024) :
                key % 2 == 0 ? new string((char)('a' + key), 128 * 1024) : "small")).ToArray();
        var allocator = new Allocator();
        var images = new Dictionary<PhysicalPageId, ReadOnlyMemory<byte>>();
        AppendMergeRunDescriptor run;
        if (mode == "stream")
        {
            run = (await writer.WriteStreamingAsync(Source(), expected.Length, 3, allocator, (batch, token) =>
            {
                Assert.IsTrue(batch.Count <= 256);
                foreach (var item in batch)
                {
                    images.Add(item.Key, item.Value);
                }
                return ValueTask.CompletedTask;
            }, default))!;
        }
        else
        {
            var image = mode == "sync" ? writer.Write(expected, 3, allocator) :
                (await writer.WriteAsync(Source(), expected.Length, 3, allocator, default))!;
            run = image.Descriptor;
            foreach (var item in image.Pages)
            {
                images.Add(item.Key, item.Value);
            }
        }
        Assert.IsTrue(run.OverflowExtents.Count > 0);
        Assert.IsTrue(images.Count > 256, "Exercise streaming batch rollover within a single overflow chain.");
        CollectionAssert.AreEquivalent(images.Keys.ToArray(), run.OwnedPages.ToArray());
        var reader = new AppendMergeRunReader<int, string>(new Pages(images), codec);
        var all = new List<AppendMergeRunEntry<int, string>>();
        await foreach (var entry in reader.ReadAsync(run, default))
        {
            all.Add(entry);
        }
        CollectionAssert.AreEqual(expected, all.ToArray());
        all.Clear();
        await foreach (var entry in reader.ReadStreamAsync(run, OrderedIndexRange<int>.Between(4, 12),
            Comparer<int>.Default, StringComparer.Ordinal, default))
        {
            all.Add(entry);
        }
        CollectionAssert.AreEqual(expected[4..13], all.ToArray());
        foreach (int key in new[] { 0, 4, 16, 17 })
        {
            var found = await reader.FindAsync(run, key, Comparer<int>.Default, StringComparer.Ordinal, default);
            CollectionAssert.AreEqual(new[] { expected[key] }, found.ToArray());
        }
        foreach (var keys in new[] { new[] { 4 }, Enumerable.Range(0, 18).ToArray() })
        {
            var found = await reader.FindManyAsync(run, keys, Comparer<int>.Default, default);
            foreach (int key in keys)
            {
                CollectionAssert.AreEqual(new[] { expected[key] }, found[key].ToArray());
            }
        }
        all.Clear();
        AppendMergeRunReadCursor? cursor = null;
        do
        {
            var page = await reader.ReadRangeAsync(run, OrderedIndexRange<int>.All, false, 0, "",
                Comparer<int>.Default, StringComparer.Ordinal, cursor, 3, default);
            all.AddRange(page.Entries);
            cursor = page.Next;
        } while (cursor is not null);
        CollectionAssert.AreEqual(expected, all.ToArray());
        var unowned = run with
        {
            OverflowExtents = []
        };
        await Assert.ThrowsExactlyAsync<PhysicalPageCorruptionException>(async () =>
        {
            await foreach (var entry in reader.ReadAsync(unowned, default))
            {
            }
        });

        async IAsyncEnumerable<AppendMergeRunEntry<int, string>> Source()
        {
            await Task.Yield();
            foreach (var entry in expected)
            {
                yield return entry;
            }
        }
    }

    [TestMethod]
    public void Overflow_record_bound_rejects_before_allocating_pages()
    {
        var codec = new AppendMergeRunCodec<int, string>(new IntCodec(), new ValidatingStringCodec());
        var allocator = new Allocator();
        var writer = new AppendMergeRunWriter<int, string>(codec, new());
        Assert.ThrowsExactly<PhysicalPageCapacityException>(() => writer.Write(
            [new(AppendMergeMutationKind.Add, 1, 0, new string('a', AppendMergeOverflowLimits.MaximumRecordBytes))], 1, allocator));
        Assert.AreEqual(PhysicalPageSpaceDirectory.FirstAccessMethodPageId, allocator.AllocatePage().Value);
    }

    [TestMethod]
    public async Task Exact_sixteen_MiB_encoded_overflow_record_roundtrips()
    {
        var codec = new AppendMergeRunCodec<int, string>(new IntCodec(), new ValidatingStringCodec());
        // The persisted entry header is 18 bytes and this key codec contributes four.
        string value = new('x', 16 * 1024 * 1024 - 22);
        var image = new AppendMergeRunWriter<int, string>(codec, new()).Write(
            [new(AppendMergeMutationKind.Add, 1, 0, value)], 1, new Allocator());
        var reader = new AppendMergeRunReader<int, string>(new Pages(image.Pages), codec);
        var found = await reader.FindAsync(image.Descriptor, 0, Comparer<int>.Default, StringComparer.Ordinal, default);
        Assert.AreEqual(value, found.Single().Value);
    }

    [TestMethod]
    [DataRow(1024)]
    [DataRow(1025)]
    public async Task Overflow_key_size_boundary_is_enforced(int length)
    {
        var codec = new AppendMergeRunCodec<string, string>(new CountingStringCodec(), new ValidatingStringCodec());
        var writer = new AppendMergeRunWriter<string, string>(codec, new());
        var allocator = new Allocator();
        string key = new('k', length);
        string value = new('v', 32768);
        if (length > 1024)
        {
            Assert.ThrowsExactly<PhysicalPageCapacityException>(() => writer.Write(
                [new(AppendMergeMutationKind.Add, 1, key, value)], 1, allocator));
            Assert.AreEqual(PhysicalPageSpaceDirectory.FirstAccessMethodPageId, allocator.AllocatePage().Value);
            return;
        }
        var image = writer.Write([new(AppendMergeMutationKind.Add, 1, key, value)], 1, allocator);
        var reader = new AppendMergeRunReader<string, string>(new Pages(image.Pages), codec);
        var found = await reader.FindAsync(image.Descriptor, key, StringComparer.Ordinal, StringComparer.Ordinal, default);
        Assert.AreEqual(value, found.Single().Value);
    }

    [TestMethod]
    [DataRow("mixed-reference")]
    [DataRow("empty-page")]
    [DataRow("child-checksum")]
    [DataRow("missing-ownership")]
    public async Task Every_read_entrypoint_rejects_corrupt_overflow_runs(string damage)
    {
        var codec = new AppendMergeRunCodec<int, string>(new IntCodec(), new ValidatingStringCodec());
        var image = new AppendMergeRunWriter<int, string>(codec, new()).Write(
            Enumerable.Range(0, 8).Select(key => new AppendMergeRunEntry<int, string>(
                AppendMergeMutationKind.Add, 1, key, new string('x', 32768))).ToArray(), 1, new Allocator());
        var run = image.Descriptor;
        var images = image.Pages.ToDictionary(item => item.Key, item => item.Value);
        var first = run.Pages[0];
        if (damage is "mixed-reference" or "empty-page")
        {
            var page = new SlottedDataPage(PhysicalPageFileOptions.DefaultPageSize, first, 1);
            if (damage == "mixed-reference")
            {
                var reference = SlottedDataPage.ReadOccupiedRecords(images[first], first).Single();
                Assert.IsTrue(page.TryInsert(reference.Span, 1, out _));
                Assert.IsTrue(page.TryInsert(codec.Encode(new(AppendMergeMutationKind.Add, 1, 0, "extra")), 1, out _));
            }
            images[first] = page.Encode();
        }
        else if (damage == "child-checksum")
        {
            var child = run.OverflowExtents[0].FirstPage;
            byte[] bytes = images[child].ToArray();
            bytes[^1] ^= 1;
            images[child] = bytes;
        }
        else
        {
            run = run with
            {
                OverflowExtents = []
            };
        }
        foreach (string mode in new[] { "scan", "stream", "point", "range", "sparse", "dense" })
        {
            var reader = new AppendMergeRunReader<int, string>(new Pages(images), codec);
            await Assert.ThrowsExactlyAsync<PhysicalPageCorruptionException>(async () =>
            {
                switch (mode)
                {
                    case "scan":
                        await foreach (var entry in reader.ReadAsync(run, default))
                        {
                        }
                        break;
                    case "stream":
                        await foreach (var entry in reader.ReadStreamAsync(run, OrderedIndexRange<int>.All,
                            Comparer<int>.Default, StringComparer.Ordinal, default))
                        {
                        }
                        break;
                    case "point":
                        await reader.FindAsync(run, 0, Comparer<int>.Default, StringComparer.Ordinal, default);
                        break;
                    case "range":
                        await reader.ReadRangeAsync(run, OrderedIndexRange<int>.All, false, 0, "",
                        Comparer<int>.Default, StringComparer.Ordinal, null, 1, default);
                        break;
                    case "sparse":
                        await reader.FindManyAsync(run, [0], Comparer<int>.Default, default);
                        break;
                    case "dense":
                        await reader.FindManyAsync(run, Enumerable.Range(0, 8).ToArray(), Comparer<int>.Default, default);
                        break;
                }
            }, $"{damage}: {mode}");
        }
    }
}
