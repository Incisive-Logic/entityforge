using EntityForge.Db.Durability;
using EntityForge.Db.Mapping;
using EntityForge.Db.Kernel;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [TestMethod]
    [DataRow("race")]
    [DataRow("lost-reply")]
    public async Task Native_local_binding_reuses_only_a_definite_winner(string outcome)
    {
        await WithReductionOwners(async (nodes, authority, client) =>
        {
            var request = await ConfinedSemanticRequest(client, nodes[0]);
            var measured = new DecliningCombinedAuthority(authority) { SemanticOutcome = "selected", NativeOutcome = outcome };
            var delivered = new PartitionApplicationClient(measured, nodes[0].Routing, nodes, ReadClock);
            if (outcome == "lost-reply")
            {
                await Assert.ThrowsAsync<IOException>(() => delivered.DeliverAsync(request.OperationId, request.Export()).AsTask());
                Assert.IsTrue((await authority.ReadApplicationAsync(request.OperationId))!.NativeLocal);
                Assert.IsNull(await nodes[0].Kernel.PartitionTransactions.ReadCoordinatorAsync(request.OperationId));
                measured.NativeOutcome = null;
            }
            var completion = await delivered.DeliverAsync(request.OperationId, request.Export());
            Assert.AreEqual(1, completion.Members.Count);
            Assert.AreEqual(1, measured.FallbackBindings);
            Assert.AreEqual(0, measured.CombinedCalls);
            Assert.IsNull(await authority.ReadPlanAsync(request.OperationId));
            Assert.IsNull(await authority.ReadDecisionAsync(request.OperationId));
        });
    }

    [TestMethod]
    public async Task Native_local_shared_clock_commit_and_abort_close_their_exact_tickets()
    {
        using var storage = new FileTimestampStore(Path.Combine(_root, "clock"), "native-local");
        var clock = new DatabaseSharedTimestampService(storage);
        await WithReductionOwners(async (nodes, authority, client) =>
        {
            var request = await ConfinedSemanticRequest(client, nodes[0]);
            await client.PrepareAsync(request.OperationId, [request.Export()], default);
            Assert.IsTrue((await authority.ReadApplicationAsync(request.OperationId))!.NativeLocal);
            var committed = await client.DeliverAsync(request.OperationId, request.Export());
            var repeated = await client.DeliverAsync(request.OperationId, request.Export());
            Assert.AreEqual(committed.Members.Single().Receipt.Version, repeated.Members.Single().Receipt.Version);
            Assert.AreEqual(DatabaseCommitOutcome.IdempotentReplay, repeated.Members.Single().Receipt.Outcome);
            var next = await ConfinedSemanticRequest(client, nodes[0]);
            await client.PrepareAsync(next.OperationId, [next.Export()], default);
            Assert.AreEqual(EntityOperationState.Aborted, (await client.AbortAsync(next.OperationId, default)).State);
            var state = (await clock.ObserveAsync()).State;
            Assert.AreEqual(state.Allocated, state.Closed);
            Assert.AreEqual(nodes[0].Kernel.CurrentVersion.Value, state.Positions[PartitionOwnedNode.TimestampParticipant(nodes[0].Domain.Identity)]);
            Assert.IsNull(await authority.ReadDecisionAsync(request.OperationId));
        }, clock);
    }

    [TestMethod]
    public async Task Native_local_abort_of_unprepared_stale_selection_does_not_require_fresh_planning()
    {
        await WithReductionOwners(async (nodes, authority, client) =>
        {
            var request = await ConfinedSemanticRequest(client, nodes[0]);
            var selection = (await PartitionSemanticPlanning.SelectAsync(client.Generation, request.OperationId,
                nodes[0].Routing, nodes, [request.Export()], default)).SelectNativeLocal();
            await authority.BindApplicationAsync(selection);
            // A different operation replaces this source after capture.
            var newer = await ConfinedSemanticRequest(client, nodes[0]);
            await client.DeliverAsync(newer.OperationId, newer.Export());
            Assert.IsNull(await nodes[0].Kernel.PartitionTransactions.ReadCoordinatorAsync(request.OperationId));
            Assert.AreEqual(EntityOperationState.Aborted, (await client.AbortAsync(request.OperationId, default)).State);
            await Assert.ThrowsAsync<EntityOperationAbortedException>(() => client.DeliverAsync(request.OperationId, request.Export()).AsTask());
            Assert.AreEqual(EntityOperationState.Committed, (await client.ResolveAsync(newer.OperationId, default)).State);
        });
    }

    [TestMethod]
    public async Task Native_local_selection_fences_external_decisions_and_persists_abort_across_reopen()
    {
        await WithReductionOwners(async (nodes, authority, client) =>
        {
            var request = await ConfinedSemanticRequest(client, nodes[0]);
            await client.PrepareAsync(request.OperationId, [request.Export()], default);
            var selection = (await authority.ReadApplicationAsync(request.OperationId))!;
            Assert.IsTrue(selection.NativeLocal);
            var entry = (await nodes[0].Kernel.PartitionTransactions.ReadCoordinatorAsync(request.OperationId))!;
            Assert.AreEqual(DurablePartitionTransactionState.Prepared, entry.State);
            var binary = PartitionSelectionCodec.DecodeApplication(PartitionSelectionCodec.EncodeApplication(selection));
            Assert.IsTrue(binary.NativeLocal);
            Assert.AreEqual(selection.Descriptor, binary.Descriptor);
            Assert.AreEqual(selection.Descriptor, selection.Export().Restore().Descriptor);
            Assert.ThrowsExactly<InvalidDataException>(() => (selection.Export() with { Version = 3 }).Restore());
            await Assert.ThrowsAsync<InvalidDataException>(() => authority.AbortApplicationAsync(request.OperationId, selection.Descriptor).AsTask());
            await Assert.ThrowsAsync<InvalidDataException>(() => authority.DecideAsync(request.OperationId, entry.Descriptor, true).AsTask());
            Assert.AreEqual(EntityOperationState.Aborted, (await client.AbortAsync(request.OperationId, default)).State);
            await Assert.ThrowsAsync<EntityOperationAbortedException>(() => client.DeliverAsync(request.OperationId, request.Export()).AsTask());
            var lease = nodes[0].CurrentLease;
            await nodes[0].DisposeAsync();
            await using var reopened = await PartitionOwnedNode.OpenAsync(Path.Combine(_root, "a"), DatabaseProfileId.Semantic,
                lease, nodes[0].Routing, new Authority { Lease = lease }, Options, transactionAuthority: authority, timestampClock: ReadClock);
            var recovered = new PartitionApplicationClient(authority, nodes[0].Routing, [reopened, .. nodes.Skip(1)], ReadClock);
            Assert.AreEqual(EntityOperationState.Aborted, (await recovered.ResolveAsync(request.OperationId, default)).State);
            await Assert.ThrowsAsync<EntityOperationAbortedException>(() => recovered.DeliverAsync(request.OperationId, request.Export()).AsTask());
            Assert.IsNull(await authority.ReadPlanAsync(request.OperationId));
            Assert.IsNull(await authority.ReadDecisionAsync(request.OperationId));
        });
    }

    [TestMethod]
    public async Task Native_local_preparation_reopens_and_maintenance_aborts_before_authority_expiry()
    {
        await WithReductionOwners(async (nodes, authority, client) =>
        {
            var request = await ConfinedSemanticRequest(client, nodes[0]);
            await client.PrepareAsync(request.OperationId, [request.Export()], default);
            var lease = nodes[0].CurrentLease;
            await nodes[0].DisposeAsync();
            await using var reopened = await PartitionOwnedNode.OpenAsync(Path.Combine(_root, "a"), DatabaseProfileId.Semantic,
                lease, nodes[0].Routing, new Authority { Lease = lease }, Options, transactionAuthority: authority, timestampClock: ReadClock);
            var all = new[] { reopened }.Concat(nodes.Skip(1)).ToArray();
            var recovered = new PartitionApplicationClient(authority, nodes[0].Routing, all, ReadClock);
            Assert.AreEqual(EntityOperationState.PlanBound, (await recovered.ResolveAsync(request.OperationId, default)).State);
            // Older cutoff retains the operation, allowing an independent terminal check.
            await PartitionRemoteRetention.RetainAsync(authority, all, nodes[0].Routing, DateTimeOffset.UtcNow.AddMinutes(-1));
            Assert.AreEqual(EntityOperationState.Aborted, (await recovered.ResolveAsync(request.OperationId, default)).State);
            Assert.IsFalse((await reopened.Kernel.PartitionTransactions.ReadAsync()).Any(e => e.State == DurablePartitionTransactionState.Prepared));
            await Assert.ThrowsAsync<EntityOperationAbortedException>(() => recovered.DeliverAsync(request.OperationId, request.Export()).AsTask());
        });
    }

    [TestMethod]
    public async Task Native_local_concurrent_delivery_and_abort_have_one_durable_winner()
    {
        await WithReductionOwners(async (nodes, authority, client) =>
        {
            var request = await ConfinedSemanticRequest(client, nodes[0]);
            await client.PrepareAsync(request.OperationId, [request.Export()], default);
            long before = nodes[0].Kernel.CurrentVersion.Value;
            async Task Attempt(Func<Task> work)
            {
                try
                {
                    await work();
                }
                catch (EntityOperationAbortedException)
                {

                }
            }
            await Task.WhenAll(Attempt(async () =>
{
    await client.DeliverAsync(request.OperationId, request.Export());
}),
                Attempt(async () =>
{
    await client.DeliverAsync(request.OperationId, request.Export());
}),
                Attempt(async () =>
{
    await client.AbortAsync(request.OperationId, default);
}));
            var final = await client.ResolveAsync(request.OperationId, default);
            Assert.IsTrue(final.State is EntityOperationState.Committed or EntityOperationState.Aborted);
            Assert.AreEqual(before + (final.State == EntityOperationState.Committed ? 1 : 0), nodes[0].Kernel.CurrentVersion.Value);
            Assert.AreEqual(final.State, (await client.AbortAsync(request.OperationId, default)).State);
            Assert.IsNull(await authority.ReadDecisionAsync(request.OperationId));
        });
    }
}
