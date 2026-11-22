using EntityForge.Db.Storage.Pages;

namespace EntityForge.Db.Storage.Pages.Tests;

[TestClass]
public sealed class ReplacementPublicationTests
{
    [TestMethod]
    public async Task Reused_workspace_pages_are_not_rewritten_but_remain_complete_redo_images()
    {
        const int ReplacementPages = 128;
        string path = Path.Combine(Path.GetTempPath(), $"entityforge-replacement-{Guid.NewGuid():N}.pages");
        try
        {
            await using var file = await PhysicalPageFile.OpenAsync(path);
            var pages = new RecordingPages(file);
            var space = await PhysicalPageSpace.OpenAsync(pages);
            var id = new PhysicalAccessPathId(Guid.NewGuid());
            var method = new PhysicalAccessMethodKind("replacement-test", 1);
            var abandoned = space.BeginTransaction(Guid.NewGuid());
            var allocation = abandoned.OpenAccessPath(id, method, -1);
            var reusable = Enumerable.Range(0, ReplacementPages).Select(_ => allocation.AllocatePage()).ToArray();
            await pages.EnsureCapacityAsync(reusable.Max(page => page.Value) + 1);
            space.Abandon(abandoned.TransactionId);

            using var workspace = space.BeginReplacementWorkspace(Guid.NewGuid());
            var expected = new Dictionary<PhysicalPageId, byte[]>();
            for (int index = 0; index < ReplacementPages; index++)
            {
                var page = workspace.AllocatePage();
                var image = new SlottedDataPage(pages.PageSize, page);
                Assert.IsTrue(image.TryInsert(BitConverter.GetBytes(page.Value), 1, out _));
                expected.Add(page, image.Encode());
                await pages.WriteAsync(page, expected[page]);
            }
            CollectionAssert.AreEquivalent(reusable, expected.Keys.ToArray());
            var root = expected.Keys.Last();
            workspace.Complete(root);
            await pages.FlushAsync();
            var transaction = space.BeginTransaction(Guid.NewGuid());
            transaction.OpenReplacementAccessPath(id, method, -1, workspace);
            var stage = await space.PrepareAsync(transaction);
            foreach (var (page, bytes) in expected)
            {
                CollectionAssert.AreEqual(bytes, stage.PhysicalMutations.Single(m => m.PageId == page.Value).Bytes.ToArray());
            }

            pages.Written.Clear();
            await space.PersistAsync(transaction.TransactionId, []);
            Assert.IsFalse(pages.Written.Any(expected.ContainsKey), "Already-written workspace pages must not be rewritten during cutover.");
            Assert.IsTrue(pages.Written.Count > 0, "Directory publication is still required.");
            space.Publish(transaction.TransactionId);
            await pages.FlushAsync();
            Assert.AreEqual(root, (await PhysicalPageSpace.OpenAsync(pages)).CaptureDirectory().Get(id).RootPage);

            // Simulate an older recovery prefix overwriting reused pages. The new transaction's
            // unchanged redo images must restore all replacement data and its directory.
            foreach (var page in expected.Keys)
            {
                await pages.WriteAsync(page, new SlottedDataPage(pages.PageSize, page).Encode());
            }

            foreach (var mutation in stage.PhysicalMutations)
            {
                await pages.WriteAsync(new(mutation.PageId), mutation.Bytes);
            }

            await pages.FlushAsync();
            var recovered = await PhysicalPageSpace.OpenAsync(pages);
            Assert.AreEqual(root, recovered.CaptureDirectory().Get(id).RootPage);
            foreach (var (page, bytes) in expected)
            {
                CollectionAssert.AreEqual(bytes, (await pages.ReadAsync(page)).ToArray());
            }
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private sealed class RecordingPages(IResizablePhysicalPageStore inner) : IResizablePhysicalPageStore
    {
        public HashSet<PhysicalPageId> Written { get; } = [];

        public int PageSize => inner.PageSize;

        public ValueTask<ReadOnlyMemory<byte>> ReadAsync(PhysicalPageId id, CancellationToken token = default) => inner.ReadAsync(id, token);

        public ValueTask WriteAsync(PhysicalPageId id, ReadOnlyMemory<byte> bytes, CancellationToken token = default)
        {
            Written.Add(id);
            return inner.WriteAsync(id, bytes, token);
        }

        public ValueTask FlushAsync(CancellationToken token = default) => inner.FlushAsync(token);

        public ValueTask<long> GetPageCountAsync(CancellationToken token = default) => inner.GetPageCountAsync(token);

        public ValueTask EnsureCapacityAsync(long count, CancellationToken token = default) => inner.EnsureCapacityAsync(count, token);

        public ValueTask TruncateAsync(long count, CancellationToken token = default) => inner.TruncateAsync(count, token);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
