using EntityForge.Db.Semantic;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [TestMethod]
    public async Task Semantic_proposal_interpretation_is_scoped_and_rejects_changed_evidence()
    {
        await WithOrdinaryTransactions(DatabaseProfileId.Semantic, async (remote, client, nodes, authority) =>
        {
            var transaction = remote.CreateTransaction();
            for (int i = 0; i < 64; i++)
            {
                transaction.Save(new Item("typed-evidence-" + i, "value"));
            }

            var binding = await PartitionSemanticPlanning.SelectAsync(client.Generation, transaction.TransactionId,
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
                using (new PartitionSemanticPlanning.InterpretationScope())
                {
                    var plan = PartitionSemanticPlanning.Plan(binding, nodes[0].Routing, nodes.Select(n => n.Domain).ToArray());
                    PartitionSemanticPlanning.RequirePlan(binding, plan);
                    Assert.AreEqual(1, interpretations);
                    Assert.IsTrue(System.Runtime.InteropServices.MemoryMarshal.TryGetArray(binding.PlanningEvidence, out var bytes));
                    int last = bytes.Offset + bytes.Count - 1;
                    byte original = bytes.Array![last];
                    bytes.Array[last] ^= 1;
                    try
                    {
                        Assert.Throws<InvalidDataException>(() => PartitionSemanticPlanning.ReadValidated(binding, nodes[0].Routing));
                    }
                    finally
                    {
                        bytes.Array[last] = original;
                    }
                }
                interpretations = 0;
                PartitionSemanticPlanning.ReadValidated(binding, nodes[0].Routing);
                Assert.AreEqual(1, interpretations, "No interpretation may survive its proposal invocation.");
            }
            finally
            {
                DistributedReadTelemetry.ObserveElapsedTicks = previous;
            }
        });
    }

    [TestMethod]
    public async Task Semantic_discovery_compiles_each_capture_once_and_decodes_each_unchanged_owner_once()
    {
        await WithOrdinaryTransactions(DatabaseProfileId.Semantic, async (remote, client, nodes, authority) =>
        {
            var transaction = remote.CreateTransaction();
            for (int i = 0; i < 64; i++)
            {
                transaction.Save(new Item("planning-reuse-" + i, "value-" + i));
            }

            var requests = transaction.Capture();
            int captures = 0, decodes = 0, compilations = 0;
            var previous = DistributedReadTelemetry.ObserveElapsedTicks;
            DistributedReadTelemetry.ObserveElapsedTicks = (stage, ticks) =>
            {
                previous?.Invoke(stage, ticks);
                if (stage == "partition.semantic-planning.compile-original")
                {
                    Interlocked.Increment(ref compilations);
                }

                if (stage == "partition.semantic-planning.capture")
                {
                    Interlocked.Increment(ref captures);
                }

                if (stage == "partition.semantic-planning.decode-expansion")
                {
                    Interlocked.Increment(ref decodes);
                }
            };
            try
            {
                var cohort = (await PartitionSemanticPlanning.SelectCohortAsync(client.Generation, nodes[0].Routing,
                    nodes, [new(transaction.TransactionId, requests)], default)).Single();
                Assert.IsNull(cohort.Failure);
                Assert.AreEqual(64, captures);
                Assert.AreEqual(64, compilations, "Pure input lowering is shared; owner expansion and mapping admission still validate fresh native state.");
                Assert.AreEqual(nodes.Length, decodes);
                captures = decodes = compilations = 0;
                var scalar = await PartitionSemanticPlanning.SelectAsync(client.Generation, transaction.TransactionId,
                    nodes[0].Routing, nodes, requests, default);
                Assert.AreEqual(64, captures, "Discovery must reuse the mapping capture pass.");
                Assert.AreEqual(0, compilations, "Identical captures reuse pure input lowering, not retained state or authority results.");
                Assert.AreEqual(nodes.Length, decodes, "Unchanged owner evidence must decode once per planning operation.");
                var left = PartitionSemanticPlanning.ReadValidated(cohort.Binding!, nodes[0].Routing);
                var right = PartitionSemanticPlanning.ReadValidated(scalar, nodes[0].Routing);
                CollectionAssert.AreEquivalent(cohort.Binding!.Owners.ToArray(), scalar.Owners.ToArray());
                foreach (var owner in left.Expansions)
                {
                    CollectionAssert.AreEqual(owner.Envelope.Expanded,
                        right.Expansions.Single(item => item.Owner == owner.Owner).Envelope.Expanded);
                }

                Console.WriteLine($"64 entity planning: captures={captures}, decoded owners={decodes}, all fresh compilations={compilations}; scalar/fused and cohort native bytes identical.");
                using var cancellation = new CancellationTokenSource();
                cancellation.Cancel();
                await Assert.ThrowsAsync<OperationCanceledException>(() => PartitionSemanticPlanning.SelectAsync(
                    client.Generation, transaction.TransactionId, nodes[0].Routing, nodes, requests, cancellation.Token).AsTask());
                Assert.IsTrue(nodes.All(node => node.UnreleasedReadCutCount == 0));
            }
            finally
            {
                DistributedReadTelemetry.ObserveElapsedTicks = previous;
            }
        });
    }

    [TestMethod]
    public async Task Semantic_planning_authority_work_does_not_multiply_with_entity_count()
    {
        await WithOrdinaryTransactions(DatabaseProfileId.Semantic, async (remote, client, nodes, authority) =>
        {
            await remote.CreateTransaction().Save(new Item("registered", "mapping")).CommitAsync();
            int executions = 0;
            var previous = DistributedReadTelemetry.ObserveElapsedTicks;
            DistributedReadTelemetry.ObserveElapsedTicks = (stage, ticks) =>
            {
                previous?.Invoke(stage, ticks);
                if (stage == "partition.authority.execution")
                {
                    Interlocked.Increment(ref executions);
                }
            };
            try
            {
                async Task<int> Plan(int count)
                {
                    var transaction = remote.CreateTransaction();
                    for (int i = 0; i < count; i++)
                    {
                        transaction.Save(new Item("planned-" + count + "-" + i, "value"));
                    }

                    executions = 0;
                    await PartitionSemanticPlanning.SelectAsync(client.Generation, transaction.TransactionId,
                        nodes[0].Routing, nodes, transaction.Capture(), default);
                    return executions;
                }
                int one = await Plan(1), many = await Plan(64);
                Console.WriteLine($"Supervised planning executions: one entity={one}; 64 entities={many}");
                Assert.IsTrue(one > 0);
                Assert.AreEqual(one, many, "One transaction should supervise one mapping admission and one combined evidence read per owner.");
                var baseline = remote.CreateTransaction();
                for (int i = 0; i < 64; i++)
                {
                    baseline.Save(new Item("baseline-" + i, "value"));
                }

                var captures = baseline.Capture();
                executions = 0;
                foreach (var owner in nodes)
                {
                    foreach (var capture in captures)
                    {
                        await owner.InitializeApplicationMappingAsync(capture);
                    }
                }

                int repeatedMappings = executions;
                executions = 0;
                var representatives = await PartitionSemanticPlanning.MappingRepresentativesAsync(captures, default);
                foreach (var owner in nodes)
                {
                    foreach (var capture in representatives)
                    {
                        await owner.InitializeApplicationMappingAsync(capture);
                    }
                }

                Console.WriteLine($"Mapping authority executions on the same owners: old={repeatedMappings}; new={executions}");
                Assert.AreEqual(128, repeatedMappings);
                Assert.AreEqual(2, executions);
            }
            finally
            {
                DistributedReadTelemetry.ObserveElapsedTicks = previous;
            }
        });
    }

    [TestMethod]
    public async Task Semantic_transaction_initializes_each_complete_mapping_set_once()
    {
        using var http = new HttpClient();
        var client = new HttpSemanticEntityClient(http, Guid.NewGuid());
        var store = client.EntityStore<Item>();
        var operation = SemanticEntityWriteOperation.Create();
        var requests = Enumerable.Range(0, 64).Select(i => (ReadOnlyMemory<byte>)store
            .CaptureSave(new(i.ToString(), "value-" + i), operation).Export()).ToArray();
        var representatives = await PartitionSemanticPlanning.MappingRepresentativesAsync(requests, default);
        Assert.AreEqual(1, representatives.Length, "64 identical maps require one admission per owner, not 64.");
        CollectionAssert.AreEqual(requests[0].ToArray(), representatives[0].ToArray());

        // A different model must still be initialized, even in the last capture.
        var different = client.EntityStore<PartitionSemanticLeaf>()
            .CaptureSave(new("leaf", "different"), operation).Export();
        var mixed = requests.Append((ReadOnlyMemory<byte>)different).ToArray();
        Assert.AreEqual(2, (await PartitionSemanticPlanning.MappingRepresentativesAsync(mixed, default)).Length);

        // The reduction is scoped to this transaction, never a cross-call cache.
        Assert.AreEqual(1, (await PartitionSemanticPlanning.MappingRepresentativesAsync(requests, default)).Length);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            PartitionSemanticPlanning.MappingRepresentativesAsync(requests, cancellation.Token).AsTask());
        var malformed = requests.ToArray();
        malformed[^1] = requests[^1][..^1];
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            PartitionSemanticPlanning.MappingRepresentativesAsync(malformed, default).AsTask());
    }

    [TestMethod]
    public async Task Semantic_transaction_with_distinct_maps_initializes_and_commits_both()
    {
        await WithOrdinaryTransactions(DatabaseProfileId.Semantic, async (remote, client, nodes, authority) =>
        {
            await remote.CreateTransaction().Save(new Item("item", "first"))
                .Save(new PartitionSemanticLeaf("leaf", "second")).CommitAsync();
            Assert.AreEqual("first", (await remote.ReadSemanticAsync<Item>("item")).Value!.Name);
            Assert.AreEqual("second", (await remote.ReadSemanticAsync<PartitionSemanticLeaf>("leaf")).Value!.Name);
        });
    }
}
