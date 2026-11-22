using EntityForge.Db.Semantic;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [TestMethod]
    public async Task Semantic_owner_groups_fall_back_for_old_remote_children_and_remove_them()
    {
        await WithOrdinaryTransactions(DatabaseProfileId.Semantic, async (remote, client, nodes, authority) =>
        {
            string key = await SemanticKeyOnOwner(client.Generation, nodes[0], id => new PartitionSemanticTree { Id = id, Name = "tree" }, "group-tree-");
            var tree = new PartitionSemanticTree
            {
                Id = key,
                Name = "before",
                Children = Enumerable.Range(0, 8).Select(index => new PartitionSemanticLeaf("group-child-" + index, "value")).ToList()
            };
            var initial = remote.CreateTransaction().Save(tree);
            await initial.CommitAsync();
            var old = PartitionSemanticPlanning.ReadValidated((await authority.ReadApplicationAsync(initial.TransactionId))!, nodes[0].Routing);
            Assert.AreEqual(2, old.Expansions.Length, "Fixture must retain remote child ownership.");
            string companion = await SemanticKeyOnOwner(client.Generation, nodes[1], id => new Item(id, "value"), "group-companion-");
            var transaction = remote.CreateTransaction().Save(new PartitionSemanticTree { Id = key, Name = "after" }).Save(new Item(companion, "value"));
            var binding = await PartitionSemanticPlanning.SelectOwnerOrDistributedAsync(client.Generation,
                transaction.TransactionId, nodes[0].Routing, nodes, transaction.Capture(), default);
            var evidence = PartitionSemanticPlanning.ReadValidated(binding, nodes[0].Routing);
            Assert.IsTrue(evidence.Evidence.Expansions.All(part => part.RequestIndices is null));
            Assert.IsTrue(evidence.Expansions.Single(part => part.Owner == nodes[1].Domain.Identity).Contributions
                .SelectMany(part => part.Session.Pages).SelectMany(page => page.Changes)
                .Any(change => change.Kind == EntityForge.Semantics.SemanticEntityChangeKind.RetractSourceEntity));
            Assert.IsNull(await authority.ReadApplicationAsync(transaction.TransactionId));
            await authority.BindApplicationAsync(binding);
            var plan = PartitionSemanticPlanning.Plan(binding, nodes[0].Routing, nodes.Select(node => node.Domain).ToArray());
            Assert.IsTrue((await new PartitionTransactionCoordinator(authority).ExecuteAsync(plan, nodes)).Decision.Commit);
            var restored = (await remote.ReadSemanticAsync<PartitionSemanticTree>(key)).Value!;
            Assert.AreEqual("after", restored.Name);
            Assert.AreEqual(0, restored.Children.Count);
            Assert.AreEqual("value", (await remote.ReadSemanticAsync<Item>(companion)).Value!.Name);
            await client.CloseReadEpochAsync();
            Assert.IsTrue(nodes.All(node => node.UnreleasedReadCutCount == 0));
        });
    }

    [TestMethod]
    public async Task Semantic_owner_groups_reject_new_governance_and_fall_back_with_existing_governance()
    {
        await WithOrdinaryTransactions(DatabaseProfileId.Semantic, async (remote, client, nodes, authority) =>
        {
            var transaction = remote.CreateTransaction();
            for (int i = 0; i < 64; i++)
            {
                transaction.Save(new Item("group-policy-" + i, "value"));
            }

            var binding = await PartitionSemanticPlanning.SelectOwnerOrDistributedAsync(client.Generation,
                transaction.TransactionId, nodes[0].Routing, nodes, transaction.Capture(), default);
            Assert.IsTrue(PartitionSemanticPlanning.Read(binding, nodes[0].Routing).Expansions.All(part => part.RequestIndices is not null));
            await authority.BindApplicationAsync(binding);
            var plan = PartitionSemanticPlanning.Plan(binding, nodes[0].Routing, nodes.Select(node => node.Domain).ToArray());
            await authority.BindAsync(plan);
            var policy = new SemanticGovernanceRuleDefinition(new("urn:test:group-policy"),
                SemanticGovernanceRuleKind.Policy, new("urn:test:definition"), "1", false);
            await client.PublishSemanticGovernanceAsync(SemanticEntityWriteOperation.Create().OperationId, nodes[1].Domain.Partition, policy);
            var outcome = await new PartitionTransactionCoordinator(authority).ExecuteAsync(plan, nodes);
            Assert.IsFalse(outcome.Decision.Commit);
            Assert.IsInstanceOfType<EntityForge.Db.Kernel.DatabasePartitionConflictException>(outcome.PreparationFailure);
            var later = remote.CreateTransaction();
            for (int i = 0; i < 64; i++)
            {
                later.Save(new Item("group-policy-later-" + i, "value"));
            }

            var fallback = await PartitionSemanticPlanning.SelectOwnerOrDistributedAsync(client.Generation,
                later.TransactionId, nodes[0].Routing, nodes, later.Capture(), default);
            Assert.IsTrue(PartitionSemanticPlanning.Read(fallback, nodes[0].Routing).Expansions.All(part => part.RequestIndices is null));
            Assert.IsNull(await authority.ReadApplicationAsync(later.TransactionId));
            Assert.IsTrue(nodes.All(node => node.UnreleasedReadCutCount == 0));
        });
    }

    [TestMethod]
    public async Task Semantic_owner_groups_cover_each_request_once_and_commit_one_atomic_transaction()
    {
        await WithOrdinaryTransactions(DatabaseProfileId.Semantic, async (remote, client, nodes, authority) =>
        {
            var transaction = remote.CreateTransaction();
            const int count = 64;
            for (int i = 0; i < count; i++)
            {
                transaction.Save(new Item("owner-group-" + i, "value-" + i));
            }

            var requests = transaction.Capture();
            var binding = await PartitionSemanticPlanning.SelectOwnerOrDistributedAsync(client.Generation,
                transaction.TransactionId, nodes[0].Routing, nodes, requests, default);
            var evidence = PartitionSemanticPlanning.ReadValidated(binding, nodes[0].Routing);
            Assert.AreEqual(nodes.Length, evidence.Expansions.Length);
            Assert.IsTrue(evidence.Evidence.Expansions.All(part => part.RequestIndices is { Length: > 0 }));
            Assert.AreEqual(count, evidence.Expansions.Sum(part => part.Envelope.Sources.Length));
            Assert.AreEqual(count, evidence.Expansions.Sum(part => part.Contributions.Count));
            foreach (var owner in nodes)
            {
                var local = PartitionSemanticPlanning.ReadForOwner(binding, owner.Routing, owner.Domain.Identity);
                Assert.AreEqual(1, local.Expansions.Length);
                Assert.AreEqual(local.Expansions[0].Envelope.Sources.Length,
                    PartitionSemanticPlanning.OwnerRequests(binding, local.Evidence, owner.Domain.Identity).Count);
            }
            Assert.IsNull(await authority.ReadApplicationAsync(transaction.TransactionId));
            Assert.IsTrue(nodes.All(node => node.UnreleasedReadCutCount == 0));
            await authority.BindApplicationAsync(binding);
            var plan = PartitionSemanticPlanning.Plan(binding, nodes[0].Routing, nodes.Select(node => node.Domain).ToArray());
            var result = await new PartitionTransactionCoordinator(authority).ExecuteAsync(plan, nodes);
            Assert.IsTrue(result.Decision.Commit);
            Assert.AreEqual(nodes.Length, result.Members.Count);
            for (int i = 0; i < count; i++)
            {
                Assert.AreEqual("value-" + i, (await remote.ReadSemanticAsync<Item>("owner-group-" + i)).Value!.Name);
            }

            var replay = await new PartitionTransactionCoordinator(authority).ExecuteAsync(plan, nodes);
            Assert.IsTrue(replay.Members.All(member => member.Receipt.Outcome == DatabaseCommitOutcome.IdempotentReplay));
        });
    }

    [TestMethod]
    public async Task Semantic_owner_group_directory_rejects_missing_duplicate_and_substituted_requests()
    {
        await WithOrdinaryTransactions(DatabaseProfileId.Semantic, async (remote, client, nodes, authority) =>
        {
            var transaction = remote.CreateTransaction();
            for (int i = 0; i < 64; i++)
            {
                transaction.Save(new Item("group-invalid-" + i, "value"));
            }

            var binding = await PartitionSemanticPlanning.SelectOwnerOrDistributedAsync(client.Generation,
                transaction.TransactionId, nodes[0].Routing, nodes, transaction.Capture(), default);
            var evidence = PartitionSemanticPlanning.Read(binding, nodes[0].Routing);
            foreach (int[]? replacement in new int[]?[] { null, [], [-1], [binding.Requests.Count], [0, 0], [1, 0] })
            {
                var expansions = evidence.Expansions.ToArray();
                expansions[0] = expansions[0] with
                {
                    RequestIndices = replacement
                };
                var malformed = new PartitionApplicationBinding(binding.Operation, binding.Owners, binding.Requests,
                    planningEvidence: PartitionSelectionCodec.EncodeEvidence(evidence with
                    {
                        Expansions = expansions
                    }));
                Assert.Throws<InvalidDataException>(() => PartitionSemanticPlanning.ReadValidated(malformed, nodes[0].Routing));
            }
            // Still total/disjoint, but assigned to the wrong owners: native fresh
            // reconstruction must reject before a commit decision can exist.
            var substituted = evidence.Expansions.Select(part => part with { RequestIndices = part.RequestIndices!.ToArray() }).ToArray();
            (substituted[0].RequestIndices![0], substituted[1].RequestIndices![0]) =
                (substituted[1].RequestIndices![0], substituted[0].RequestIndices![0]);
            substituted = substituted.Select(part => part with { RequestIndices = part.RequestIndices!.Order().ToArray() }).ToArray();
            var wrong = new PartitionApplicationBinding(binding.Operation, binding.Owners, binding.Requests,
                planningEvidence: PartitionSelectionCodec.EncodeEvidence(evidence with
                {
                    Expansions = substituted
                }));
            await authority.BindApplicationAsync(wrong);
            var plan = PartitionSemanticPlanning.Plan(wrong, nodes[0].Routing, nodes.Select(node => node.Domain).ToArray());
            await authority.BindAsync(plan);
            await Assert.ThrowsAsync<Exception>(() => nodes[0].PrepareNativeAsync(plan).AsTask());
            Assert.AreEqual(0, (await nodes[0].Kernel.PartitionTransactions.ReadAsync()).Count);
            await authority.DecideAsync(plan.Operation, plan.Descriptor, false);
            Assert.IsTrue(nodes.All(node => node.UnreleasedReadCutCount == 0));
        });
    }
}
