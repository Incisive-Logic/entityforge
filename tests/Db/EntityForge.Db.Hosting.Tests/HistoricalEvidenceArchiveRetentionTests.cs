using System.Text.Json;
using EntityForge.Db.Storage;
using EntityForge.Db.Storage.Local;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class HistoricalEvidenceArchiveRetentionTests
{
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
    public async Task Prune_removes_only_expired_archives_and_is_idempotent()
    {
        _directory = NewDirectory();
        DateTimeOffset now = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);
        await WriteArchiveAsync("expired", now.AddDays(-2), now.AddDays(-1), 17);
        await WriteArchiveAsync("current", now.AddDays(-1), now.AddDays(1), 19);
        await WriteArchiveAsync("indefinite", now.AddDays(-10), null, 23);
        var manager = new LocalHistoricalEvidenceArchiveManager(new(_directory), new FixedTimeProvider(now));

        HistoricalEvidenceArchivePruneResult first = await manager.PruneExpiredAsync();
        HistoricalEvidenceArchivePruneResult second = await manager.PruneExpiredAsync();

        Assert.AreEqual(new(1, 17), first);
        Assert.AreEqual(new(0, 0), second);
        Assert.IsFalse(File.Exists(Path.Combine(_directory, "expired.json")));
        Assert.IsFalse(File.Exists(Path.Combine(_directory, "expired.efarchive")));
        Assert.IsTrue(File.Exists(Path.Combine(_directory, "current.json")));
        Assert.IsTrue(File.Exists(Path.Combine(_directory, "indefinite.json")));
    }

    [TestMethod]
    public async Task Prune_rejects_inconsistent_manifest_identity_without_deleting_payload()
    {
        _directory = NewDirectory();
        DateTimeOffset now = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);
        await WriteArchiveAsync("actual", now.AddDays(-2), now.AddDays(-1), 11, "different");
        var manager = new LocalHistoricalEvidenceArchiveManager(new(_directory), new FixedTimeProvider(now));

        await Assert.ThrowsExceptionAsync<InvalidDataException>(async () => await manager.PruneExpiredAsync());

        Assert.IsTrue(File.Exists(Path.Combine(_directory, "actual.json")));
        Assert.IsTrue(File.Exists(Path.Combine(_directory, "actual.efarchive")));
    }

    private async Task WriteArchiveAsync(string fileId, DateTimeOffset created, DateTimeOffset? retainUntil,
        int payloadBytes, string? manifestId = null)
    {
        Directory.CreateDirectory(_directory!);
        string archiveId = manifestId ?? fileId;
        var manifest = new HistoricalEvidenceArchiveInfo(archiveId, Guid.NewGuid(), DatabaseProfileId.Semantic,
            new(1), created, retainUntil, "unused", payloadBytes, archiveId + ".efarchive");
        await File.WriteAllBytesAsync(Path.Combine(_directory!, fileId + ".efarchive"), new byte[payloadBytes]);
        await File.WriteAllBytesAsync(Path.Combine(_directory!, fileId + ".json"),
            JsonSerializer.SerializeToUtf8Bytes(manifest));
    }

    private static string NewDirectory() => Path.Combine(Path.GetTempPath(), "entityforge-archive-retention-tests",
        Guid.NewGuid().ToString("N"));

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
