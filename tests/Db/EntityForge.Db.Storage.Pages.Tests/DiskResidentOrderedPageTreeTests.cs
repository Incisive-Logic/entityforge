using System.Buffers.Binary;
using System.Text;
using EntityForge.Db.AccessPaths;
using EntityForge.Db.Storage;

namespace EntityForge.Db.Storage.Pages.Tests;

[TestClass]
public sealed class DiskResidentOrderedPageTreeTests
{
    private static readonly PhysicalAccessPathId AccessPathId = new(Guid.Parse("70819041-cf78-48f0-95a0-6327451a653b"));
    private static readonly PhysicalAccessMethodKind AccessMethod = new("ordered.test", 1);
    private static readonly Dictionary<object, PhysicalPageSpace> PageSpaces = [];
    private static readonly Dictionary<(object Tree, Guid Transaction), OrderedPageTreeStage> Stages = [];
    private const int EntryCount = 1_000;
    private const int QueryPageSize = 37;
    private string? _directory;

    [TestCleanup]
    public void Cleanup()
    {
        if (_directory is not null && Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }

    [TestMethod]
    public async Task Incremental_catalog_publication_preserves_reused_pages_after_reopen()
    {
        string path = NewPath();
        string value = new('x', 2000);
        await using (PhysicalPageFile pages = await PhysicalPageFile.OpenAsync(path))
        {
            var tree = await OpenAsync(pages);
            Guid first = Guid.NewGuid();
            await PrepareAsync(tree, first, Enumerable.Range(0, 1000).Select(index =>
                new OrderedPageTreeMutation<int, string>(OrderedPageTreeMutationKind.Add, index, value)).ToArray());
            await PublishAsync(tree, first);
            using var retained = tree.CaptureSnapshot();
            Guid second = Guid.NewGuid();
            await PrepareAsync(tree, second, [new(OrderedPageTreeMutationKind.Add, 500, "extra")]);
            await PublishAsync(tree, second);
            CollectionAssert.AreEqual(new[] { value }, (await retained.FindAsync(500)).ToArray());
            using var current = tree.CaptureSnapshot();
            Assert.AreEqual(2, (await current.FindAsync(500)).Count);
        }
        await using var reopenedPages = await PhysicalPageFile.OpenAsync(path);
        var reopened = await OpenAsync(reopenedPages);
        using var snapshot = reopened.CaptureSnapshot();
        Assert.AreEqual(1001L, reopened.Count);
        Assert.AreEqual(2, (await snapshot.FindAsync(500)).Count);
        CollectionAssert.AreEqual(new[] { value }, (await snapshot.FindAsync(999)).ToArray());
    }

    [TestMethod]
    public async Task Published_state_is_descriptor_only_and_reopens_without_leaf_warmup()
    {
        string path = NewPath();
        await using (PhysicalPageFile pages = await PhysicalPageFile.OpenAsync(path))
        {
            DiskResidentOrderedPageTree<int, string> tree = await OpenAsync(pages);
            Guid transaction = Guid.NewGuid();
            await PrepareAsync(tree, transaction, Enumerable.Range(0, EntryCount)
                .Select(index => new OrderedPageTreeMutation<int, string>(OrderedPageTreeMutationKind.Add,
                    index / 5, $"value-{index:D5}-{new string('x', 40)}")).ToArray());
            await PublishAsync(tree, transaction);
            Assert.AreEqual(EntryCount, tree.Count);
            await using DiskResidentOrderedPageTreeSnapshot<int, string> snapshot = tree.CaptureSnapshot();
            IReadOnlyList<string> duplicates = await snapshot.FindAsync(20);
            Assert.AreEqual(5, duplicates.Count);
        }

        await using PhysicalPageFile reopenedPages = await PhysicalPageFile.OpenAsync(path);
        DiskResidentOrderedPageTree<int, string> reopened = await OpenAsync(reopenedPages);
        Assert.AreEqual(EntryCount, reopened.Count);
        await using DiskResidentOrderedPageTreeSnapshot<int, string> reopenedSnapshot = reopened.CaptureSnapshot();
        Assert.AreEqual(5, (await reopenedSnapshot.FindAsync(20)).Count);
    }

    [TestMethod]
    public async Task Copy_on_write_snapshot_remains_stable_after_later_publication()
    {
        string path = NewPath();
        await using PhysicalPageFile pages = await PhysicalPageFile.OpenAsync(path);
        DiskResidentOrderedPageTree<int, string> tree = await OpenAsync(pages);
        Guid first = Guid.NewGuid();
        await PrepareAsync(tree, first, [new(OrderedPageTreeMutationKind.Add, 1, "before")]);
        await PublishAsync(tree, first);
        await using DiskResidentOrderedPageTreeSnapshot<int, string> snapshot = tree.CaptureSnapshot();

        Guid second = Guid.NewGuid();
        await PrepareAsync(tree, second,
            [new(OrderedPageTreeMutationKind.Remove, 1, "before"),
                new(OrderedPageTreeMutationKind.Add, 1, "after")]);
        await PublishAsync(tree, second);

        CollectionAssert.AreEqual(new[] { "before" }, (await snapshot.FindAsync(1)).ToArray());
        await using DiskResidentOrderedPageTreeSnapshot<int, string> current = tree.CaptureSnapshot();
        CollectionAssert.AreEqual(new[] { "after" }, (await current.FindAsync(1)).ToArray());
    }

    [TestMethod]
    public async Task Unordered_mutations_route_across_leaves_and_preserve_equal_composite_sequence()
    {
        string path = NewPath();
        await using PhysicalPageFile pages = await PhysicalPageFile.OpenAsync(path);
        DiskResidentOrderedPageTree<int, string> tree = await OpenAsync(pages);
        Guid initial = Guid.NewGuid();
        await PrepareAsync(tree, initial, Enumerable.Range(0, EntryCount)
            .Select(index => new OrderedPageTreeMutation<int, string>(OrderedPageTreeMutationKind.Add,
                index, $"value-{index:D5}-{new string('x', 40)}")).ToArray());
        await PublishAsync(tree, initial);
        Guid update = Guid.NewGuid();
        var mutations = new List<OrderedPageTreeMutation<int, string>>();
        for (int key = EntryCount - 1; key >= 0; key -= 10)
        {
            string value = $"value-{key:D5}-{new string('x', 40)}";
            mutations.Add(new(OrderedPageTreeMutationKind.Remove, key, value));
            mutations.Add(new(OrderedPageTreeMutationKind.Add, key, value));
        }

        await PrepareAsync(tree, update, mutations);
        await PublishAsync(tree, update);

        Assert.AreEqual(EntryCount, tree.Count);
        await using DiskResidentOrderedPageTreeSnapshot<int, string> snapshot = tree.CaptureSnapshot();
        CollectionAssert.AreEqual(new[] { "value-00000-" + new string('x', 40) },
            (await snapshot.FindAsync(0)).ToArray());
        CollectionAssert.AreEqual(new[] { "value-00990-" + new string('x', 40) },
            (await snapshot.FindAsync(990)).ToArray());
    }

    [TestMethod]
    public async Task Cursor_pages_duplicate_composite_entries_without_loss()
    {
        string path = NewPath();
        await using PhysicalPageFile pages = await PhysicalPageFile.OpenAsync(path);
        DiskResidentOrderedPageTree<int, string> tree = await OpenAsync(pages);
        Guid transaction = Guid.NewGuid();
        await PrepareAsync(tree, transaction, Enumerable.Range(0, EntryCount)
            .Select(index => new OrderedPageTreeMutation<int, string>(OrderedPageTreeMutationKind.Add,
                index / 7, $"value-{index:D5}")).ToArray());
        await PublishAsync(tree, transaction);
        await using DiskResidentOrderedPageTreeSnapshot<int, string> snapshot = tree.CaptureSnapshot();
        var values = new List<KeyValuePair<int, string>>();
        OrderedPageTreeCursor<int, string>? cursor = null;
        do
        {
            OrderedPageTreePage<int, string> page = await snapshot.ReadPageAsync(QueryPageSize, cursor: cursor);
            values.AddRange(page.Items);
            cursor = page.NextCursor;
        }
        while (cursor is not null);
        Assert.AreEqual(EntryCount, values.Count);
        Assert.AreEqual(EntryCount, values.Distinct().Count());
    }

    [TestMethod]
    public async Task Idempotent_add_does_not_rewrite_pages_or_advance_generation()
    {
        string path = NewPath();
        await using PhysicalPageFile pages = await PhysicalPageFile.OpenAsync(path);
        DiskResidentOrderedPageTree<int, string> tree = await OpenAsync(pages);
        Guid first = Guid.NewGuid();
        await PrepareAsync(tree, first, [new(OrderedPageTreeMutationKind.Add, 1, "value")]);
        await PublishAsync(tree, first);
        OrderedPageTreeGeneration generation = tree.Generation;

        Guid duplicate = Guid.NewGuid();
        OrderedPageTreeStage stage = await PrepareAsync(tree, duplicate,
            [new(OrderedPageTreeMutationKind.Add, 1, "value")]);
        tree.Publish(duplicate);

        Assert.AreEqual(0, stage.PhysicalMutations.Count);
        Assert.AreEqual(generation, tree.Generation);
    }

    [TestMethod]
    public async Task Unique_batch_lookup_reads_keys_across_multiple_leaves()
    {
        string path = NewPath();
        await using PhysicalPageFile pages = await PhysicalPageFile.OpenAsync(path);
        DiskResidentOrderedPageTree<int, string> tree = await OpenAsync(pages);
        Guid transaction = Guid.NewGuid();
        await PrepareAsync(tree, transaction, Enumerable.Range(0, EntryCount)
            .Select(index => new OrderedPageTreeMutation<int, string>(OrderedPageTreeMutationKind.Add,
                index, $"value-{index:D5}-{new string('x', 40)}")).ToArray());
        await PublishAsync(tree, transaction);
        await using DiskResidentOrderedPageTreeSnapshot<int, string> snapshot = tree.CaptureSnapshot();

        IReadOnlyDictionary<int, string> actual = await snapshot.FindUniqueAsync([999, 3, 500, 3]);

        Assert.AreEqual(3, actual.Count);
        Assert.AreEqual("value-00003-" + new string('x', 40), actual[3]);
        Assert.AreEqual("value-00500-" + new string('x', 40), actual[500]);
        Assert.AreEqual("value-00999-" + new string('x', 40), actual[999]);
    }

    [TestMethod]
    public async Task Duplicate_batch_lookup_preserves_values_and_reports_missing_keys()
    {
        string path = NewPath();
        await using PhysicalPageFile pages = await PhysicalPageFile.OpenAsync(path);
        DiskResidentOrderedPageTree<int, string> tree = await OpenAsync(pages);
        Guid transaction = Guid.NewGuid();
        await PrepareAsync(tree, transaction,
            [new(OrderedPageTreeMutationKind.Add, 1, "alpha"),
                new(OrderedPageTreeMutationKind.Add, 1, "beta"),
                new(OrderedPageTreeMutationKind.Add, 3, "gamma")]);
        await PublishAsync(tree, transaction);
        await using DiskResidentOrderedPageTreeSnapshot<int, string> snapshot = tree.CaptureSnapshot();

        IReadOnlyDictionary<int, IReadOnlyList<string>> actual =
            await snapshot.FindManyAsync([3, 1, 2, 1]);

        Assert.AreEqual(2, actual.Count);
        CollectionAssert.AreEqual(new[] { "alpha", "beta" }, actual[1].ToArray());
        Assert.IsFalse(actual.ContainsKey(2));
        CollectionAssert.AreEqual(new[] { "gamma" }, actual[3].ToArray());
    }

    [TestMethod]
    public async Task Bounded_scan_returns_only_entries_from_candidate_leaf_range()
    {
        string path = NewPath();
        await using PhysicalPageFile pages = await PhysicalPageFile.OpenAsync(path);
        DiskResidentOrderedPageTree<int, string> tree = await OpenAsync(pages);
        Guid transaction = Guid.NewGuid();
        await PrepareAsync(tree, transaction, Enumerable.Range(0, EntryCount)
            .Select(index => new OrderedPageTreeMutation<int, string>(OrderedPageTreeMutationKind.Add,
                index, $"value-{index:D5}-{new string('x', 40)}")).ToArray());
        await PublishAsync(tree, transaction);
        await using DiskResidentOrderedPageTreeSnapshot<int, string> snapshot = tree.CaptureSnapshot();

        OrderedPageTreePage<int, string> page = await snapshot.ReadPageAsync(QueryPageSize,
            OrderedIndexRange<int>.Between(480, 519));

        Assert.AreEqual(QueryPageSize, page.Items.Count);
        Assert.IsTrue(page.Items.All(item => item.Key is >= 480 and <= 519));
        Assert.AreEqual(480, page.Items[0].Key);
        Assert.IsNotNull(page.NextCursor);
    }

    [TestMethod]
    public async Task Maintenance_releases_snapshot_safe_retired_leaves_without_changing_results()
    {
        string path = NewPath();
        await using PhysicalPageFile pages = await PhysicalPageFile.OpenAsync(path);
        DiskResidentOrderedPageTree<int, string> tree = await OpenAsync(pages);
        Guid first = Guid.NewGuid();
        await PrepareAsync(tree, first, [new(OrderedPageTreeMutationKind.Add, 1, "before")]);
        await PublishAsync(tree, first);
        Guid second = Guid.NewGuid();
        await PrepareAsync(tree, second,
            [new(OrderedPageTreeMutationKind.Remove, 1, "before"),
                new(OrderedPageTreeMutationKind.Add, 1, "after")]);
        await PublishAsync(tree, second);
        PhysicalPageSpace pageSpace = PageSpaces[tree];
        long before = pageSpace.CaptureDirectory().Get(AccessPathId).Extents.Sum(item => item.PageCount);
        Guid maintenanceId = Guid.NewGuid();
        PhysicalPageSpaceTransaction transaction = pageSpace.BeginTransaction(maintenanceId);
        PhysicalAccessPathDirectoryEntry current = pageSpace.CaptureDirectory().Get(AccessPathId);
        OrderedPageTreeStage maintenance = (await tree.PrepareMaintenanceAsync(maintenanceId,
            transaction.OpenAccessPath(AccessPathId, AccessMethod, current.Generation)))!;
        await pageSpace.PrepareAsync(transaction);
        await pageSpace.PersistAsync(maintenanceId, maintenance.PhysicalMutations);
        tree.Publish(maintenanceId);
        pageSpace.Publish(maintenanceId);

        long after = pageSpace.CaptureDirectory().Get(AccessPathId).Extents.Sum(item => item.PageCount);
        Assert.IsTrue(after < before);
        await using DiskResidentOrderedPageTreeSnapshot<int, string> snapshot = tree.CaptureSnapshot();
        CollectionAssert.AreEqual(new[] { "after" }, (await snapshot.FindAsync(1)).ToArray());
    }

    [TestMethod]
    public async Task Full_replacement_preserves_pages_reachable_by_an_older_snapshot()
    {
        string path = NewPath();
        await using PhysicalPageFile pages = await PhysicalPageFile.OpenAsync(path);
        DiskResidentOrderedPageTree<int, string> tree = await OpenAsync(pages);
        Guid initial = Guid.NewGuid();
        await PrepareAsync(tree, initial,
            [new(OrderedPageTreeMutationKind.Add, 1, "before")]);
        await PublishAsync(tree, initial);
        await using DiskResidentOrderedPageTreeSnapshot<int, string> retained = tree.CaptureSnapshot();
        PhysicalPageSpace pageSpace = PageSpaces[tree];
        PhysicalAccessPathDirectoryEntry previous = pageSpace.CaptureDirectory().Get(AccessPathId);
        HashSet<long> previousPages = previous.Extents.SelectMany(extent => Enumerable.Range(0,
            checked((int)extent.PageCount)).Select(offset => extent.FirstPage.Value + offset)).ToHashSet();
        Guid replacementId = Guid.NewGuid();
        PhysicalPageWorkspace workspace = pageSpace.BeginReplacementWorkspace(replacementId);
        await tree.BuildReplacementAsync(replacementId, workspace, flushPages: false);
        Guid cutover = Guid.NewGuid();
        PhysicalPageSpaceTransaction transaction = pageSpace.BeginTransaction(cutover);
        transaction.OpenReplacementAccessPath(AccessPathId, AccessMethod,
            tree.GetReplacementExpectedGeneration(replacementId).Value, workspace);
        OrderedPageTreeStage stage = tree.PrepareReplacement(cutover, replacementId);
        await pageSpace.PrepareAsync(transaction);
        await pageSpace.PersistAsync(cutover, stage.PhysicalMutations);
        tree.Publish(cutover);
        pageSpace.Publish(cutover);

        CollectionAssert.AreEqual(new[] { "before" }, (await retained.FindAsync(1)).ToArray());
        PhysicalAccessPathDirectoryEntry current = pageSpace.CaptureDirectory().Get(AccessPathId);
        Assert.IsTrue(previousPages.All(page => current.Extents.Any(extent => extent.Contains(new(page)))));
    }

    private static async ValueTask<DiskResidentOrderedPageTree<int, string>> OpenAsync(
        IResizablePhysicalPageStore pages)
    {
        PhysicalPageSpace pageSpace = await PhysicalPageSpace.OpenAsync(pages);
        PhysicalAccessPathDirectoryEntry? published = pageSpace.CaptureDirectory().Entries
            .SingleOrDefault(entry => entry.AccessPathId == AccessPathId);
        DiskResidentOrderedPageTree<int, string> tree = await DiskResidentOrderedPageTree<int, string>.OpenAsync(
            pages, AccessPathId, AccessMethod, published, new IntCodec(), new StringCodec(),
            valueComparer: StringComparer.Ordinal);
        PageSpaces[tree] = pageSpace;
        return tree;
    }

    private static async ValueTask<OrderedPageTreeStage> PrepareAsync(
        DiskResidentOrderedPageTree<int, string> tree, Guid transactionId,
        IReadOnlyList<OrderedPageTreeMutation<int, string>> mutations)
    {
        PhysicalPageSpace pageSpace = PageSpaces[tree];
        PhysicalAccessPathDirectoryEntry? current = pageSpace.CaptureDirectory().Entries
            .SingleOrDefault(entry => entry.AccessPathId == AccessPathId);
        PhysicalPageSpaceTransaction transaction = pageSpace.BeginTransaction(transactionId);
        PhysicalAccessPathAllocationScope scope = transaction.OpenAccessPath(
            AccessPathId, AccessMethod, current?.Generation ?? -1);
        OrderedPageTreeStage stage = await tree.PrepareAsync(transactionId, mutations, scope);
        if (stage.PhysicalMutations.Count != 0)
        {
            await pageSpace.PrepareAsync(transaction);
            Stages.Add((tree, transactionId), stage);
        }
        else
        {
            pageSpace.Abandon(transactionId);
        }

        return stage;
    }

    private static async ValueTask PublishAsync(DiskResidentOrderedPageTree<int, string> tree, Guid transactionId)
    {
        PhysicalPageSpace pageSpace = PageSpaces[tree];
        OrderedPageTreeStage stage = Stages[(tree, transactionId)];
        await pageSpace.PersistAsync(transactionId, stage.PhysicalMutations);
        tree.Publish(transactionId);
        pageSpace.Publish(transactionId);
        Stages.Remove((tree, transactionId));
    }

    private string NewPath()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"entityforge-disk-tree-{Guid.NewGuid():N}");
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
}
