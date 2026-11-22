using System.Diagnostics;
using EntityForge.Db.Mapping;
using EntityForge.Db.Semantic;
using EntityForge.Semantics.Protobuf;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [TestMethod]
    [DataRow("normal")]
    [DataRow("race")]
    [DataRow("lost-reply")]
    public async Task Semantic_declined_combined_selection_reuses_exact_planning_capture(string outcome)
    {
        await WithOrdinaryTransactions(DatabaseProfileId.Semantic, async (remote, client, nodes, authority) =>
        {
            var declining = new DecliningCombinedAuthority(authority) { SemanticOutcome = "declined", FallbackOutcome = outcome };
            declining.Release.TrySetResult();
            var application = new PartitionApplicationClient(declining, nodes[0].Routing, nodes, ReadClock);
            var transaction = remote.CreateTransaction();
            const int Entities = 64;
            for (int i = 0; i < Entities; i++)
            {
                transaction.Save(new Item("declining-" + i, new string('x', 128)));
            }

            int captures = 0;
            var previous = DistributedReadTelemetry.ObserveElapsedTicks;
            DistributedReadTelemetry.ObserveElapsedTicks = (name, ticks) =>
            {
                previous?.Invoke(name, ticks);
                if (name == "partition.semantic-planning.capture")
                {
                    Interlocked.Increment(ref captures);
                }
            };
            try
            {
                if (outcome == "lost-reply")
                {
                    await Assert.ThrowsExactlyAsync<IOException>(() => application.DeliverAsync(transaction.TransactionId, transaction.Capture()).AsTask());
                    Assert.IsNull(await authority.ReadDecisionAsync(transaction.TransactionId));
                    foreach (var node in nodes)
                    {
                        Assert.AreEqual(0, (await node.Kernel.PartitionTransactions.ReadAsync()).Count);
                    }
                }
                await application.DeliverAsync(transaction.TransactionId, transaction.Capture());
                Assert.AreEqual(1, declining.CombinedCalls);
                Assert.AreEqual(1, declining.FallbackBindings);
                Console.WriteLine($"Declined 64-entity selection: planning captures={captures}");
                Assert.AreEqual(Entities, captures, "A definite pre-submission decline must reuse its complete frozen planning proposal.");
                var selected = (await authority.ReadApplicationAsync(transaction.TransactionId))!;
                var plan = (await authority.ReadPlanAsync(transaction.TransactionId))!;
                PartitionSemanticPlanning.RequirePlan(selected, plan);
                long combinedBytes = (long)PartitionSelectionCodec.EncodeApplication(selected).Length + PartitionSelectionCodec.EncodePlan(plan).Length;
                Console.WriteLine($"Encoded combined selection={combinedBytes} bytes; production combined body limit={GrpcEtcdPartitionTransactionAuthority.MaximumEncodedPlanBytes / 2} bytes.");
                var replay = await application.DeliverAsync(transaction.TransactionId, transaction.Capture());
                Assert.IsTrue(replay.Members.All(member => member.Receipt.Outcome == DatabaseCommitOutcome.IdempotentReplay));
                Assert.AreEqual(Entities, captures, "Replay must not plan again.");
            }
            finally
            {
                DistributedReadTelemetry.ObserveElapsedTicks = previous;
            }
        });
    }

    [TestMethod]
    public async Task Semantic_reconstruction_phase_ledger_preserves_multi_entity_commits()
    {
        await WithOrdinaryTransactions(DatabaseProfileId.Semantic, async (remote, client, nodes, authority) =>
        {
            var stages = new Dictionary<string, (long Count, long Ticks)>();
            var previous = DistributedReadTelemetry.ObserveElapsedTicks;
            DistributedReadTelemetry.ObserveElapsedTicks = (name, ticks) =>
            {
                previous?.Invoke(name, ticks);
                if (!name.StartsWith("partition.semantic-reconstruct.", StringComparison.Ordinal))
                {
                    return;
                }

                lock (stages)
                {
                    var prior = stages.GetValueOrDefault(name);
                    stages[name] = (prior.Count + 1, prior.Ticks + ticks);
                }
            };
            try
            {
                const int Transactions = 16;
                const int Entities = 64;
                for (int transaction = 0; transaction < Transactions; transaction++)
                {
                    var write = remote.CreateTransaction();
                    for (int entity = 0; entity < Entities; entity++)
                    {
                        write.Save(new Item($"ledger-{transaction}-{entity}", "complete-value"));
                    }

                    await write.CommitAsync();
                }
                foreach (var stage in stages.OrderBy(stage => stage.Key))
                {
                    Console.WriteLine($"{stage.Key}: count={stage.Value.Count}, totalMs={stage.Value.Ticks * 1000.0 / Stopwatch.Frequency:F3}, meanMs={stage.Value.Ticks * 1000.0 / Stopwatch.Frequency / stage.Value.Count:F3}");
                }

                Assert.AreEqual(4, stages.Count);
                Assert.IsFalse(stages.ContainsKey("partition.semantic-reconstruct.expand"));
                Assert.IsFalse(stages.ContainsKey("partition.semantic-reconstruct.compare"));
                Assert.IsTrue(stages.Values.All(stage => stage.Count >= Transactions));
                Assert.AreEqual(1, stages.Values.Select(stage => stage.Count).Distinct().Count());
            }
            finally
            {
                DistributedReadTelemetry.ObserveElapsedTicks = previous;
            }
        });
    }

    [TestMethod]
    public async Task Semantic_compiled_delivery_preserves_wire_bytes_ownership_and_bounds_without_unused_envelope()
    {
        using var http = new HttpClient();
        var store = new HttpSemanticEntityClient(http, Guid.NewGuid()).EntityStore<Item>();
        var capture = store.CaptureSave(new("compiled-delivery", "complete-value"), SemanticEntityWriteOperation.Create());
        var original = await PartitionSemanticPlanning.OriginalTypedAsync(capture);
        var metadata = original.Metadata;
        var session = original.Session;
        var limits = new SemanticEntityStoreLimits(metadata.Entities, metadata.Depth, metadata.Bytes);
        var codec = new SemanticChangeSessionTransportCodec();
        byte[][] parts = [codec.Serialize(session.Start), codec.Serialize(session.Pages.Single()), codec.Serialize(session.Close)];
        SemanticEntityWrite Previous() => new(metadata.DatabaseIncarnation, metadata.Manifests, session, limits, retainCompiled: true);
        SemanticEntityWrite Current() => new(metadata.DatabaseIncarnation, metadata.Manifests, session, limits, parts, retainCompiled: true);
        await using var old = Previous();
        await using var current = Current();
        CollectionAssert.AreEqual(old.Export(), current.Export());
        Assert.AreEqual(old.OperationId, current.OperationId);
        Assert.AreEqual(old.ResultingPosition, current.ResultingPosition);
        Assert.AreEqual(old.Compiled!.Value.Metadata, current.Compiled!.Value.Metadata);
        CollectionAssert.AreEqual(old.Export(), SemanticEntityWrite.Restore(current.Export()).Export());

        const int Iterations = 256;
        for (int i = 0; i < 8; i++)
        {
            await using var a = Previous();
            await using var b = Current();
        }
        (long Bytes, double Milliseconds) Measure(Func<SemanticEntityWrite> create)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            var watch = Stopwatch.StartNew();
            for (int i = 0; i < Iterations; i++)
            {
                var write = create();
                Assert.IsNotNull(write.Compiled);
            }
            return (GC.GetAllocatedBytesForCurrentThread() - before, watch.Elapsed.TotalMilliseconds);
        }
        var previous = Measure(Previous);
        var candidate = Measure(Current);
        Console.WriteLine($"Compiled delivery {Iterations} calls: previous={previous.Bytes} bytes/{previous.Milliseconds:F3}ms; current={candidate.Bytes} bytes/{candidate.Milliseconds:F3}ms");
        Assert.IsTrue(candidate.Bytes < previous.Bytes, "Typed delivery must avoid the unused payload and envelope allocations.");
        var expected = current.Export();
        foreach (var part in parts)
        {
            Array.Fill(part, (byte)0);
        }

        CollectionAssert.AreEqual(expected, current.Export(), "Serialized scratch bytes must not back the retained typed session.");
        var exported = current.Export();
        Array.Fill(exported, (byte)0);
        CollectionAssert.AreEqual(expected, current.Export(), "Export must return owned bytes.");
        EntityRecordDeliveryCodec.ValidateBounds(metadata, EntityRecordDeliveryCodec.MaximumPayloadBytes);
        Assert.ThrowsExactly<InvalidDataException>(() => EntityRecordDeliveryCodec.ValidateBounds(metadata, 0));
        Assert.ThrowsExactly<InvalidDataException>(() => EntityRecordDeliveryCodec.ValidateBounds(metadata, -1));
        Assert.ThrowsExactly<InvalidDataException>(() => EntityRecordDeliveryCodec.ValidateBounds(metadata, (long)EntityRecordDeliveryCodec.MaximumPayloadBytes + 1));
    }
}
