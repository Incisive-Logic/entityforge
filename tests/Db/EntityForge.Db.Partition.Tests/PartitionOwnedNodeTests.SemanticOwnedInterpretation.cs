using EntityForge.Db.Semantic;
using EntityForge.Db.Mapping;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [TestMethod]
    public async Task Request_interpretation_is_owned_scoped_and_checks_every_association()
    {
        using var http = new HttpClient();
        Guid database = Guid.NewGuid();
        var operation = SemanticEntityWriteOperation.Create();
        var captured = new HttpSemanticEntityClient(http, database).EntityStore<Item>()
            .CaptureSave(new("root", "value"), operation).Export();
        SemanticEntityRequest first;
        using (new PartitionSemanticRequests.Scope())
        {
            first = PartitionSemanticRequests.Restore(captured);
            await Task.Yield();
            Assert.AreSame(first, PartitionSemanticRequests.Restore(captured.ToArray()));
            PartitionSemanticRequests.Validate(database, operation.OperationId, [captured]);
            Assert.Throws<InvalidDataException>(() => PartitionSemanticRequests.Validate(Guid.NewGuid(), operation.OperationId, [captured]));
            Assert.Throws<InvalidDataException>(() => PartitionSemanticRequests.Validate(database, Guid.NewGuid(), [captured]));
            using (new PartitionSemanticRequests.Scope())
            {
                Assert.AreNotSame(first, PartitionSemanticRequests.Restore(captured));
            }

            Assert.AreSame(first, PartitionSemanticRequests.Restore(captured));
            captured[^1] ^= 1;
            Assert.Throws<InvalidDataException>(() => PartitionSemanticRequests.Restore(captured));
            captured[^1] ^= 1;
            Assert.AreSame(first, PartitionSemanticRequests.Restore(captured));
            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
                Assert.AreSame(first, PartitionSemanticRequests.Restore(captured)))));
        }
        Assert.AreNotSame(first, PartitionSemanticRequests.Restore(captured));
    }

    [TestMethod]
    public async Task Closed_submission_scope_allows_draining_but_cannot_retain_interpretations()
    {
        using var http = new HttpClient();
        var captured = new HttpSemanticEntityClient(http, Guid.NewGuid()).EntityStore<Item>()
            .CaptureSave(new("root", "value"), SemanticEntityWriteOperation.Create()).Export();
        var scope = new PartitionSemanticPlanning.InterpretationScope();
        var first = PartitionSemanticRequests.Restore(captured);
        scope.Dispose();
        await Task.Run(() =>
        {
            using var activation = scope.Activate();
            var next = PartitionSemanticRequests.Restore(captured);
            Assert.AreNotSame(first, next);
            Assert.AreNotSame(next, PartitionSemanticRequests.Restore(captured));
        });
    }

    [TestMethod]
    public void Restored_compilation_identity_is_exact_without_metadata_reserialization()
    {
        using var http = new HttpClient();
        var captured = new HttpSemanticEntityClient(http, Guid.NewGuid()).EntityStore<BinaryOriginal>()
            .CaptureSave(new("root", [1, 2, 3]), SemanticEntityWriteOperation.Create());
        var restored = SemanticEntityRequest.Restore(captured.Export());
        byte[] expected = EntityRecordDeliveryCodec.Encode(restored._metadata with
        {
            DatabaseIncarnation = Guid.Empty
        }, restored._payload);
        CollectionAssert.AreEqual(expected, restored.CompilationIdentity().ToArray());
        CollectionAssert.AreEqual(expected, restored.ForDatabase(Guid.NewGuid()).CompilationIdentity().ToArray());
        Assert.IsFalse(expected.AsSpan().SequenceEqual(restored.ForOperation(Guid.NewGuid()).CompilationIdentity().Span));
        const int Repetitions = 64;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < Repetitions; i++)
        {
            _ = EntityRecordDeliveryCodec.Encode(restored._metadata with
            {
                DatabaseIncarnation = Guid.Empty
            }, restored._payload);
        }

        long previous = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < Repetitions; i++)
        {
            _ = restored.CompilationIdentity();
        }

        long current = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"Compilation identity repeated {Repetitions}: prior={previous}, current={current} allocated bytes; isolated work, not live speed.");
        Assert.IsTrue(current < previous);
    }

    [TestMethod]
    public async Task Owner_group_proposal_keeps_validation_until_selection_and_rejects_mutation()
    {
        await WithOrdinaryTransactions(DatabaseProfileId.Semantic, async (remote, client, nodes, authority) =>
        {
            var transaction = remote.CreateTransaction();
            for (int i = 0; i < 64; i++)
            {
                transaction.Save(new Item("owned-proposal-" + i, "value-" + i));
            }

            PartitionApplicationBinding binding;
            using (new PartitionSemanticPlanning.InterpretationScope())
            {
                binding = await PartitionSemanticPlanning.SelectOwnerOrDistributedAsync(client.Generation, transaction.TransactionId,
                    nodes[0].Routing, nodes, transaction.Capture(), default);
                int interpretations = 0;
                var previous = DistributedReadTelemetry.ObserveElapsedTicks;
                DistributedReadTelemetry.ObserveElapsedTicks = (stage, ticks) =>
                {
                    previous?.Invoke(stage, ticks);
                    if (stage == "partition.semantic-planning.interpret-evidence")
                    {
                        Interlocked.Increment(ref interpretations);
                    }
                };
                try
                {
                    var plan = PartitionSemanticPlanning.Plan(binding, nodes[0].Routing, nodes.Select(n => n.Domain).ToArray());
                    PartitionSemanticPlanning.RequirePlan(binding, plan);
                    Assert.AreEqual(0, interpretations, "Already validated proposals should not be decoded again locally.");
                    Assert.IsTrue(System.Runtime.InteropServices.MemoryMarshal.TryGetArray(binding.PlanningEvidence, out var bytes));
                    int last = bytes.Offset + bytes.Count - 1;
                    bytes.Array![last] ^= 1;
                    try
                    {
                        Assert.Throws<InvalidDataException>(() => PartitionSemanticPlanning.ReadValidated(binding, nodes[0].Routing));
                    }
                    finally
                    {
                        bytes.Array[last] ^= 1;
                    }
                }
                finally
                {
                    DistributedReadTelemetry.ObserveElapsedTicks = previous;
                }
            }
            // Transport/retention restore carries no local validation certificate.
            var independent = binding.Export().Restore();
            var evidence = PartitionSemanticPlanning.ReadValidated(independent, nodes[0].Routing);
            Assert.AreEqual(nodes.Length, evidence.Expansions.Length);
        });
    }
}
