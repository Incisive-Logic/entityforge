using EntityForge.Db.Semantic;
using EntityForge.Db;
using EntityForge.Db.Storage.Local;
using EntityForge.Db.Storage.Pages;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class LocalDatabaseFormatManagerTests
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
    public async Task Empty_directory_is_initialized_once_and_reopened_as_current()
    {
        LocalDatabaseFormatManager manager = Manager(DatabaseProfileId.Semantic);
        LocalDatabaseFormatDiscovery created = await manager.EnsureCurrentAsync();
        LocalDatabaseFormatDiscovery reopened = await manager.EnsureCurrentAsync();
        Assert.AreEqual(LocalDatabaseFormatDisposition.EmptyInitialized, created.Disposition);
        Assert.AreEqual(LocalDatabaseFormatDisposition.Current, reopened.Disposition);
        Assert.AreEqual(DatabaseProfileId.Semantic, reopened.Format.Profile);
        Assert.AreNotEqual(Guid.Empty, created.Format.DatabaseIncarnation);
        Assert.AreEqual(created.Format.DatabaseIncarnation, reopened.Format.DatabaseIncarnation);
    }

    [TestMethod]
    public async Task Manifest_profile_prevents_wrong_profile_open()
    {
        await Manager(DatabaseProfileId.PropertyGraph).EnsureCurrentAsync();
        await Assert.ThrowsExceptionAsync<EntityForgeDbFormatException>(async () =>
            await Manager(DatabaseProfileId.Semantic).EnsureCurrentAsync());
    }

    [TestMethod]
    public async Task Durable_artifacts_without_manifest_are_rejected_not_adopted()
    {
        Directory.CreateDirectory(DirectoryPath());
        await using (var log = await GroupedRedoDatabaseCommitLog.OpenAsync(
                         Path.Combine(_directory!, "entityforge.wal")))
        {
        }
        await Assert.ThrowsExceptionAsync<EntityForgeDbFormatException>(async () =>
            await Manager(DatabaseProfileId.Semantic).EnsureCurrentAsync());
        Assert.IsFalse(File.Exists(Path.Combine(_directory!, LocalDatabaseFormatManager.DefaultManifestFileName)));
    }

    [TestMethod]
    public async Task Neutral_wal_and_checkpoint_headers_are_validated_for_both_profiles()
    {
        foreach (DatabaseProfileId profile in new[] { DatabaseProfileId.PropertyGraph, DatabaseProfileId.Semantic, DatabaseProfileId.Rdf })
        {
            _directory = Path.Combine(Path.GetTempPath(), $"entityforge-format-{profile}-{Guid.NewGuid():N}");
            await Manager(profile).EnsureCurrentAsync();
            await using (var log = await GroupedRedoDatabaseCommitLog.OpenAsync(
                             Path.Combine(_directory, "entityforge.wal")))
            {
            }
            await using (var checkpoint = new FileDatabaseCheckpointStore(Path.Combine(_directory, "entityforge.checkpoint")))
            {
                await checkpoint.SaveAsync(new(Guid.NewGuid(), new(0), new(profile, "test.checkpoint", 1, Array.Empty<byte>())));
            }
            Assert.AreEqual(LocalDatabaseFormatDisposition.Current,
                (await Manager(profile).EnsureCurrentAsync()).Disposition);
            Directory.Delete(_directory, true);
            _directory = null;
        }
    }

    private LocalDatabaseFormatManager Manager(DatabaseProfileId profile) => new(
        DirectoryPath(), "entityforge.wal", "entityforge.checkpoint", profile: profile);

    private string DirectoryPath() => _directory ??=
        Path.Combine(Path.GetTempPath(), $"entityforge-format-{Guid.NewGuid():N}");
}
