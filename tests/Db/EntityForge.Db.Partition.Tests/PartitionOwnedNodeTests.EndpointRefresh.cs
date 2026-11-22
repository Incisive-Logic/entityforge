using System.Net;
using EntityForge.Db.Graph;
using EntityForge.Db.Mapping;
using EntityForge.Db.Rdf;
using EntityForge.Db.Semantic;
using EntityForge.Db.Distributed.Sharding;
using EntityForge.Db.Distributed.Hosting;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [TestMethod]
    public async Task Endpoint_refresh_replays_the_exact_request_after_a_lost_successful_application_reply()
    {
        var placement = new Authority();
        await using var decisions = FilePartitionTransactionAuthority.Open(Path.Combine(_root, "authority"));
        await using var node = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "node"), DatabaseProfileId.PropertyGraph,
            placement.Lease, Routing, placement, Options, transactionAuthority: decisions, timestampClock: ReadClock);
        using var certificate = PartitionCertificate();
        await using var service = new PartitionParticipantHttpService(node);
        await using var host = await StartPartitionHostAsync(service, certificate);
        var address = PartitionAddress(host);
        var handler = new LoseOldEndpointReply(address, new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, observed, _, _) => observed?.Thumbprint == certificate.Thumbprint
        });
        using var http = new HttpClient(handler);
        int observations = 0;
        var remote = new HttpPartitionTransactionParticipant(http, new("https://retired.invalid"), PartitionToken, node.Domain, decisions.Identity,
            _ =>
            {
                observations++;
                return ValueTask.FromResult(address);
            });
        var application = new PartitionApplicationClient(decisions, Routing, [remote], ReadClock);
        var capture = new HttpGraphEntityClient(http, node.Domain.Generation).EntityStore<Item>();
        var request = capture.CaptureSave(new("one", "first"), new(EntityOperationIds.New()));
        var receipt = await application.DeliverAsync(request.OperationId, request.Export());
        Assert.AreEqual(DatabaseCommitOutcome.IdempotentReplay, receipt.Members[0].Receipt.Outcome);
        Assert.AreEqual(1, observations);
        Assert.AreEqual(2, handler.ApplicationBodies.Count);
        CollectionAssert.AreEqual(handler.ApplicationBodies[0], handler.ApplicationBodies[1]);
        var later = capture.CaptureSave(new("one", "later"), new(EntityOperationIds.New()));
        await application.DeliverAsync(later.OperationId, later.Export());
        var read = await PartitionRemoteRead.ExecuteAsync([remote], Routing,
            (cut, token) => cut.ReadAsync("a", PartitionApplicationRouting.RootRead(DatabaseProfileId.PropertyGraph, later.Export()), token));
        StringAssert.Contains(System.Text.Encoding.UTF8.GetString(read.Value), "later");
        Assert.AreEqual(1, observations, "Healthy requests must not add a placement lookup just to refresh an endpoint hint.");
    }

    [DataTestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph, false)]
    [DataRow(DatabaseProfileId.PropertyGraph, true)]
    [DataRow(DatabaseProfileId.Rdf, false)]
    [DataRow(DatabaseProfileId.Rdf, true)]
    [DataRow(DatabaseProfileId.Semantic, false)]
    [DataRow(DatabaseProfileId.Semantic, true)]
    public async Task Transferred_owner_refreshes_old_endpoint_and_preserves_exact_replay(DatabaseProfileId profile, bool live)
    {
        var placement = new Authority();
        await using var lifetime = await ApplicationAuthorityLifetime.OpenAsync(Path.Combine(_root, "authority"),
            placement.Lease.Placement.Claim.Generation.Value, profile, Routing, live);
        var decisions = lifetime.Authority;
        await using var source = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "source"), profile,
            placement.Lease, Routing, placement, Options, transactionAuthority: decisions, timestampClock: ReadClock);
        using var certificate = PartitionCertificate();
        await using var sourceService = new PartitionParticipantHttpService(source);
        await using var sourceHost = await StartPartitionHostAsync(sourceService, certificate);
        using var http = new HttpClient(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert?.Thumbprint == certificate.Thumbprint
        });
        byte[] Capture(Guid id, string value) => profile switch
        {
            DatabaseProfileId.PropertyGraph => new HttpGraphEntityClient(http, source.Domain.Generation).EntityStore<Item>().CaptureSave(new("one", value), new(id)).Export(),
            DatabaseProfileId.Rdf => new HttpRdfEntityClient(http, source.Domain.Generation).EntityStore<Item>().CaptureSave(new("one", value), new(id)).Export(),
            _ => new HttpSemanticEntityClient(http, source.Domain.Generation).EntityStore<Item>().CaptureSave(new("one", value), new(id, DateTimeOffset.UnixEpoch)).Export()
        };
        Uri destinationAddress = PartitionAddress(sourceHost);
        int refreshes = 0;
        var remote = new HttpPartitionTransactionParticipant(http, PartitionAddress(sourceHost), PartitionToken, source.Domain, decisions.Identity,
            _ =>
            {
                refreshes++;
                return ValueTask.FromResult(destinationAddress);
            });
        var application = new PartitionApplicationClient(decisions, Routing, [remote], ReadClock);
        var operation = EntityOperationIds.New();
        var bytes = Capture(operation, "before");
        var original = await application.DeliverAsync(operation, bytes);
        var imagePath = Path.Combine(_root, "image");
        var image = await source.SealAndExportAsync(new(Guid.NewGuid()), imagePath);
        using var reservation = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        destinationAddress = new($"https://127.0.0.1:{port}");
        await using var transfer = new PartitionOwnedTransfer(imagePath, Path.Combine(_root, "destination"), image, Routing, placement,
            new("destination"), destinationAddress, Options, decisions, timestampClock: ReadClock);
        placement.LoseNextCutoverReply = true;
        await Assert.ThrowsExactlyAsync<IOException>(() => transfer.CompleteAsync().AsTask());
        var destination = await transfer.CompleteAsync();
        Assert.AreEqual(source.Domain, destination.Domain);
        await using var destinationService = new PartitionParticipantHttpService(destination);
        await using var destinationHost = await StartPartitionHostAsync(destinationService, certificate, port);
        await sourceHost.StopAsync();
        await sourceService.DisposeAsync();
        await source.DisposeAsync();
        var replay = await application.DeliverAsync(operation, bytes);
        Assert.AreEqual(original.Members[0].Receipt.Version, replay.Members[0].Receipt.Version);
        Assert.AreEqual(DatabaseCommitOutcome.IdempotentReplay, replay.Members[0].Receipt.Outcome);
        Assert.AreEqual(1, refreshes);
        var next = EntityOperationIds.New();
        await application.DeliverAsync(next, Capture(next, "later"));
        var repeated = await application.DeliverAsync(operation, bytes);
        Assert.AreEqual(original.Members[0].Receipt.Version, repeated.Members[0].Receipt.Version);
        Assert.AreEqual(EntityOperationState.Committed, (await application.ResolveAsync(next, default)).State);
    }

    private sealed class LoseOldEndpointReply(Uri actual, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        internal List<byte[]> ApplicationBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/application", StringComparison.Ordinal))
            {
                ApplicationBodies.Add(await request.Content!.ReadAsByteArrayAsync(token));
            }

            bool old = request.RequestUri.Host == "retired.invalid";
            if (old)
            {
                request.RequestUri = new Uri(actual, request.RequestUri.PathAndQuery);
            }

            var response = await base.SendAsync(request, token);
            if (old && response.StatusCode == HttpStatusCode.OK)
            {
                response.Dispose();
                throw new IOException("The previous endpoint's successful reply was lost.");
            }
            return response;
        }
    }
}
