using System.Reflection;
using dotnet_etcd;
using Grpc.Core;
using EntityForge.Db.Kernel;
using EntityForge.Db.Mapping;
using EntityForge.Db.Distributed.Hosting;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [TestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph, false, false)]
    [DataRow(DatabaseProfileId.PropertyGraph, false, true)]
    [DataRow(DatabaseProfileId.PropertyGraph, true, false)]
    [DataRow(DatabaseProfileId.PropertyGraph, true, true)]
    [DataRow(DatabaseProfileId.Rdf, false, false)]
    [DataRow(DatabaseProfileId.Rdf, false, true)]
    [DataRow(DatabaseProfileId.Rdf, true, false)]
    [DataRow(DatabaseProfileId.Rdf, true, true)]
    public async Task Epoch_home_status_avoids_external_reads_but_joins_closure(DatabaseProfileId profile, bool etcd, bool transfer)
    {
        using var files = new FileServingEpochStore(Path.Combine(_root, "epochs"));
        using var client = etcd ? new EtcdClient(RequireLiveEtcdEndpoint(), configureChannelOptions: o => o.Credentials = ChannelCredentials.Insecure) : null;
        string prefix = "/entityforge/epoch-status-tests/" + Guid.NewGuid().ToString("N") + "/";
        var counted = new CountEpochReads(client is null ? files : new EtcdServingEpochStore(client, prefix));
        var placements = new DurableServingEpochAuthority(counted);
        try
        {
            var lease = await placements.AcquireAsync(new("a"), new("source"), new("https://source.test"),
                DistributedDatabaseGeneration.New(), Routing.MapVersion, Options.LeaseLifetime);
            await using var control = FilePartitionTransactionAuthority.Open(Path.Combine(_root, "control"));
            var homes = new Dictionary<LogicalShardId, IPartitionCoordinatorHomeParticipant>();
            var authority = new PartitionNativeHomeAuthority(control, Routing, shard => homes[shard]);
            await using var node = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "native"), profile,
                lease, Routing, placements, Options, transactionAuthority: authority);
            homes[node.Domain.Partition] = node;
            var home = (IPartitionCoordinatorHomeParticipant)node;
            Guid operation = EntityOperationIds.New();
            int reads = counted.Reads;
            Assert.IsNull(await home.ReadHomeAsync(operation, CancellationToken.None));
            Assert.AreEqual(reads, counted.Reads, "Epoch status must not consult the external store.");
            // Reconciliation needs status while the ordinary write fence is sealed.
            await using (var maintenance = await node.DrainForMaintenanceAsync(CancellationToken.None))
            {
                reads = counted.Reads;
                Assert.IsNull(await home.ReadHomeAsync(operation, CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.AreEqual(reads, counted.Reads);
            }

            var admission = PartitionAdmission(node.Kernel);
            await admission.WaitAsync();
            Task? close = null;
            Task<EntityForge.Db.Durability.DurablePartitionTransaction?> pending;
            try
            {
                pending = home.ReadHomeAsync(operation, CancellationToken.None).AsTask();
                Assert.IsFalse(pending.IsCompleted);
                close = transfer
                    ? node.SealAndExportAsync(new(Guid.NewGuid()), Path.Combine(_root, "image")).AsTask()
                    : node.DisposeAsync().AsTask();
                var closing = typeof(PartitionOwnedNode).GetField("_closing", BindingFlags.Instance | BindingFlags.NonPublic)!;
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (!(bool)closing.GetValue(node)!)
                {
                    await Task.Delay(1, timeout.Token);
                }
            }
            finally
            {
                admission.Release();
            }
            // The previously admitted read must not return definitive absence from a closing owner.
            var failure = await Assert.ThrowsAsync<Exception>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            await close!.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsTrue(failure is InvalidOperationException or StaleShardPlacementException,
                "Closure must report unavailable/stale status, not definitive absence.");
            await Assert.ThrowsAsync<InvalidOperationException>(() => home.ReadHomeAsync(operation, CancellationToken.None).AsTask());
        }
        finally
        {
            if (client is not null)
            {
                await client.DeleteAsync(new Etcdserverpb.DeleteRangeRequest
                {
                    Key = Google.Protobuf.ByteString.CopyFromUtf8(prefix),
                    RangeEnd = Google.Protobuf.ByteString.CopyFromUtf8(prefix[..^1] + "0")
                });
            }
        }
    }

    private sealed class CountEpochReads(IServingEpochStore inner) : IServingEpochStore
    {
        internal int Reads;

        public ValueTask<string> IdentityAsync(CancellationToken token) => inner.IdentityAsync(token);

        public ValueTask<bool> ReplaceAsync(LogicalShardId shard, long revision, ServingEpochRecord value, CancellationToken token) =>
            inner.ReplaceAsync(shard, revision, value, token);

        public ValueTask<ServingEpochObservation> ReadAsync(LogicalShardId shard, CancellationToken token)
        {
            Interlocked.Increment(ref Reads);
            return inner.ReadAsync(shard, token);
        }
    }
}
