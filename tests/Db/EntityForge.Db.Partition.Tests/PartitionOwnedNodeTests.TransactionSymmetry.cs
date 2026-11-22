using EntityForge.Db.Semantic;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    private async Task WithOrdinaryTransactions(DatabaseProfileId profile,
        Func<HttpPartitionEntityClient, PartitionApplicationClient, PartitionOwnedNode[], IPartitionTransactionAuthority, Task> action, bool singleOwner = false,
        Func<Guid, DistributedRoutingBinding, ValueTask<IPartitionTransactionAuthority>>? authorityFactory = null)
    {
        var routing = new StableHashDistributedShardRouter(new(new(1), singleOwner ? [new("a")] : [new("a"), new("b")])).Binding;
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
        await using var fileAuthority = FilePartitionTransactionAuthority.Open(Path.Combine(_root, "authority"));
        IPartitionTransactionAuthority authority = authorityFactory is null ? fileAuthority :
            await authorityFactory(a.Lease.Placement.Claim.Generation.Value, routing);
        await using var first = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "a"), profile, a.Lease, routing, a, Options, transactionAuthority: authority, timestampClock: ReadClock);
        await using var second = singleOwner ? null : await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "b"), profile, b.Lease, routing, b, Options, transactionAuthority: authority, timestampClock: ReadClock);
        PartitionOwnedNode[] nodes = second is null ? [first] : [first, second];
        using var certificate = PartitionCertificate();
        using var http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, observed, _, _) => observed?.Thumbprint == certificate.Thumbprint });
        await using var peerService = second is null ? null : new PartitionParticipantHttpService(second);
        await using var peerHost = peerService is null ? null : await StartPartitionHostAsync(peerService, certificate);
        IPartitionTransactionParticipant[] participants = second is null ? [first] :
            [first, new HttpPartitionTransactionParticipant(http, PartitionAddress(peerHost!), PartitionToken, second.Domain, authority.Identity)];
        var client = new PartitionApplicationClient(authority, routing, participants, ReadClock);
        await using var applicationService = new PartitionApplicationHttpService(client);
        await using var gateway = await StartApplicationHostAsync(applicationService, certificate);
        var remote = new HttpPartitionEntityClient(http, PartitionAddress(gateway), PartitionToken, client.Generation, profile);
        await action(remote, client, nodes, authority);
    }

    [DataTestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph, false)]
    [DataRow(DatabaseProfileId.Rdf, false)]
    [DataRow(DatabaseProfileId.Semantic, false)]
    [DataRow(DatabaseProfileId.Semantic, true)]
    public async Task All_profiles_use_the_same_public_transaction_API(DatabaseProfileId profile, bool singleOwner)
    {
        await WithOrdinaryTransactions(profile, async (remote, client, nodes, authority) =>
        {
            var transaction = remote.CreateTransaction();
            for (int i = 0; i < 64; i++)
            {
                transaction.Save(new Item(i.ToString(), "value-" + i));
            }

            Assert.AreEqual(64, transaction.Count);
            var receipt = await transaction.CommitAsync();
            Assert.AreEqual(transaction.TransactionId, receipt.OperationId);
            Assert.AreSame(receipt, await transaction.CommitAsync());
            async Task<string?> Read(string id) => profile switch
            {
                DatabaseProfileId.PropertyGraph => (await remote.ReadGraphAsync<Item>(id)).Value?.Name,
                DatabaseProfileId.Rdf => (await remote.ReadRdfAsync<Item>(id)).Value?.Name,
                _ => (await remote.ReadSemanticAsync<Item>(id)).Value?.Name
            };
            for (int i = 0; i < 64; i++)
            {
                Assert.AreEqual("value-" + i, await Read(i.ToString()));
            }

            var update = remote.CreateTransaction();
            update.Save(new Item("0", "replacement")).Delete<Item>("1");
            await update.CommitAsync();
            Assert.AreEqual("replacement", await Read("0"));
            Assert.IsNull(await Read("1"));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var cancelled = remote.CreateTransaction().Save(new Item("cancelled", "never"));
            await Assert.ThrowsAsync<OperationCanceledException>(() => cancelled.CommitAsync(cancellation.Token).AsTask());
            Assert.IsNull(await authority.ReadApplicationAsync(cancelled.TransactionId));
            Assert.IsNull(await Read("cancelled"));
        }, singleOwner);
    }

    [TestMethod]
    public async Task A_conflict_in_one_Semantic_contribution_rejects_the_whole_transaction()
    {
        await WithOrdinaryTransactions(DatabaseProfileId.Semantic, async (remote, client, nodes, authority) =>
        {
            await remote.CreateTransaction().Save(new Item("left", "before-left")).Save(new Item("right", "before-right")).CommitAsync();
            var transaction = remote.CreateTransaction().Save(new Item("left", "after-left")).Save(new Item("right", "after-right"));
            var selected = await PartitionSemanticPlanning.SelectAsync(client.Generation, transaction.TransactionId,
                nodes[0].Routing, nodes, transaction.Capture(), default);
            var plan = PartitionSemanticPlanning.Plan(selected, nodes[0].Routing, nodes.Select(n => n.Domain).ToArray());
            await authority.BindApplicationAsync(selected);
            await authority.BindAsync(plan);
            await remote.CreateTransaction().Save(new Item("right", "concurrent")).CommitAsync();
            var result = await new PartitionTransactionCoordinator(authority).ExecuteSelectedAsync(plan, nodes, default);
            Assert.IsFalse(result.Decision.Commit);
            Assert.AreEqual("before-left", (await remote.ReadSemanticAsync<Item>("left")).Value!.Name);
            Assert.AreEqual("concurrent", (await remote.ReadSemanticAsync<Item>("right")).Value!.Name);
            foreach (var node in nodes)
            {
                Assert.IsTrue((await node.Kernel.PartitionTransactions.ReadAsync()).All(p =>
                    p.State != EntityForge.Db.Durability.DurablePartitionTransactionState.Prepared));
            }

            await remote.CreateTransaction().Save(new Item("left", "later-left")).Save(new Item("right", "later-right")).CommitAsync();
            Assert.AreEqual("later-left", (await remote.ReadSemanticAsync<Item>("left")).Value!.Name);
            Assert.AreEqual("later-right", (await remote.ReadSemanticAsync<Item>("right")).Value!.Name);
        });
    }
}
