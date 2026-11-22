using Microsoft.AspNetCore.Builder;
using EntityForge.Db.Mapping;
using dotnet_etcd;
using EntityForge.Db.Distributed.Hosting;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Distributed.Tests;

public sealed partial class PublicExecutionTests
{
    [TestMethod]
    public async Task Public_transfer_replays_original_operation_and_rejects_missing_post_cutover_storage()
    {
        string? endpoint = Environment.GetEnvironmentVariable("ENTITYFORGE_ETCD_ENDPOINT");
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            Assert.Inconclusive("Set ENTITYFORGE_ETCD_ENDPOINT.");
        }

        string prefix = "/entityforge/production-stack-tests/" + Guid.NewGuid().ToString("N") + "/";
        string root = Path.Combine(Path.GetTempPath(), "ef-public-stack-" + Guid.NewGuid().ToString("N"));
        string source = Path.Combine(root, "source"), destination = Path.Combine(root, "destination"), image = Path.Combine(root, "image");
        using var etcd = new EtcdClient(endpoint!);
        var placements = PartitionServingPlacement.OpenEtcd(etcd, prefix + "placements/");
        var routing = new StableHashDistributedShardRouter(new(new(1), [new("a")])).Binding;
        Guid generation = Guid.NewGuid();
        var clock = new Clock(generation);
        var options = new PartitionDatabaseNodeOptions { TimestampClock = clock };
        var decisions = await PartitionDecisionAuthority.OpenEtcdAsync(etcd, prefix + "decisions/", generation, DatabaseProfileId.PropertyGraph, routing);
        using var certificate = Certificate();
        await using var firstHost = Host(certificate);
        await using var secondHost = Host(certificate);
        await using var application = Host(certificate);
        await using var newApplication = Host(certificate);
        using var http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, c, _, _) => c?.Thumbprint == certificate.Thumbprint });
        PartitionDatabaseNode? node = null;
        PartitionDatabaseTransfer? transfer = null;
        PartitionDatabaseGateway? gateway = null, newGateway = null;
        try
        {
            var lease = await placements.AcquireAsync(new("a"), new("source"), new("https://127.0.0.1:1"), new(generation), routing.MapVersion, TimeSpan.FromMinutes(5));
            node = await PartitionDatabaseNode.CreateAsync(source, DatabaseProfileId.PropertyGraph, lease, routing, placements, decisions, options: options);
            node.MapParticipant(firstHost, new(Token));
            await firstHost.StartAsync();
            await node.CompleteRecoveryAsync();
            gateway = new(decisions, routing, [new(node.Identity, Address(firstHost))], http, Token, placements, timestampClock: clock);
            gateway.MapApplication(application, new(Token));
            await application.StartAsync();
            var client = new HttpPartitionEntityClient(http, Address(application), Token, generation, DatabaseProfileId.PropertyGraph);
            var receipt = await client.SaveGraphAsync(new Item("transfer", "original"));
            await application.StopAsync();
            await gateway.DisposeAsync();
            gateway = null;
            await firstHost.StopAsync();
            var sealedImage = await node.SealAndExportAsync(new(Guid.NewGuid()), image);
            Assert.AreEqual(sealedImage, await node.SealAndExportAsync(new(sealedImage.Transfer), image));
            await node.DisposeAsync();
            node = null;
            Assert.AreEqual(sealedImage, await PartitionDatabaseTransfer.ResumeSourceExportAsync(source, image,
                DatabaseProfileId.PropertyGraph, lease, routing, placements, decisions, new(sealedImage.Transfer), options: options));
            // Bind the real destination address before activating it.
            secondHost.MapGet("/qualification-ready", () => true);
            await secondHost.StartAsync();
            transfer = await PartitionDatabaseTransfer.OpenAsync(image, destination, sealedImage, routing, placements, new("destination"), Address(secondHost), decisions, options: options);
            var active = await transfer.CompleteAsync();
            Assert.AreSame(active, await transfer.CompleteAsync());
            active.MapParticipant(secondHost, new(Token));
            await active.CompleteRecoveryAsync();
            newGateway = new(decisions, routing, [new(active.Identity, Address(secondHost))], http, Token, placements, timestampClock: clock);
            newGateway.MapApplication(newApplication, new(Token));
            await newApplication.StartAsync();
            var restored = new HttpPartitionEntityClient(http, Address(newApplication), Token, generation, DatabaseProfileId.PropertyGraph);
            Assert.AreEqual(EntityOperationState.Committed, (await restored.ResolveAsync(receipt.OperationId)).State);
            Assert.AreEqual("original", (await restored.ReadGraphAsync<Item>("transfer")).Value!.Name);
            await restored.SaveGraphAsync(new Item("transfer", "after cutover"));
            await transfer.RetireSourceAsync(source);
            await newApplication.StopAsync();
            await newGateway.DisposeAsync();
            newGateway = null;
            await secondHost.StopAsync();
            await transfer.DisposeAsync();
            transfer = null;
            // Simulate lost current recovery storage without destroying it.
            Directory.Move(destination, Path.Combine(root, "lost-current-storage"));
            await using var retry = await PartitionDatabaseTransfer.OpenAsync(image, destination, sealedImage, routing, placements,
                new("destination"), Address(secondHost), decisions, options: options);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => retry.CompleteAsync().AsTask());
        }
        finally
        {
            await application.StopAsync();
            await newApplication.StopAsync();
            if (gateway is not null)
            {
                await gateway.DisposeAsync();
            }

            if (newGateway is not null)
            {
                await newGateway.DisposeAsync();
            }

            await firstHost.StopAsync();
            await secondHost.StopAsync();
            if (node is not null)
            {
                await node.DisposeAsync();
            }

            if (transfer is not null)
            {
                await transfer.DisposeAsync();
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
