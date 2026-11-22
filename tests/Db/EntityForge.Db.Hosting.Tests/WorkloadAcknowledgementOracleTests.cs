namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public void Workload_oracle_recovers_interrupted_compaction_without_loss_or_double_count(int boundary)
    {
        string directory = NewDirectory();
        using (var oracle = new WorkloadAcknowledgementOracle(directory, 4, 2))
        {
            oracle.Append(0, 0, 1, Guid.NewGuid());
            oracle.Append(1, 3, 1, Guid.NewGuid());
            Assert.ThrowsExactly<IOException>(() => oracle.Checkpoint(at =>
            {
                if ((int)at == boundary)
                {
                    throw new IOException("Injected oracle checkpoint interruption.");
                }
            }));
        }
        var recovered = WorkloadAcknowledgementOracle.Recover(directory);
        CollectionAssert.AreEqual(new[] { 1, 0, 0, 1 }, recovered.Values);
        Assert.AreEqual(2L, recovered.Acknowledged);
        using (var reopened = new WorkloadAcknowledgementOracle(directory, 4, 2))
        {
            reopened.Append(0, 0, 2, Guid.NewGuid());
            reopened.Append(1, 1, 1, Guid.NewGuid());
            reopened.Checkpoint();
            Assert.ThrowsExactly<ObjectDisposedException>(() => reopened.Append(0, 2, 1, Guid.NewGuid()));
        }
        recovered = WorkloadAcknowledgementOracle.Recover(directory);
        CollectionAssert.AreEqual(new[] { 2, 1, 0, 1 }, recovered.Values);
        Assert.AreEqual(4L, recovered.Acknowledged);
        Assert.AreEqual(0, Directory.GetFiles(directory, "*.jsonl").Length);
    }

    [TestMethod]
    public async Task Workload_oracle_bounds_journal_retention_across_concurrent_windows()
    {
        string directory = NewDirectory();
        for (int window = 0; window < 20; window++)
        {
            using var oracle = new WorkloadAcknowledgementOracle(directory, 8, 8);
            await Task.WhenAll(Enumerable.Range(0, 8).Select(actor => Task.Run(() =>
                oracle.Append(actor, actor, window + 1, Guid.NewGuid()))));
            oracle.Checkpoint();
            Assert.AreEqual(1, Directory.GetFiles(directory).Length);
            Assert.IsTrue(new FileInfo(Path.Combine(directory, "state.json")).Length < 4096);
        }
        var recovered = WorkloadAcknowledgementOracle.Recover(directory);
        Assert.AreEqual(160L, recovered.Acknowledged);
        CollectionAssert.AreEqual(Enumerable.Repeat(20, 8).ToArray(), recovered.Values);
    }

    [TestMethod]
    public void Workload_oracle_refuses_corrupt_or_missing_state_instead_of_resetting()
    {
        string directory = NewDirectory();
        using (var oracle = new WorkloadAcknowledgementOracle(directory, 4, 2))
        {
            oracle.Append(0, 0, 1, Guid.NewGuid());
        }
        string state = Path.Combine(directory, "state.json");
        string original = File.ReadAllText(state);
        File.WriteAllText(state, original.Replace("Sha256\":\"", "Sha256\":\"BAD", StringComparison.Ordinal));
        Assert.ThrowsExactly<InvalidDataException>(() => WorkloadAcknowledgementOracle.Recover(directory));
        File.Delete(state);
        Assert.ThrowsExactly<InvalidDataException>(() => new WorkloadAcknowledgementOracle(directory, 4, 2));
    }
}
