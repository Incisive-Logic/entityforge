using EntityForge.Db;
using EntityForge.Db.Storage.Local;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class FormatArtifactReadFailureTests
{
    [TestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph, "entityforge.wal", false)]
    [DataRow(DatabaseProfileId.PropertyGraph, "entityforge.wal", true)]
    [DataRow(DatabaseProfileId.PropertyGraph, "entityforge.checkpoint", false)]
    [DataRow(DatabaseProfileId.PropertyGraph, "entityforge.checkpoint", true)]
    [DataRow(DatabaseProfileId.Rdf, "entityforge.wal", false)]
    [DataRow(DatabaseProfileId.Rdf, "entityforge.wal", true)]
    [DataRow(DatabaseProfileId.Rdf, "entityforge.checkpoint", false)]
    [DataRow(DatabaseProfileId.Rdf, "entityforge.checkpoint", true)]
    public async Task Directory_at_durable_file_path_is_rejected_without_initializing_or_replacing_manifest(
        DatabaseProfileId profile, string artifact, bool existingManifest)
    {
        string root = Path.Combine(Path.GetTempPath(), "ef-format-read-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var manager = new LocalDatabaseFormatManager(root, "entityforge.wal", "entityforge.checkpoint", profile: profile);
            string manifest = Path.Combine(root, LocalDatabaseFormatManager.DefaultManifestFileName);
            byte[]? before = null;
            if (existingManifest)
            {
                await manager.EnsureCurrentAsync();
                before = await File.ReadAllBytesAsync(manifest);
            }

            string blocked = Path.Combine(root, artifact);
            Directory.CreateDirectory(blocked);
            string sentinel = Path.Combine(blocked, "preserve.txt");
            await File.WriteAllTextAsync(sentinel, "existing artifact directory");
            await Assert.ThrowsExactlyAsync<EntityForgeDbFormatException>(async () => await manager.EnsureCurrentAsync());
            Assert.AreEqual("existing artifact directory", await File.ReadAllTextAsync(sentinel));
            if (before is null)
            {
                Assert.IsFalse(File.Exists(manifest), "Invalid artifacts must not acquire a fresh database incarnation.");
            }
            else
            {
                CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(manifest));
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
