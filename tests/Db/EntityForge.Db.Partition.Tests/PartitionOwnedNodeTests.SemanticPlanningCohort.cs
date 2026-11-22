using System.Diagnostics;
using EntityForge.Db.Semantic;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [TestMethod]
    public async Task Semantic_planning_failure_drains_started_owner_reads_before_releasing_cut()
    {
        await WithReductionOwners(async (nodes, authority, client) =>
        {
            var request = await ConfinedSemanticRequest(client, nodes[1]);
            var companion = (await ConfinedSemanticRequest(client, nodes[0])).ForOperation(request.OperationId);
            var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int entered = 0;
            var peers = nodes.Select((owner, index) => new PhaseParticipant(owner)
            {
                BeforeRead = async (query, token) =>
                {
                    if (query.Kind != "semantic-expansion")
                    {
                        return;
                    }

                    if (Interlocked.Increment(ref entered) == 2)
                    {
                        arrived.TrySetResult();
                    }

                    await arrived.Task.WaitAsync(token);
                    if (index == 0)
                    {
                        failed.TrySetResult();
                        throw new IOException("Injected owner projection failure.");
                    }
                    await release.Task.WaitAsync(token);
                }
            }).ToArray();
            var running = PartitionSemanticPlanning.SelectCohortAsync(client.Generation, nodes[0].Routing,
                peers, [new(request.OperationId, [request.Export(), companion.Export()])], default).AsTask();
            try
            {
                await failed.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.IsFalse(running.IsCompleted);
                Assert.IsTrue(nodes.All(node => node.UnreleasedReadCutCount > 0));
            }
            finally
            {
                release.TrySetResult();
            }
            Assert.IsInstanceOfType<IOException>((await running).Single().Failure);
            Assert.IsTrue(nodes.All(node => node.UnreleasedReadCutCount == 0));
            var later = await PartitionSemanticPlanning.SelectCohortAsync(client.Generation, nodes[0].Routing,
                nodes, [new(request.OperationId, [request.Export(), companion.Export()])], default);
            Assert.IsNull(later.Single().Failure);
            Assert.IsNull(await authority.ReadApplicationAsync(request.OperationId));
        });
    }

    [TestMethod]
    public async Task Semantic_planning_cohort_shares_one_cut_with_exact_independent_evidence_and_commits()
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
        await using var first = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "a"), DatabaseProfileId.Semantic, a.Lease, routing, a, Options, transactionAuthority: decisions, timestampClock: ReadClock);
        await using var second = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "b"), DatabaseProfileId.Semantic, b.Lease, routing, b, Options, transactionAuthority: decisions, timestampClock: ReadClock);
        IPartitionTransactionParticipant[] owners = [first, second];
        using var http = new HttpClient();
        var store = new HttpSemanticEntityClient(http, first.Domain.Generation).EntityStore<Item>();
        var writes = Enumerable.Range(0, 8).Select(i =>
        {
            var capture = store.CaptureSave(new(i.ToString(), "value-" + i), SemanticEntityWriteOperation.Create());
            return new PartitionIndependentWrite(capture.OperationId, [capture.Export()]);
        }).ToArray();
        // Cohort projection must dispatch independent owners together. A gate,
        // rather than a stopwatch threshold, makes serial dispatch fail clearly.
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int entered = 0;
        var peers = owners.Select(owner => new PhaseParticipant(owner)
        {
            BeforeRead = async (query, token) =>
            {
                if (query.Kind != "semantic-expansion")
                {
                    return;
                }

                if (Interlocked.Increment(ref entered) == owners.Length)
                {
                    arrived.TrySetResult();
                }

                await release.Task.WaitAsync(token);
            }
        }).ToArray();
        var barrierRequest = await ConfinedSemanticRequest(new(decisions, routing, owners, ReadClock), second);
        var barrierCompanion = (await ConfinedSemanticRequest(new(decisions, routing, owners, ReadClock), first)).ForOperation(barrierRequest.OperationId);
        var parallel = PartitionSemanticPlanning.SelectCohortAsync(first.Domain.Generation, routing,
            peers, [new(barrierRequest.OperationId, [barrierRequest.Export(), barrierCompanion.Export()])], default).AsTask();
        try
        {
            await arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            release.TrySetResult();
        }
        Assert.IsNull((await parallel)[0].Failure);
        Assert.AreEqual(owners.Length, entered);
        Assert.IsTrue(new[] { first, second }.All(owner => owner.UnreleasedReadCutCount == 0));
        int reads = 0;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == DistributedReadTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = activity =>
{
    if (activity.OperationName == "partition.read.complete")
    {
        Interlocked.Increment(ref reads);
    }

}
        };
        ActivitySource.AddActivityListener(listener);
        var scalar = new List<PartitionApplicationBinding>();
        foreach (var write in writes)
        {
            scalar.Add(await PartitionSemanticPlanning.SelectAsync(first.Domain.Generation, write.Operation, routing, owners, write.Requests, default));
        }

        Assert.AreEqual(8, reads);
        reads = 0;
        var cohort = await PartitionSemanticPlanning.SelectCohortAsync(first.Domain.Generation, routing, owners, writes, default);
        Assert.AreEqual(1, reads, "Eight exact captures must share one complete read-cut lifecycle.");
        for (int i = 0; i < writes.Length; i++)
        {
            Assert.IsNull(cohort[i].Failure);
            var encoded = cohort[i].Binding!.PlanningEvidence.ToArray();
            CollectionAssert.AreEqual(encoded, PartitionSelectionCodec.EncodeEvidence(PartitionSelectionCodec.DecodeEvidence(encoded)));
            var corrupt = encoded.ToArray();
            corrupt[^1] ^= 1;
            Assert.Throws<InvalidDataException>(() => PartitionSelectionCodec.DecodeEvidence(corrupt));
            // The initial-projection fast path and an ordinary read encode JSON
            // escaping differently. Compare the entire decoded envelope, while
            // keeping expanded native payload bytes and positions exact.
            byte[] CanonicalEvidence(PartitionApplicationBinding binding)
            {
                var evidence = PartitionSemanticPlanning.Read(binding, routing);
                return System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(evidence with
                {
                    Expansions = evidence.Expansions.Select(e => e with
                    {
                        Envelope = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
                            PartitionSemanticEnvelopeCodec.Decode(e.Envelope))
                    }).ToArray()
                });
            }
            CollectionAssert.AreEqual(CanonicalEvidence(scalar[i]), CanonicalEvidence(cohort[i].Binding!));
            CollectionAssert.AreEqual(scalar[i].Requests[0].ToArray(), cohort[i].Binding!.Requests[0].ToArray());
            CollectionAssert.AreEqual(scalar[i].Owners.ToArray(), cohort[i].Binding!.Owners.ToArray());
            Assert.AreEqual(PartitionSemanticPlanning.Plan(scalar[i], routing, owners.Select(o => o.Domain).ToArray()).Descriptor,
                PartitionSemanticPlanning.Plan(cohort[i].Binding!, routing, owners.Select(o => o.Domain).ToArray()).Descriptor);
        }
        // A malformed capture must not erase the independently valid neighbour.
        var mixed = await PartitionSemanticPlanning.SelectCohortAsync(first.Domain.Generation, routing, owners,
            [new(EntityOperationIds.New(), [new byte[] { 1 }]), writes[0]], default);
        Assert.IsNotNull(mixed[0].Failure);
        Assert.IsNull(mixed[1].Failure);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var cancelledResults = await PartitionSemanticPlanning.SelectCohortAsync(first.Domain.Generation, routing, owners, writes, cancelled.Token);
        Assert.IsTrue(cancelledResults.All(r => r.Binding is null && r.Failure is OperationCanceledException));
        // Separate decisions and receipts: no merge into an atomic eight-entity write.
        foreach (var result in cohort)
        {
            var binding = result.Binding!;
            var plan = PartitionSemanticPlanning.Plan(binding, routing, owners.Select(o => o.Domain).ToArray());
            await decisions.BindApplicationAsync(binding);
            await decisions.BindAsync(plan);
            var completion = await new PartitionTransactionCoordinator(decisions).ExecuteSelectedAsync(plan, owners.Where(o => plan.Members.Any(m => m.Domain == o.Domain)).ToArray(), default);
            Assert.IsTrue(completion.Decision.Commit);
            Assert.AreEqual(binding.Operation, completion.Decision.Operation);
            var replay = await new PartitionTransactionCoordinator(decisions).ExecuteSelectedAsync(plan, owners.Where(o => plan.Members.Any(m => m.Domain == o.Domain)).ToArray(), default);
            Assert.IsTrue(replay.Members.All(m => m.Receipt.Outcome == DatabaseCommitOutcome.IdempotentReplay));
        }
        var measured = new DecliningCombinedAuthority(decisions) { SemanticOutcome = "selected" };
        measured.Release.TrySetResult();
        var client = new PartitionApplicationClient(measured, routing, owners, ReadClock);
        // The production queue may share planning but must return eight distinct completions.
        var updates = Enumerable.Range(0, 8).Select(i => store.CaptureSave(new(i.ToString(), "changed-" + i), SemanticEntityWriteOperation.Create())).ToArray();
        var completions = await Task.WhenAll(updates.Select(c => client.DeliverAsync(c.OperationId, c.Export()).AsTask()));
        Assert.AreEqual(8, completions.Select(c => c.OperationId).Distinct().Count());
        var conflicts = new[] { "conflict-first", "conflict-second" }.Select(value =>
        {
            var c = store.CaptureSave(new("0", value), SemanticEntityWriteOperation.Create());
            return new PartitionIndependentWrite(c.OperationId, [c.Export()]);
        }).ToArray();
        var conflictingPlans = await PartitionSemanticPlanning.SelectCohortAsync(first.Domain.Generation, routing, owners, conflicts, default);
        for (int i = 0; i < conflictingPlans.Length; i++)
        {
            var binding = conflictingPlans[i].Binding!;
            var plan = PartitionSemanticPlanning.Plan(binding, routing, owners.Select(o => o.Domain).ToArray());
            await decisions.BindApplicationAsync(binding);
            await decisions.BindAsync(plan);
            var result = await new PartitionTransactionCoordinator(decisions).ExecuteSelectedAsync(plan, owners.Where(o => plan.Members.Any(m => m.Domain == o.Domain)).ToArray(), default);
            Assert.AreEqual(i == 0, result.Decision.Commit, "Sharing a cut must not admit a stale overlapping source write.");
        }
        var later = store.CaptureSave(new("0", "later-success"), SemanticEntityWriteOperation.Create());
        await client.DeliverAsync(later.OperationId, later.Export());
        var never = store.CaptureSave(new("never", "cancelled"), SemanticEntityWriteOperation.Create());
        await Assert.ThrowsAsync<OperationCanceledException>(() => client.DeliverAsync(never.OperationId, never.Export(), cancelled.Token).AsTask());
        Assert.IsNull(await decisions.ReadApplicationAsync(never.OperationId));

    }
}
