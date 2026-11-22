using EntityForge.Db.Semantic;
using EntityForge.Db.Distributed.Hosting;
using EntityForge.Db.Distributed.Sharding;
using EntityForge.Db.Mapping;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [EntityType("partition-node-tests", "semantic-tree")]
    public sealed class PartitionSemanticTree
    {
        public string Id { get; set; } = "";

        public string Name { get; set; } = "";

        [EntityOwned] public List<PartitionSemanticLeaf> Children { get; set; } = [];
    }

    [EntityType("partition-node-tests", "semantic-leaf")]
    public sealed record PartitionSemanticLeaf(string Id, string Name);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Semantic_partition_sessions_replace_replay_prepare_and_abort_over_https(bool timestamps) =>
        await SemanticPartitionLifecycleAsync(timestamps, false);

    private async Task SemanticPartitionLifecycleAsync(bool timestamps, bool live)
    {
        using var timestampStore = new FileTimestampStore(Path.Combine(_root, "clock"), "domain");
        EntityForge.Db.Kernel.DatabaseSharedTimestampService? clock = null; // Historical shared-oracle variants now exercise the one production clock protocol.
        var routing = new StableHashDistributedShardRouter(new(new(1), [new("a"), new("b")])).Binding;
        var a = new Authority();
        var b = new Authority
        {
            Lease = a.Lease with
            {
                Placement = a.Lease.Placement with
                {
                    Claim = a.Lease.Placement.Claim with
                    {
                        Shard = new("b"),
                        Owner = new("second")
                    }
                }
            }
        };
        await using var authorityLifetime = await ApplicationAuthorityLifetime.OpenAsync(Path.Combine(_root, "authority"),
            a.Lease.Placement.Claim.Generation.Value, DatabaseProfileId.Semantic, routing, live);
        var authority = authorityLifetime.Authority;
        await using var first = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "a"), DatabaseProfileId.Semantic, a.Lease, routing, a, Options, transactionAuthority: authority, timestamps: clock, timestampClock: ReadClock);
        await using var second = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "b"), DatabaseProfileId.Semantic, b.Lease, routing, b, Options, transactionAuthority: authority, timestamps: clock, timestampClock: ReadClock);
        using var certificate = PartitionCertificate();
        await using var serviceA = new PartitionParticipantHttpService(first);
        await using var serviceB = new PartitionParticipantHttpService(second);
        await using var hostA = await StartPartitionHostAsync(serviceA, certificate);
        await using var hostB = await StartPartitionHostAsync(serviceB, certificate);
        using var http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, observed, _, _) => observed?.Thumbprint == certificate.Thumbprint });
        var remoteA = new HttpPartitionTransactionParticipant(http, PartitionAddress(hostA), PartitionToken, first.Domain, authority.Identity) { SharedTimestamps = clock };
        var remoteB = new HttpPartitionTransactionParticipant(http, PartitionAddress(hostB), PartitionToken, second.Domain, authority.Identity) { SharedTimestamps = clock };
        var client = new PartitionApplicationClient(authority, routing, [remoteA, remoteB],
            ReadClock);
        var store = new HttpSemanticEntityClient(http, client.Generation).EntityStore<Item>();
        await using var applicationService = new PartitionApplicationHttpService(client);
        await using var gateway = await StartApplicationHostAsync(applicationService, certificate);
        var remote = new HttpPartitionEntityClient(http, PartitionAddress(gateway), PartitionToken, client.Generation, DatabaseProfileId.Semantic);
        var original = store.CaptureSave(new("one", "before"), SemanticEntityWriteOperation.Create());
        var receipt = await client.DeliverAsync(original.OperationId, original.Export());
        Assert.AreEqual(1, receipt.Members.Count);
        var changed = store.CaptureSave(new("one", "after"), SemanticEntityWriteOperation.Create());
        await client.PrepareAsync(changed.OperationId, [changed.Export()], CancellationToken.None);
        Assert.AreEqual(EntityOperationState.PlanBound, (await client.ResolveAsync(changed.OperationId, CancellationToken.None)).State);
        await client.DeliverAsync(changed.OperationId, changed.Export());
        var read = await remote.ReadSemanticAsync<Item>("one");
        Assert.AreEqual("after", read.Value!.Name);
        Assert.AreEqual(2, read.Position.Positions.Count);
        Assert.AreEqual(EntityOperationState.Committed, (await client.ResolveAsync(changed.OperationId, CancellationToken.None)).State);
        var replay = await client.DeliverAsync(original.OperationId, original.Export());
        Assert.IsTrue(replay.Members.All(member => member.Receipt.Outcome == DatabaseCommitOutcome.IdempotentReplay));
        var aborted = store.CaptureSave(new("one", "never"), SemanticEntityWriteOperation.Create());
        await client.PrepareAsync(aborted.OperationId, [aborted.Export()], CancellationToken.None);
        Assert.AreEqual(EntityOperationState.Aborted, (await client.AbortAsync(aborted.OperationId, CancellationToken.None)).State);
        Assert.AreEqual("after", (await remote.ReadSemanticAsync<Item>("one")).Value!.Name);
        var tree = new PartitionSemanticTree { Id = "tree", Name = "root", Children = [new("left", "one"), new("right", "two")] };
        await remote.SaveSemanticAsync(tree);
        var loaded = (await remote.ReadSemanticAsync<PartitionSemanticTree>("tree")).Value!;
        Assert.AreEqual(2, loaded.Children.Count);
        CollectionAssert.AreEquivalent(new[] { "one", "two" }, loaded.Children.Select(child => child.Name).ToArray());
        tree.Children.RemoveAt(0);
        await remote.SaveSemanticAsync(tree);
        Assert.AreEqual(1, (await remote.ReadSemanticAsync<PartitionSemanticTree>("tree")).Value!.Children.Count);
        await remote.DeleteSemanticAsync<PartitionSemanticTree>("tree");
        Assert.IsNull((await remote.ReadSemanticAsync<PartitionSemanticTree>("tree")).Value);
        // Publication uses the same authenticated remote participant protocol as data writes.
        var policyReceipt = await client.PublishSemanticGovernanceAsync(SemanticEntityWriteOperation.Create().OperationId,
            second.Domain.Partition, new(new("urn:test:remote-disabled-policy"), SemanticGovernanceRuleKind.Policy,
                new("urn:test:definition"), "1", false));
        Assert.AreEqual(2, policyReceipt.Members.Count);
        var governed = store.CaptureSave(new("one", "policy owner included"), SemanticEntityWriteOperation.Create());
        Assert.AreEqual(2, (await client.DeliverAsync(governed.OperationId, governed.Export())).Members.Count);
        Assert.AreEqual("policy owner included", (await remote.ReadSemanticAsync<Item>("one")).Value!.Name);
    }
}
