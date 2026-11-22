using EntityForge.Db.Storage;
using EntityForge.Db.Storage.Local;
using EntityForge.Db.Storage.Pages;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class BackupRecoveryEvidenceTests
{
    [TestMethod]
    [DataRow("backup")]
    [DataRow("restore-absent")]
    [DataRow("restore-empty")]
    public async Task Cancellation_before_archive_or_restore_publication_cleans_owned_staging(string mode)
    {
        await CreateBackup();
        byte[] original = await File.ReadAllBytesAsync(At("good.backup"));
        string unrelated = At("good.backup.new");
        await File.WriteAllTextAsync(unrelated, "unrelated operator file");
        string target = At("cancelled-restore");
        if (mode == "restore-empty")
        {
            Directory.CreateDirectory(target);
        }
        using var cancellation = new CancellationTokenSource();
        var observer = new CancelBeforePublication(mode == "backup"
            ? StoragePublicationStage.BeforeBackupReplacement : StoragePublicationStage.BeforeRestoreReplacement, cancellation);
        var manager = new LocalDatabaseBackupManager(observer);
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
        {
            if (mode == "backup")
            {
                await manager.CreateAsync(At("checkpoint"), At("journal"), At("pages"), At("good.backup"), cancellation.Token);
            }
            else
            {
                await manager.RestoreAsync(At("good.backup"), target, "checkpoint", "wal", "manifest", "journal", "pages",
                    new GroupedRedoRestoreLogInitializer(), cancellation.Token);
            }
        });
        Assert.IsTrue(observer.ReachedBoundary, "The operation must reach the populated, validated staging image before cancellation.");
        Assert.IsTrue(cancellation.IsCancellationRequested);
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(At("good.backup")));
        Assert.AreEqual("unrelated operator file", await File.ReadAllTextAsync(unrelated));
        Assert.AreEqual(0, Directory.EnumerateFiles(_directory, "good.backup.backup-*").Count());
        Assert.AreEqual(0, Directory.EnumerateDirectories(_directory, "good.backup.journal-*").Count());
        Assert.AreEqual(0, Directory.EnumerateDirectories(_directory, "good.backup*.verify-*").Count());
        Assert.AreEqual(0, Directory.EnumerateDirectories(_directory, "cancelled-restore.restore-*").Count());
        Assert.AreEqual(mode == "restore-empty", Directory.Exists(target));
        if (Directory.Exists(target))
        {
            Assert.AreEqual(0, Directory.EnumerateFileSystemEntries(target).Count());
        }
        await new LocalDatabaseBackupManager().VerifyAsync(At("good.backup"));
        await Restore("after-cancellation");
        Assert.IsTrue(File.Exists(At("after-cancellation/checkpoint")));
    }

    private sealed class CancelBeforePublication(StoragePublicationStage boundary, CancellationTokenSource cancellation) : IStoragePublicationObserver
    {
        internal bool ReachedBoundary
        {
            get; private set;
        }

        public void Reached(StoragePublicationStage stage)
        {
            if (stage == boundary)
            {
                ReachedBoundary = true;
                cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
            }
        }
    }
}
