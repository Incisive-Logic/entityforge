using EntityForge.Db.Durability;
using EntityForge.Db.Kernel;
using EntityForge.Db.Storage;
using EntityForge.Db.Storage.Local;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class DurableDirectoryPublicationTests
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "ef-directory-publication", Guid.NewGuid().ToString("N"));

    [TestCleanup]
    public void Cleanup()
    {
        DurableDirectory.BeforeSync.Value = null;
        if (Directory.Exists(_path))
        {
            Directory.Delete(_path, true);
        }
    }

    [TestMethod]
    public async Task Repeated_checkpoint_only_skips_successfully_prepared_ancestors_in_this_store()
    {
        string path = Path.Combine(_path, "checkpoint");
        var checkpoint = new DurableCheckpointEnvelope(Guid.NewGuid(), new(1),
            new(DatabaseProfileId.PropertyGraph, "test", 1, new byte[] { 7 }));
        var synchronized = new List<string>();
        DurableDirectory.BeforeSync.Value = synchronized.Add;
        await using (var store = new FileDatabaseCheckpointStore(path))
        {
            await store.SaveAsync(checkpoint);
            CollectionAssert.Contains(synchronized, Path.GetDirectoryName(_path)!);
            synchronized.Clear();
            await store.SaveAsync(new(checkpoint.DatabaseIncarnation, new(2), checkpoint.ProfileState));
            CollectionAssert.AreEqual(new[] { _path }, synchronized,
                "Every replacement still requires its publication directory sync.");
            Assert.AreEqual(new DatabaseVersion(2), (await store.LoadAsync())!.Version);
        }
        synchronized.Clear();
        await using var reopened = new FileDatabaseCheckpointStore(path);
        await reopened.SaveAsync(new(checkpoint.DatabaseIncarnation, new(3), checkpoint.ProfileState));
        CollectionAssert.Contains(synchronized, Path.GetDirectoryName(_path)!);
    }

    [TestMethod]
    public async Task Failed_checkpoint_creation_and_publication_retry_resynchronize_ancestors()
    {
        string path = Path.Combine(_path, "checkpoint");
        var checkpoint = new DurableCheckpointEnvelope(Guid.NewGuid(), new(1),
            new(DatabaseProfileId.PropertyGraph, "test", 1, new byte[] { 7 }));
        await using var store = new FileDatabaseCheckpointStore(path);
        DurableDirectory.BeforeSync.Value = _ => throw new IOException("Creation sync failure.");
        await Assert.ThrowsExceptionAsync<EntityForgeDbCheckpointException>(async () => await store.SaveAsync(checkpoint));
        Assert.IsTrue(Directory.Exists(_path));
        var synchronized = new List<string>();
        DurableDirectory.BeforeSync.Value = synchronized.Add;
        await store.SaveAsync(checkpoint);
        CollectionAssert.Contains(synchronized, Path.GetDirectoryName(_path)!);
        DurableDirectory.BeforeSync.Value = _ => throw new IOException("Publication sync failure.");
        await Assert.ThrowsExceptionAsync<EntityForgeDbCheckpointException>(async () => await store.SaveAsync(checkpoint));
        synchronized.Clear();
        DurableDirectory.BeforeSync.Value = synchronized.Add;
        await store.SaveAsync(checkpoint);
        CollectionAssert.Contains(synchronized, Path.GetDirectoryName(_path)!);
    }

    [TestMethod]
    public async Task Missing_checkpoint_directory_is_recreated_and_fully_synchronized()
    {
        string path = Path.Combine(_path, "checkpoint");
        var checkpoint = new DurableCheckpointEnvelope(Guid.NewGuid(), new(1),
            new(DatabaseProfileId.PropertyGraph, "test", 1, new byte[] { 7 }));
        await using var store = new FileDatabaseCheckpointStore(path);
        await store.SaveAsync(checkpoint);
        File.Delete(path);
        Directory.Delete(_path);
        var synchronized = new List<string>();
        DurableDirectory.BeforeSync.Value = synchronized.Add;
        await store.SaveAsync(checkpoint);
        CollectionAssert.Contains(synchronized, Path.GetDirectoryName(_path)!);
        Assert.AreEqual(checkpoint.Version, (await store.LoadAsync())!.Version);
    }

    [TestMethod]
    public async Task Checkpoint_does_not_acknowledge_replacement_until_directory_sync_and_reopen_reconciles()
    {
        string path = Path.Combine(_path, "checkpoint");
        var observer = new ArmCheckpointPublication();
        await using var store = new FileDatabaseCheckpointStore(path, observer: observer);
        var checkpoint = new DurableCheckpointEnvelope(Guid.NewGuid(), new(3),
            new(DatabaseProfileId.PropertyGraph, "test", 1, new byte[] { 7 }));
        DurableDirectory.BeforeSync.Value = _ =>
        {
            if (observer.Armed)
            {
                throw new IOException("Directory sync failed after checkpoint rename.");
            }
        };
        await Assert.ThrowsExceptionAsync<EntityForgeDbCheckpointException>(async () => await store.SaveAsync(checkpoint));
        Assert.IsTrue(File.Exists(path), "The failure must occur after replacement, not before writing.");
        Assert.IsFalse(observer.Completed);
        await using var reopened = new FileDatabaseCheckpointStore(path);
        await Assert.ThrowsExceptionAsync<EntityForgeDbCheckpointException>(async () => await reopened.LoadAsync());
        DurableDirectory.BeforeSync.Value = null;
        Assert.AreEqual(checkpoint.Version, (await reopened.LoadAsync())!.Version);
    }

    [TestMethod]
    public async Task Failed_retention_root_sync_preserves_predecessor_artifacts_and_reopens()
    {
        Guid incarnation = Guid.NewGuid();
        CommittedChangeSet Change(long version) => new(incarnation, new(version), Guid.NewGuid(),
            DatabaseProfileId.PropertyGraph, new DatabaseChangeCursorCodec().Encode(incarnation, new(version)),
            new("test", 1, new byte[] { 1 }, 1));
        var first = Change(1);
        var second = Change(2);
        var retention = new DatabaseChangeJournalRetentionOptions(1, 1, TimeSpan.Zero);
        string[] predecessors;
        await using (var store = new SegmentedDatabaseCommittedChangeStore(_path, retention))
        {
            await store.AppendAsync(first);
            await store.PublishThroughAsync(first.Cursor);
            await store.NotifyPublishedAsync(first.Cursor);
            await store.AppendAsync(second);
            await store.PublishThroughAsync(second.Cursor);
            predecessors = Directory.GetFiles(_path).Where(file => file.EndsWith(".efcd") || file.EndsWith(".efcb")).ToArray();
            Assert.IsTrue(predecessors.Length >= 4);
            DurableDirectory.BeforeSync.Value = _ => throw new IOException("Retention root synchronization failed.");
            try
            {
                await Assert.ThrowsExceptionAsync<EntityForgeDbStorageException>(async () => await store.NotifyPublishedAsync(second.Cursor));
                Assert.IsTrue(predecessors.All(File.Exists), "No predecessor can be deleted before the new root is durable.");
            }
            finally
            {
                DurableDirectory.BeforeSync.Value = null;
            }
        }
        await using var reopened = new SegmentedDatabaseCommittedChangeStore(_path, retention);
        await reopened.NotifyPublishedAsync(second.Cursor);
        var page = await reopened.ReadPageAfterAsync(first.Cursor, new(), 10);
        Assert.AreEqual(1, page.Changes.Count);
        Assert.AreEqual(second.TransactionId, page.Changes[0].TransactionId);
    }

    [TestMethod]
    public void Directory_creation_retry_reconciles_existing_ancestors()
    {
        string nested = Path.Combine(_path, "a", "b");
        DurableDirectory.BeforeSync.Value = _ => throw new IOException("Interrupted directory creation.");
        Assert.ThrowsException<IOException>(() => DurableDirectory.Create(nested));
        Assert.IsTrue(Directory.Exists(nested));
        var synchronized = new List<string>();
        DurableDirectory.BeforeSync.Value = synchronized.Add;
        DurableDirectory.Create(nested);
        CollectionAssert.Contains(synchronized, nested);
        CollectionAssert.Contains(synchronized, _path);
        CollectionAssert.Contains(synchronized, Path.GetDirectoryName(_path)!);
    }

    [TestMethod]
    public async Task Archive_cannot_acknowledge_manifest_until_directory_sync_and_verify_reconciles()
    {
        string checkpoint = Path.Combine(_path, "checkpoint");
        string pages = Path.Combine(_path, "pages");
        Directory.CreateDirectory(_path);
        await File.WriteAllBytesAsync(pages, []);
        await using (var store = new FileDatabaseCheckpointStore(checkpoint, pagePath: pages, walFloor: () => 0))
        {
            await store.SaveAsync(new(Guid.NewGuid(), new(1),
                new(DatabaseProfileId.PropertyGraph, "test", 1, new byte[] { 1 })));
        }

        string archives = Path.Combine(_path, "archives");
        var observer = new ArmArchivePublication();
        var manager = new LocalHistoricalEvidenceArchiveManager(new(archives), observer: observer);
        DurableDirectory.BeforeSync.Value = _ =>
        {
            if (observer.Armed)
            {
                throw new IOException("Archive directory synchronization failed.");
            }
        };
        await Assert.ThrowsExceptionAsync<IOException>(async () =>
            await manager.CreateAsync(checkpoint, Path.Combine(_path, "journal"), pages));
        Assert.IsFalse(observer.Completed);
        string manifest = Directory.GetFiles(archives, "*.json").Single();
        string id = Path.GetFileNameWithoutExtension(manifest);
        await Assert.ThrowsExceptionAsync<EntityForgeDbCheckpointException>(async () => await manager.VerifyAsync(id));
        DurableDirectory.BeforeSync.Value = null;
        Assert.AreEqual(id, (await manager.VerifyAsync(id)).ArchiveId);
    }

    private sealed class ArmArchivePublication : IStoragePublicationObserver
    {
        internal bool Armed;
        internal bool Completed;

        public void Reached(StoragePublicationStage stage)
        {
            if (stage == StoragePublicationStage.BeforeArchiveManifestPublication)
            {
                Armed = true;
            }

            if (stage == StoragePublicationStage.AfterArchiveManifestPublication)
            {
                Completed = true;
            }
        }
    }

    private sealed class ArmCheckpointPublication : IDatabaseCheckpointPublicationObserver
    {
        internal bool Armed;
        internal bool Completed;

        public void Reached(DatabaseCheckpointPublicationStage stage)
        {
            if (stage == DatabaseCheckpointPublicationStage.BeforeReplacement)
            {
                Armed = true;
            }
            else
            {
                Completed = true;
            }
        }
    }
}
