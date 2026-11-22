using EntityForge.Db.Mapping;
using EntityForge.Db.Durability;
using EntityForge.Db.Graph;
using EntityForge.Db.Rdf;
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
    public async Task Native_home_delivers_two_owner_mapped_save_without_central_operation_records(DatabaseProfileId profile, bool stream, bool live)
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
        await using var controlLifetime = await ApplicationAuthorityLifetime.OpenAsync(Path.Combine(_root, "authority"),
            a.Lease.Placement.Claim.Generation.Value, profile, routing, live);
        var control = controlLifetime.Authority;
        var homes = new Dictionary<LogicalShardId, IPartitionCoordinatorHomeParticipant>();
        var decisions = new PartitionNativeHomeAuthority(control, routing, shard => homes[shard]);
        await using var first = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "a"), profile, a.Lease, routing, a, Options, transactionAuthority: decisions, timestampClock: ReadClock);
        homes.Add(first.Domain.Partition, first);
        await using var second = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "b"), profile, b.Lease, routing, b, Options, transactionAuthority: decisions, timestampClock: ReadClock);
        homes.Add(second.Domain.Partition, second);
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
        homes[first.Domain.Partition] = remoteA;
        homes[second.Domain.Partition] = remoteB;
        Guid operation = EntityOperationIds.New();
        string value = "home";
        byte[] Capture(string id) => profile == DatabaseProfileId.PropertyGraph
            ? new HttpGraphEntityClient(http, first.Domain.Generation).EntityStore<Item>().CaptureSave(new(id, value), new GraphEntityWriteOptions(operation)).Export()
            : new HttpRdfEntityClient(http, first.Domain.Generation).EntityStore<Item>().CaptureSave(new(id, value), new RdfEntityWriteOptions(operation)).Export();
        ReadOnlyMemory<byte> Request(string owner)
        {
            for (int index = 0; ; index++)
            {
                var request = Capture(index.ToString());
                if (PartitionApplicationRouting.Owner(first.Domain.Generation, profile, routing, operation, request).Value == owner)
                {
                    return request;
                }
            }
        }
        ReadOnlyMemory<byte>[] requests = [Request("a"), Request("b")];
        var client = new PartitionApplicationClient(decisions, routing, [remoteA, remoteB], ReadClock);
        if (stream)
        {
            await first.EnableDecisionNotificationsAsync();
            await second.EnableDecisionNotificationsAsync();
            await client.PrepareAsync(operation, requests, CancellationToken.None);
            var plan = (await decisions.ReadPlanAsync(operation))!;
            await SelectTimestampDecisionAsync(decisions, plan, first, second);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            foreach (var node in new[] { first, second })
            {
                while (!(await node.Kernel.PartitionTransactions.ReadAsync(deadline.Token)).Any(entry =>
                    entry.Operation == operation && entry.State == DurablePartitionTransactionState.Committed))
                {
                    await Task.Delay(10, deadline.Token);
                }
            }
        }
        var simultaneous = await Task.WhenAll(client.DeliverAsync(operation, requests).AsTask(), client.DeliverAsync(operation, requests).AsTask());
        var result = simultaneous[0];
        CollectionAssert.AreEquivalent(result.Members.Select(member => member.Receipt.Version.Value).ToArray(),
            simultaneous[1].Members.Select(member => member.Receipt.Version.Value).ToArray());
        Assert.AreEqual(2, result.Members.Count);
        value = "conflicting";
        await Assert.ThrowsExactlyAsync<EntityForgeDbIdempotencyException>(() => client.DeliverAsync(operation, [Request("a"), Request("b")]).AsTask());
        value = "home";
        Assert.IsNull(await control.ReadApplicationAsync(operation));
        Assert.IsNull(await control.ReadPlanAsync(operation));
        Assert.IsNull(await control.ReadDecisionAsync(operation));
        Assert.IsTrue((await decisions.ReadDecisionAsync(operation))!.Commit);
        var entries = (await first.Kernel.PartitionTransactions.ReadAsync()).Concat(await second.Kernel.PartitionTransactions.ReadAsync()).ToArray();
        Assert.AreEqual(1, entries.Count(entry => entry.Coordinator is not null));
        Assert.IsTrue(entries.All(entry => entry.State == DurablePartitionTransactionState.Committed));
        // A home response can lose readability after its native commit. The
        // authority must preserve the distributed commit classification.
        var homeShard = PartitionCoordinatorHome.Owner(operation, routing);
        var homeNode = homeShard == first.Domain.Partition ? first : second;
        var originalHome = homes[homeShard];
        var receipt = (await homeNode.Kernel.PartitionTransactions.ResolveAsync(operation,
            (await decisions.ReadPlanAsync(operation))!.Descriptor))!;
        homes[homeShard] = new CommittedFailureHome(originalHome, receipt);
        var incomplete = await Assert.ThrowsExactlyAsync<PartitionDecisionDeliveryException>(() =>
            decisions.DecideAsync(operation, (entries.Single(entry => entry.Coordinator is not null)).Descriptor, true).AsTask());
        Assert.IsTrue(incomplete.Decision.Commit);
        Assert.AreEqual(operation, incomplete.Decision.Operation);
        homes[homeShard] = originalHome;
        var replay = await client.DeliverAsync(operation, requests);
        CollectionAssert.AreEquivalent(result.Members.Select(member => member.Receipt.Version.Value).ToArray(),
            replay.Members.Select(member => member.Receipt.Version.Value).ToArray());
        // Failure after the home commits is a distributed known-commit response,
        // not a generic transport failure. Exact retry completes the other member.
        var originalOperation = operation;
        var originalRequests = requests;
        operation = EntityOperationIds.New();
        requests = [Request("a"), Request("b")];
        var otherShard = routing.Shards.Single(shard => shard != PartitionCoordinatorHome.Owner(operation, routing));
        var other = homes[otherShard];
        homes[otherShard] = new HomeFailureParticipant((IPartitionTransactionParticipant)other, other, failPrepare: false);
        var known = await Assert.ThrowsExactlyAsync<PartitionDecisionDeliveryException>(() => client.DeliverAsync(operation, requests).AsTask());
        Assert.IsTrue(known.Decision.Commit);
        homes[otherShard] = other;
        Assert.AreEqual(2, (await client.DeliverAsync(operation, requests)).Members.Count);
        // Explicit commitment is a separate wire result, followed by an exact readable join.
        operation = EntityOperationIds.New();
        requests = [Request("a"), Request("b")];
        var committingHome = PartitionCoordinatorHome.Owner(operation, routing) == first.Domain.Partition ? remoteA : remoteB;
        var committingApplication = new PartitionApplicationBinding(operation, [first.Domain.Identity, second.Domain.Identity], requests);
        var committedOnly = await committingHome.CommitHomeAsync(committingApplication);
        Assert.IsTrue(committedOnly.Commit);
        Assert.AreEqual(operation, committedOnly.Operation);
        Assert.AreEqual(2, (await committingHome.WaitHomeAsync(committingApplication, CancellationToken.None)).Members.Count);
        long firstVersion = first.Kernel.CurrentVersion.Value, secondVersion = second.Kernel.CurrentVersion.Value;
        Assert.AreEqual(2, (await committingHome.WaitHomeAsync(committingApplication, CancellationToken.None)).Members.Count);
        Assert.AreEqual(firstVersion, first.Kernel.CurrentVersion.Value);
        Assert.AreEqual(secondVersion, second.Kernel.CurrentVersion.Value);
        // A lost preparation reply can still leave native preparation behind.
        // The home selects abort and delivers it before reporting terminal abort.
        operation = EntityOperationIds.New();
        requests = [Request("a"), Request("b")];
        otherShard = routing.Shards.Single(shard => shard != PartitionCoordinatorHome.Owner(operation, routing));
        other = homes[otherShard];
        homes[otherShard] = new HomeFailureParticipant((IPartitionTransactionParticipant)other, other, failPrepare: true);
        await Assert.ThrowsExactlyAsync<EntityOperationAbortedException>(() => client.DeliverAsync(operation, requests).AsTask());
        homes[otherShard] = other;
        Assert.IsFalse((await decisions.ReadDecisionAsync(operation))!.Commit);
        foreach (var node in new[] { first, second })
        {
            Assert.AreEqual(DurablePartitionTransactionState.Aborted,
                (await node.Kernel.PartitionTransactions.ReadAsync()).Single(entry => entry.Operation == operation).State);
        }

        await Assert.ThrowsExactlyAsync<EntityOperationAbortedException>(() => client.DeliverAsync(operation, requests).AsTask());
        operation = originalOperation;
        requests = originalRequests;
        await serviceA.DisposeAsync();
        await serviceB.DisposeAsync();
        await hostA.StopAsync();
        await hostB.StopAsync();
        await first.DisposeAsync();
        await second.DisposeAsync();
        await using var recoveredA = await PartitionOwnedNode.OpenAsync(Path.Combine(_root, "a"), profile, a.Lease, routing, a, Options, transactionAuthority: decisions, timestampClock: ReadClock);
        await using var recoveredB = await PartitionOwnedNode.OpenAsync(Path.Combine(_root, "b"), profile, b.Lease, routing, b, Options, transactionAuthority: decisions, timestampClock: ReadClock);
        Assert.IsTrue(recoveredA.IsRecoveringHome);
        Assert.IsTrue(recoveredB.IsRecoveringHome);
        await using var recoveredServiceA = new PartitionParticipantHttpService(recoveredA);
        await using var recoveredServiceB = new PartitionParticipantHttpService(recoveredB);
        await using var recoveredHostA = await StartPartitionHostAsync(recoveredServiceA, certificate);
        await using var recoveredHostB = await StartPartitionHostAsync(recoveredServiceB, certificate);
        remoteA = new(http, PartitionAddress(recoveredHostA), PartitionToken, recoveredA.Domain, decisions.Identity);
        remoteB = new(http, PartitionAddress(recoveredHostB), PartitionToken, recoveredB.Domain, decisions.Identity);
        homes[first.Domain.Partition] = remoteA;
        homes[second.Domain.Partition] = remoteB;
        await Task.WhenAll(recoveredA.CompleteNativeHomeRecoveryAsync().AsTask(), recoveredB.CompleteNativeHomeRecoveryAsync().AsTask());
        client = new(decisions, routing, [remoteA, remoteB]);
        Assert.AreEqual(2, (await client.DeliverAsync(operation, requests)).Members.Count);
        operation = EntityOperationIds.New();
        var abandoned = new PartitionApplicationBinding(operation, [first.Domain.Identity, second.Domain.Identity], [Request("a"), Request("b")]);
        var proposals = await Task.WhenAll(remoteA.ProposeApplicationAsync(abandoned).AsTask(), remoteB.ProposeApplicationAsync(abandoned).AsTask());
        await decisions.SelectCombinedAsync(abandoned, new(operation, routing.MapVersion, proposals));
        await PartitionRemoteRetention.RetainAsync(decisions, [remoteA, remoteB], routing, DateTimeOffset.UtcNow.AddMinutes(-1));
        Assert.IsFalse((await decisions.ReadDecisionAsync(operation))!.Commit);
        await PartitionRemoteRetention.RetainAsync(decisions, [remoteA, remoteB], routing, DateTimeOffset.UtcNow);
        Assert.AreEqual(0, (await recoveredA.Kernel.PartitionTransactions.ReadAsync()).Count);
        Assert.AreEqual(0, (await recoveredB.Kernel.PartitionTransactions.ReadAsync()).Count);

        // Move the actual coordinator home with committed evidence, then reopen
        // read-only status before completing destination recovery.
        do
        {
            operation = EntityOperationIds.New();
        }
        while (PartitionCoordinatorHome.Owner(operation, routing) != recoveredA.Domain.Partition);
        requests = [Request("a"), Request("b")];
        var beforeTransfer = await client.DeliverAsync(operation, requests);
        await recoveredServiceA.DisposeAsync();
        await recoveredHostA.StopAsync();
        string imagePath = Path.Combine(_root, "home-image"), destinationPath = Path.Combine(_root, "home-destination");
        var image = await recoveredA.SealAndExportAsync(new(Guid.NewGuid()), imagePath);
        await using var transfer = new PartitionOwnedTransfer(imagePath, destinationPath, image, routing, a,
            new("destination"), new("https://destination.test"), Options, decisions, timestampClock: ReadClock);
        var destination = await transfer.CompleteAsync();
        Assert.IsTrue(destination.IsRecoveringHome);
        await using var destinationService = new PartitionParticipantHttpService(destination);
        await using var destinationHost = await StartPartitionHostAsync(destinationService, certificate);
        remoteA = new(http, PartitionAddress(destinationHost), PartitionToken, destination.Domain, decisions.Identity);
        homes[destination.Domain.Partition] = remoteA;
        await destination.CompleteNativeHomeRecoveryAsync();
        client = new(decisions, routing, [remoteA, remoteB]);
        var afterTransfer = await client.DeliverAsync(operation, requests);
        CollectionAssert.AreEquivalent(beforeTransfer.Members.Select(member => member.Receipt.Version.Value).ToArray(),
            afterTransfer.Members.Select(member => member.Receipt.Version.Value).ToArray());
        await PartitionRemoteRetention.RetainAsync(decisions, [remoteA, remoteB], routing, DateTimeOffset.UtcNow);
        Assert.AreEqual(0, (await destination.Kernel.PartitionTransactions.ReadAsync()).Count);
        await Assert.ThrowsExactlyAsync<EntityOperationExpiredException>(() => remoteB.ApplyPartitionDecisionAsync(originalOperation).AsTask());
        operation = EntityOperationIds.New();
        requests = [Request("a"), Request("b")];
        await client.DeliverAsync(operation, requests);
        await destinationService.DisposeAsync();
        await destinationHost.StopAsync();
        await transfer.DisposeAsync();
        // Preserve the lost recovery set for test cleanup; never reactivate its
        // earlier transfer image under the existing post-cutover incarnation.
        Directory.Move(destinationPath, destinationPath + ".lost");
        await using var obsolete = new PartitionOwnedTransfer(imagePath, destinationPath, image, routing, a,
            new("destination"), new("https://destination.test"), Options, decisions, timestampClock: ReadClock);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => obsolete.CompleteAsync().AsTask());
    }

    private sealed class CommittedFailureHome(IPartitionCoordinatorHomeParticipant inner, DatabaseCommitReceipt receipt) : IPartitionCoordinatorHomeParticipant
    {
        public PartitionExecutionDomain Domain => inner.Domain;

        public ValueTask<DurablePartitionTransaction?> ReadHomeAsync(Guid operation, CancellationToken token) => inner.ReadHomeAsync(operation, token);

        public ValueTask<DurablePartitionTransaction> SelectHomeAsync(PartitionApplicationBinding application, PartitionTransactionPlan proposal, CancellationToken token) => inner.SelectHomeAsync(application, proposal, token);

        public ValueTask<DurablePartitionTransaction> DecideHomeAsync(Guid operation, string descriptor, bool commit, CancellationToken token) =>
            ValueTask.FromException<DurablePartitionTransaction>(new EntityOperationCommittedException<DatabaseCommitReceipt>(receipt, receipt, new IOException("Lost home completion.")));

        public IAsyncEnumerable<PartitionDecisionNotification> ReadHomeNotificationsAsync(CancellationToken token) => inner.ReadHomeNotificationsAsync(token);
    }

    private sealed class HomeFailureParticipant(IPartitionTransactionParticipant inner, IPartitionCoordinatorHomeParticipant home, bool failPrepare)
        : IPartitionTransactionParticipant, IPartitionCoordinatorHomeParticipant
    {
        public PartitionExecutionDomain Domain => inner.Domain;

        public string? TransactionAuthorityIdentity => inner.TransactionAuthorityIdentity;

        public bool SupportsApplicationProposal => true;

        public ValueTask<PartitionTransactionMember> ProposeApplicationAsync(PartitionApplicationBinding application, CancellationToken token) => inner.ProposeApplicationAsync(application, token);

        public async ValueTask PrepareNativeAsync(PartitionTransactionPlan plan, CancellationToken token)
        {
            await inner.PrepareNativeAsync(plan, token);
            if (failPrepare)
            {
                throw new IOException("Lost prepared reply.");
            }
        }

        public async ValueTask<DurableTimestampState?> PrepareWithTimestampAsync(PartitionTransactionPlan plan, CancellationToken token)
        {
            var vote = await inner.PrepareWithTimestampAsync(plan, token);
            if (failPrepare)
            {
                throw new IOException("Lost prepared reply.");
            }

            return vote;
        }

        public async ValueTask<DurableTimestampState> PrepareTimestampNativeAsync(PartitionTransactionPlan plan, CancellationToken token)
        {
            var vote = await inner.PrepareTimestampNativeAsync(plan, token);
            if (failPrepare)
            {
                throw new IOException("Lost prepared reply.");
            }

            return vote;
        }

        public ValueTask<DatabaseCommitReceipt?> ApplyPartitionDecisionAsync(Guid operation, CancellationToken token) => failPrepare
            ? inner.ApplyPartitionDecisionAsync(operation, token) : ValueTask.FromException<DatabaseCommitReceipt?>(new IOException("Application unavailable."));

        public ValueTask<DurablePartitionTransaction?> ReadHomeAsync(Guid operation, CancellationToken token) => home.ReadHomeAsync(operation, token);

        public ValueTask<DurablePartitionTransaction> SelectHomeAsync(PartitionApplicationBinding application, PartitionTransactionPlan proposal, CancellationToken token) => home.SelectHomeAsync(application, proposal, token);

        public ValueTask<DurablePartitionTransaction> DecideHomeAsync(Guid operation, string descriptor, bool commit, CancellationToken token) => home.DecideHomeAsync(operation, descriptor, commit, token);

        public IAsyncEnumerable<PartitionDecisionNotification> ReadHomeNotificationsAsync(CancellationToken token) => home.ReadHomeNotificationsAsync(token);

        public ValueTask<PartitionCommitResult> ExecuteMappedAsync(Guid operation, ReadOnlyMemory<byte> captured, CancellationToken token) => inner.ExecuteMappedAsync(operation, captured, token);

        public ValueTask<PartitionCommitResult> ExecuteApplicationAsync(Guid operation, CancellationToken token) => inner.ExecuteApplicationAsync(operation, token);

        public ValueTask<PartitionTransactionMember> PlanApplicationAsync(Guid operation, CancellationToken token) => inner.PlanApplicationAsync(operation, token);

        public ValueTask PrepareLocalApplicationAsync(Guid operation, CancellationToken token) => inner.PrepareLocalApplicationAsync(operation, token);

        public ValueTask InitializeApplicationMappingAsync(ReadOnlyMemory<byte> captured, CancellationToken token) => inner.InitializeApplicationMappingAsync(captured, token);

        public ValueTask<PartitionMemberResolution> ResolveApplicationAsync(Guid operation, bool abort, CancellationToken token) => inner.ResolveApplicationAsync(operation, abort, token);
    }
}
