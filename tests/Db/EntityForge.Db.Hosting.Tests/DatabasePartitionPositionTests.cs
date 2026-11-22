using System.Text.Json;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class DatabasePartitionPositionTests
{
    private static DatabasePartitionIdentity Identity(string partition, Guid generation) =>
        new(generation, DatabaseProfileId.PropertyGraph, "routing", partition, Guid.NewGuid());

    [TestMethod]
    public void Position_coverage_never_compares_unrelated_local_counters()
    {
        Guid generation = Guid.NewGuid();
        var a = Identity("a", generation);
        var b = Identity("b", generation);
        var required = new DatabasePositionVector([new(a, new(7)), new(b, new(2))]);
        Assert.IsFalse(new DatabasePositionVector([new(a, new(100))]).Covers(required));
        Assert.IsFalse(new DatabasePositionVector([new(a, new(100)), new(b, new(1))]).Covers(required));
        Assert.IsTrue(new DatabasePositionVector([new(b, new(2)), new(a, new(8))]).Covers(required));
        Assert.IsFalse(new DatabasePartitionPosition(Identity("a", generation), new(100)).Covers(new(a, new(7))));
        Assert.IsFalse(new DatabasePartitionPosition(a, new(100)).Covers(new(b, new(1))));
    }

    [TestMethod]
    public void Vector_owns_collection_and_rejects_mixed_domains_and_duplicate_owners()
    {
        Guid generation = Guid.NewGuid();
        var a = Identity("a", generation);
        var b = Identity("b", generation);
        DatabasePartitionPosition[] input = [new(b, new(2)), new(a, new(4))];
        var vector = new DatabasePositionVector(input);
        input[0] = new(a, new(99));
        Assert.AreEqual("a", vector.Positions[0].Identity.Partition);
        Assert.AreEqual(new DatabaseVersion(2), vector.Positions[1].Version);
        Assert.ThrowsExactly<ArgumentException>(() => new DatabasePositionVector([new(a, new(1)), new(Identity("a", generation), new(2))]));
        Assert.ThrowsExactly<ArgumentException>(() => new DatabasePositionVector([new(a, new(1)), new(Identity("b", Guid.NewGuid()), new(2))]));
        Assert.ThrowsExactly<ArgumentException>(() => new DatabasePositionVector([]));
    }

    [TestMethod]
    public void Distributed_completion_roundtrips_local_positions_and_checks_each_member()
    {
        Guid generation = Guid.NewGuid(), operation = Guid.NewGuid();
        var a = Identity("a", generation);
        var b = Identity("b", generation);
        DatabaseCommitReceipt Native(DatabasePartitionIdentity identity, long position, Guid? id = null) =>
            new(identity.Incarnation, id ?? operation, identity.Profile, new(position), DatabaseCommitOutcome.AppliedWithChanges, null, 1);
        var result = new DatabaseDistributedCommitReceipt(operation,
            [new(a, Native(a, 103)), new(b, Native(b, 8))]);
        var restored = JsonSerializer.Deserialize<DatabaseDistributedCommitReceipt>(JsonSerializer.Serialize(result))!;
        Assert.AreEqual(operation, restored.OperationId);
        Assert.IsTrue(restored.Position.Covers(result.Position));
        Assert.IsTrue(result.Position.Covers(restored.Position));
        Assert.AreEqual(2, restored.Members.Count);
        Assert.ThrowsExactly<ArgumentException>(() => new DatabasePartitionCommit(a, Native(b, 103)));
        Assert.ThrowsExactly<ArgumentException>(() => new DatabaseDistributedCommitReceipt(operation,
            [new(a, Native(a, 103)), new(b, Native(b, 8, Guid.NewGuid()))]));
        Assert.ThrowsExactly<ArgumentException>(() => new DatabasePartitionCommit(a,
            new(a.Incarnation, operation, a.Profile, new(1), DatabaseCommitOutcome.Committed, null, null)));
    }
}
