using System.Buffers.Binary;
using System.Text;

namespace EntityForge.Db.Storage.Pages.Tests;

[TestClass]
public sealed class OrderedPageTreeTests
{
    private const int InitialEntryCount = 400;
    private const int PageSize = 31;
    private string? _directory;

    [DataTestMethod]
    [DataRow(1000)]
    [DataRow(10000)]
    [DataRow(100000)]
    public async Task Loading_key_distribution_exposes_leaf_and_catalog_write_work(int population)
    {
        const int TransactionEntities = 64;
        // Match Graph's scalar access-entry widths: 21-byte key, 16-byte value.
        var keys = new LoadingKeyCodec();
        var values = new LoadingValueCodec();
        await using var pages = await PhysicalPageFile.OpenAsync(NewPath());
        var tree = await OrderedPageTree<int, string>.OpenAsync(pages, keys, values,
            valueComparer: StringComparer.Ordinal);
        string Value(int key) => key.ToString("D16");
        Guid seed = Guid.NewGuid();
        await tree.PrepareAsync(seed, Enumerable.Range(0, population).Select(i =>
            new OrderedPageTreeMutation<int, string>(OrderedPageTreeMutationKind.Add, i * 2, Value(i * 2))).ToArray());
        await tree.PublishAsync(seed);
        foreach (bool spread in new[] { false, true })
        {
            var mutations = Enumerable.Range(0, TransactionEntities).Select(i =>
            {
                int key = (spread ? i * (population / TransactionEntities) : population / 2 + i) * 2 + 1;
                return new OrderedPageTreeMutation<int, string>(OrderedPageTreeMutationKind.Add, key, Value(key));
            }).ToArray();
            keys.Calls = values.Calls = 0;
            Guid transaction = Guid.NewGuid();
            var stage = await tree.PrepareAsync(transaction, mutations);
            Console.WriteLine($"population={population}; spread={spread}; pages={stage.PhysicalMutations.Count}; " +
                $"keyEncodes={keys.Calls}; valueEncodes={values.Calls}; catalogDescriptorEncodes={keys.Calls - values.Calls}; " +
                $"afterImageBytes={stage.PhysicalMutations.Sum(m => (long)m.Bytes.Length)}");
            Assert.IsTrue(values.Calls >= TransactionEntities);
            tree.Abandon(transaction);
            Assert.AreEqual(population, tree.Count);
            foreach (var mutation in mutations)
            {
                Assert.AreEqual(0, tree.Find(mutation.Key).Count);
            }
        }
        var reopened = await OrderedPageTree<int, string>.OpenAsync(pages, keys, values,
            valueComparer: StringComparer.Ordinal);
        Assert.AreEqual(population, reopened.Count);
        foreach (int key in Enumerable.Range(0, population).Select(i => i * 2))
        {
            Assert.AreEqual(Value(key), reopened.Find(key).Single());
        }
    }

    private sealed class LoadingKeyCodec : IOrderedIndexCodec<int>
    {
        public int Calls;

        public byte[] Encode(int value)
        {
            Calls++;
            var bytes = new byte[21];
            BinaryPrimitives.WriteInt32BigEndian(bytes, value);
            return bytes;
        }

        public int Decode(ReadOnlySpan<byte> bytes) => BinaryPrimitives.ReadInt32BigEndian(bytes);
    }

    private sealed class LoadingValueCodec : IOrderedIndexCodec<string>
    {
        public int Calls;

        public byte[] Encode(string value)
        {
            Calls++;
            return Encoding.UTF8.GetBytes(value);
        }

        public string Decode(ReadOnlySpan<byte> bytes) => Encoding.UTF8.GetString(bytes);
    }

    [DataTestMethod]
    [DataRow(10000)]
    [DataRow(100000)]
    public async Task Add_batches_preserve_untouched_pages_and_exact_multimap_on_reopen(int population)
    {
        await using var pages = await PhysicalPageFile.OpenAsync(NewPath());
        var tree = await OpenAsync(pages);
        string Value(int i) => "value-" + i.ToString("D6") + new string('x', 80);
        var expected = Enumerable.Range(0, population).ToDictionary(i => i * 2, i => new HashSet<string> { Value(i) });
        Guid seed = Guid.NewGuid();
        var initial = await tree.PrepareAsync(seed, expected.SelectMany(p => p.Value.Select(v =>
            new OrderedPageTreeMutation<int, string>(OrderedPageTreeMutationKind.Add, p.Key, v))).ToArray());
        await tree.PublishAsync(seed);
        var additions = Enumerable.Range(0, 64).Select(i => new OrderedPageTreeMutation<int, string>(
            OrderedPageTreeMutationKind.Add, population + i, Value(population + i))).Concat([
                new(OrderedPageTreeMutationKind.Add, -1, "before-first"),
                new(OrderedPageTreeMutationKind.Add, population * 2 + 1, "after-last")]).ToArray();
        Guid abandoned = Guid.NewGuid();
        await tree.PrepareAsync(abandoned, additions);
        tree.Abandon(abandoned);
        Assert.AreEqual(population, tree.Count);
        Guid commit = Guid.NewGuid();
        var stage = await tree.PrepareAsync(commit, additions.Concat(additions).ToArray());
        Console.WriteLine($"Population={population}; initial pages={initial.PhysicalMutations.Count}; incremental pages={stage.PhysicalMutations.Count}");
        Assert.IsTrue(stage.PhysicalMutations.Count < initial.PhysicalMutations.Count / 4,
            $"Incremental pages {stage.PhysicalMutations.Count}, initial {initial.PhysicalMutations.Count}.");
        await tree.PublishAsync(commit);
        foreach (var addition in additions)
        {
            if (!expected.TryGetValue(addition.Key, out var values))
            {
                expected.Add(addition.Key, values = []);
            }

            values.Add(addition.Value);
        }
        var reopened = await OpenAsync(pages);
        Assert.AreEqual(expected.Sum(p => p.Value.Count), reopened.Count);
        foreach (var pair in expected)
        {
            CollectionAssert.AreEquivalent(pair.Value.ToArray(), reopened.Find(pair.Key).ToArray());
        }

        Guid duplicate = Guid.NewGuid();
        var noChange = await reopened.PrepareAsync(duplicate, additions);
        Assert.IsTrue(noChange.PhysicalMutations.Count < stage.PhysicalMutations.Count);
        await reopened.PublishAsync(duplicate);
        Assert.AreEqual(tree.Count, reopened.Count);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (_directory is not null && Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Uncommitted_first_replacement_keeps_the_selected_file_reopenable(bool abandon)
    {
        string path = NewPath();
        await using (var pages = await PhysicalPageFile.OpenAsync(path))
        {
            var tree = await OpenAsync(pages);
            Guid replacement = Guid.NewGuid();
            await tree.BuildReplacementAsync(replacement,
                Enumerable.Range(0, 400).Select(i => new KeyValuePair<int, string>(i, "value" + i)).ToArray());
            Assert.AreEqual(0L, await pages.GetPageCountAsync());
            if (abandon)
            {
                tree.AbandonReplacement(replacement);
            }
            // Otherwise drop the in-memory preparation, as on process loss before kernel admission.
        }
        await using var reopenedPages = await PhysicalPageFile.OpenAsync(path);
        var reopened = await OpenAsync(reopenedPages);
        Assert.AreEqual(0L, (long)reopened.Count);
        Guid next = Guid.NewGuid();
        await reopened.BuildReplacementAsync(next, new[] { new KeyValuePair<int, string>(7, "seven") });
        var stage = reopened.PrepareReplacement(next, next);
        Assert.IsTrue(stage.PhysicalMutations.Count > 1, "First publication must protect referenced pages as well as the root.");
        await reopened.PublishAsync(next);
        await reopenedPages.FlushAsync();
        var restored = await OpenAsync(reopenedPages);
        Assert.AreEqual("seven", restored.Find(7).Single());
    }

    [TestMethod]
    public async Task Abandoned_first_build_does_not_recycle_nonexistent_pages_into_a_larger_retry()
    {
        await using var pages = await PhysicalPageFile.OpenAsync(NewPath());
        var tree = await OpenAsync(pages);
        Guid first = Guid.NewGuid(), second = Guid.NewGuid();
        await tree.BuildReplacementAsync(first, Enumerable.Range(0, 400)
            .Select(i => new KeyValuePair<int, string>(i, "value" + i)).ToArray());
        tree.AbandonReplacement(first);
        await tree.BuildReplacementAsync(second, Enumerable.Range(0, 2000)
            .Select(i => new KeyValuePair<int, string>(i, "value" + i)).ToArray());
        Assert.AreEqual(0L, await pages.GetPageCountAsync());
        tree.PrepareReplacement(second, second);
        await tree.PublishAsync(second);
        await pages.FlushAsync();
        var reopened = await OpenAsync(pages);
        Assert.AreEqual(2000L, (long)reopened.Count);
        Assert.AreEqual("value1999", reopened.Find(1999).Single());
    }

    [TestMethod]
    public async Task Initial_image_budget_rejects_before_file_growth_and_allows_smaller_retry()
    {
        await using var pages = await PhysicalPageFile.OpenAsync(NewPath());
        var tree = await OpenAsync(pages);
        tree.MaximumInitialImageBytes = pages.PageSize;
        await Assert.ThrowsExceptionAsync<OrderedPageTreeException>(async () =>
            await tree.BuildReplacementAsync(Guid.NewGuid(), new[] { new KeyValuePair<int, string>(1, "one") }));
        Assert.AreEqual(0L, await pages.GetPageCountAsync());
        tree.MaximumInitialImageBytes = 4L * pages.PageSize;
        Guid retry = Guid.NewGuid();
        await tree.BuildReplacementAsync(retry, new[] { new KeyValuePair<int, string>(1, "one") });
        tree.PrepareReplacement(retry, retry);
        await tree.PublishAsync(retry);
        await pages.FlushAsync();
        Assert.AreEqual("one", (await OpenAsync(pages)).Find(1).Single());
    }

    [TestMethod]
    public async Task Incremental_mutations_publish_selected_format_topology_and_recover_without_rebuild()
    {
        string path = NewPath();
        await using (PhysicalPageFile pages = await PhysicalPageFile.OpenAsync(path))
        {
            OrderedPageTree<int, string> tree = await OpenAsync(pages);
            OrderedPageTreeMutation<int, string>[] initial = Enumerable.Range(0, InitialEntryCount)
                .Select(value => new OrderedPageTreeMutation<int, string>(OrderedPageTreeMutationKind.Add,
                    value / 4, $"value-{value:D4}-{new string('x', 80)}")).ToArray();
            Guid transaction = Guid.NewGuid();
            OrderedPageTreeStage staged = await tree.PrepareAsync(transaction, initial);
            Assert.IsTrue(staged.PhysicalMutations.Count > 2);
            await tree.PublishAsync(transaction);

            Guid incrementalTransaction = Guid.NewGuid();
            OrderedPageTreeStage incremental = await tree.PrepareAsync(incrementalTransaction,
                [new(OrderedPageTreeMutationKind.Add, InitialEntryCount, "incremental")]);
            Assert.IsTrue(incremental.PhysicalMutations.Count <= 3,
                "A non-splitting mutation should touch the catalog, target leaf and at most one linked neighbour.");
            await tree.PublishAsync(incrementalTransaction);
            Assert.AreEqual("incremental", tree.Find(InitialEntryCount).Single());
        }

        await using PhysicalPageFile reopenedPages = await PhysicalPageFile.OpenAsync(path);
        OrderedPageTree<int, string> reopened = await OpenAsync(reopenedPages);
        Assert.AreEqual("incremental", reopened.Find(InitialEntryCount).Single());
        Assert.AreEqual(InitialEntryCount + 1, reopened.Count);
    }

    [TestMethod]
    public async Task Wal_protected_staged_writes_defer_the_page_file_flush_until_the_durability_barrier()
    {
        string path = NewPath();
        await using var pages = new CountingPageStore(await PhysicalPageFile.OpenAsync(path));
        OrderedPageTree<int, string> tree = await OpenAsync(pages);
        Guid transaction = Guid.NewGuid();
        await tree.PrepareAsync(transaction, [new(OrderedPageTreeMutationKind.Add, 1, "one")]);

        await tree.WriteStagedAsync(transaction);
        Assert.AreEqual(0, pages.Flushes);
        tree.Publish(transaction);
        Assert.AreEqual("one", tree.Find(1).Single());

        await tree.FlushAsync();
        Assert.AreEqual(1, pages.Flushes);
    }

    [TestMethod]
    public async Task Cursor_is_generation_bound_and_duplicate_values_page_without_loss()
    {
        string path = NewPath();
        await using PhysicalPageFile pages = await PhysicalPageFile.OpenAsync(path);
        OrderedPageTree<int, string> tree = await OpenAsync(pages);
        Guid first = Guid.NewGuid();
        await tree.PrepareAsync(first, Enumerable.Range(0, 90)
            .Select(value => new OrderedPageTreeMutation<int, string>(OrderedPageTreeMutationKind.Add,
                value / 3, $"value-{value:D3}")).ToArray());
        await tree.PublishAsync(first);
        var items = new List<KeyValuePair<int, string>>();
        OrderedPageTreeCursor<int, string>? cursor = null;
        do
        {
            OrderedPageTreePage<int, string> page = tree.ReadPage(PageSize, cursor: cursor);
            items.AddRange(page.Items);
            cursor = page.NextCursor;
        } while (cursor is not null);
        Assert.AreEqual(90, items.Count);

        OrderedPageTreeCursor<int, string> stale = tree.ReadPage(2).NextCursor!;
        Guid second = Guid.NewGuid();
        await tree.PrepareAsync(second, [new(OrderedPageTreeMutationKind.Add, 100, "later")]);
        await tree.PublishAsync(second);
        Assert.ThrowsException<ArgumentException>(() => tree.ReadPage(2, cursor: stale));
    }

    [TestMethod]
    public async Task Abandoned_generation_neither_publishes_nor_changes_the_page_file()
    {
        string path = NewPath();
        await using PhysicalPageFile pages = await PhysicalPageFile.OpenAsync(path);
        OrderedPageTree<int, string> tree = await OpenAsync(pages);
        Guid transaction = Guid.NewGuid();
        await tree.PrepareAsync(transaction, [new(OrderedPageTreeMutationKind.Add, 1, "one")]);
        tree.Abandon(transaction);

        Assert.AreEqual(0, tree.Count);
        Assert.AreEqual(0L, await pages.GetPageCountAsync());
    }

    [TestMethod]
    public async Task Snapshot_is_generation_stable_and_catalog_pages_are_reused()
    {
        string path = NewPath();
        await using PhysicalPageFile pages = await PhysicalPageFile.OpenAsync(path);
        OrderedPageTree<int, string> tree = await OpenAsync(pages);
        Guid initial = Guid.NewGuid();
        await tree.PrepareAsync(initial, Enumerable.Range(0, InitialEntryCount)
            .Select(value => new OrderedPageTreeMutation<int, string>(OrderedPageTreeMutationKind.Add,
                value, $"value-{value:D4}-{new string('x', 80)}")).ToArray());
        await tree.PublishAsync(initial);
        OrderedPageTreeSnapshot<int, string> snapshot = tree.CaptureSnapshot();

        Guid first = Guid.NewGuid();
        await tree.PrepareAsync(first, [new(OrderedPageTreeMutationKind.Add, -2, "first")]);
        await tree.PublishAsync(first);
        long afterFirst = await pages.GetPageCountAsync();
        Guid second = Guid.NewGuid();
        await tree.PrepareAsync(second, [new(OrderedPageTreeMutationKind.Add, -1, "second")]);
        await tree.PublishAsync(second);

        Assert.AreEqual(afterFirst, await pages.GetPageCountAsync(),
            "Stable catalog topology must reuse its branch pages.");
        Assert.AreEqual(InitialEntryCount, snapshot.Count);
        Assert.AreEqual(0, snapshot.Find(-1).Count);
        Assert.AreNotEqual(snapshot.Generation, tree.Generation);
    }

    [TestMethod]
    public async Task Deletion_merges_adjacent_leaves_when_the_combined_bytes_fit()
    {
        string path = NewPath();
        await using PhysicalPageFile pages = await PhysicalPageFile.OpenAsync(path);
        OrderedPageTree<int, string> tree = await OpenAsync(pages);
        OrderedPageTreeMutation<int, string>[] additions = Enumerable.Range(0, InitialEntryCount)
            .Select(value => new OrderedPageTreeMutation<int, string>(OrderedPageTreeMutationKind.Add,
                value, $"value-{value:D4}-{new string('x', 80)}")).ToArray();
        Guid initial = Guid.NewGuid();
        await tree.PrepareAsync(initial, additions);
        await tree.PublishAsync(initial);

        Guid deletion = Guid.NewGuid();
        await tree.PrepareAsync(deletion, additions.Take(InitialEntryCount - 2)
            .Select(item => item with { Kind = OrderedPageTreeMutationKind.Remove }).ToArray());
        await tree.PublishAsync(deletion);

        Assert.AreEqual(2, tree.Count);
        Assert.AreEqual(2, tree.ReadPage(PageSize).Items.Count);
    }

    [TestMethod]
    public async Task Deleting_the_final_entry_persists_an_empty_tree_that_can_be_reused_after_restart()
    {
        string path = NewPath();
        await using (PhysicalPageFile pages = await PhysicalPageFile.OpenAsync(path))
        {
            OrderedPageTree<int, string> tree = await OpenAsync(pages);
            Guid addition = Guid.NewGuid();
            await tree.PrepareAsync(addition, [new(OrderedPageTreeMutationKind.Add, 1, "one")]);
            await tree.PublishAsync(addition);

            Guid deletion = Guid.NewGuid();
            await tree.PrepareAsync(deletion, [new(OrderedPageTreeMutationKind.Remove, 1, "one")]);
            await tree.PublishAsync(deletion);

            Assert.AreEqual(0, tree.Count);
            Assert.AreEqual(0, tree.ReadPage(PageSize).Items.Count);
        }

        await using PhysicalPageFile reopenedPages = await PhysicalPageFile.OpenAsync(path);
        OrderedPageTree<int, string> reopened = await OpenAsync(reopenedPages);
        Assert.AreEqual(0, reopened.Count);

        Guid refill = Guid.NewGuid();
        await reopened.PrepareAsync(refill, [new(OrderedPageTreeMutationKind.Add, 2, "two")]);
        await reopened.PublishAsync(refill);
        Assert.AreEqual("two", reopened.Find(2).Single());
    }

    [TestMethod]
    public async Task Freed_leaf_pages_are_catalogued_across_restart_and_reused_without_file_growth()
    {
        string path = NewPath();
        long pagesAfterDelete;
        await using (PhysicalPageFile pages = await PhysicalPageFile.OpenAsync(path))
        {
            OrderedPageTree<int, string> tree = await OpenAsync(pages);
            OrderedPageTreeMutation<int, string>[] additions = Enumerable.Range(0, InitialEntryCount)
                .Select(value => new OrderedPageTreeMutation<int, string>(OrderedPageTreeMutationKind.Add,
                    value, $"value-{value:D4}-{new string('x', 80)}")).ToArray();
            Guid initial = Guid.NewGuid();
            await tree.PrepareAsync(initial, additions);
            await tree.PublishAsync(initial);
            Guid deletion = Guid.NewGuid();
            await tree.PrepareAsync(deletion, additions.Take(InitialEntryCount / 2)
                .Select(item => item with { Kind = OrderedPageTreeMutationKind.Remove }).ToArray());
            await tree.PublishAsync(deletion);
            pagesAfterDelete = await pages.GetPageCountAsync();
        }

        await using PhysicalPageFile reopenedPages = await PhysicalPageFile.OpenAsync(path);
        OrderedPageTree<int, string> reopened = await OpenAsync(reopenedPages);
        Guid refill = Guid.NewGuid();
        await reopened.PrepareAsync(refill, Enumerable.Range(InitialEntryCount, InitialEntryCount / 2)
            .Select(value => new OrderedPageTreeMutation<int, string>(OrderedPageTreeMutationKind.Add,
                value, $"value-{value:D4}-{new string('x', 80)}")).ToArray());
        await reopened.PublishAsync(refill);

        Assert.AreEqual(pagesAfterDelete, await reopenedPages.GetPageCountAsync(),
            "Restarted allocation should consume durable free leaf pages before extending the file.");
    }

    [TestMethod]
    public async Task Flushed_replacement_remains_invisible_until_root_cutover_and_survives_restart()
    {
        string path = NewPath();
        await using (PhysicalPageFile pages = await PhysicalPageFile.OpenAsync(path))
        {
            OrderedPageTree<int, string> tree = await OpenAsync(pages);
            Guid initial = Guid.NewGuid();
            await tree.PrepareAsync(initial,
                [new(OrderedPageTreeMutationKind.Add, 1, "before")]);
            await tree.PublishAsync(initial);
            OrderedPageTreeSnapshot<int, string> retained = tree.CaptureSnapshot();

            Guid replacement = Guid.NewGuid();
            await tree.BuildReplacementAsync(replacement,
                [new(2, "after"), new(3, "third")]);
            Assert.AreEqual("before", tree.Find(1).Single());
            Assert.AreEqual(0, tree.Find(2).Count);

            Guid cutover = Guid.NewGuid();
            OrderedPageTreeStage stage = tree.PrepareReplacement(cutover, replacement);
            Assert.AreEqual(1, stage.PhysicalMutations.Count,
                "Only the already-flushed replacement root participates in cutover redo.");
            await tree.PublishAsync(cutover);

            Assert.AreEqual("before", retained.Find(1).Single());
            Assert.AreEqual("after", tree.Find(2).Single());
            Assert.AreEqual(0, tree.Find(1).Count);
        }

        await using PhysicalPageFile reopenedPages = await PhysicalPageFile.OpenAsync(path);
        OrderedPageTree<int, string> reopened = await OpenAsync(reopenedPages);
        Assert.AreEqual("after", reopened.Find(2).Single());
        Assert.AreEqual("third", reopened.Find(3).Single());
    }

    [TestMethod]
    public async Task Replacement_rejects_a_changed_live_generation()
    {
        string path = NewPath();
        await using PhysicalPageFile pages = await PhysicalPageFile.OpenAsync(path);
        OrderedPageTree<int, string> tree = await OpenAsync(pages);
        Guid initial = Guid.NewGuid();
        await tree.PrepareAsync(initial, [new(OrderedPageTreeMutationKind.Add, 1, "one")]);
        await tree.PublishAsync(initial);

        Guid replacement = Guid.NewGuid();
        await tree.BuildReplacementAsync(replacement, [new(1, "one")]);
        Guid concurrent = Guid.NewGuid();
        await tree.PrepareAsync(concurrent, [new(OrderedPageTreeMutationKind.Add, 2, "two")]);
        await tree.PublishAsync(concurrent);

        Assert.ThrowsException<OrderedPageTreeReplacementStaleException>(() =>
            tree.PrepareReplacement(Guid.NewGuid(), replacement));
    }

    [TestMethod]
    public async Task Replacement_reservations_are_disjoint_from_concurrent_foreground_allocation()
    {
        string path = NewPath();
        var pages = new BlockingWritePageStore(await PhysicalPageFile.OpenAsync(path));
        await using (pages)
        {
            OrderedPageTree<int, string> tree = await OpenAsync(pages);
            Guid initial = Guid.NewGuid();
            await tree.PrepareAsync(initial, Enumerable.Range(0, 300)
                .Select(value => new OrderedPageTreeMutation<int, string>(OrderedPageTreeMutationKind.Add,
                    value, $"value-{value:D3}-{new string('x', 80)}")).ToArray());
            await tree.PublishAsync(initial);

            pages.Arm();
            Guid replacement = Guid.NewGuid();
            Task replacementBuild = tree.BuildReplacementAsync(replacement,
                Enumerable.Range(0, 300).Select(value =>
                    new KeyValuePair<int, string>(value, $"replacement-{value:D3}-{new string('y', 80)}")).ToArray())
                .AsTask();
            PhysicalPageId replacementPage = await pages.WriteBlocked;

            Guid foreground = Guid.NewGuid();
            OrderedPageTreeStage foregroundStage = await tree.PrepareAsync(foreground,
                [new(OrderedPageTreeMutationKind.Add, 400, "foreground")]);
            Assert.IsFalse(foregroundStage.PhysicalMutations.Any(item => item.PageId == replacementPage.Value));

            pages.Release();
            await replacementBuild;
            await tree.PublishAsync(foreground);
            Assert.ThrowsException<OrderedPageTreeReplacementStaleException>(() =>
                tree.PrepareReplacement(Guid.NewGuid(), replacement));
            tree.AbandonReplacement(replacement);
            Assert.AreEqual("foreground", tree.Find(400).Single());
        }

        await using PhysicalPageFile reopenedPages = await PhysicalPageFile.OpenAsync(path);
        OrderedPageTree<int, string> reopened = await OpenAsync(reopenedPages);
        Assert.AreEqual("foreground", reopened.Find(400).Single());
    }

    [TestMethod]
    public async Task Replacement_sorts_and_removes_duplicate_entries()
    {
        string path = NewPath();
        await using PhysicalPageFile pages = await PhysicalPageFile.OpenAsync(path);
        OrderedPageTree<int, string> tree = await OpenAsync(pages);
        Guid replacement = Guid.NewGuid();

        await tree.BuildReplacementAsync(replacement,
            [new(3, "three"), new(1, "one"), new(2, "two"), new(1, "one"), new(2, "second")]);
        Guid cutover = Guid.NewGuid();
        _ = tree.PrepareReplacement(cutover, replacement);
        await tree.PublishAsync(cutover);

        Assert.AreEqual("one", tree.Find(1).Single());
        CollectionAssert.AreEqual(new[] { "second", "two" }, tree.Find(2).ToArray());
        Assert.AreEqual("three", tree.Find(3).Single());
        CollectionAssert.AreEqual(new[] { "one", "second", "two", "three" },
            tree.FindMany([1, 2, 3]).ToArray());
    }

    [TestMethod]
    public async Task Repeated_replacements_reuse_retired_generations_without_extending_the_page_file()
    {
        string path = NewPath();
        await using PhysicalPageFile pages = await PhysicalPageFile.OpenAsync(path);
        OrderedPageTree<int, string> tree = await OpenAsync(pages);
        KeyValuePair<int, string>[] entries = Enumerable.Range(0, 2_000)
            .Select(value => new KeyValuePair<int, string>(value, $"value-{value:D4}-{new string('x', 80)}"))
            .ToArray();

        await ReplaceAsync(tree, entries);
        await ReplaceAsync(tree, entries);
        long reusableEnvelope = await pages.GetPageCountAsync();

        await ReplaceAsync(tree, entries);
        await ReplaceAsync(tree, entries);

        Assert.AreEqual(reusableEnvelope, await pages.GetPageCountAsync(),
            "Once two safe generations exist, replacement should alternate between them instead of growing the file.");
        Assert.AreEqual(entries[^1].Value, tree.Find(entries[^1].Key).Single());
    }

    [TestMethod]
    public async Task Prepared_replacement_pages_remain_reserved_until_root_publication()
    {
        string path = NewPath();
        KeyValuePair<int, string>[] first = Enumerable.Range(0, 1_000)
            .Select(value => new KeyValuePair<int, string>(value, $"first-{value:D4}-{new string('a', 80)}"))
            .ToArray();
        KeyValuePair<int, string>[] second = first.Select(item =>
            new KeyValuePair<int, string>(item.Key, item.Value.Replace("first", "second"))).ToArray();
        KeyValuePair<int, string>[] competing = first.Select(item =>
            new KeyValuePair<int, string>(item.Key, item.Value.Replace("first", "competing"))).ToArray();
        await using (PhysicalPageFile pages = await PhysicalPageFile.OpenAsync(path))
        {
            OrderedPageTree<int, string> tree = await OpenAsync(pages);
            await ReplaceAsync(tree, first);
            await ReplaceAsync(tree, first);
            Guid replacement = Guid.NewGuid();
            await tree.BuildReplacementAsync(replacement, second);
            Guid cutover = Guid.NewGuid();
            _ = tree.PrepareReplacement(cutover, replacement);

            Guid laterReplacement = Guid.NewGuid();
            await tree.BuildReplacementAsync(laterReplacement, competing);
            await tree.PublishAsync(cutover);
            tree.AbandonReplacement(laterReplacement);
            Assert.AreEqual(second[^1].Value, tree.Find(second[^1].Key).Single());
        }

        await using PhysicalPageFile reopenedPages = await PhysicalPageFile.OpenAsync(path);
        OrderedPageTree<int, string> reopened = await OpenAsync(reopenedPages);
        Assert.AreEqual(second[^1].Value, reopened.Find(second[^1].Key).Single());
    }

    private static async ValueTask ReplaceAsync(OrderedPageTree<int, string> tree,
        IReadOnlyList<KeyValuePair<int, string>> entries)
    {
        Guid replacement = Guid.NewGuid();
        await tree.BuildReplacementAsync(replacement, entries);
        Guid cutover = Guid.NewGuid();
        _ = tree.PrepareReplacement(cutover, replacement);
        await tree.PublishAsync(cutover);
    }

    private static ValueTask<OrderedPageTree<int, string>> OpenAsync(PhysicalPageFile pages) =>
        OpenAsync((IResizablePhysicalPageStore)pages);

    private static ValueTask<OrderedPageTree<int, string>> OpenAsync(IResizablePhysicalPageStore pages) =>
        OrderedPageTree<int, string>.OpenAsync(pages, new IntCodec(), new StringCodec(),
            valueComparer: StringComparer.Ordinal);

    private string NewPath()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"entityforge-tree-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        return Path.Combine(_directory, "tree.pages");
    }

    private sealed class IntCodec : IOrderedIndexCodec<int>
    {
        public byte[] Encode(int value)
        {
            byte[] result = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32BigEndian(result, value);
            return result;
        }

        public int Decode(ReadOnlySpan<byte> value) => BinaryPrimitives.ReadInt32BigEndian(value);
    }

    private sealed class StringCodec : IOrderedIndexCodec<string>
    {
        public byte[] Encode(string value) => Encoding.UTF8.GetBytes(value);

        public string Decode(ReadOnlySpan<byte> value) => Encoding.UTF8.GetString(value);
    }

    private sealed class CountingPageStore(IResizablePhysicalPageStore inner) : IResizablePhysicalPageStore
    {
        public int Flushes
        {
            get; private set;
        }

        public int PageSize => inner.PageSize;

        public ValueTask<ReadOnlyMemory<byte>> ReadAsync(PhysicalPageId pageId,
            CancellationToken cancellationToken = default) => inner.ReadAsync(pageId, cancellationToken);

        public ValueTask WriteAsync(PhysicalPageId pageId, ReadOnlyMemory<byte> page,
            CancellationToken cancellationToken = default) => inner.WriteAsync(pageId, page, cancellationToken);

        public async ValueTask FlushAsync(CancellationToken cancellationToken = default)
        {
            Flushes++;
            await inner.FlushAsync(cancellationToken);
        }

        public ValueTask<long> GetPageCountAsync(CancellationToken cancellationToken = default) =>
            inner.GetPageCountAsync(cancellationToken);

        public ValueTask EnsureCapacityAsync(long pageCount, CancellationToken cancellationToken = default) =>
            inner.EnsureCapacityAsync(pageCount, cancellationToken);

        public ValueTask TruncateAsync(long pageCount, CancellationToken cancellationToken = default) =>
            inner.TruncateAsync(pageCount, cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class BlockingWritePageStore(IResizablePhysicalPageStore inner) : IResizablePhysicalPageStore
    {
        private readonly TaskCompletionSource<PhysicalPageId> _blocked =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _armed;

        public Task<PhysicalPageId> WriteBlocked => _blocked.Task;

        public int PageSize => inner.PageSize;

        public void Arm() => Volatile.Write(ref _armed, 1);

        public void Release() => _release.TrySetResult();

        public ValueTask<ReadOnlyMemory<byte>> ReadAsync(PhysicalPageId pageId,
            CancellationToken cancellationToken = default) => inner.ReadAsync(pageId, cancellationToken);

        public async ValueTask WriteAsync(PhysicalPageId pageId, ReadOnlyMemory<byte> page,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _armed, 0) != 0)
            {
                _blocked.TrySetResult(pageId);
                await _release.Task.WaitAsync(cancellationToken);
            }
            await inner.WriteAsync(pageId, page, cancellationToken);
        }

        public ValueTask FlushAsync(CancellationToken cancellationToken = default) => inner.FlushAsync(cancellationToken);

        public ValueTask<long> GetPageCountAsync(CancellationToken cancellationToken = default) =>
            inner.GetPageCountAsync(cancellationToken);

        public ValueTask EnsureCapacityAsync(long pageCount, CancellationToken cancellationToken = default) =>
            inner.EnsureCapacityAsync(pageCount, cancellationToken);

        public ValueTask TruncateAsync(long pageCount, CancellationToken cancellationToken = default) =>
            inner.TruncateAsync(pageCount, cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
