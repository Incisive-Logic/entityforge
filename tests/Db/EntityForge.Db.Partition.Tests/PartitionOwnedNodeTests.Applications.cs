using EntityForge.Db.Graph;
using EntityForge.Db.Mapping;
using EntityForge.Db.Rdf;
using EntityForge.Db.Distributed.Hosting;
using EntityForge.Db.Distributed.Sharding;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using System.Net;
using dotnet_etcd;
using Etcdserverpb;
using Google.Protobuf;
using Grpc.Core;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [TestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph)]
    [DataRow(DatabaseProfileId.Rdf)]
    [DataRow(DatabaseProfileId.Semantic)]
    public async Task Public_owner_capture_roundtrips_and_delivers_each_supported_profile(DatabaseProfileId profile)
    {
        var placements = new Authority();
        await using var decisions = FilePartitionTransactionAuthority.Open(Path.Combine(_root, "authority"));
        await using var node = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "node"), profile,
            placements.Lease, Routing, placements, Options, transactionAuthority: decisions, timestampClock: ReadClock);
        var client = new PartitionApplicationClient(decisions, Routing, [node], ReadClock);
        await using var application = new PartitionApplicationHttpService(client);
        using var certificate = PartitionCertificate();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
        await using var gateway = builder.Build();
        application.Map(gateway, new(PartitionToken));
        await gateway.StartAsync();
        using var http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, observed, _, _) => observed?.Thumbprint == certificate.Thumbprint });
        var remote = new HttpPartitionEntityClient(http, PartitionAddress(gateway), PartitionToken, client.Generation, profile);
        var capability = await remote.GetOwnerCapabilitiesAsync();
        var item = new Item("one", "original");
        var capture = profile switch
        {
            DatabaseProfileId.PropertyGraph => remote.CaptureOwnerGraphSave(item, capability),
            DatabaseProfileId.Rdf => remote.CaptureOwnerRdfSave(item, capability),
            _ => remote.CaptureOwnerSemanticSave(item, capability)
        };
        var restored = PartitionOwnerCapture.Restore(capture.Export());
        var result = await restored.DeliverAsync(remote);
        Assert.AreEqual(result.Members[0].Receipt.Version, (await capture.DeliverAsync(remote)).Members[0].Receipt.Version);
        Assert.AreEqual(EntityOperationState.Committed, (await remote.ResolveAsync(capture.OperationId)).State);
        Assert.IsNull(await decisions.ReadApplicationAsync(capture.OperationId));
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public async Task Reopening_pre_arbitration_enrollment_is_rejected_before_lease_renewal(int oldFormat)
    {
        var placements = new Authority();
        string path = Path.Combine(_root, "old-node");
        PartitionNodeMetadata metadata;
        await using (var node = await PartitionOwnedNode.CreateAsync(path, DatabaseProfileId.PropertyGraph,
            placements.Lease, Routing, placements, Options, timestampClock: ReadClock))
        {
            metadata = node.Enrollment;
        }

        string enrollment = Path.Combine(path, PartitionOwnedNode.EnrollmentFile);
        File.Delete(enrollment);
        await PartitionMetadataFile.WriteNewAsync(enrollment, metadata with
        {
            Format = oldFormat
        }, CancellationToken.None);
        int renewals = placements.Renewals;
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await PartitionOwnedNode.OpenAsync(path, DatabaseProfileId.PropertyGraph, placements.Lease, Routing, placements, Options, timestampClock: ReadClock));
        Assert.AreEqual(renewals, placements.Renewals);
    }

    [DataTestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph, false)]
    [DataRow(DatabaseProfileId.Rdf, false)]
    [DataRow(DatabaseProfileId.PropertyGraph, true)]
    [DataRow(DatabaseProfileId.Rdf, true)]
    public async Task Logical_application_routes_local_and_cross_owner_batches_and_replays_over_https(DatabaseProfileId profile, bool liveEtcd)
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
        await using var authorityLifetime = await ApplicationAuthorityLifetime.OpenAsync(Path.Combine(_root, "authority"),
            a.Lease.Placement.Claim.Generation.Value, profile, routing, liveEtcd);
        var decisions = authorityLifetime.Authority;
        await using var first = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "a"), profile, a.Lease, routing, a, Options, transactionAuthority: decisions, timestampClock: ReadClock);
        await using var second = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "b"), profile, b.Lease, routing, b, Options, transactionAuthority: decisions, timestampClock: ReadClock);
        using var certificate = PartitionCertificate();
        await using var serviceA = new PartitionParticipantHttpService(first);
        await using var serviceB = new PartitionParticipantHttpService(second);
        await using var hostA = await StartPartitionHostAsync(serviceA, certificate);
        await using var hostB = await StartPartitionHostAsync(serviceB, certificate);
        using var http = new HttpClient(new LoseFirstApplicationReply(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, observed, _, _) => observed?.Thumbprint == certificate.Thumbprint
        }));
        var client = new PartitionApplicationClient(decisions, routing,
            [new HttpPartitionTransactionParticipant(http, PartitionAddress(hostA), PartitionToken, first.Domain, decisions.Identity),
                new HttpPartitionTransactionParticipant(http, PartitionAddress(hostB), PartitionToken, second.Domain, decisions.Identity)], ReadClock);
        await using var application = new PartitionApplicationHttpService(client);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
        await using var gateway = builder.Build();
        application.Map(gateway, new(PartitionToken), new(PartitionToken + "-admin"));
        await gateway.StartAsync();
        var remoteApplication = new HttpPartitionEntityClient(http, PartitionAddress(gateway), PartitionToken, client.Generation, profile);
        byte[] Capture(string id, string value, Guid operation) => profile == DatabaseProfileId.PropertyGraph
            ? new HttpGraphEntityClient(http, client.Generation).EntityStore<Item>().CaptureSave(new(id, value), new GraphEntityWriteOptions(operation)).Export()
            : new HttpRdfEntityClient(http, client.Generation).EntityStore<Item>().CaptureSave(new(id, value), new RdfEntityWriteOptions(operation)).Export();
        string Identity(string owner, int skip = 0)
        {
            for (int i = skip; ; i++)
            {
                var operation = Guid.NewGuid();
                if (PartitionApplicationRouting.Owner(client.Generation, profile, routing, operation, Capture(i.ToString(), "seed", operation)).Value == owner)
                {
                    return i.ToString();
                }
            }
        }
        string left = Identity("a"), right = Identity("b"), otherLeft = Identity("a", int.Parse(left) + 1);
        var unknown = Guid.NewGuid();
        Assert.AreEqual(EntityForge.Db.Mapping.EntityOperationState.NotFound, (await remoteApplication.ResolveAsync(unknown)).State);
        Assert.AreEqual(EntityForge.Db.Mapping.EntityOperationState.NotFound, (await remoteApplication.AbortAsync(unknown)).State);
        foreach (bool coordinated in new[] { false, true })
        {
            var pending = Guid.NewGuid();
            ReadOnlyMemory<byte>[] pendingRequests = coordinated
                ? [Capture(left, "prepared", pending), Capture(right, "prepared", pending)]
                : [Capture(left, "prepared", pending)];
            Assert.AreEqual(EntityForge.Db.Mapping.EntityOperationState.PlanBound, (await remoteApplication.PrepareAsync(pending, pendingRequests)).State);
            Assert.AreEqual(EntityForge.Db.Mapping.EntityOperationState.PlanBound, (await remoteApplication.ResolveAsync(pending)).State);
            Assert.AreEqual(EntityForge.Db.Mapping.EntityOperationState.Aborted, (await remoteApplication.AbortAsync(pending)).State);
            Assert.AreEqual(EntityForge.Db.Mapping.EntityOperationState.Aborted, (await remoteApplication.AbortAsync(pending)).State);
            await Assert.ThrowsExactlyAsync<EntityOperationAbortedException>(() => remoteApplication.DeliverAsync(pending, pendingRequests).AsTask());
        }
        var localOperation = Guid.NewGuid();
        var localRequest = Capture(left, "local", localOperation);
        await Assert.ThrowsExactlyAsync<PartitionApplicationUncertainException>(async () => await remoteApplication.DeliverAsync(localOperation, [localRequest]));
        var local = await remoteApplication.DeliverAsync(localOperation, [localRequest]);
        Assert.AreEqual(DatabaseCommitOutcome.IdempotentReplay, local.Members[0].Receipt.Outcome);
        Assert.AreEqual(1, local.Members.Count);
        Assert.AreEqual(EntityForge.Db.Mapping.EntityOperationState.Committed, (await remoteApplication.ResolveAsync(localOperation)).State);
        Assert.AreEqual(EntityForge.Db.Mapping.EntityOperationState.Committed, (await remoteApplication.AbortAsync(localOperation)).State);
        Assert.AreEqual(first.Domain.Identity, local.Members[0].Identity);
        Assert.IsNull(await decisions.ReadPlanAsync(localOperation));
        var replay = await remoteApplication.DeliverAsync(localOperation, [localRequest]);
        Assert.AreEqual(DatabaseCommitOutcome.IdempotentReplay, replay.Members[0].Receipt.Outcome);
        await Assert.ThrowsExactlyAsync<EntityForgeDbIdempotencyException>(async () =>
            await client.DeliverAsync(localOperation, Capture(right, "changed-owner", localOperation)));

        var sameOwner = Guid.NewGuid();
        ReadOnlyMemory<byte>[] sameRequests = [Capture(left, "same-batch", sameOwner), Capture(otherLeft, "same-batch", sameOwner)];
        var same = await remoteApplication.DeliverAsync(sameOwner, sameRequests);
        Assert.AreEqual(1, same.Members.Count);
        Assert.AreEqual(DatabaseCommitOutcome.IdempotentReplay, (await client.DeliverAsync(sameOwner, sameRequests)).Members[0].Receipt.Outcome);

        var crossOperation = Guid.NewGuid();
        ReadOnlyMemory<byte>[] crossRequests = [Capture(left, "cross", crossOperation), Capture(right, "cross", crossOperation)];
        var cross = await remoteApplication.DeliverAsync(crossOperation, crossRequests);
        Assert.AreEqual(2, cross.Members.Count);
        var crossResolution = await remoteApplication.ResolveAsync(crossOperation);
        Assert.AreEqual(EntityForge.Db.Mapping.EntityOperationState.Committed, crossResolution.State);
        Assert.IsTrue(crossResolution.Receipt!.Position.Covers(cross.Position));
        Assert.AreEqual(EntityForge.Db.Mapping.EntityOperationState.Committed, (await remoteApplication.AbortAsync(crossOperation)).State);
        Assert.IsTrue((await decisions.ReadDecisionAsync(crossOperation))!.Commit);
        var laterOperation = Guid.NewGuid();
        await client.DeliverAsync(laterOperation, Capture(left, "later", laterOperation));
        var crossReplay = await remoteApplication.DeliverAsync(crossOperation, crossRequests);
        Assert.IsTrue(crossReplay.Members.All(member => member.Receipt.Outcome == DatabaseCommitOutcome.IdempotentReplay));
        Assert.IsTrue(crossReplay.Position.Covers(cross.Position));
        var leftQuery = PartitionApplicationRouting.RootRead(profile, crossRequests[0]);
        var rightQuery = PartitionApplicationRouting.RootRead(profile, crossRequests[1]);
        if (profile == DatabaseProfileId.PropertyGraph)
        {
            Assert.AreEqual("later", (await remoteApplication.ReadGraphAsync<Item>(left, minimumPosition: cross.Position)).Value!.Name);
            Assert.AreEqual("cross", (await remoteApplication.ReadGraphAsync<Item>(right)).Value!.Name);
            var read = await remoteApplication.ReadGraphVerticesAsync([new(leftQuery.Identity), new(rightQuery.Identity)], cross.Position);
            Assert.AreEqual(2, read.Position.Positions.Count);
            Assert.IsTrue(read.Value[0]!.Properties.Values.Contains(new DatabaseValue.String("later")));
            Assert.IsTrue(read.Value[1]!.Properties.Values.Contains(new DatabaseValue.String("cross")));
        }
        else
        {
            Assert.AreEqual("later", (await remoteApplication.ReadRdfAsync<Item>(left, minimumPosition: cross.Position)).Value!.Name);
            Assert.AreEqual("cross", (await remoteApplication.ReadRdfAsync<Item>(right)).Value!.Name);
            var read = await remoteApplication.ReadRdfSubjectsAsync([new(leftQuery.Subject!), new(rightQuery.Subject!)], cross.Position);
            Assert.AreEqual(2, read.Position.Positions.Count);
            Assert.IsTrue(read.Value[0].Any(quad => quad.Object is EntityForge.Db.Rdf.Contracts.RdfLiteral { LexicalForm: "later" }));
            Assert.IsTrue(read.Value[1].Any(quad => quad.Object is EntityForge.Db.Rdf.Contracts.RdfLiteral { LexicalForm: "cross" }));
        }
        // The owner-addressed namespace uses the same HTTPS/native machinery,
        // while bypassing central binding for both Graph and RDF.
        var ownerOperation = PartitionOwnerOperation.New(routing, new("a"), DateTimeOffset.UtcNow.AddMinutes(-2));
        var ownerRequest = Capture(left, "later", ownerOperation);
        var ownerUncertain = await Assert.ThrowsExactlyAsync<PartitionApplicationUncertainException>(() =>
            remoteApplication.DeliverAsync(ownerOperation, [ownerRequest]).AsTask());
        Assert.AreEqual(ownerOperation, ownerUncertain.Retry.OperationId);
        var ownerResult = await PartitionApplicationRetry.Restore(ownerUncertain.Retry.Export()).DeliverAsync(remoteApplication);
        Assert.IsNull(await decisions.ReadApplicationAsync(ownerOperation));
        var ownerReplay = await remoteApplication.DeliverAsync(ownerOperation, [ownerRequest]);
        Assert.AreEqual(ownerResult.Members[0].Receipt.Version, ownerReplay.Members[0].Receipt.Version);
        Assert.AreEqual(DatabaseCommitOutcome.IdempotentReplay, ownerReplay.Members[0].Receipt.Outcome);
        Assert.AreEqual(EntityForge.Db.Mapping.EntityOperationState.Committed, (await remoteApplication.ResolveAsync(ownerOperation)).State);
        var capability = await remoteApplication.GetOwnerCapabilitiesAsync();
        Assert.AreEqual(routing.Fingerprint, capability.Routing.Fingerprint);
        var handle = profile == DatabaseProfileId.PropertyGraph
            ? remoteApplication.CaptureOwnerGraphSave(new Item(left, "later"), capability)
            : remoteApplication.CaptureOwnerRdfSave(new Item(left, "later"), capability);
        var restoredHandle = PartitionOwnerCapture.Restore(handle.Export());
        var handled = await restoredHandle.DeliverAsync(remoteApplication);
        Assert.AreEqual(handled.Members[0].Receipt.Version, (await handle.DeliverAsync(remoteApplication)).Members[0].Receipt.Version);
        Assert.IsNull(await decisions.ReadApplicationAsync(handle.OperationId));
        // Retention is reachable through the public application service too, and
        // completing an identical cutoff again must be harmless.
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-1);
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => remoteApplication.RetainAsync(cutoff).AsTask());
        var administrator = new HttpPartitionEntityClient(http, PartitionAddress(gateway), PartitionToken + "-admin", client.Generation, profile);
        await administrator.RetainAsync(cutoff);
        await administrator.RetainAsync(cutoff);
        await Assert.ThrowsExactlyAsync<EntityOperationExpiredException>(() => client.DeliverAsync(ownerOperation, ownerRequest).AsTask());
        var expiredResolution = await Assert.ThrowsExactlyAsync<HttpRequestException>(() => remoteApplication.ResolveAsync(ownerOperation).AsTask());
        Assert.AreEqual(HttpStatusCode.Gone, expiredResolution.StatusCode);
        if (profile == DatabaseProfileId.PropertyGraph)
        {
            Assert.AreEqual("later", (await (await first.CreateGraphExecutionAsync()).ReadAsync<Item>(first.Domain, left))!.Value.Name);
            Assert.AreEqual("cross", (await (await second.CreateGraphExecutionAsync()).ReadAsync<Item>(second.Domain, right))!.Value.Name);
        }
        else
        {
            Assert.AreEqual("later", (await (await first.CreateRdfExecutionAsync()).ReadAsync<Item>(first.Domain, left))!.Value.Name);
            Assert.AreEqual("cross", (await (await second.CreateRdfExecutionAsync()).ReadAsync<Item>(second.Domain, right))!.Value.Name);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Logical_application_binding_follows_preserved_incarnation_across_transfer_and_reopen(bool ownerIdentity)
    {
        var placements = new Authority();
        await using var decisions = FilePartitionTransactionAuthority.Open(Path.Combine(_root, "authority"));
        await using var source = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "source"), DatabaseProfileId.PropertyGraph,
            placements.Lease, Routing, placements, Options, transactionAuthority: decisions, timestampClock: ReadClock);
        using var http = new HttpClient();
        var capture = new HttpGraphEntityClient(http, source.Domain.Generation).EntityStore<Item>();
        var request = capture.CaptureSave(new("one", "before"), new(ownerIdentity ? PartitionOwnerOperation.New(Routing, new("a")) : Guid.NewGuid()));
        var first = await new PartitionApplicationClient(decisions, Routing, [source], ReadClock).DeliverAsync(request.OperationId, request.Export());
        string imagePath = Path.Combine(_root, "image"), destinationPath = Path.Combine(_root, "destination");
        var image = await source.SealAndExportAsync(new(Guid.NewGuid()), imagePath);
        await using var transfer = new PartitionOwnedTransfer(imagePath, destinationPath, image, Routing, placements,
            new("destination"), new("https://destination.test"), Options, decisions, timestampClock: ReadClock);
        placements.LoseNextCutoverReply = true;
        await Assert.ThrowsExactlyAsync<IOException>(async () => await transfer.CompleteAsync());
        var destination = await transfer.CompleteAsync();
        var client = new PartitionApplicationClient(decisions, Routing, [destination], ReadClock);
        var later = capture.CaptureSave(new("one", "later"), new(Guid.NewGuid()));
        await client.DeliverAsync(later.OperationId, later.Export());
        var replay = await client.DeliverAsync(request.OperationId, request.Export());
        Assert.AreEqual(first.Members[0].Receipt.Version, replay.Members[0].Receipt.Version);
        Assert.AreEqual(DatabaseCommitOutcome.IdempotentReplay, replay.Members[0].Receipt.Outcome);
        await transfer.DisposeAsync();
        await using var restarted = new PartitionOwnedTransfer(imagePath, destinationPath, image, Routing, placements,
            new("destination"), new("https://destination.test"), Options, decisions, timestampClock: ReadClock);
        var reopened = await restarted.CompleteAsync();
        replay = await new PartitionApplicationClient(decisions, Routing, [reopened], ReadClock).DeliverAsync(request.OperationId, request.Export());
        Assert.AreEqual(first.Members[0].Receipt.Version, replay.Members[0].Receipt.Version);
        Assert.AreEqual("later", (await (await reopened.CreateGraphExecutionAsync()).ReadAsync<Item>(reopened.Domain, "one"))!.Value.Name);
        await restarted.DisposeAsync();
        // Losing current storage must not authorise replay of the authentic but
        // obsolete source image under its preserved incarnation.
        string missingDestination = Path.Combine(_root, "replacement-destination");
        await using var obsolete = new PartitionOwnedTransfer(imagePath, missingDestination, image, Routing, placements,
            new("destination"), new("https://destination.test"), Options, decisions, timestampClock: ReadClock);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => await obsolete.CompleteAsync());
        placements.HideAuthorityState = true; // Expired lease is not proof of pre-cutover state.
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => await obsolete.CompleteAsync());
        Assert.IsFalse(File.Exists(Path.Combine(missingDestination, PartitionOwnedNode.EnrollmentFile)));
    }

    private sealed class LoseFirstApplicationReply(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        private int _lost;
        private int _lostOwner;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var response = await base.SendAsync(request, token);
            bool readable = request.RequestUri!.AbsolutePath == PartitionApplicationWire.Path && response.IsSuccessStatusCode &&
                System.Text.Json.JsonSerializer.Deserialize<PartitionApplicationWire.Response>(
                    await response.Content.ReadAsByteArrayAsync(token), PartitionParticipantWire.Json)?.Code == "readable";
            if (readable &&
                PartitionOwnerOperation.IsOwnerLocal(System.Text.Json.JsonSerializer.Deserialize<PartitionApplicationWire.Request>(
                    await request.Content!.ReadAsByteArrayAsync(token), PartitionParticipantWire.Json)!.Operation) &&
                Interlocked.Exchange(ref _lostOwner, 1) == 0)
            {
                response.Dispose();
                throw new IOException("Lost owner-local response after native completion.");
            }
            if (readable &&
                Interlocked.Exchange(ref _lost, 1) == 0)
            {
                response.Dispose();
                throw new IOException("Lost application response after native completion.");
            }
            return response;
        }
    }

    private sealed class ApplicationAuthorityLifetime(IPartitionTransactionAuthority authority,
        FilePartitionTransactionAuthority? file = null, EtcdClient? etcd = null, string? prefix = null) : IAsyncDisposable
    {
        internal IPartitionTransactionAuthority Authority => authority;

        internal static async ValueTask<ApplicationAuthorityLifetime> OpenAsync(string path, Guid generation, DatabaseProfileId profile,
            DistributedRoutingBinding routing, bool live)
        {
            if (!live)
            {
                var file = FilePartitionTransactionAuthority.Open(path);
                return new(file, file);
            }
            string? endpoint = Environment.GetEnvironmentVariable("ENTITYFORGE_TEST_ETCD_ENDPOINT");
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                Assert.Inconclusive("Set ENTITYFORGE_TEST_ETCD_ENDPOINT.");
                throw new InvalidOperationException();
            }
            var client = new EtcdClient(endpoint, configureChannelOptions: options => options.Credentials = ChannelCredentials.Insecure);
            string prefix = "/entityforge/application-routing-test/" + Guid.NewGuid().ToString("N") + "/";
            try
            {
                return new(await GrpcEtcdPartitionTransactionAuthority.OpenAsync(client, prefix, generation, profile, routing), etcd: client, prefix: prefix);
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (file is not null)
            {
                await file.DisposeAsync();
            }

            if (etcd is not null)
            {
                try
                {
                    await etcd.DeleteAsync(new DeleteRangeRequest { Key = ByteString.CopyFromUtf8(prefix!), RangeEnd = ByteString.CopyFromUtf8(prefix![..^1] + "0") });
                }
                finally
                {
                    etcd.Dispose();
                }
            }
        }
    }
}
