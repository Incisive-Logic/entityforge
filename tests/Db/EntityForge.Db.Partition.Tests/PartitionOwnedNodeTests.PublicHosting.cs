using System.Net;
using EntityForge.Db.Graph;
using EntityForge.Db.Distributed.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [TestMethod]
    public async Task Public_hosting_composes_save_vector_read_and_reopen_without_exposing_native_owners_to_callers()
    {
        var placement = new Authority();
        await using var files = FilePartitionTransactionAuthority.Open(Path.Combine(_root, "authority"));
        var decisions = new PartitionDecisionAuthority(files);
        string directory = Path.Combine(_root, "node");
        await using var node = await PartitionDatabaseNode.CreateAsync(directory, DatabaseProfileId.PropertyGraph,
            placement.Lease, Routing, placement, decisions, Options, options: new()
            {
                TimestampClock = ReadClock
            });
        using var certificate = PartitionCertificate();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
        await using var participantHost = builder.Build();
        node.MapParticipant(participantHost, new(PartitionToken));
        await participantHost.StartAsync();
        using var http = new HttpClient(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, observed, _, _) => observed?.Thumbprint == certificate.Thumbprint
        });
        var wrongPhysicalOwner = new DatabasePartitionIdentity(node.Identity.Generation, node.Identity.Profile,
            node.Identity.Routing, node.Identity.Partition, Guid.NewGuid());
        Assert.ThrowsExactly<ArgumentException>(() => new PartitionDatabaseGateway(decisions, Routing,
            [new(wrongPhysicalOwner, PartitionAddress(participantHost))], http, PartitionToken, placement, timestampClock: ReadClock, localNode: node));
        await using var gateway = new PartitionDatabaseGateway(decisions, Routing,
            [new(node.Identity, PartitionAddress(participantHost))], http, PartitionToken, placement, timestampClock: ReadClock, localNode: node);
        var applicationBuilder = WebApplication.CreateBuilder();
        applicationBuilder.Logging.ClearProviders();
        applicationBuilder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
        await using var applicationHost = applicationBuilder.Build();
        gateway.MapApplication(applicationHost, new(PartitionToken + "-application"));
        await applicationHost.StartAsync();
        var client = new HttpPartitionEntityClient(http, PartitionAddress(applicationHost), PartitionToken + "-application",
            node.Identity.Generation, node.Identity.Profile);
        var request = new HttpGraphEntityClient(http, node.Identity.Generation).EntityStore<Item>()
            .CaptureSave(new("one", "hosted"), new(EntityOperationIds.New()));
        var receipt = await client.DeliverAsync(request.OperationId, [request.Export()]);
        var query = PartitionApplicationRouting.RootRead(DatabaseProfileId.PropertyGraph, request.Export());
        var read = await client.ReadGraphVerticesAsync([new(query.Identity)], receipt.Position);
        Assert.IsTrue(read.Value.Single()!.Properties.Values.Contains(new DatabaseValue.String("hosted")));
        var disabled = await Assert.ThrowsExactlyAsync<HttpRequestException>(() => client.RetainAsync(DateTimeOffset.UtcNow.AddMinutes(-1)).AsTask());
        Assert.AreEqual(HttpStatusCode.NotFound, disabled.StatusCode, "Maintenance must be explicitly mapped.");
        await applicationHost.StopAsync();
        await gateway.DisposeAsync();
        await participantHost.StopAsync();
        await node.DisposeAsync();
        Assert.ThrowsExactly<ObjectDisposedException>(() => node.MapParticipant(participantHost, new(PartitionToken)));
        Assert.ThrowsExactly<ObjectDisposedException>(() => new PartitionDatabaseGateway(decisions, Routing,
            [new(node.Identity, PartitionAddress(participantHost))], http, PartitionToken, placement, timestampClock: ReadClock, localNode: node));
        await using var reopened = await PartitionDatabaseNode.OpenAsync(directory, DatabaseProfileId.PropertyGraph,
            placement.Lease, Routing, placement, decisions, Options, options: new()
            {
                TimestampClock = ReadClock
            });
        Assert.AreEqual(node.Identity, reopened.Identity);
    }
}
