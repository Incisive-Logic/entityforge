using System.Security.Cryptography;
using EntityForge.Db;
using EntityForge.Db.Kernel;
using EntityForge.Db.Storage.Local;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class JournalArtifactReadFailureTests
{
    [TestMethod]
    [DataRow("publication.efcp", false)]
    [DataRow("publication.efcp", true)]
    [DataRow("catalog-head.efch", false)]
    [DataRow("catalog-head.efch", true)]
    public async Task Journal_recovery_refuses_directory_metadata_without_reclaiming_artifacts(string name, bool publish)
    {
        string root = Path.Combine(Path.GetTempPath(), "ef-journal-read-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Guid incarnation = Guid.NewGuid();
            var cursor = new DatabaseChangeCursorCodec().Encode(incarnation, new(1));
            var change = new CommittedChangeSet(incarnation, new(1), Guid.NewGuid(), DatabaseProfileId.PropertyGraph,
                cursor, new("test.codec", 1, new byte[] { 1, 2, 3 }, 1, new([], [])));
            await using (var store = new SegmentedDatabaseCommittedChangeStore(root))
            {
                await store.AppendAsync(change);
                if (publish)
                {
                    await store.PublishThroughAsync(cursor);
                }
            }
            string metadata = Path.Combine(root, name);
            if (File.Exists(metadata))
            {
                File.Move(metadata, metadata + ".preserved");
            }
            Directory.CreateDirectory(metadata);
            await File.WriteAllTextAsync(Path.Combine(metadata, "sentinel"), "preserve");
            var before = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                .ToDictionary(path => path, path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
            await using (var reopened = new SegmentedDatabaseCommittedChangeStore(root))
            {
                await Assert.ThrowsExactlyAsync<EntityForgeDbFormatException>(async () => await reopened.GetBoundsAsync());
            }
            CollectionAssert.AreEquivalent(before.Keys.ToArray(), Directory.GetFiles(root, "*", SearchOption.AllDirectories));
            foreach (var file in before)
            {
                Assert.AreEqual(file.Value, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file.Key))), file.Key);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
