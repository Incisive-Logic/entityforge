using EntityForge.Db.Graph;
using EntityForge.Db.Semantic;
using EntityForge.Db.Mapping;
using EntityForge.Db.Durability;
using EntityForge.Db.Distributed.Hosting;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Late_preparation_joins_abort_that_already_passed_owner(bool live)
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
        await using var lifetime = await ApplicationAuthorityLifetime.OpenAsync(Path.Combine(_root, "authority"), a.Lease.Placement.Claim.Generation.Value, DatabaseProfileId.PropertyGraph, routing, live);
        var authority = new PausedDecisionObservation(lifetime.Authority);
        await using var first = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "a"), DatabaseProfileId.PropertyGraph, a.Lease, routing, a, Options, transactionAuthority: authority, timestampClock: ReadClock);
        await using var second = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "b"), DatabaseProfileId.PropertyGraph, b.Lease, routing, b, Options, transactionAuthority: authority, timestampClock: ReadClock);
        Guid operation = EntityOperationIds.New();
        var plan = new PartitionTransactionPlan(operation, routing.MapVersion,
            [new(first.Domain, new(0), NativePayload(DatabaseProfileId.PropertyGraph, routing, new("a"), operation)),
                new(second.Domain, new(0), NativePayload(DatabaseProfileId.PropertyGraph, routing, new("b"), operation))]);
        await authority.BindAsync(plan);
        await first.PrepareNativeAsync(plan);
        authority.Pause = true;
        var preparing = second.PrepareNativeAsync(plan).AsTask();
        await authority.Observed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            await authority.DecideAsync(operation, plan.Descriptor, false);
            await first.ApplyPartitionDecisionAsync(operation);
            Assert.IsNull(await second.ApplyPartitionDecisionAsync(operation));
        }
        finally
        {
            authority.Resume.TrySetResult();
        }
        await preparing.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsTrue((await second.Kernel.PartitionTransactions.ReadAsync()).All(entry => entry.State != DurablePartitionTransactionState.Prepared));
        // A fresh whole-partition reservation proves no abandoned prepared owner remains.
        Guid next = EntityOperationIds.New();
        var nextPlan = new PartitionTransactionPlan(next, routing.MapVersion,
            [new(first.Domain, first.Kernel.CurrentVersion, NativePayload(DatabaseProfileId.PropertyGraph, routing, new("a"), next)),
                new(second.Domain, second.Kernel.CurrentVersion, NativePayload(DatabaseProfileId.PropertyGraph, routing, new("b"), next))]);
        await authority.BindAsync(nextPlan);
        await second.PrepareNativeAsync(nextPlan);
        await authority.DecideAsync(next, nextPlan.Descriptor, false);
        await second.ApplyPartitionDecisionAsync(next);
    }

    [DataTestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph, false)]
    [DataRow(DatabaseProfileId.PropertyGraph, true)]
    [DataRow(DatabaseProfileId.Semantic, false)]
    [DataRow(DatabaseProfileId.Semantic, true)]
    public async Task Application_abort_needs_neither_old_snapshot_nor_native_plan(DatabaseProfileId profile, bool live)
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
        await using var lifetime = await ApplicationAuthorityLifetime.OpenAsync(Path.Combine(_root, "authority"), a.Lease.Placement.Claim.Generation.Value, profile, routing, live);
        var authority = lifetime.Authority;
        await using var first = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "a"), profile, a.Lease, routing, a, Options, transactionAuthority: authority, timestampClock: ReadClock);
        await using var second = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "b"), profile, b.Lease, routing, b, Options, transactionAuthority: authority, timestampClock: ReadClock);
        using var http = new HttpClient();
        IPartitionTransactionParticipant[] owners = [first, second];
        var operation = EntityOperationIds.New();
        byte[] captured = profile == DatabaseProfileId.PropertyGraph
            ? new HttpGraphEntityClient(http, first.Domain.Generation).EntityStore<Item>().CaptureSave(new("one", "old"), new(operation)).Export()
            : new HttpSemanticEntityClient(http, first.Domain.Generation).EntityStore<Item>().CaptureSave(new("one", "old"), new(operation, DateTimeOffset.UnixEpoch)).Export();
        if (profile == DatabaseProfileId.PropertyGraph)
        {
            for (int index = 0; PartitionApplicationRouting.Owner(first.Domain.Generation, profile, routing, operation, captured).Value != "a"; index++)
            {
                captured = new HttpGraphEntityClient(http, first.Domain.Generation).EntityStore<Item>().CaptureSave(new(index.ToString(), "old"), new(operation)).Export();
            }
        }
        var selection = profile == DatabaseProfileId.PropertyGraph
            ? await PartitionGraphPlanning.SelectAsync(first.Domain.Generation, operation, routing, owners, [captured], CancellationToken.None)
            : await PartitionSemanticPlanning.SelectAsync(first.Domain.Generation, operation, routing, owners, [captured], CancellationToken.None);
        await authority.BindApplicationAsync(selection);
        var proposal = new PartitionTransactionPlan(operation, routing.MapVersion,
            await Task.WhenAll(new[] { first, second }.Where(n => selection.Owners.Contains(n.Domain.Identity))
                .Select(n => n.PlanApplicationAsync(operation).AsTask())));
        if (profile == DatabaseProfileId.PropertyGraph)
        {
            Guid intervening = EntityOperationIds.New();
            await first.Kernel.CommitWithReceiptAsync(intervening, NativePayload(profile, routing, new("a"), intervening));
            // Reconstruction uses immutable planning evidence, not a fresh plan
            // at the advanced position. Conflict checks belong to preparation.
            var original = proposal.Members.Single(member => member.Domain == first.Domain);
            var reconstructed = await first.PlanApplicationAsync(operation);
            Assert.IsTrue(first.Kernel.CurrentVersion > original.ReadVersion);
            Assert.AreEqual(original.ReadVersion, reconstructed.ReadVersion);
            CollectionAssert.AreEqual(original.Payload.ToArray(), reconstructed.Payload.ToArray());
        }
        // Abort must not contact
        // owners at all, even if they are unavailable and no native plan exists.
        await first.DisposeAsync();
        await second.DisposeAsync();
        var client = new PartitionApplicationClient(authority, routing, owners, ReadClock);
        Assert.AreEqual(EntityOperationState.Aborted, (await client.AbortAsync(operation, CancellationToken.None)).State);
        Assert.AreEqual(EntityOperationState.Aborted, (await client.ResolveAsync(operation, CancellationToken.None)).State);
        Assert.IsNull(await authority.ReadPlanAsync(operation));
        await Assert.ThrowsExactlyAsync<EntityOperationAbortedException>(() => authority.BindAsync(proposal).AsTask());
        Assert.IsTrue((await authority.ReadDecisionAsync(operation))!.ApplicationOnly);
        // Repeat proves durable terminal selection, not an in-memory cancellation.
        Assert.AreEqual(EntityOperationState.Aborted, (await client.AbortAsync(operation, CancellationToken.None)).State);
    }

    [DataTestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph)]
    [DataRow(DatabaseProfileId.Rdf)]
    [DataRow(DatabaseProfileId.Semantic)]
    public async Task Default_convenience_retains_exact_capture_after_lost_success(DatabaseProfileId profile)
    {
        var placements = new Authority();
        await using var authority = FilePartitionTransactionAuthority.Open(Path.Combine(_root, "authority"));
        await using var node = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "node"), profile, placements.Lease, Routing, placements, Options, transactionAuthority: authority, timestampClock: ReadClock);
        using var certificate = PartitionCertificate();
        using var loss = new CaptureLostReply(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert?.Thumbprint == certificate.Thumbprint });
        using var http = new HttpClient(loss);
        var execution = new PartitionApplicationClient(authority, Routing, [node], ReadClock);
        await using var service = new PartitionApplicationHttpService(execution);
        await using var gateway = await StartApplicationHostAsync(service, certificate);
        var client = new HttpPartitionEntityClient(http, PartitionAddress(gateway), PartitionToken, node.Domain.Generation, profile);
        var entity = new Item("identity", "saved");
        var ambiguous = await Assert.ThrowsExactlyAsync<PartitionApplicationUncertainException>(async () =>
        {
            if (profile == DatabaseProfileId.PropertyGraph)
            {
                await client.SaveGraphAsync(entity);
            }
            else if (profile == DatabaseProfileId.Rdf)
            {
                await client.SaveRdfAsync(entity);
            }
            else
            {
                await client.SaveSemanticAsync(entity);
            }
        });
        Assert.IsInstanceOfType<IOException>(ambiguous.InnerException);
        Assert.AreNotEqual(Guid.Empty, ambiguous.Retry.OperationId);
        Assert.AreEqual(EntityOperationState.Committed, (await client.ResolveAsync(ambiguous.Retry.OperationId)).State);
        var replay = await ambiguous.Retry.DeliverAsync(client);
        Assert.AreEqual(ambiguous.Retry.OperationId, replay.OperationId);
        Assert.AreEqual(DatabaseCommitOutcome.IdempotentReplay, replay.Members.Single().Receipt.Outcome);
        Assert.AreEqual(2, loss.Submissions);
    }

    private sealed class CaptureLostReply(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        private byte[]? _original;
        internal int Submissions;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            bool submission = request.RequestUri!.AbsolutePath == PartitionApplicationWire.Path;
            if (submission)
            {
                var bytes = await request.Content!.ReadAsByteArrayAsync(token);
                if (_original is null)
                {
                    _original = bytes;
                }
                else
                {
                    CollectionAssert.AreEqual(_original, bytes);
                }

                Submissions++;
            }
            var response = await base.SendAsync(request, token);
            if (submission && Submissions == 1)
            {
                response.EnsureSuccessStatusCode();
                response.Dispose();
                throw new IOException("Lost acknowledged response.");
            }
            return response;
        }
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Preplan_abort_and_plan_selection_have_one_durable_winner(bool live)
    {
        var routing = new StableHashDistributedShardRouter(new(new(1), [new("a"), new("b")])).Binding;
        var generation = Guid.NewGuid();
        await using var lifetime = await ApplicationAuthorityLifetime.OpenAsync(Path.Combine(_root, "authority"), generation, DatabaseProfileId.PropertyGraph, routing, live);
        var authority = lifetime.Authority;
        for (int index = 0; index < 8; index++)
        {
            var operation = EntityOperationIds.New();
            var plan = new PartitionTransactionPlan(operation, routing.MapVersion,
                [new(new(generation, new("a"), routing.Fingerprint, DatabaseProfileId.PropertyGraph, Guid.NewGuid()), new(0), new byte[] { 1 }),
                    new(new(generation, new("b"), routing.Fingerprint, DatabaseProfileId.PropertyGraph, Guid.NewGuid()), new(0), new byte[] { 2 })]);
            var application = new PartitionApplicationBinding(operation, plan.Members.Select(member => member.Domain.Identity).ToArray(), [new byte[] { 3 }]);
            await authority.BindApplicationAsync(application);
            async Task Bind()
            {
                try
                {
                    await authority.BindAsync(plan);
                }
                catch (EntityOperationAbortedException)
                {

                }
            }
            await Task.WhenAll(Bind(), authority.AbortApplicationAsync(operation, application.Descriptor).AsTask());
            var decision = (await authority.ReadDecisionAsync(operation))!;
            var selected = await authority.ReadPlanAsync(operation);
            Assert.IsFalse(decision.Commit);
            Assert.AreEqual(selected is null, decision.ApplicationOnly);
            Assert.AreEqual(selected?.Descriptor ?? application.Descriptor, decision.Descriptor);
            Assert.AreEqual(decision, await authority.AbortApplicationAsync(operation, application.Descriptor));
            if (selected is not null)
            {
                Assert.AreEqual(decision, await authority.DecideAsync(operation, selected.Descriptor, true));
            }
        }
    }

    private sealed class PausedDecisionObservation(IPartitionTransactionAuthority inner) : IPartitionTransactionAuthority
    {
        internal bool Pause;
        internal TaskCompletionSource Observed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Resume = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Identity => inner.Identity;

        public ValueTask RequireUnenrolledAsync(PartitionAuthorityEnrollment partition, CancellationToken token = default) => inner.RequireUnenrolledAsync(partition, token);

        public ValueTask EnrollAsync(PartitionExecutionDomain d, bool create, CancellationToken t = default) => inner.EnrollAsync(d, create, t);

        public ValueTask BindApplicationAsync(PartitionApplicationBinding b, CancellationToken t = default) => inner.BindApplicationAsync(b, t);

        public ValueTask<PartitionApplicationBinding?> ReadApplicationAsync(Guid id, CancellationToken t = default) => inner.ReadApplicationAsync(id, t);

        public ValueTask BindAsync(PartitionTransactionPlan p, CancellationToken t = default) => inner.BindAsync(p, t);

        public ValueTask<PartitionTransactionPlan?> ReadPlanAsync(Guid id, CancellationToken t = default) => inner.ReadPlanAsync(id, t);

        public ValueTask<PartitionTransactionDecision?> ReadDecisionAsync(Guid id, CancellationToken t = default) => inner.ReadDecisionAsync(id, t);

        public async ValueTask<IReadOnlyList<PartitionTerminalObservation>> ReadTerminalAsync(IReadOnlyList<Guid> ids,
            CancellationToken t = default, IReadOnlyDictionary<Guid, string>? selected = null)
        {
            var result = await inner.ReadTerminalAsync(ids, t, selected);
            if (Pause)
            {
                Pause = false;
                Observed.TrySetResult();
                await Resume.Task.WaitAsync(t);
            }
            return result;
        }

        public ValueTask<PartitionTransactionDecision> DecideAsync(Guid id, string d, bool commit, CancellationToken t = default) => inner.DecideAsync(id, d, commit, t);

        public ValueTask<PartitionTransactionDecision> AbortApplicationAsync(Guid id, string d, CancellationToken t = default) => inner.AbortApplicationAsync(id, d, t);

        public ValueTask RetainAsync(long floor, CancellationToken t = default) => inner.RetainAsync(floor, t);
    }
}
