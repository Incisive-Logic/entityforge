using EntityForge.Db.Distributed.Hosting;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [DataTestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph)]
    [DataRow(DatabaseProfileId.Rdf)]
    public async Task Remote_retention_resolves_preparation_and_retries_a_lost_authority_completion(DatabaseProfileId profile)
    {
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
        await using var files = FilePartitionTransactionAuthority.Open(Path.Combine(_root, "authority"));
        var decisions = new LostRetentionReply(files) { FailReply = true };
        await using var first = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "a"), profile, a.Lease, routing, a, Options, transactionAuthority: decisions);
        await using var second = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "b"), profile, b.Lease, routing, b, Options, transactionAuthority: decisions);
        using var certificate = PartitionCertificate();
        await using var serviceA = new PartitionParticipantHttpService(first);
        await using var serviceB = new PartitionParticipantHttpService(second);
        await using var hostA = await StartPartitionHostAsync(serviceA, certificate);
        await using var hostB = await StartPartitionHostAsync(serviceB, certificate);
        using var http = new HttpClient(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, observed, _, _) => observed?.Thumbprint == certificate.Thumbprint
        });
        var remoteA = new HttpPartitionTransactionParticipant(http, PartitionAddress(hostA), PartitionToken, first.Domain, decisions.Identity);
        var remoteB = new HttpPartitionTransactionParticipant(http, PartitionAddress(hostB), PartitionToken, second.Domain, decisions.Identity);
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-1);
        Guid old = EntityOperationIds.New(cutoff.AddMinutes(-1));
        var plan = new PartitionTransactionPlan(old, routing.MapVersion,
            [new(first.Domain, new(0), NativePayload(profile, routing, new("a"), old)),
                new(second.Domain, new(0), NativePayload(profile, routing, new("b"), old))]);
        await decisions.BindAsync(plan);
        await remoteA.PrepareNativeAsync(plan);
        await Assert.ThrowsExactlyAsync<AggregateException>(async () =>
            await PartitionRemoteRetention.RetainAsync(decisions, [remoteA, remoteB], routing, cutoff));
        await PartitionRemoteRetention.RetainAsync(decisions, [remoteA, remoteB], routing, cutoff);
        await Assert.ThrowsExactlyAsync<EntityOperationExpiredException>(async () => await decisions.ReadPlanAsync(old));
        Assert.AreEqual(0, (await first.Kernel.PartitionTransactions.ReadAsync()).Count);
        Assert.AreEqual(0, (await second.Kernel.PartitionTransactions.ReadAsync()).Count);
        Guid fresh = EntityOperationIds.New();
        var next = new PartitionTransactionPlan(fresh, routing.MapVersion,
            [new(first.Domain, new(0), NativePayload(profile, routing, new("a"), fresh)),
                new(second.Domain, new(0), NativePayload(profile, routing, new("b"), fresh))]);
        var result = await new PartitionTransactionCoordinator(decisions).ExecuteAsync(next, [remoteA, remoteB]);
        Assert.IsTrue(result.Decision.Commit);
        Assert.AreEqual(2, result.Completion.Members.Count);
    }

    [TestMethod]
    public async Task Node_disposal_drains_a_remote_maintenance_lease_without_waiting_for_its_expiry()
    {
        var placement = new Authority();
        await using var decisions = FilePartitionTransactionAuthority.Open(Path.Combine(_root, "authority"));
        await using var node = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "node"), DatabaseProfileId.PropertyGraph,
            placement.Lease, Routing, placement, Options, transactionAuthority: decisions);
        var lease = await node.AcquireMaintenanceAsync(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(25));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => await node.ReadCutAsync(lease, new("vertex", Guid.NewGuid())));
        await node.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }
}
