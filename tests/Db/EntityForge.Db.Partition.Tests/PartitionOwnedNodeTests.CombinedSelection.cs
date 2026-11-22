using EntityForge.Db.Graph;
using EntityForge.Db.Rdf;
using EntityForge.Db.Semantic;
using EntityForge.Db.Distributed.Hosting;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [TestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph)]
    [DataRow(DatabaseProfileId.Rdf)]
    public async Task Unselected_proposals_do_not_bind_prepare_or_mutate_application_data(DatabaseProfileId profile)
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
        await using var decisions = FilePartitionTransactionAuthority.Open(Path.Combine(_root, "authority"));
        await using var first = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "a"), profile, a.Lease, routing, a, Options, transactionAuthority: decisions, timestampClock: ReadClock);
        await using var second = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "b"), profile, b.Lease, routing, b, Options, transactionAuthority: decisions, timestampClock: ReadClock);
        using var http = new HttpClient();
        var operation = EntityOperationIds.New();
        byte[] Capture(string id) => profile == DatabaseProfileId.PropertyGraph
            ? new HttpGraphEntityClient(http, first.Domain.Generation).EntityStore<Item>().CaptureSave(new(id, "proposal"), new GraphEntityWriteOptions(operation)).Export()
            : new HttpRdfEntityClient(http, first.Domain.Generation).EntityStore<Item>().CaptureSave(new(id, "proposal"), new RdfEntityWriteOptions(operation)).Export();
        ReadOnlyMemory<byte> Request(string owner)
        {
            for (int i = 0; ; i++)
            {
                var request = Capture(i.ToString());
                if (PartitionApplicationRouting.Owner(first.Domain.Generation, profile, routing, operation, request).Value == owner)
                {
                    return request;
                }
            }
        }
        var binding = new PartitionApplicationBinding(operation, [first.Domain.Identity, second.Domain.Identity], [Request("a"), Request("b")]);
        var proposals = await Task.WhenAll(first.ProposeApplicationAsync(binding).AsTask(), second.ProposeApplicationAsync(binding).AsTask());
        Assert.IsNull(await decisions.ReadApplicationAsync(operation));
        Assert.IsNull(await decisions.ReadPlanAsync(operation));
        Assert.IsNull(await decisions.ReadDecisionAsync(operation));
        Assert.AreEqual(0, (await first.Kernel.PartitionTransactions.ReadAsync()).Count);
        Assert.AreEqual(0, (await second.Kernel.PartitionTransactions.ReadAsync()).Count);
        // Only after selecting the exact pair may native preparation begin.
        await decisions.BindApplicationAsync(binding);
        var plan = new PartitionTransactionPlan(operation, routing.MapVersion, proposals);
        var completion = await new PartitionTransactionCoordinator(decisions).ExecuteAsync(plan, [first, second]);
        Assert.IsTrue(completion.Decision.Commit);
        Assert.AreEqual(2, completion.Members.Count);
        // A bounded capability may decline after planning. The fallback must not
        // recapture mutable caller buffers after the selection await.
        operation = EntityOperationIds.New();
        var buffers = new[] { Request("a").ToArray(), Request("b").ToArray() };
        var expected = buffers.Select(bytes => bytes.ToArray()).ToArray();
        var declined = new DecliningCombinedAuthority(decisions);
        var application = new PartitionApplicationClient(declined, routing, [first, second], ReadClock);
        var delivery = application.DeliverAsync(operation, buffers.Select(bytes => (ReadOnlyMemory<byte>)bytes).ToArray()).AsTask();
        try
        {
            await declined.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            foreach (var buffer in buffers)
            {
                Array.Clear(buffer);
            }
        }
        finally
        {
            declined.Release.TrySetResult();
        }
        Assert.AreEqual(2, (await delivery).Members.Count);
        var selected = (await decisions.ReadApplicationAsync(operation))!;
        for (int i = 0; i < expected.Length; i++)
        {
            CollectionAssert.AreEqual(expected[i], selected.Requests[i].ToArray());
        }
    }

    [TestMethod]
    [DataRow("selected")]
    [DataRow("declined")]
    [DataRow("lost-reply")]
    [DataRow("race")]
    public async Task Semantic_combined_selection_preserves_native_validation_replay_and_uncertain_outcomes(string outcome)
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
        await using var decisions = FilePartitionTransactionAuthority.Open(Path.Combine(_root, "authority"));
        var measured = new DecliningCombinedAuthority(decisions) { SemanticOutcome = outcome };
        measured.Release.TrySetResult();
        await using var first = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "a"), DatabaseProfileId.Semantic, a.Lease, routing, a, Options, transactionAuthority: measured, timestampClock: ReadClock);
        await using var second = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "b"), DatabaseProfileId.Semantic, b.Lease, routing, b, Options, transactionAuthority: measured, timestampClock: ReadClock);
        using var http = new HttpClient();
        var client = new PartitionApplicationClient(measured, routing, [first, second], ReadClock);
        var store = new HttpSemanticEntityClient(http, client.Generation).EntityStore<PartitionSemanticTree>();
        // Exercise combined selection with a genuinely multi-owner request. A
        // confined Semantic request now selects native local execution instead.
        string key = await CrossOwnerSemanticKeyAsync(first);
        var captured = store.CaptureSave(new()
        {
            Id = key,
            Name = "before",
            Children = Enumerable.Range(0, 8).Select(i => new PartitionSemanticLeaf("child-" + i, "value")).ToList()
        }, SemanticEntityWriteOperation.Create());
        if (outcome == "lost-reply")
        {
            await Assert.ThrowsExactlyAsync<IOException>(() => client.DeliverAsync(captured.OperationId, captured.Export()).AsTask());
            Assert.IsNull(await decisions.ReadDecisionAsync(captured.OperationId), "Unknown selection must not execute or fall back.");
            Assert.AreEqual(0, (await first.Kernel.PartitionTransactions.ReadAsync()).Count);
            Assert.AreEqual(0, (await second.Kernel.PartitionTransactions.ReadAsync()).Count);
        }
        var result = await client.DeliverAsync(captured.OperationId, captured.Export());
        Assert.AreEqual(2, result.Members.Count);
        Assert.AreEqual(1, measured.CombinedCalls);
        var selected = (await decisions.ReadApplicationAsync(captured.OperationId))!;
        var plan = (await decisions.ReadPlanAsync(captured.OperationId))!;
        PartitionSemanticPlanning.RequirePlan(selected, plan);
        var rawApplication = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(selected.Export());
        var rawPlan = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(FilePartitionTransactionAuthority.StoredPlan.From(plan));
        int storedBytes = 0;
        foreach (var raw in new[] { rawApplication, rawPlan })
        {
            var encoded = PartitionSelectionStorage.Encode(raw);
            CollectionAssert.AreEqual(raw, PartitionSelectionStorage.Decode(encoded));
            storedBytes += encoded.Length;
            Console.WriteLine($"Selection storage: {raw.Length} -> {encoded.Length} bytes");
        }

        Assert.IsTrue(storedBytes * 5 < (rawApplication.Length + rawPlan.Length) * 3, "Total actual Semantic selection should materially shrink without omitted state.");
        var wrong = new PartitionTransactionPlan(plan.Operation, routing.MapVersion,
            plan.Members.Select(m => new PartitionTransactionMember(m.Domain, m.ReadVersion, new byte[] { 1 })).ToArray());
        Assert.ThrowsExactly<InvalidDataException>(() => PartitionSemanticPlanning.RequirePlan(selected, wrong));
        var replay = await client.DeliverAsync(captured.OperationId, captured.Export());
        Assert.IsTrue(replay.Members.All(m => m.Receipt.Outcome == DatabaseCommitOutcome.IdempotentReplay));
        Assert.AreEqual(outcome == "declined" ? 1 : 0, measured.FallbackBindings);
        var changed = store.CaptureSave(new()
        {
            Id = key,
            Name = "after",
            Children = Enumerable.Range(0, 8).Select(i => new PartitionSemanticLeaf("child-" + i, "value")).ToList()
        }, SemanticEntityWriteOperation.Create());
        measured.SemanticOutcome = "selected";
        await client.DeliverAsync(changed.OperationId, changed.Export());
        Assert.AreEqual(2, measured.CombinedCalls);
    }

    private sealed class DecliningCombinedAuthority(IPartitionTransactionAuthority inner) : IPartitionTransactionAuthority, IPartitionCombinedSelectionAuthority
    {
        internal string? SemanticOutcome
        {
            get; set;
        }

        internal string? NativeOutcome
        {
            get; set;
        }

        internal string? FallbackOutcome
        {
            get; set;
        }

        public bool SupportsSemanticEvidence => SemanticOutcome is not null;

        internal int CombinedCalls, FallbackBindings;

        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Identity => inner.Identity;

        public async ValueTask<PartitionTransactionPlan?> SelectCombinedAsync(PartitionApplicationBinding application, PartitionTransactionPlan proposal, CancellationToken token = default)
        {
            CombinedCalls++;
            Entered.TrySetResult();
            await Release.Task.WaitAsync(token);
            if (SemanticOutcome is null or "declined")
            {
                return null;
            }

            PartitionSemanticPlanning.RequirePlan(application, proposal);
            await inner.BindApplicationAsync(application, token);
            await inner.BindAsync(proposal, token);
            if (SemanticOutcome == "lost-reply")
            {
                throw new IOException("Selected pair reply lost.");
            }

            if (SemanticOutcome == "race")
            {
                throw new EntityForgeDbIdempotencyException(application.Operation);
            }

            return proposal;
        }

        public ValueTask RequireUnenrolledAsync(PartitionAuthorityEnrollment partition, CancellationToken token = default) => inner.RequireUnenrolledAsync(partition, token);

        public ValueTask EnrollAsync(PartitionExecutionDomain domain, bool create, CancellationToken token = default) => inner.EnrollAsync(domain, create, token);

        public async ValueTask BindApplicationAsync(PartitionApplicationBinding binding, CancellationToken token = default)
        {
            FallbackBindings++;
            await inner.BindApplicationAsync(binding, token);
            if (FallbackOutcome == "race")
            {
                throw new EntityForgeDbIdempotencyException(binding.Operation);
            }

            if (FallbackOutcome == "lost-reply")
            {
                throw new IOException("Fallback application selection reply lost.");
            }

            if (binding.NativeLocal && NativeOutcome == "race")
            {
                throw new EntityForgeDbIdempotencyException(binding.Operation);
            }

            if (binding.NativeLocal && NativeOutcome == "lost-reply")
            {
                throw new IOException("Native selection reply lost.");
            }
        }

        public ValueTask<PartitionApplicationBinding?> ReadApplicationAsync(Guid operation, CancellationToken token = default) => inner.ReadApplicationAsync(operation, token);

        public ValueTask<PartitionTransactionDecision> AbortApplicationAsync(Guid operation, string descriptor, CancellationToken token = default) => inner.AbortApplicationAsync(operation, descriptor, token);

        public ValueTask BindAsync(PartitionTransactionPlan plan, CancellationToken token = default) => inner.BindAsync(plan, token);

        public ValueTask<PartitionTransactionPlan?> ReadPlanAsync(Guid operation, CancellationToken token = default) => inner.ReadPlanAsync(operation, token);

        public ValueTask<PartitionTransactionDecision?> ReadDecisionAsync(Guid operation, CancellationToken token = default) => inner.ReadDecisionAsync(operation, token);

        public ValueTask<PartitionTransactionDecision> DecideAsync(Guid operation, string descriptor, bool commit, CancellationToken token = default) => inner.DecideAsync(operation, descriptor, commit, token);

        public ValueTask<PartitionTransactionDecision> DecideTimestampedAsync(Guid operation, string descriptor,
            EntityForge.Db.Durability.DurableTimestampState timestamp, CancellationToken token = default) =>
            inner.DecideTimestampedAsync(operation, descriptor, timestamp, token);

        public ValueTask RetainAsync(long cutoff, CancellationToken token = default) => inner.RetainAsync(cutoff, token);
    }
}
