using EntityForge.Db.Semantic;
using EntityForge.Db.Mapping;
using EntityForge.Semantics;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    private static async Task<string> SemanticKeyOnOwner<T>(Guid generation, PartitionOwnedNode node,
        Func<string, T> create, string prefix) where T : class
    {
        using var http = new HttpClient();
        var store = new HttpSemanticEntityClient(http, generation).EntityStore<T>();
        const int MaximumCandidates = 1024;
        for (int i = 0; i < MaximumCandidates; i++)
        {
            string key = prefix + i;
            var original = await PartitionSemanticPlanning.OriginalTypedAsync(store.CaptureSave(create(key), SemanticEntityWriteOperation.Create()));
            if (ProfilePartitionRouting.SemanticHome(node.Routing, original.Session.Start.Source.SourceSystem,
                    original.Session.Start.Source.SourcePartition) == node.Domain.Partition &&
                original.Session.Pages.SelectMany(page => page.Changes).All(change => node.Routing.Route(
                    DistributedDatabaseProfile.Semantic, ProfilePartitionRouting.SemanticOwner(change.Subject)) == node.Domain.Partition))
            {
                return key;
            }
        }
        throw new AssertFailedException("Missing local candidate.");
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(1)]
    public async Task Semantic_complete_owner_transaction_supports_references_multiple_entities_replacement_delete_and_replay(int index)
    {
        await WithOrdinaryTransactions(DatabaseProfileId.Semantic, async (remote, client, nodes, authority) =>
        {
            var owner = nodes[index];
            string leaf = await SemanticKeyOnOwner(client.Generation, owner, id => new PartitionSemanticLeaf(id, "target"), "target-");
            string parent = await SemanticKeyOnOwner(client.Generation, owner, id => new SemanticReferenceParent { Id = id }, "parent-");
            string item = await SemanticKeyOnOwner(client.Generation, owner, id => new Item(id, "value"), "item-");
            await remote.CreateTransaction().Save(new PartitionSemanticLeaf(leaf, "target")).CommitAsync();
            var transaction = remote.CreateTransaction()
                .Save(new SemanticReferenceParent { Id = parent, Name = "parent", Peer = new(leaf, "target") })
                .Save(new Item(item, "companion"));
            var receipt = await transaction.CommitAsync();
            Assert.AreEqual(1, receipt.Members.Count);
            Assert.AreEqual(owner.Domain.Identity, receipt.Members.Single().Identity);
            var selected = (await authority.ReadApplicationAsync(transaction.TransactionId))!;
            Assert.IsTrue(selected.NativeLocal);
            Assert.AreEqual(1, PartitionSemanticPlanning.ReadValidated(selected, owner.Routing).Evidence.Position.Positions.Count);
            Assert.AreEqual("parent", (await remote.ReadSemanticAsync<SemanticReferenceParent>(parent)).Value!.Name);
            Assert.AreEqual("companion", (await remote.ReadSemanticAsync<Item>(item)).Value!.Name);
            var replay = await client.DeliverAsync(transaction.TransactionId, transaction.Capture());
            Assert.AreEqual(DatabaseCommitOutcome.IdempotentReplay, replay.Members.Single().Receipt.Outcome);
            Assert.AreEqual(receipt.Members.Single().Receipt.Version, replay.Members.Single().Receipt.Version);

            // A target changed after planning invalidates the whole local transaction.
            var stale = remote.CreateTransaction().Save(new SemanticReferenceParent { Id = parent, Name = "must abort" }).Delete<Item>(item);
            var proposal = await PartitionSemanticPlanning.SelectOwnerOrDistributedAsync(client.Generation, stale.TransactionId,
                owner.Routing, nodes, stale.Capture(), default);
            await authority.BindApplicationAsync(proposal.SelectNativeLocal());
            await remote.CreateTransaction().Save(new PartitionSemanticLeaf(leaf, "changed target")).CommitAsync();
            Exception? rejected = null;
            try
            {
                await stale.CommitAsync();
            }
            catch (Exception failure)
            {
                rejected = failure;
            }
            Assert.IsNotNull(rejected, "Stale relationship dependencies must reject the complete transaction.");
            Assert.AreEqual("parent", (await remote.ReadSemanticAsync<SemanticReferenceParent>(parent)).Value!.Name);
            Assert.AreEqual("companion", (await remote.ReadSemanticAsync<Item>(item)).Value!.Name);

            // The old target must remain protected after the reference disappears.
            var removal = remote.CreateTransaction().Save(new SemanticReferenceParent { Id = parent, Name = "removed" }).Delete<Item>(item);
            await removal.CommitAsync();
            var removed = (await authority.ReadApplicationAsync(removal.TransactionId))!;
            Assert.IsTrue(removed.NativeLocal);
            var original = await PartitionSemanticPlanning.OriginalTypedAsync(SemanticEntityRequest.Restore(transaction.Capture()[0].Span));
            var target = original.Session.Pages.SelectMany(page => page.Changes).SelectMany(change => change.Relationships).Single().Target;
            Assert.IsTrue(PartitionSemanticPlanning.ReadValidated(removed, owner.Routing).Expansions.Single().Contributions
                .SelectMany(contribution => contribution.Read!.Entities).Contains(target));
            Assert.IsNull((await remote.ReadSemanticAsync<Item>(item)).Value);
            Assert.IsNull((await remote.ReadSemanticAsync<SemanticReferenceParent>(parent)).Value!.Peer);
            var deletion = remote.CreateTransaction().Delete<SemanticReferenceParent>(parent).Delete<PartitionSemanticLeaf>(leaf);
            await deletion.CommitAsync();
            Assert.IsTrue((await authority.ReadApplicationAsync(deletion.TransactionId))!.NativeLocal);
            Assert.IsNull((await remote.ReadSemanticAsync<SemanticReferenceParent>(parent)).Value);
            Assert.IsNull((await remote.ReadSemanticAsync<PartitionSemanticLeaf>(leaf)).Value);
            await client.CloseReadEpochAsync();
            Assert.IsTrue(nodes.All(node => node.UnreleasedReadCutCount == 0), string.Join(",", nodes.Select(node => node.UnreleasedReadCutCount)));
        });
    }

    [TestMethod]
    public async Task Semantic_complete_owner_planning_avoids_unrelated_owners_and_declines_remote_targets_without_selecting()
    {
        await WithReductionOwners(async (nodes, authority, client) =>
        {
            var request = await ConfinedSemanticRequest(client, nodes[0]);
            var touched = new HashSet<string>();
            var peers = nodes.Select(node => new PhaseParticipant(node)
            {
                BeforeRead = (query, token) =>
                {
                    touched.Add(node.Domain.Partition.Value);
                    return ValueTask.CompletedTask;
                }
            }).ToArray();
            var local = await PartitionSemanticPlanning.SelectOwnerOrDistributedAsync(client.Generation, request.OperationId,
                nodes[0].Routing, peers, [request.Export()], default);
            CollectionAssert.AreEquivalent(new[] { nodes[0].Domain.Partition.Value }, touched.ToArray());
            Assert.AreEqual(1, local.Owners.Count);
            Assert.IsNull(await authority.ReadApplicationAsync(request.OperationId));
            string parent = await SemanticKeyOnOwner(client.Generation, nodes[0], id => new SemanticReferenceParent { Id = id }, "remote-parent-");
            string leaf = await SemanticKeyOnOwner(client.Generation, nodes[1], id => new PartitionSemanticLeaf(id, "target"), "remote-leaf-");
            using var http = new HttpClient();
            var store = new HttpSemanticEntityClient(http, client.Generation).EntityStore<SemanticReferenceParent>();
            var cross = store.CaptureSave(new()
            {
                Id = parent,
                Peer = new(leaf, "target")
            }, SemanticEntityWriteOperation.Create());
            var distributed = await PartitionSemanticPlanning.SelectOwnerOrDistributedAsync(client.Generation, cross.OperationId,
                nodes[0].Routing, peers, [cross.Export()], default);
            Assert.IsTrue(distributed.Owners.Any(owner => owner == nodes[1].Domain.Identity));
            Assert.IsTrue(distributed.Owners.Count > 1);
            Assert.IsNull(await authority.ReadApplicationAsync(cross.OperationId));
            Assert.IsTrue(nodes.All(node => node.UnreleasedReadCutCount == 0), string.Join(",", nodes.Select(node => node.UnreleasedReadCutCount)));
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => PartitionSemanticPlanning.SelectOwnerOrDistributedAsync(
                client.Generation, cross.OperationId, nodes[0].Routing, peers, [cross.Export()], cancelled.Token).AsTask());
        });
    }

    [TestMethod]
    public async Task Semantic_complete_owner_aggregate_removes_owned_children_and_reopens_exactly()
    {
        await WithReductionOwners(async (nodes, authority, client) =>
        {
            var owner = nodes[2];
            string key = await SemanticKeyOnOwner(client.Generation, owner, id => new PartitionSemanticTree
            {
                Id = id,
                Name = "tree",
                Children = [new("owned-leaf", "child")]
            }, "tree-");
            using var http = new HttpClient();
            var store = new HttpSemanticEntityClient(http, client.Generation).EntityStore<PartitionSemanticTree>();
            var first = store.CaptureSave(new()
            {
                Id = key,
                Name = "tree",
                Children = [new("owned-leaf", "child")]
            }, SemanticEntityWriteOperation.Create());
            var initial = await client.DeliverAsync(first.OperationId, first.Export());
            Assert.IsTrue((await authority.ReadApplicationAsync(first.OperationId))!.NativeLocal);
            var next = store.CaptureSave(new()
            {
                Id = key,
                Name = "empty"
            }, SemanticEntityWriteOperation.Create());
            var receipt = await client.DeliverAsync(next.OperationId, next.Export());
            var binding = (await authority.ReadApplicationAsync(next.OperationId))!;
            Assert.IsTrue(binding.NativeLocal);
            var changes = PartitionSemanticPlanning.ReadValidated(binding, owner.Routing).Expansions.Single().Contributions
                .SelectMany(c => c.Session.Pages).SelectMany(page => page.Changes).ToArray();
            Assert.IsTrue(changes.Count(change => change.Kind == SemanticEntityChangeKind.RetractSourceEntity) >= 1);
            var lease = owner.CurrentLease;
            await owner.DisposeAsync();
            await using var reopened = await PartitionOwnedNode.OpenAsync(Path.Combine(_root, owner.Domain.Partition.Value),
                DatabaseProfileId.Semantic, lease, owner.Routing, new Authority { Lease = lease }, Options,
                transactionAuthority: authority, timestampClock: ReadClock);
            var recovered = new PartitionApplicationClient(authority, owner.Routing,
                nodes.Select(node => node == owner ? reopened : node).ToArray(), ReadClock);
            var replay = await recovered.DeliverAsync(next.OperationId, next.Export());
            Assert.AreEqual(DatabaseCommitOutcome.IdempotentReplay, replay.Members.Single().Receipt.Outcome);
            Assert.AreEqual(receipt.Members.Single().Receipt.Version, replay.Members.Single().Receipt.Version);
            var deletion = store.CaptureDelete(key, SemanticEntityWriteOperation.Create());
            await recovered.DeliverAsync(deletion.OperationId, deletion.Export());
            Assert.IsTrue((await authority.ReadApplicationAsync(deletion.OperationId))!.NativeLocal);
            Assert.AreEqual(0, reopened.UnreleasedReadCutCount);
        });
    }

    [TestMethod]
    public async Task Semantic_complete_owner_policy_is_enforced_and_remote_policy_requires_coordination()
    {
        await WithReductionOwners(async (nodes, authority, client) =>
        {
            var request = await ConfinedSemanticRequest(client, nodes[0]);
            var original = await PartitionSemanticPlanning.OriginalTypedAsync(request);
            var concept = original.Session.Pages.SelectMany(page => page.Changes).Single().Subject.Concept;
            var policy = new SemanticGovernanceRuleDefinition(new("urn:test:complete-owner-policy"), SemanticGovernanceRuleKind.Policy,
                new("urn:test:missing-evaluator"), "1", true, new([concept], SemanticGovernanceEnforcement.RejectViolation));
            await client.PublishSemanticGovernanceAsync(SemanticEntityWriteOperation.Create().OperationId, nodes[0].Domain.Partition, policy);
            var local = await PartitionSemanticPlanning.SelectOwnerOrDistributedAsync(client.Generation, request.OperationId,
                nodes[0].Routing, nodes, [request.Export()], default);
            Assert.AreEqual(1, local.Owners.Count);
            var before = nodes[0].Kernel.CurrentVersion;
            Exception? failure = null;
            try
            {
                await client.DeliverAsync(request.OperationId, request.Export());
            }
            catch (Exception observed)
            {
                failure = observed;
            }
            Assert.IsNotNull(failure, "A local policy must not be bypassed when no evaluator exists.");
            Assert.AreEqual(before, nodes[0].Kernel.CurrentVersion);
            await client.PublishSemanticGovernanceAsync(SemanticEntityWriteOperation.Create().OperationId, nodes[3].Domain.Partition,
                policy with
                {
                    Identifier = new("urn:test:remote-complete-policy")
                });
            var remote = request.ForOperation(SemanticEntityWriteOperation.Create().OperationId);
            var distributed = await PartitionSemanticPlanning.SelectOwnerOrDistributedAsync(client.Generation, remote.OperationId,
                nodes[0].Routing, nodes, [remote.Export()], default);
            Assert.IsTrue(distributed.Owners.Contains(nodes[3].Domain.Identity));
            Assert.IsTrue(distributed.Owners.Count > 1);
            Assert.IsTrue(nodes.All(node => node.UnreleasedReadCutCount == 0));
        });
    }

    [TestMethod]
    public async Task Semantic_complete_owner_cohort_shares_local_cuts_and_matches_distributed_native_payloads()
    {
        await WithReductionOwners(async (nodes, authority, client) =>
        {
            var captures = new List<PartitionIndependentWrite>();
            using var http = new HttpClient();
            var store = new HttpSemanticEntityClient(http, client.Generation).EntityStore<Item>();
            for (int index = 0; index < PartitionSemanticPlanning.MaximumPlanningCohort; index++)
            {
                string key = await SemanticKeyOnOwner(client.Generation, nodes[0], id => new Item(id, "value"), "cohort-" + index + "-");
                var request = store.CaptureSave(new(key, "value"), SemanticEntityWriteOperation.Create());
                captures.Add(new(request.OperationId, [request.Export()]));
            }
            var oracle = await PartitionSemanticPlanning.SelectCohortAsync(client.Generation, nodes[0].Routing, nodes, captures, default);
            int reads = 0;
            using var listener = new System.Diagnostics.ActivityListener
            {
                ShouldListenTo = source => source.Name == DistributedReadTelemetry.SourceName,
                Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) => System.Diagnostics.ActivitySamplingResult.AllData,
                ActivityStarted = activity =>
{
    if (activity.OperationName == "partition.read.complete")
    {
        Interlocked.Increment(ref reads);
    }

}
            };
            System.Diagnostics.ActivitySource.AddActivityListener(listener);
            var reduced = await PartitionSemanticPlanning.SelectCohortAsync(client.Generation, nodes[0].Routing, nodes, captures, default, preferOwner: true);
            Assert.AreEqual(1, reads);
            for (int index = 0; index < captures.Count; index++)
            {
                Assert.IsNull(reduced[index].Failure);
                var local = PartitionSemanticPlanning.ReadValidated(reduced[index].Binding!, nodes[0].Routing);
                Assert.AreEqual(1, local.Evidence.Position.Positions.Count);
                CollectionAssert.AreEqual(PartitionSemanticPlanning.ReadValidated(oracle[index].Binding!, nodes[0].Routing).Expansions.Single().Envelope.Expanded,
                    local.Expansions.Single().Envelope.Expanded);
                await authority.BindApplicationAsync(reduced[index].Binding!.SelectNativeLocal());
                Assert.AreEqual(1, (await client.DeliverAsync(captures[index].Operation, captures[index].Requests)).Members.Count);
            }
            Assert.IsTrue(nodes.All(node => node.UnreleasedReadCutCount == 0));
        });
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task Semantic_complete_owner_cohort_failure_remains_independent_and_releases_pins(bool mapping)
    {
        await WithReductionOwners(async (nodes, authority, client) =>
        {
            var first = await ConfinedSemanticRequest(client, nodes[0]);
            var second = first.ForOperation(SemanticEntityWriteOperation.Create().OperationId);
            ValueTask Reject(ReadOnlyMemory<byte> captured, CancellationToken token)
            {
                if (SemanticEntityRequest.Restore(captured.Span).OperationId == first.OperationId)
                {
                    throw new InvalidDataException("Injected independent proposal failure.");
                }

                return ValueTask.CompletedTask;
            }
            var peers = nodes.Select(node => new PhaseParticipant(node)
            {
                BeforeMapping = mapping ? Reject : null,
                BeforeRead = (query, token) => !mapping && query.Kind == "semantic-expansion"
                    ? Reject(PartitionSemanticRequests.Decode(query.Captured!)[0], token)
                    : ValueTask.CompletedTask
            }).ToArray();
            var results = await PartitionSemanticPlanning.SelectCohortAsync(client.Generation, nodes[0].Routing, peers,
                [new(first.OperationId, [first.Export()]), new(second.OperationId, [second.Export()])], default, preferOwner: true);
            Assert.IsNotNull(results[0].Failure);
            Assert.IsNull(results[0].Binding);
            Assert.IsNull(results[1].Failure);
            Assert.IsNotNull(results[1].Binding);
            Assert.IsTrue(nodes.All(node => node.UnreleasedReadCutCount == 0));
            Assert.IsNull(await authority.ReadApplicationAsync(first.OperationId));
            Assert.IsNull(await authority.ReadApplicationAsync(second.OperationId));
        });
    }

    [TestMethod]
    public async Task Semantic_complete_owner_cohort_dispatches_distinct_owners_concurrently()
    {
        await WithReductionOwners(async (nodes, authority, client) =>
        {
            var first = await ConfinedSemanticRequest(client, nodes[0]);
            var second = await ConfinedSemanticRequest(client, nodes[1]);
            var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int entered = 0;
            var peers = nodes.Select(node => new PhaseParticipant(node)
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

                    await release.Task.WaitAsync(token);
                }
            }).ToArray();
            var pending = PartitionSemanticPlanning.SelectCohortAsync(client.Generation, nodes[0].Routing, peers,
                [new(first.OperationId, [first.Export()]), new(second.OperationId, [second.Export()])], default, preferOwner: true).AsTask();
            try
            {
                await arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally
            {
                release.TrySetResult();
            }
            var results = await pending;
            Assert.IsTrue(results.All(result => result.Failure is null && result.Binding?.Owners.Count == 1));
            Assert.AreEqual(2, entered);
            Assert.IsTrue(nodes.All(node => node.UnreleasedReadCutCount == 0));
        });
    }

}
