using dotnet_etcd;
using Etcdserverpb;
using Google.Protobuf;
using Grpc.Core;
using EntityForge.Db.Distributed.Hosting;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [DataTestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph, 30)]
    [DataRow(DatabaseProfileId.Rdf, 30)]
    [DataRow(DatabaseProfileId.PropertyGraph, 120)]
    [DataRow(DatabaseProfileId.Rdf, 120)]
    [TestCategory("EtcdIntegration")]
    public async Task Live_etcd_https_partition_decisions_recover_lost_reply_and_replay(DatabaseProfileId profile, int requestSeconds)
    {
        string? address = Environment.GetEnvironmentVariable("ENTITYFORGE_TEST_ETCD_ENDPOINT");
        if (string.IsNullOrWhiteSpace(address))
        {
            Assert.Inconclusive("Set ENTITYFORGE_TEST_ETCD_ENDPOINT.");
            return;
        }
        using var client = new EtcdClient(address, configureChannelOptions: options => options.Credentials = ChannelCredentials.Insecure);
        string prefix = "/entityforge/partition-node/" + Guid.NewGuid().ToString("N") + "/";
        var routing = new StableHashDistributedShardRouter(new(new(1), [new("a"), new("b")])).Binding;
        Guid generation = Guid.NewGuid();
        using var placements = new GrpcEtcdShardPlacementAuthority([client], new([new(address)], prefix + "placements/"));
        try
        {
            var decisions = await GrpcEtcdPartitionTransactionAuthority.OpenAsync(client, prefix + "decisions/", generation, profile, routing,
                requestTimeout: TimeSpan.FromSeconds(requestSeconds));
            using var reservationA = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            using var reservationB = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            reservationA.Start();
            reservationB.Start();
            int portA = ((System.Net.IPEndPoint)reservationA.LocalEndpoint).Port;
            int portB = ((System.Net.IPEndPoint)reservationB.LocalEndpoint).Port;
            reservationA.Stop();
            reservationB.Stop();
            var a = await placements.AcquireAsync(new("a"), new("first"), new($"https://127.0.0.1:{portA}"), new(generation), routing.MapVersion, Options.LeaseLifetime);
            var b = await placements.AcquireAsync(new("b"), new("second"), new($"https://127.0.0.1:{portB}"), new(generation), routing.MapVersion, Options.LeaseLifetime);
            await using var first = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "a"), profile, a, routing, placements,
                Options, transactionAuthority: decisions);
            await using var second = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "b"), profile, b, routing, placements,
                Options, transactionAuthority: decisions);
            using var certificate = PartitionCertificate();
            await using var serviceA = new PartitionParticipantHttpService(first);
            await using var serviceB = new PartitionParticipantHttpService(second);
            await using var hostA = await StartPartitionHostAsync(serviceA, certificate, portA);
            await using var hostB = await StartPartitionHostAsync(serviceB, certificate, portB);
            using var http = new HttpClient(new LosePartitionApplyReply(new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, observed, _, _) => observed?.Thumbprint == certificate.Thumbprint
            }));
            var remoteA = new HttpPartitionTransactionParticipant(http, PartitionAddress(hostA), PartitionToken, first.Domain, decisions.Identity);
            var remoteB = new HttpPartitionTransactionParticipant(http, PartitionAddress(hostB), PartitionToken, second.Domain, decisions.Identity);
            Guid operation = Guid.NewGuid();
            var plan = new PartitionTransactionPlan(operation, routing.MapVersion,
                [new(first.Domain, new(0), NativePayload(profile, routing, new("a"), operation)),
                    new(second.Domain, new(0), NativePayload(profile, routing, new("b"), operation))]);
            var coordinator = new PartitionTransactionCoordinator(decisions);
            var lost = await Assert.ThrowsExactlyAsync<PartitionDecisionDeliveryException>(async () =>
                await coordinator.ExecuteAsync(plan, [remoteA, remoteB]));
            Assert.IsTrue(lost.Decision.Commit);
            Assert.AreEqual(1L, first.Kernel.CurrentVersion.Value);
            Assert.AreEqual(1L, second.Kernel.CurrentVersion.Value, "Parallel delivery drains the other owner even when one reply is lost.");
            var committed = await coordinator.ExecuteAsync(plan, [remoteA, remoteB]);
            Assert.IsTrue(committed.Decision.Commit);
            Assert.AreEqual(2, committed.Members.Count);
            Assert.IsTrue(committed.Members.All(r => r.Receipt.Version.Value == 1));
            var replay = await coordinator.ExecuteAsync(plan, [remoteA, remoteB]);
            Assert.AreEqual(committed.Decision, replay.Decision);
            Assert.IsTrue(replay.Members.All(r => r.Receipt.Version.Value == 1));
            var cut = await PartitionGlobalRead.ExecuteAsync([first, second], (_, _) => ValueTask.FromResult(true));
            Assert.IsTrue(cut.Value);
            Assert.IsTrue(cut.Positions.All(p => p.Version.Value == 1));
            await PartitionRetention.RetainAsync(decisions, [first, second], DateTimeOffset.UtcNow.AddMinutes(-1));
            Assert.AreEqual(0, (await first.Kernel.CreateCheckpointAsync()).PartitionTransactions.Count);
            await Assert.ThrowsExactlyAsync<EntityOperationExpiredException>(() => decisions.ReadDecisionAsync(operation).AsTask());
        }
        finally
        {
            await client.DeleteAsync(new DeleteRangeRequest
            {
                Key = ByteString.CopyFromUtf8(prefix),
                RangeEnd = ByteString.CopyFromUtf8(prefix[..^1] + "0")
            });
        }
    }
}
