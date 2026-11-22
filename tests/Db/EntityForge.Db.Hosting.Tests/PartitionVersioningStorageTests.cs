using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Rdf;
using EntityForge.Db.Semantic;
using EntityForge.Db.Storage.Local;
using EntityForge.Db.Storage.Pages;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class PartitionVersioningStorageTests
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ef-partition-mode-" + Guid.NewGuid().ToString("N"));

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    [DataTestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph)]
    [DataRow(DatabaseProfileId.Rdf)]
    [DataRow(DatabaseProfileId.Semantic)]
    public async Task Every_profile_pins_mode_before_first_write_and_rejects_incompatible_reopen(DatabaseProfileId profile)
    {
        string local = Path.Combine(_root, "local"), ordinary = Path.Combine(_root, "ordinary");
        await OpenAndClose(profile, local, DatabaseVersioningMode.PartitionLocal, checkpoint: true);
        await Assert.ThrowsExactlyAsync<EntityForgeDbFormatException>(() => OpenAndClose(profile, local, DatabaseVersioningMode.LocalOrAuthoritative));
        await OpenAndClose(profile, local, DatabaseVersioningMode.PartitionLocal);
        await OpenAndClose(profile, ordinary, DatabaseVersioningMode.LocalOrAuthoritative);
        await Assert.ThrowsExactlyAsync<EntityForgeDbFormatException>(() => OpenAndClose(profile, ordinary, DatabaseVersioningMode.PartitionLocal));
        var checkpoint = await new FileDatabaseCheckpointStore(Path.Combine(local, "entityforge.checkpoint")).LoadAsync();
        Assert.AreEqual(DatabaseVersioningMode.PartitionLocal, checkpoint!.VersioningMode);
    }

    private static async Task OpenAndClose(DatabaseProfileId profile, string path, DatabaseVersioningMode mode, bool checkpoint = false)
    {
        switch (profile)
        {
            case DatabaseProfileId.PropertyGraph:
                await using (var db = await GraphDatabase.OpenLocalAsync(new(path)
                {
                    VersioningMode = mode
                }))
                {
                    if (checkpoint)
                    {
                        await db.CreateCheckpointAsync();
                    }
                }

                break;
            case DatabaseProfileId.Rdf:
                await using (var db = await RdfDatabase.OpenLocalAsync(new(path)
                {
                    VersioningMode = mode
                }))
                {
                    if (checkpoint)
                    {
                        await db.CreateCheckpointAsync();
                    }
                }

                break;
            case DatabaseProfileId.Semantic:
                await using (var db = await SemanticDatabase.OpenLocalAsync(new(path)
                {
                    VersioningMode = mode
                }))
                {
                    if (checkpoint)
                    {
                        await db.CreateCheckpointAsync();
                    }
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(profile));
        }
    }

    [TestMethod]
    public async Task Backup_restore_keeps_partition_mode_and_writable_local_recovery()
    {
        string source = Path.Combine(_root, "source"), restored = Path.Combine(_root, "restored");
        string backup = Path.Combine(_root, "backup");
        var vertex = GraphVertexId.New();
        await using (var db = await GraphDatabase.OpenLocalAsync(new(source)
        {
            VersioningMode = DatabaseVersioningMode.PartitionLocal
        }))
        {
            await db.CommitAsync(new(Guid.NewGuid(), [new CreateVertex(vertex, ["Item"])]));
            await db.CreateCheckpointAsync();
        }
        var manager = new LocalDatabaseBackupManager();
        var created = await manager.CreateAsync(Path.Combine(source, "entityforge.checkpoint"), Path.Combine(source, "entityforge.changes"),
            Path.Combine(source, "entityforge.pages"), backup);
        Assert.AreEqual(DatabaseVersioningMode.PartitionLocal, created.VersioningMode);
        Assert.AreEqual(created.VersioningMode, (await manager.VerifyAsync(backup)).VersioningMode);
        await manager.RestoreAsync(backup, restored, "entityforge.checkpoint", "entityforge.wal", "entityforge.manifest",
            "entityforge.changes", "entityforge.pages", new GroupedRedoRestoreLogInitializer());
        await Assert.ThrowsExactlyAsync<EntityForgeDbFormatException>(() => OpenAndClose(DatabaseProfileId.PropertyGraph, restored, DatabaseVersioningMode.LocalOrAuthoritative));
        await using (var db = await GraphDatabase.OpenLocalAsync(new(restored)
        {
            VersioningMode = DatabaseVersioningMode.PartitionLocal
        }))
        {
            Assert.IsNotNull(db.GetVertex(vertex));
            await db.CommitAsync(new(Guid.NewGuid(), [new SetVertexProperty(vertex, "name", new DatabaseValue.String("after"))]));
        }
        await using var reopened = await GraphDatabase.OpenLocalAsync(new(restored)
        {
            VersioningMode = DatabaseVersioningMode.PartitionLocal
        });
        Assert.AreEqual(new DatabaseValue.String("after"), reopened.GetVertex(vertex)!.Properties["name"]);
    }
}
