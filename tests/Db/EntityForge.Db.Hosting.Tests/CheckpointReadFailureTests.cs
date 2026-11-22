using EntityForge.Db;
using EntityForge.Db.Storage.Local;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class CheckpointReadFailureTests
{
    [TestMethod]
    public async Task Directory_at_checkpoint_path_is_a_read_failure_not_an_absent_checkpoint()
    {
        string root = Path.Combine(Path.GetTempPath(), "ef-checkpoint-read-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string checkpoint = Path.Combine(root, "kernel.checkpoint");
            Directory.CreateDirectory(checkpoint);
            string sentinel = Path.Combine(checkpoint, "preserve.txt");
            await File.WriteAllTextAsync(sentinel, "existing data must not be treated as empty");
            await using var store = new FileDatabaseCheckpointStore(checkpoint);
            await Assert.ThrowsExactlyAsync<EntityForgeDbCheckpointException>(async () => await store.LoadAsync());
            Assert.AreEqual("existing data must not be treated as empty", await File.ReadAllTextAsync(sentinel));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Missing_checkpoint_returns_null_without_creating_storage(bool missingParent)
    {
        string root = Path.Combine(Path.GetTempPath(), "ef-checkpoint-read-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string parent = missingParent ? Path.Combine(root, "missing") : root;
            string checkpoint = Path.Combine(parent, "kernel.checkpoint");
            await using var store = new FileDatabaseCheckpointStore(checkpoint);
            Assert.IsNull(await store.LoadAsync());
            Assert.IsFalse(File.Exists(checkpoint));
            if (missingParent)
            {
                Assert.IsFalse(Directory.Exists(parent));
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
