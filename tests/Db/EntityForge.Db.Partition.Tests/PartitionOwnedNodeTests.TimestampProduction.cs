using EntityForge.Db.Distributed.Hosting;
using EntityForge.Db.Kernel;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [DataTestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph)]
    [DataRow(DatabaseProfileId.Rdf)]
    [DataRow(DatabaseProfileId.Semantic)]
    public async Task Production_node_refuses_missing_or_unhealthy_clock_before_creating_storage(DatabaseProfileId profile)
    {
        var placement = new Authority();
        await using var files = FilePartitionTransactionAuthority.Open(Path.Combine(_root, "decisions"));
        var decisions = new PartitionDecisionAuthority(files);
        string path = Path.Combine(_root, "not-created");
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => PartitionDatabaseNode.CreateAsync(path, profile,
            placement.Lease, Routing, placement, decisions, Options).AsTask());
        Assert.IsFalse(Directory.Exists(path));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => PartitionDatabaseNode.CreateAsync(path, profile,
            placement.Lease, Routing, placement, decisions, Options,
            options: new()
            {
                TimestampClock = new UnhealthyProductionClock()
            }).AsTask());
        Assert.IsFalse(Directory.Exists(path));
        using var http = new HttpClient();
        Assert.ThrowsExactly<ArgumentException>(() => new PartitionDatabaseGateway(decisions, Routing, [], http,
            "test-token", placement));
    }

    [TestMethod]
    public async Task Public_timestamp_node_and_gateway_reject_different_clock_domains()
    {
        var placement = new Authority();
        await using var files = FilePartitionTransactionAuthority.Open(Path.Combine(_root, "decisions"));
        var decisions = new PartitionDecisionAuthority(files);
        await using var node = await PartitionDatabaseNode.CreateAsync(Path.Combine(_root, "node"), DatabaseProfileId.PropertyGraph,
            placement.Lease, Routing, placement, decisions, Options, options: new()
            {
                TimestampClock = ReadClock
            });
        using var http = new HttpClient();
        Assert.ThrowsExactly<ArgumentException>(() => new PartitionDatabaseGateway(decisions, Routing,
            [new(node.Identity, new("https://localhost:22001"))], http, "test-token", placement,
            timestampClock: new SafeReadClock(), localNode: node));
    }

    private sealed class UnhealthyProductionClock : IDatabaseBoundedClock
    {
        public Guid Domain { get; } = Guid.NewGuid();

        public DatabaseClockInterval Read() => throw new InvalidOperationException("Clock source unavailable.");
    }
}
