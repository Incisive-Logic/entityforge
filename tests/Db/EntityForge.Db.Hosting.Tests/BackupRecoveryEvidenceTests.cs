using System.Buffers.Binary;
using System.Security.Cryptography;
using EntityForge.Db.Durability;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Kernel;
using EntityForge.Db.Storage;
using EntityForge.Db.Storage.Local;
using EntityForge.Db.Storage.Pages;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed partial class BackupRecoveryEvidenceTests
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ef-backup-evidence-" + Guid.NewGuid().ToString("N"));
    private readonly Guid _incarnation = Guid.NewGuid();

    private string At(string name) => Path.Combine(_directory, name);

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }

    [TestMethod]
    public async Task Graph_restore_recovers_edge_and_physical_adjacency_after_durable_unpublished_write()
    {
        string source = At("graph-source"), restored = At("graph-restored");
        var from = GraphVertexId.New();
        var to = GraphVertexId.New();
        var edge = GraphEdgeId.New();
        await using (var graph = await GraphDatabase.OpenLocalAsync(new(source)))
        {
            await graph.CommitAsync(new(Guid.NewGuid(), [new CreateVertex(from, ["Node"]), new CreateVertex(to, ["Node"])]));
            await graph.CreateCheckpointAsync();
        }
        var manager = new LocalDatabaseBackupManager();
        await manager.CreateAsync(Path.Combine(source, LocalGraphDatabaseOptions.DefaultCheckpointFileName),
            Path.Combine(source, LocalGraphDatabaseOptions.DefaultChangeJournalDirectoryName),
            Path.Combine(source, LocalGraphDatabaseOptions.DefaultPageFileName), At("graph.backup"));
        await manager.RestoreAsync(At("graph.backup"), restored, LocalGraphDatabaseOptions.DefaultCheckpointFileName,
            LocalGraphDatabaseOptions.DefaultCommitLogFileName, LocalGraphDatabaseOptions.DefaultManifestFileName,
            LocalGraphDatabaseOptions.DefaultChangeJournalDirectoryName, LocalGraphDatabaseOptions.DefaultPageFileName, new GroupedRedoRestoreLogInitializer());
        await using (var graph = await GraphDatabase.OpenLocalAsync(new(restored), new StopAfterDurability(), null))
        {
            await Assert.ThrowsAsync<EntityForgeDbCommitException>(async () =>
                await graph.CommitAsync(new(Guid.NewGuid(), [new CreateEdge(edge, from, to, "links")])));
        }

        await using var recovered = await GraphDatabase.OpenLocalAsync(new(restored));
        Assert.IsNotNull(recovered.GetEdge(edge));
        Assert.AreEqual(1, recovered.Outgoing(from).Count);
        Assert.AreEqual(1, recovered.Incoming(to).Count);
    }

    private sealed class StopAfterDurability : IRedoDurabilityObserver
    {
        public void Reached(RedoDurabilityStage stage)
        {
            if (stage == RedoDurabilityStage.AfterWalFlush)
            {
                throw new IOException("Stop before native publication.");
            }
        }
    }

    [TestMethod]
    public async Task Restored_wal_advances_past_preserved_pages_and_replays_unpublished_update()
    {
        await CreateBackup();
        await Restore("restored");
        string wal = At("restored/wal"), pages = At("restored/pages");
        await using (var log = await GroupedRedoDatabaseCommitLog.OpenAsync(wal))
        {
            Assert.AreEqual(2L, log.GetRedoStatus().DurableLsn); // Logical checkpoint version is 100.
            byte[] before = await File.ReadAllBytesAsync(pages);
            byte[] after = new SlottedDataPage(PhysicalPageFileOptions.DefaultPageSize, new(0), generation: 42).Encode();
            await log.CommitWithPhysicalMutationsAsync(Transaction(101), [new(0, 0, after, before)]);
            Assert.AreEqual(3L, log.GetRedoStatus().DurableLsn);
            // Stop after durable WAL, deliberately never publish the physical update.
        }
        await using (var log = await GroupedRedoDatabaseCommitLog.OpenAsync(wal))
        {
            await using (var physical = await PhysicalPageFile.OpenAsync(pages))
            {
                await log.RecoverPagesAsync(physical);
                await log.RecoverPagesAsync(physical);
                var recovered = SlottedDataPage.Decode((await physical.ReadAsync(new(0))).Span, new(0));
                Assert.AreEqual(42L, recovered.Generation);
                Assert.AreEqual(3L, recovered.PageLsn);
            }
        }
    }

    [TestMethod]
    public async Task Restore_floor_covers_valid_physical_page_clocks_as_well_as_source_wal()
    {
        Directory.CreateDirectory(_directory);
        var page = new SlottedDataPage(PhysicalPageFileOptions.DefaultPageSize, new(0));
        Assert.IsTrue(page.TryInsert(new byte[] { 1 }, 70, out _));
        await File.WriteAllBytesAsync(At("pages"), page.Encode());
        await using (var store = new FileDatabaseCheckpointStore(At("checkpoint"), pagePath: At("pages"), walFloor: () => 2))
        {
            await store.SaveAsync(new(_incarnation, new(100), new(DatabaseProfileId.PropertyGraph, "test", 1, new byte[] { 1 })));
        }

        await new LocalDatabaseBackupManager().CreateAsync(At("checkpoint"), At("journal"), At("pages"), At("good.backup"));
        await Restore("clock");
        await using var log = await GroupedRedoDatabaseCommitLog.OpenAsync(At("clock/wal"));
        Assert.AreEqual(70L, log.GetRedoStatus().DurableLsn);
        await log.CommitAsync(Transaction(101));
        Assert.AreEqual(71L, log.GetRedoStatus().DurableLsn);
    }

    [TestMethod]
    public async Task File_backup_rejects_a_mismatched_empty_physical_file()
    {
        await CreateBackup();
        await File.WriteAllBytesAsync(At("empty"), []);
        await Assert.ThrowsExceptionAsync<EntityForgeDbBackupException>(async () =>
            await new LocalDatabaseBackupManager().CreateAsync(At("checkpoint"), At("journal"), At("empty"), At("bad.backup")));
        Assert.IsFalse(File.Exists(At("bad.backup")));
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Checksum_valid_mismatched_recovery_image_is_rejected_before_restore_publication(bool removePages)
    {
        await CreateBackup();
        byte[] backup = await File.ReadAllBytesAsync(At("good.backup"));
        byte[] pages = await File.ReadAllBytesAsync(At("pages"));
        int offset = backup.AsSpan().IndexOf(pages);
        Assert.IsTrue(offset > 8);
        if (removePages)
        {
            byte[] shortened = new byte[backup.Length - pages.Length];
            backup.AsSpan(0, offset).CopyTo(shortened);
            backup.AsSpan(offset + pages.Length).CopyTo(shortened.AsSpan(offset));
            BinaryPrimitives.WriteInt64LittleEndian(shortened.AsSpan(offset - 8), 0);
            BinaryPrimitives.WriteInt64LittleEndian(shortened.AsSpan(5), shortened.Length - 13 - 32);
            backup = shortened;
        }
        else
        {
            backup[offset + 20] ^= 1;
        }
        // Recompute the container checksum: packaging integrity is deliberately valid.
        SHA256.HashData(backup.AsSpan(13, backup.Length - 13 - 32)).CopyTo(backup.AsSpan(backup.Length - 32));
        await File.WriteAllBytesAsync(At("good.backup"), backup);
        await Assert.ThrowsExceptionAsync<EntityForgeDbBackupException>(async () => await Restore("rejected"));
        Assert.IsFalse(Directory.Exists(At("rejected")));
        await Assert.ThrowsExceptionAsync<EntityForgeDbBackupException>(async () =>
            await new LocalDatabaseBackupManager().VerifyAsync(At("good.backup")));
    }

    [TestMethod]
    public async Task Metadata_only_checkpoint_with_bound_empty_image_is_restorable()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllBytesAsync(At("pages"), []);
        await using (var store = new FileDatabaseCheckpointStore(At("checkpoint"), pagePath: At("pages"), walFloor: () => 17))
        {
            await store.SaveAsync(new(_incarnation, new(100), new(DatabaseProfileId.PropertyGraph, "metadata", 1, new byte[] { 1 })));
        }

        await new LocalDatabaseBackupManager().CreateAsync(At("checkpoint"), At("journal"), At("pages"), At("good.backup"));
        await Restore("metadata");
        await using var log = await GroupedRedoDatabaseCommitLog.OpenAsync(At("metadata/wal"));
        Assert.AreEqual(17L, log.GetRedoStatus().DurableLsn);
        Assert.AreEqual(0L, new FileInfo(At("metadata/pages")).Length);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Journal_rejects_rechecksummed_out_of_bounds_record_or_index_before_allocating(bool index)
    {
        var cursor = new DatabaseChangeCursorCodec().Encode(_incarnation, new(1));
        var change = new CommittedChangeSet(_incarnation, new(1), Guid.NewGuid(), DatabaseProfileId.PropertyGraph,
            cursor, new("test", 1, new byte[] { 1 }, 1));
        DatabaseChangeJournalSnapshot snapshot;
        await using (var source = new SegmentedDatabaseCommittedChangeStore(At("journal-source")))
        {
            await source.AppendAsync(change);
            await source.PublishThroughAsync(cursor);
            await source.NotifyPublishedAsync(cursor);
            snapshot = await source.CaptureSnapshotThroughAsync(cursor);
        }
        Directory.CreateDirectory(At("journal-bad"));
        foreach (var artifact in snapshot.Artifacts)
        {
            byte[] bytes = artifact.Content.ToArray();
            if (artifact.Name == "manifest.efcm")
            {
                var manifest = ChangeJournalManifestCodec.Read(bytes[..^32]);
                var segment = manifest.Segments.Single();
                segment = index ? segment with
                {
                    IndexLength = int.MaxValue
                } : segment with
                {
                    RecordLength = int.MaxValue
                };
                bytes = ChecksummedJournalArtifactStore.Encode(ChangeJournalManifestCodec.Write(manifest with
                {
                    Segments = [segment]
                }));
            }
            await File.WriteAllBytesAsync(Path.Combine(At("journal-bad"), artifact.Name), bytes);
        }
        await using var restored = new SegmentedDatabaseCommittedChangeStore(At("journal-bad"));
        await restored.NotifyPublishedAsync(cursor);
        await Assert.ThrowsExceptionAsync<EntityForgeDbFormatException>(async () =>
            await restored.ReadPageAfterAsync(null, new(DatabaseProfileId.PropertyGraph), 1));
    }

    [DataTestMethod]
    [DataRow("complete")]
    [DataRow("retained")]
    [DataRow("missing")]
    [DataRow("index")]
    [DataRow("effects")]
    [DataRow("classification")]
    public async Task Recovery_set_cross_checks_retained_transactions_and_routing_indexes(string scenario)
    {
        long[] versions = [1, 3, 7]; // Deliberate gaps: not every version carries changes.
        var changes = versions.Select(version => new CommittedChangeSet(_incarnation, new(version), Guid.NewGuid(),
            DatabaseProfileId.PropertyGraph, new DatabaseChangeCursorCodec().Encode(_incarnation, new(version)),
            new("test", 1, new byte[] { 1 }, 1, new([new("subject", version.ToString())])))).ToArray();
        DatabaseChangeJournalSnapshot snapshot;
        await using (var journal = new SegmentedDatabaseCommittedChangeStore(At("journal-source")))
        {
            await journal.AppendBatchAsync(changes);
            await journal.PublishThroughAsync(changes[^1].Cursor);
            await journal.NotifyPublishedAsync(changes[^1].Cursor);
            snapshot = await journal.CaptureSnapshotThroughAsync(changes[^1].Cursor);
        }
        Directory.CreateDirectory(At("journal"));
        foreach (var artifact in snapshot.Artifacts)
        {
            byte[] bytes = artifact.Content.ToArray();
            if (artifact.Name == "manifest.efcm")
            {
                var manifest = ChangeJournalManifestCodec.Read(bytes[..^32]);
                if (scenario == "missing")
                {
                    manifest = manifest with
                    {
                        Segments = [manifest.Segments[0], manifest.Segments[2]]
                    };
                }

                if (scenario == "retained")
                {
                    manifest = manifest with
                    {
                        RetentionFloor = changes[0].Cursor.Value,
                        RetentionFloorVersion = 1,
                        Segments = manifest.Segments.Skip(1).ToArray()
                    };
                }

                if (scenario == "index")
                {
                    var entries = manifest.Segments.ToArray();
                    entries[0] = entries[0] with
                    {
                        IndexFile = entries[1].IndexFile,
                        IndexOffset = entries[1].IndexOffset,
                        IndexLength = entries[1].IndexLength,
                        IndexChecksum = entries[1].IndexChecksum
                    };
                    manifest = manifest with
                    {
                        Segments = entries
                    };
                }
                bytes = ChecksummedJournalArtifactStore.Encode(ChangeJournalManifestCodec.Write(manifest));
            }
            await File.WriteAllBytesAsync(Path.Combine(At("journal"), artifact.Name), bytes);
        }
        var evidence = changes.Select(change => new DurableCommittedTransaction(change.TransactionId, change.Version,
            SHA256.HashData(new byte[] { 1 }), DurableCommittedChangeKind.UserDataWithChanges,
            scenario == "effects" && change.Version.Value == 3 ? 2 : 1)).ToList();
        if (scenario == "classification")
        {
            evidence[1] = new(evidence[1].TransactionId, evidence[1].Version,
            evidence[1].ContentHash, DurableCommittedChangeKind.UserDataNoChange, 0);
        }

        evidence.Add(new(Guid.NewGuid(), new(5), SHA256.HashData(new byte[] { 2 }), DurableCommittedChangeKind.UserDataNoChange, 0));
        var checkpoint = new DurableCheckpointEnvelope(_incarnation, new(7),
            new(DatabaseProfileId.PropertyGraph, "test", 1, new byte[] { 1 }),
            evidence.OrderBy(item => item.Version.Value).ToArray(), changes[^1].Cursor);
        await File.WriteAllBytesAsync(At("pages"), []);
        await using (var checkpoints = new FileDatabaseCheckpointStore(At("checkpoint"), pagePath: At("pages"), walFloor: () => 10))
        {
            await checkpoints.SaveAsync(checkpoint);
        }

        await using var candidate = new SegmentedDatabaseCommittedChangeStore(At("journal"));
        if (scenario is "complete" or "retained")
        {
            await candidate.ValidateRecoverySetAsync(checkpoint, default);
            await new LocalDatabaseBackupManager().CreateAsync(At("checkpoint"), At("journal"), At("pages"), At("good.backup"));
            await new LocalDatabaseBackupManager().VerifyAsync(At("good.backup"));
            await Restore("valid-restore");
        }
        else
        {
            await Assert.ThrowsExceptionAsync<InvalidDataException>(async () =>
                await candidate.ValidateRecoverySetAsync(checkpoint, default));
            await Assert.ThrowsExceptionAsync<EntityForgeDbBackupException>(async () =>
                await new LocalDatabaseBackupManager().CreateAsync(At("checkpoint"), At("journal"), At("pages"), At("bad.backup")));
            Assert.IsFalse(File.Exists(At("bad.backup")));
        }
    }

    [TestMethod]
    public async Task Restore_preserves_a_file_created_in_the_destination_before_publication()
    {
        await CreateBackup();
        string destination = At("concurrent-target");
        Directory.CreateDirectory(destination);
        var manager = new LocalDatabaseBackupManager(new WriteBeforeRestore(destination));
        await Assert.ThrowsExactlyAsync<EntityForgeDbBackupException>(async () => await manager.RestoreAsync(
            At("good.backup"), destination, "checkpoint", "wal", "manifest", "journal", "pages",
            new GroupedRedoRestoreLogInitializer()));
        Assert.AreEqual("operator data", await File.ReadAllTextAsync(Path.Combine(destination, "sentinel")));
        Assert.AreEqual(1, Directory.EnumerateFileSystemEntries(destination).Count());
        Assert.AreEqual(0, Directory.EnumerateDirectories(_directory, "concurrent-target.restore-*.new").Count());
    }

    private sealed class WriteBeforeRestore(string destination) : IStoragePublicationObserver
    {
        public void Reached(StoragePublicationStage stage)
        {
            if (stage == StoragePublicationStage.BeforeRestoreReplacement)
            {
                File.WriteAllText(Path.Combine(destination, "sentinel"), "operator data");
            }
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Backup_publication_preserves_unrelated_temporary_files_and_previous_backup_on_failure(bool fail)
    {
        await CreateBackup();
        byte[] original = await File.ReadAllBytesAsync(At("good.backup"));
        string unrelated = At("good.backup.new");
        await File.WriteAllTextAsync(unrelated, "another operation");
        var manager = new LocalDatabaseBackupManager(fail ? new FailBackupPublication() : null);
        async Task Replace() => await manager.CreateAsync(At("checkpoint"), At("journal"), At("pages"), At("good.backup"));
        if (fail)
        {
            await Assert.ThrowsExactlyAsync<EntityForgeDbBackupException>(Replace);
            CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(At("good.backup")));
        }
        else
        {
            await Replace();
        }
        await manager.VerifyAsync(At("good.backup"));
        Assert.AreEqual("another operation", await File.ReadAllTextAsync(unrelated));
        Assert.AreEqual(0, Directory.EnumerateFiles(_directory, "good.backup.backup-*.new").Count());
        Assert.AreEqual(0, Directory.EnumerateDirectories(_directory, "good.backup.journal-*").Count());
    }

    private sealed class FailBackupPublication : IStoragePublicationObserver
    {
        public void Reached(StoragePublicationStage stage)
        {
            if (stage == StoragePublicationStage.BeforeBackupReplacement)
            {
                throw new IOException("Injected failure before replacing the existing backup.");
            }
        }
    }

    private async Task CreateBackup()
    {
        Directory.CreateDirectory(_directory);
        await using (var log = await GroupedRedoDatabaseCommitLog.OpenAsync(At("wal")))
        {
            await log.CommitAsync(Transaction(10));
            byte[] image = new SlottedDataPage(PhysicalPageFileOptions.DefaultPageSize, new(0), generation: 1).Encode();
            await log.CommitWithPhysicalMutationsAsync(Transaction(100), [DatabasePhysicalPageMutation.CreatePage(0, image)]);
            await using (var pages = await PhysicalPageFile.OpenAsync(At("pages")))
            {
                await log.RecoverPagesAsync(pages);
                await pages.FlushAsync();
            }
            await using var store = new FileDatabaseCheckpointStore(At("checkpoint"), pagePath: At("pages"),
                walFloor: () => log.GetRedoStatus().DurableLsn);
            await store.SaveAsync(new(_incarnation, new(100), new(DatabaseProfileId.PropertyGraph, "test", 1, new byte[] { 1 })));
            await log.ReclaimThroughAsync(new(100));
            Assert.AreEqual(2L, log.GetRedoStatus().DurableLsn);
        }
        await new LocalDatabaseBackupManager().CreateAsync(At("checkpoint"), At("journal"), At("pages"), At("good.backup"));
    }

    private ValueTask<LocalDatabaseBackupInfo> Restore(string name) => new LocalDatabaseBackupManager().RestoreAsync(
        At("good.backup"), At(name), "checkpoint", "wal", "manifest", "journal", "pages", new GroupedRedoRestoreLogInitializer());

    private DurableTransactionEnvelope Transaction(long version) => new(_incarnation, new(version), Guid.NewGuid(),
        new(DatabaseProfileId.PropertyGraph, "test", 1, new byte[] { 1 }), DurableCommittedChangeKind.UserDataNoChange, null);
}
