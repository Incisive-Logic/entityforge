using EntityForge.Db;
using EntityForge.Db.Kernel;
using EntityForge.Db.Storage;
using EntityForge.Db.Storage.Local;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class JournalPublicationTests
{
    private string _path = null!;
    private readonly Guid _id = Guid.NewGuid();

    private CommittedChangeSet Change(long version) => new(_id, new(version), Guid.NewGuid(),
        DatabaseProfileId.PropertyGraph, new DatabaseChangeCursorCodec().Encode(_id, new(version)),
        new("test", 1, new byte[] { 1 }, 1));

    [TestInitialize]
    public void Initialize() =>
        _path = Path.Combine(Path.GetTempPath(), "ef-publication-tests", Guid.NewGuid().ToString("N"));

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_path))
        {
            Directory.Delete(_path, true);
        }
    }

    [TestMethod]
    public async Task Bounds_do_not_copy_retained_catalog_and_pages_hide_unpublished_tail()
    {
        const int Count = 20_000;
        const int Repetitions = 32;
        const long MaximumBoundsAllocation = 64 * 1024;
        var changes = Enumerable.Range(1, Count).Select(i => Change(i)).ToArray();
        await using var store = new SegmentedDatabaseCommittedChangeStore(_path);
        await store.AppendBatchAsync(changes);
        Assert.IsNull((await store.GetBoundsAsync()).Earliest);
        await store.PublishThroughAsync(changes[^2].Cursor);
        await store.NotifyPublishedAsync(changes[^2].Cursor);
        _ = await store.GetBoundsAsync();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < Repetitions; i++)
        {
            var bounds = await store.GetBoundsAsync();
            Assert.AreEqual(changes[0].Cursor, bounds.Earliest);
            Assert.AreEqual(changes[^2].Cursor, bounds.Current);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"Bounds: records={Count}; calls={Repetitions}; allocated={allocated}");
        Assert.IsTrue(allocated < MaximumBoundsAllocation, "Bounds must not allocate proportional to retained history.");
        var page = await store.ReadPageAfterAsync(changes[^3].Cursor, new(), 10);
        Assert.AreEqual(1, page.Changes.Count);
        Assert.AreEqual(changes[^2].Cursor, page.NextCursor);
        Assert.IsFalse(page.HasMore);
        await store.PublishThroughAsync(changes[^1].Cursor);
        await store.NotifyPublishedAsync(changes[^1].Cursor);
        Assert.AreEqual(changes[^1].Cursor, (await store.GetBoundsAsync()).Current);
        Assert.AreEqual(1, (await store.ReadPageAfterAsync(page.NextCursor, new(), 10)).Changes.Count);
    }

    [TestMethod]
    public async Task Cursor_only_publication_leaves_catalog_unchanged_and_reopens_before_notification()
    {
        var first = Change(1);
        var second = Change(2);
        await using (var store = new SegmentedDatabaseCommittedChangeStore(_path))
        {
            await store.AppendBatchAsync([first, second]);
            byte[] catalog = await File.ReadAllBytesAsync(Path.Combine(_path, "manifest.efcm"));
            await store.PublishThroughAsync(first.Cursor);
            await store.PublishThroughAsync(second.Cursor);
            await store.PublishThroughAsync(first.Cursor);
            CollectionAssert.AreEqual(catalog, await File.ReadAllBytesAsync(Path.Combine(_path, "manifest.efcm")));
            Assert.IsNull((await store.GetBoundsAsync()).Current);
        }
        await using var reopened = new SegmentedDatabaseCommittedChangeStore(_path);
        await reopened.NotifyPublishedAsync(second.Cursor);
        Assert.AreEqual(second.Cursor, (await reopened.GetBoundsAsync()).Current);
        Assert.AreEqual(2, (await reopened.ReadPageAfterAsync(null, new(), 10)).Changes.Count);
    }

    [TestMethod]
    public async Task Catalog_append_folds_previous_frontier_without_publishing_the_new_change()
    {
        var first = Change(1);
        var second = Change(2);
        await using (var store = new SegmentedDatabaseCommittedChangeStore(_path))
        {
            await store.AppendAsync(first);
            await store.PublishThroughAsync(first.Cursor);
            await store.AppendAsync(second);
        }
        await using var reopened = new SegmentedDatabaseCommittedChangeStore(_path);
        await reopened.NotifyPublishedAsync(first.Cursor);
        Assert.AreEqual(first.Cursor, (await reopened.GetBoundsAsync()).Current);
        await Assert.ThrowsExactlyAsync<EntityForgeDbFormatException>(async () =>
            await reopened.NotifyPublishedAsync(second.Cursor));
        await reopened.PublishThroughAsync(second.Cursor);
        await reopened.NotifyPublishedAsync(second.Cursor);
    }

    [TestMethod]
    public async Task Later_catalog_and_retention_supersede_old_publication_even_after_its_entry_is_removed()
    {
        var first = Change(1);
        var second = Change(2);
        var retention = new DatabaseChangeJournalRetentionOptions(1, 1, TimeSpan.Zero);
        byte[] oldPublication;
        await using (var store = new SegmentedDatabaseCommittedChangeStore(_path, retention))
        {
            await store.AppendAsync(first);
            await store.PublishThroughAsync(first.Cursor);
            oldPublication = await File.ReadAllBytesAsync(Path.Combine(_path, SegmentedDatabaseCommittedChangeStore.PublicationFileName));
            await store.NotifyPublishedAsync(first.Cursor);
            await store.AppendAsync(second);
            await store.PublishThroughAsync(second.Cursor);
            await store.NotifyPublishedAsync(second.Cursor);
            Assert.AreEqual(1, (await store.GetStatusAsync()).SegmentCount);
        }
        // A stale file left by an earlier successful publication is redundant after catalog replacement.
        await File.WriteAllBytesAsync(Path.Combine(_path, SegmentedDatabaseCommittedChangeStore.PublicationFileName), oldPublication);
        await using var reopened = new SegmentedDatabaseCommittedChangeStore(_path, retention);
        await reopened.NotifyPublishedAsync(second.Cursor);
        Assert.AreEqual(second.Cursor, (await reopened.GetBoundsAsync()).Current);
        Assert.AreEqual(1, (await reopened.GetStatusAsync()).SegmentCount);
    }

    [DataTestMethod]
    [DataRow(1L)]
    [DataRow(2L)]
    public async Task Snapshot_collapses_publication_into_a_self_contained_catalog(long through)
    {
        var first = Change(1);
        var second = Change(2);
        await using var store = new SegmentedDatabaseCommittedChangeStore(_path);
        await store.AppendBatchAsync([first, second]);
        await store.PublishThroughAsync(second.Cursor);
        var cursor = through == 1 ? first.Cursor : second.Cursor;
        var snapshot = await store.CaptureSnapshotThroughAsync(cursor);
        Assert.IsFalse(snapshot.Artifacts.Any(x => x.Name == SegmentedDatabaseCommittedChangeStore.PublicationFileName));
        string copy = Path.Combine(_path, "snapshot");
        Directory.CreateDirectory(copy);
        foreach (var artifact in snapshot.Artifacts)
        {
            await File.WriteAllBytesAsync(Path.Combine(copy, artifact.Name), artifact.Content.ToArray());
        }

        await using var reopened = new SegmentedDatabaseCommittedChangeStore(copy);
        await reopened.NotifyPublishedAsync(cursor);
        Assert.AreEqual((int)through, (await reopened.ReadPageAfterAsync(null, new(), 10)).Changes.Count);
        string fileCopy = Path.Combine(_path, "file-snapshot");
        var files = await store.CaptureSnapshotFilesThroughAsync(cursor, fileCopy, default);
        Assert.IsFalse(files.Any(x => x.Name == SegmentedDatabaseCommittedChangeStore.PublicationFileName));
        await using var fileReopened = new SegmentedDatabaseCommittedChangeStore(fileCopy);
        await fileReopened.NotifyPublishedAsync(cursor);
        Assert.AreEqual((int)through, (await fileReopened.ReadPageAfterAsync(null, new(), 10)).Changes.Count);
    }

    [TestMethod]
    public async Task Failed_publication_and_abandoned_temporary_file_do_not_advance_visibility()
    {
        var first = Change(1);
        var second = Change(2);
        string temporary = Path.Combine(_path, SegmentedDatabaseCommittedChangeStore.PublicationFileName + ".new");
        await using (var store = new SegmentedDatabaseCommittedChangeStore(_path))
        {
            await store.AppendBatchAsync([first, second]);
            await store.PublishThroughAsync(first.Cursor);
            Directory.CreateDirectory(temporary);
            await Assert.ThrowsExactlyAsync<EntityForgeDbStorageException>(async () => await store.PublishThroughAsync(second.Cursor));
            Directory.Delete(temporary);
            await Assert.ThrowsExactlyAsync<EntityForgeDbStorageException>(async () => await store.NotifyPublishedAsync(second.Cursor));
        }
        await File.WriteAllBytesAsync(temporary, new byte[] { 1, 2 });
        await using var reopened = new SegmentedDatabaseCommittedChangeStore(_path);
        await reopened.NotifyPublishedAsync(first.Cursor);
        Assert.IsFalse(File.Exists(temporary));
        await Assert.ThrowsExactlyAsync<EntityForgeDbFormatException>(async () => await reopened.NotifyPublishedAsync(second.Cursor));
        await reopened.PublishThroughAsync(second.Cursor);
        await reopened.NotifyPublishedAsync(second.Cursor);
    }

    [TestMethod]
    public async Task Failed_catalog_replacement_requires_reopen_before_new_publication()
    {
        var first = Change(1);
        var second = Change(2);
        string temporary = Path.Combine(_path, SegmentedDatabaseCommittedChangeStore.CatalogHeadFileName + ".new");
        await using (var store = new SegmentedDatabaseCommittedChangeStore(_path))
        {
            await store.AppendAsync(first);
            await store.PublishThroughAsync(first.Cursor);
            Directory.CreateDirectory(temporary);
            await Assert.ThrowsExactlyAsync<EntityForgeDbStorageException>(async () => await store.AppendAsync(second));
            Directory.Delete(temporary);
            await Assert.ThrowsExactlyAsync<EntityForgeDbStorageException>(async () => await store.GetBoundsAsync());
            await Assert.ThrowsExactlyAsync<EntityForgeDbStorageException>(async () => await store.PublishThroughAsync(first.Cursor));
        }
        await using var reopened = new SegmentedDatabaseCommittedChangeStore(_path);
        await reopened.NotifyPublishedAsync(first.Cursor);
        await reopened.AppendAsync(second);
        await Assert.ThrowsExactlyAsync<EntityForgeDbFormatException>(async () => await reopened.NotifyPublishedAsync(second.Cursor));
        await reopened.PublishThroughAsync(second.Cursor);
        await reopened.NotifyPublishedAsync(second.Cursor);
    }

    [DataTestMethod]
    [DataRow("CatalogGeneration")]
    [DataRow("DatabaseIncarnation")]
    [DataRow("Version")]
    [DataRow("missing-field")]
    [DataRow("checksum")]
    [DataRow("missing-catalog")]
    public async Task Invalid_publication_is_rejected_on_reopen(string defect)
    {
        var first = Change(1);
        await using (var store = new SegmentedDatabaseCommittedChangeStore(_path))
        {
            await store.AppendAsync(first);
            await store.PublishThroughAsync(first.Cursor);
        }
        var artifacts = new ChecksummedJournalArtifactStore(_path);
        string name = SegmentedDatabaseCommittedChangeStore.PublicationFileName;
        if (defect == "missing-catalog")
        {
            File.Delete(Path.Combine(_path, "manifest.efcm"));
        }
        else if (defect == "checksum")
        {
            await File.WriteAllBytesAsync(Path.Combine(_path, name), new byte[] { 1 });
        }
        else
        {
            var json = JsonNode.Parse(await artifacts.ReadAsync(name, default))!;
            if (defect == "missing-field")
            {
                json.AsObject().Remove("CatalogGeneration");
            }
            else if (defect == "DatabaseIncarnation")
            {
                json[defect] = Guid.NewGuid().ToString();
            }
            else
            {
                json[defect] = 999L;
            }

            await artifacts.WriteAsync(name, JsonSerializer.SerializeToUtf8Bytes(json), true, default);
        }
        await using var reopened = new SegmentedDatabaseCommittedChangeStore(_path);
        await Assert.ThrowsExactlyAsync<EntityForgeDbFormatException>(async () => await reopened.GetBoundsAsync());
    }
}
