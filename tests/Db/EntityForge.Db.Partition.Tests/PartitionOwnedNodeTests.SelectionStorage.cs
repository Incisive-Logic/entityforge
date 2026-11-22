using dotnet_etcd;
using Etcdserverpb;
using Google.Protobuf;
using Grpc.Core;
using System.Text.Json;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [TestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph)]
    [DataRow(DatabaseProfileId.Rdf)]
    [DataRow(DatabaseProfileId.Semantic)]
    [TestCategory("EtcdIntegration")]
    public async Task Selection_storage_live_etcd_reopens_replays_and_reclaims_uncompressed_binary_byte_charges(DatabaseProfileId profile)
    {
        var address = Environment.GetEnvironmentVariable("ENTITYFORGE_TEST_ETCD_ENDPOINT");
        if (address is null)
        {
            Assert.Inconclusive("Dedicated etcd required.");
            return;
        }
        using var client = new EtcdClient(address, configureChannelOptions: o => o.Credentials = ChannelCredentials.Insecure);
        string prefix = "/entityforge/storage-proof/" + Guid.NewGuid().ToString("N") + "/";
        var routing = new StableHashDistributedShardRouter(new(new(1), [new("a"), new("b")])).Binding;
        var generation = Guid.NewGuid();
        try
        {
            var authority = await GrpcEtcdPartitionTransactionAuthority.OpenAsync(client, prefix, generation, profile, routing);
            var a = new PartitionExecutionDomain(generation, new("a"), routing.Fingerprint, profile, Guid.NewGuid());
            var b = new PartitionExecutionDomain(generation, new("b"), routing.Fingerprint, profile, Guid.NewGuid());
            await authority.EnrollAsync(a, true);
            await authority.EnrollAsync(b, true);
            var operation = EntityOperationIds.New();
            var plan = new PartitionTransactionPlan(operation, routing.MapVersion,
                [new(a, new(0), new byte[16384]), new(b, new(0), new byte[16384])]);
            var application = new PartitionApplicationBinding(operation, [a.Identity, b.Identity], [new byte[8192]]);
            if (profile == DatabaseProfileId.Semantic)
            {
                // Exercise the same shared binary records through the independent
                // binding path; combined Semantic selection requires real closure.
                await authority.BindApplicationAsync(application);
                await authority.BindAsync(plan);
            }
            else
            {
                await authority.SelectCombinedAsync(application, plan);
            }

            var stored = await client.GetAsync(prefix + "plans/" + operation.ToString("N"));
            Assert.IsTrue(stored.Kvs[0].Value.Length < 4096);
            var reopened = await GrpcEtcdPartitionTransactionAuthority.OpenAsync(client, prefix, generation, profile, routing);
            Assert.AreEqual(plan.Descriptor, (await reopened.ReadPlanAsync(operation))!.Descriptor);
            Assert.AreEqual(application.Descriptor, (await reopened.ReadApplicationAsync(operation))!.Descriptor);
            if (profile == DatabaseProfileId.Semantic)
            {
                await reopened.BindApplicationAsync(application);
                await reopened.BindAsync(plan);
            }
            else
            {
                Assert.AreEqual(plan.Descriptor, (await reopened.SelectCombinedAsync(application, plan))!.Descriptor);
            }

            await reopened.DecideAsync(operation, plan.Descriptor, false);
            var observation = (await reopened.ReadTerminalAsync([operation])).Single();
            Assert.IsNull(observation.Failure);
            Assert.AreEqual(plan.Descriptor, observation.Plan!.Descriptor);
            var usage = JsonSerializer.Deserialize<PartitionApplicationUsage>((await client.GetAsync(prefix + "application-usage")).Kvs[0].Value.Span)!;
            Assert.AreEqual((long)PartitionSelectionCodec.EncodeApplication(application).Length, usage.Bytes);
            Assert.AreEqual((long)PartitionSelectionCodec.EncodePlan(plan).Length, usage.PlanBytes);
            await reopened.RetainAsync(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            usage = JsonSerializer.Deserialize<PartitionApplicationUsage>((await client.GetAsync(prefix + "application-usage")).Kvs[0].Value.Span)!;
            Assert.AreEqual(new PartitionApplicationUsage(0, 0), usage);
            Assert.AreEqual(0, (await client.GetAsync(prefix + "plans/" + operation.ToString("N"))).Kvs.Count);
            await Assert.ThrowsExactlyAsync<EntityOperationExpiredException>(() => reopened.ReadPlanAsync(operation).AsTask());
            var domainKey = prefix + "domain";
            var oldDomain = System.Text.Json.Nodes.JsonNode.Parse((await client.GetAsync(domainKey)).Kvs[0].Value.ToStringUtf8())!;
            oldDomain["Format"] = oldDomain["Format"]!.GetValue<int>() - 1;
            await client.PutAsync(domainKey, oldDomain.ToJsonString());
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => GrpcEtcdPartitionTransactionAuthority.OpenAsync(
                client, prefix, generation, profile, routing).AsTask());
        }
        finally
        {
            await client.DeleteAsync(new DeleteRangeRequest { Key = ByteString.CopyFromUtf8(prefix), RangeEnd = ByteString.CopyFromUtf8(prefix[..^1] + "0") });
        }
    }
}
