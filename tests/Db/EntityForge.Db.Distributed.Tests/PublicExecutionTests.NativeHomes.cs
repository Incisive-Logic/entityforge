using EntityForge.Db.Mapping;
using dotnet_etcd;
using EntityForge.Db.Distributed.Hosting;
using EntityForge.Db.Distributed.Sharding;
using EntityForge.Db.Graph;
using EntityForge.Db.Rdf;

namespace EntityForge.Db.Distributed.Tests;

public sealed partial class PublicExecutionTests
{
    [TestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph)]
    [DataRow(DatabaseProfileId.Rdf)]
    public async Task Public_native_homes_execute_two_owner_batch_and_exact_replay(DatabaseProfileId profile)
    {
        string? endpoint = Environment.GetEnvironmentVariable("ENTITYFORGE_ETCD_ENDPOINT");
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            Assert.Inconclusive("Set ENTITYFORGE_ETCD_ENDPOINT.");
        }

        string prefix = "/entityforge/production-stack-tests/" + Guid.NewGuid().ToString("N") + "/";
        string root = Path.Combine(Path.GetTempPath(), "ef-public-stack-" + Guid.NewGuid().ToString("N"));
        using var etcd = new EtcdClient(endpoint!);
        var placements = PartitionServingPlacement.OpenEtcd(etcd, prefix + "placements/");
        var routing = new StableHashDistributedShardRouter(new(new(1), [new("a"), new("b")])).Binding;
        Guid generation = Guid.NewGuid();
        var clock = new Clock(generation);
        var options = new PartitionDatabaseNodeOptions { TimestampClock = clock };
        var control = await PartitionDecisionAuthority.OpenEtcdAsync(etcd, prefix + "decisions/", generation, profile, routing);
        using var certificate = Certificate();
        await using var firstHost = Host(certificate);
        await using var secondHost = Host(certificate);
        await using var application = Host(certificate);
        using var http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, c, _, _) => c?.Thumbprint == certificate.Thumbprint });
        var directory = new Dictionary<LogicalShardId, PartitionRemoteOwner>();
        var decisions = control.WithNativeHomes(routing, shard => directory[shard], http, Token, placements);
        PartitionDatabaseNode? first = null, second = null;
        PartitionDatabaseGateway? gateway = null;
        try
        {
            var a = await placements.AcquireAsync(new("a"), new("first"), new("https://127.0.0.1:1"), new(generation), routing.MapVersion, TimeSpan.FromMinutes(5));
            var b = await placements.AcquireAsync(new("b"), new("second"), new("https://127.0.0.1:2"), new(generation), routing.MapVersion, TimeSpan.FromMinutes(5));
            first = await PartitionDatabaseNode.CreateAsync(Path.Combine(root, "a"), profile, a, routing, placements, decisions, options: options);
            second = await PartitionDatabaseNode.CreateAsync(Path.Combine(root, "b"), profile, b, routing, placements, decisions, options: options);
            first.MapParticipant(firstHost, new(Token));
            second.MapParticipant(secondHost, new(Token));
            await firstHost.StartAsync();
            await secondHost.StartAsync();
            directory.Add(new("a"), new(first.Identity, Address(firstHost)));
            directory.Add(new("b"), new(second.Identity, Address(secondHost)));
            await first.CompleteRecoveryAsync(enableDecisionNotifications: true);
            await second.CompleteRecoveryAsync(enableDecisionNotifications: true);
            gateway = new(decisions, routing, directory.Values.ToArray(), http, Token, placements, timestampClock: clock);
            gateway.MapApplication(application, new(Token));
            await application.StartAsync();
            var client = new HttpPartitionEntityClient(http, Address(application), Token, generation, profile);
            Assert.AreEqual("native-home", (await client.GetExecutionCapabilitiesAsync()).DecisionExecution);
            // Discover a root on each owner through the public receipt, without internal routing helpers.
            var roots = new Dictionary<string, Item>();
            for (int index = 0; roots.Count < 2 && index < 64; index++)
            {
                var item = new Item(index.ToString(), "seed");
                var seed = profile == DatabaseProfileId.PropertyGraph ? await client.SaveGraphAsync(item) : await client.SaveRdfAsync(item);
                roots.TryAdd(seed.Members.Single().Identity.Partition, item);
            }
            Assert.AreEqual(2, roots.Count);
            Guid operation = EntityOperationIds.New();
            ReadOnlyMemory<byte>[] requests = roots.Values.Select(item => (ReadOnlyMemory<byte>)(profile == DatabaseProfileId.PropertyGraph
                ? new HttpGraphEntityClient(http, generation).EntityStore<Item>().CaptureSave(item with
                {
                    Name = "batch"
                }, new(operation)).Export()
                : new HttpRdfEntityClient(http, generation).EntityStore<Item>().CaptureSave(item with
                {
                    Name = "batch"
                }, new(operation)).Export())).ToArray();
            var result = await client.DeliverAsync(operation, requests);
            Assert.AreEqual(2, result.Members.Count);
            var replay = await client.DeliverAsync(operation, requests);
            CollectionAssert.AreEquivalent(result.Members.Select(m => m.Receipt.Version).ToArray(), replay.Members.Select(m => m.Receipt.Version).ToArray());
            Assert.AreEqual(EntityOperationState.Committed, (await client.ResolveAsync(operation)).State);
            foreach (var item in roots.Values)
            {
                var read = profile == DatabaseProfileId.PropertyGraph
                    ? await client.ReadGraphAsync<Item>(item.Id, minimumPosition: result.Position)
                    : await client.ReadRdfAsync<Item>(item.Id, minimumPosition: result.Position);
                Assert.AreEqual("batch", read.Value!.Name);
            }
        }
        finally
        {
            await application.StopAsync();
            if (gateway is not null)
            {
                await gateway.DisposeAsync();
            }

            await firstHost.StopAsync();
            await secondHost.StopAsync();
            if (first is not null)
            {
                await first.DisposeAsync();
            }

            if (second is not null)
            {
                await second.DisposeAsync();
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }

            var cleanup = new Etcdserverpb.TxnRequest();
            cleanup.Success.Add(new Etcdserverpb.RequestOp
            {
                RequestDeleteRange = new Etcdserverpb.DeleteRangeRequest
                {
                    Key = Google.Protobuf.ByteString.CopyFromUtf8(prefix),
                    RangeEnd = Google.Protobuf.ByteString.CopyFromUtf8(prefix[..^1] + "0")
                }
            });
            await etcd.TransactionAsync(cleanup);
        }
    }
}
