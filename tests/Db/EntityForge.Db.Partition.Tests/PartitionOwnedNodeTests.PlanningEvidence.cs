using EntityForge.Db.Graph;
using EntityForge.Db.Semantic;
using EntityForge.Db.Distributed.Hosting;
using EntityForge.Db.Distributed.Sharding;
using EntityForge.Db.Mapping;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [TestMethod]
    public async Task Graph_delete_evidence_allows_unrelated_writes_and_empty_owner_preparation()
    {
        await WithProfileFragments(DatabaseProfileId.PropertyGraph, async (_, _, _, remote, http, nodes, authority) =>
        {
            await remote.SaveGraphAsync(new Item("one", "before"));
            await remote.SaveGraphAsync(new Item("unrelated", "before"));
            var operation = EntityOperationIds.New();
            var store = new HttpGraphEntityClient(http, remote.Generation).EntityStore<Item>();
            var request = store.CaptureDelete("one", new(operation));
            var selection = await PartitionGraphPlanning.SelectAsync(remote.Generation, operation, nodes[0].Routing,
                nodes, [request.Export()], CancellationToken.None);
            await authority.BindApplicationAsync(selection);
            var members = await Task.WhenAll(nodes.Select(node => node.PlanApplicationAsync(operation).AsTask()));
            Assert.IsTrue(members.Any(member =>
            {
                var transaction = GraphTransactionCodec.Decode(operation, member.Payload.Span);
                return transaction.Mutations.Count == 0 && transaction.ReadPredicates is { Count: 0 };
            }), "This regression must exercise a participant with no local dependencies.");
            await remote.SaveGraphAsync(new Item("unrelated", "changed"));
            var plan = new PartitionTransactionPlan(operation, nodes[0].Routing.MapVersion, members);
            await authority.BindAsync(plan);
            try
            {
                foreach (var node in nodes)
                {
                    await node.PrepareNativeAsync(plan);
                }
                await SelectTimestampDecisionAsync(authority, plan, nodes);
            }
            catch
            {
                await authority.DecideAsync(operation, plan.Descriptor, false);
                throw;
            }
            finally
            {
                foreach (var node in nodes)
                {
                    await node.ApplyPartitionDecisionAsync(operation);
                }
            }
            Assert.IsNull((await remote.ReadGraphAsync<Item>("one")).Value);
            Assert.AreEqual("changed", (await remote.ReadGraphAsync<Item>("unrelated")).Value!.Name);
        }, allRemoteParticipants: true);
    }

    [TestMethod]
    public async Task Graph_evidence_reconstructs_after_writes_but_rejects_changed_facts()
    {
        await WithProfileFragments(DatabaseProfileId.PropertyGraph, async (_, _, _, remote, http, nodes, authority) =>
        {
            await remote.SaveGraphAsync(new Item("one", "before"));
            var operation = EntityOperationIds.New();
            var store = new HttpGraphEntityClient(http, remote.Generation).EntityStore<Item>();
            var request = store.CaptureDelete("one", new(operation));
            var selection = await PartitionGraphPlanning.SelectAsync(remote.Generation, operation, nodes[0].Routing,
                nodes, [request.Export()], CancellationToken.None);
            await authority.BindApplicationAsync(selection);
            var members = await Task.WhenAll(nodes.Select(node => node.PlanApplicationAsync(operation).AsTask()));
            await remote.SaveGraphAsync(new Item("one", "changed"));
            Assert.IsTrue(nodes.Where((node, index) => node.Kernel.CurrentVersion > members[index].ReadVersion).Any());
            for (int index = 0; index < nodes.Length; index++)
            {
                var reconstructed = await nodes[index].PlanApplicationAsync(operation);
                CollectionAssert.AreEqual(members[index].Payload.ToArray(), reconstructed.Payload.ToArray());
                Assert.AreEqual(members[index].ReadVersion, reconstructed.ReadVersion);
            }
            var plan = new PartitionTransactionPlan(operation, nodes[0].Routing.MapVersion, members);
            await authority.BindAsync(plan);
            bool rejected = false;
            try
            {
                foreach (var node in nodes)
                {
                    await node.PrepareNativeAsync(plan);
                }
            }
            catch (EntityForge.Db.Kernel.DatabasePartitionConflictException)
            {
                rejected = true;
            }
            finally
            {
                await authority.DecideAsync(operation, plan.Descriptor, false);
                foreach (var node in nodes)
                {
                    await node.ApplyPartitionDecisionAsync(operation);
                }
            }
            Assert.IsTrue(rejected, "Changed facts must not be committed from old planning evidence.");
            Assert.AreEqual("changed", (await remote.ReadGraphAsync<Item>("one")).Value!.Name);
        }, allRemoteParticipants: true);
    }

    [DataTestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph, false)]
    [DataRow(DatabaseProfileId.PropertyGraph, true)]
    [DataRow(DatabaseProfileId.Semantic, false)]
    [DataRow(DatabaseProfileId.Semantic, true)]
    public async Task Evidence_backed_plan_rejects_substitution_and_recovers_partial_commit(DatabaseProfileId profile, bool live)
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
        string secondPath = Path.Combine(_root, "b");
        await using var second = await PartitionOwnedNode.CreateAsync(secondPath, profile, b.Lease, routing, b, Options, transactionAuthority: authority, timestampClock: ReadClock);
        IPartitionTransactionParticipant[] owners = [first, second];
        using var http = new HttpClient();
        string semanticKey = profile == DatabaseProfileId.Semantic ? await CrossOwnerSemanticKeyAsync(first) : "one";
        byte[] Capture(Guid operation) => profile == DatabaseProfileId.PropertyGraph
            ? new HttpGraphEntityClient(http, first.Domain.Generation).EntityStore<Item>().CaptureSave(new("one", "value"), new(operation)).Export()
            : new HttpSemanticEntityClient(http, first.Domain.Generation).EntityStore<PartitionSemanticTree>().CaptureSave(new()
            {
                Id = semanticKey,
                Name = "value",
                Children = Enumerable.Range(0, 8).Select(i => new PartitionSemanticLeaf("evidence-child-" + i, "value")).ToList()
            }, new(operation, DateTimeOffset.UnixEpoch)).Export();
        async ValueTask<PartitionApplicationBinding> Select(Guid operation) => profile == DatabaseProfileId.PropertyGraph
            ? await PartitionGraphPlanning.SelectAsync(first.Domain.Generation, operation, routing, owners, [Capture(operation)], CancellationToken.None)
            : await PartitionSemanticPlanning.SelectAsync(first.Domain.Generation, operation, routing, owners, [Capture(operation)], CancellationToken.None);

        var invalidOperation = EntityOperationIds.New();
        var invalid = await Select(invalidOperation);
        Assert.AreEqual(2, invalid.Owners.Count, "Partial delivery requires two real participants.");
        await authority.BindApplicationAsync(invalid);
        var validLeft = await first.PlanApplicationAsync(invalidOperation);
        var validRight = await second.PlanApplicationAsync(invalidOperation);
        var altered = validLeft.Payload.ToArray();
        altered[^1] ^= 1;
        var invalidPlan = new PartitionTransactionPlan(invalidOperation, routing.MapVersion, [new(first.Domain, validLeft.ReadVersion, altered), validRight]);
        if (profile == DatabaseProfileId.Semantic && live)
        {
            // Referenced plans must match their retained payload before selection,
            // as well as at native admission. Rejection cannot leave a native plan.
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => authority.BindAsync(invalidPlan).AsTask());
            Assert.IsNull(await authority.ReadPlanAsync(invalidOperation));
        }
        else
        {
            await authority.BindAsync(invalidPlan);
        }

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => first.PrepareNativeAsync(invalidPlan).AsTask());
        Assert.AreEqual(0, (await first.Kernel.PartitionTransactions.ReadAsync()).Count);
        if (profile == DatabaseProfileId.Semantic && live)
        {
            await authority.AbortApplicationAsync(invalidOperation, invalid.Descriptor);
        }
        else
        {
            await authority.DecideAsync(invalidOperation, invalidPlan.Descriptor, false);
        }

        var operation = EntityOperationIds.New();
        var selection = await Select(operation);
        await authority.BindApplicationAsync(selection);
        var plan = new PartitionTransactionPlan(operation, routing.MapVersion,
            [await first.PlanApplicationAsync(operation), await second.PlanApplicationAsync(operation)]);
        await authority.BindAsync(plan);
        await first.PrepareNativeAsync(plan);
        await second.PrepareNativeAsync(plan);
        await SelectTimestampDecisionAsync(authority, plan, first, second);
        await first.ApplyPartitionDecisionAsync(operation);
        var observer = new PartitionApplicationClient(authority, routing, owners, ReadClock);
        Assert.AreEqual(EntityOperationState.CommittedPendingReadability, (await observer.ResolveAsync(operation, CancellationToken.None)).State);
        await second.DisposeAsync();
        var unavailable = await Assert.ThrowsExactlyAsync<PartitionDecisionDeliveryException>(() => observer.ResolveAsync(operation, CancellationToken.None).AsTask());
        Assert.IsTrue(unavailable.Decision.Commit);
        Assert.IsNotNull(unavailable.InnerException);
        using (var certificate = PartitionCertificate())
        {
            using (var observationHttp = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert?.Thumbprint == certificate.Thumbprint }))
            {
                await using var service = new PartitionApplicationHttpService(observer);
                await using var gateway = await StartApplicationHostAsync(service, certificate);
                var remote = new HttpPartitionEntityClient(observationHttp, PartitionAddress(gateway), PartitionToken, first.Domain.Generation, profile);
                Assert.AreEqual(EntityOperationState.CommittedPendingReadability, (await remote.ResolveAsync(operation)).State);
            }
        }
        await using var reopened = await PartitionOwnedNode.OpenAsync(secondPath, profile, b.Lease, routing, b, Options, transactionAuthority: authority, timestampClock: ReadClock);
        var restored = new PartitionApplicationClient(authority, routing, [first, reopened], ReadClock);
        Assert.AreEqual(EntityOperationState.Committed, (await restored.ResolveAsync(operation, CancellationToken.None)).State);
        var replay = await new PartitionTransactionCoordinator(authority).ExecuteAsync(plan, [first, reopened]);
        Assert.IsTrue(replay.Members.All(member => member.Receipt.Outcome == DatabaseCommitOutcome.IdempotentReplay));
    }
}
