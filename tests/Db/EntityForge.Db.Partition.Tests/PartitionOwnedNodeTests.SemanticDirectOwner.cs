using EntityForge.Db.Semantic;
using EntityForge.Db.Mapping;
using EntityForge.Semantics;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [TestMethod]
    public async Task Semantic_direct_owner_saves_use_ordinary_api_on_every_owner_and_replay_after_reopen()
    {
        await WithReductionOwners(async (nodes, authority, client) =>
        {
            using var certificate = PartitionCertificate();
            using var http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, observed, _, _) => observed?.Thumbprint == certificate.Thumbprint });
            await using var participantA = new PartitionParticipantHttpService(nodes[0]);
            await using var participantB = new PartitionParticipantHttpService(nodes[1]);
            await using var participantC = new PartitionParticipantHttpService(nodes[2]);
            await using var participantD = new PartitionParticipantHttpService(nodes[3]);
            await using var hostA = await StartPartitionHostAsync(participantA, certificate);
            await using var hostB = await StartPartitionHostAsync(participantB, certificate);
            await using var hostC = await StartPartitionHostAsync(participantC, certificate);
            await using var hostD = await StartPartitionHostAsync(participantD, certificate);
            var hosts = new[] { hostA, hostB, hostC, hostD };
            client = new PartitionApplicationClient(authority, nodes[0].Routing,
                nodes.Select((node, i) => new HttpPartitionTransactionParticipant(http, PartitionAddress(hosts[i]),
                    PartitionToken, node.Domain, authority.Identity)).ToArray(), ReadClock);
            await using var service = new PartitionApplicationHttpService(client);
            await using var gateway = await StartApplicationHostAsync(service, certificate);
            var remote = new HttpPartitionEntityClient(http, PartitionAddress(gateway), PartitionToken, client.Generation, DatabaseProfileId.Semantic);
            var saved = new List<(SemanticEntityRequest Request, DatabaseDistributedCommitReceipt Receipt)>();
            foreach (var node in nodes)
            {
                var candidate = await ConfinedSemanticRequest(client, node);
                var original = await PartitionSemanticPlanning.OriginalTypedAsync(candidate);
                string key = ((StringSemanticValue)original.Session.Pages.Single().Changes.Single().Subject.Value).Value;
                var probeSave = candidate.ForOperation(PartitionOwnerOperation.New(node.Routing, node.Domain.Partition));
                await client.DeliverAsync(probeSave.OperationId, probeSave.Export());
                var result = await remote.SaveSemanticAsync(new Item(key, "direct"));
                Assert.IsTrue(PartitionOwnerOperation.IsOwnerLocal(result.OperationId));
                Assert.AreEqual(node.Domain.Identity, result.Members.Single().Identity);
                Assert.IsNull(await authority.ReadApplicationAsync(result.OperationId));
                Assert.IsNull(await authority.ReadPlanAsync(result.OperationId));
                var explicitRequest = candidate.ForOperation(PartitionOwnerOperation.New(node.Routing, node.Domain.Partition));
                var explicitResult = await client.DeliverAsync(explicitRequest.OperationId, explicitRequest.Export());
                Assert.IsTrue(PartitionOwnerOperation.IsOwnerLocal(explicitResult.OperationId));
                Assert.AreEqual(DatabaseCommitOutcome.IdempotentReplay,
                    (await client.DeliverAsync(explicitRequest.OperationId, explicitRequest.Export())).Members.Single().Receipt.Outcome);
                saved.Add((explicitRequest, explicitResult));
                var next = await remote.SaveSemanticAsync(new Item(key, "successor"));
                Assert.IsTrue(PartitionOwnerOperation.IsOwnerLocal(next.OperationId), "Native source coverage must survive each ordinary save.");
                Assert.AreEqual("successor", (await remote.ReadSemanticAsync<Item>(key)).Value!.Name);
                var deleted = await remote.DeleteSemanticAsync<Item>(key);
                Assert.IsTrue(PartitionOwnerOperation.IsOwnerLocal(deleted.OperationId));
                Assert.IsNull((await remote.ReadSemanticAsync<Item>(key)).Value);
                Assert.IsTrue(PartitionOwnerOperation.IsOwnerLocal((await remote.SaveSemanticAsync(new Item(key, "restored"))).OperationId));
            }
            await gateway.StopAsync();
            await service.DisposeAsync();
            foreach (var host in hosts)
            {
                await host.StopAsync();
            }

            var leases = nodes.Select(node => node.CurrentLease).ToArray();
            foreach (var node in nodes)
            {
                await node.DisposeAsync();
            }

            var reopened = new List<PartitionOwnedNode>();
            try
            {
                for (int i = 0; i < nodes.Length; i++)
                {
                    reopened.Add(await PartitionOwnedNode.OpenAsync(Path.Combine(_root, nodes[i].Domain.Partition.Value),
                    DatabaseProfileId.Semantic, leases[i], nodes[i].Routing, new Authority { Lease = leases[i] }, Options,
                    transactionAuthority: authority, timestampClock: ReadClock));
                }

                var recovered = new PartitionApplicationClient(authority, nodes[0].Routing, reopened, ReadClock);
                foreach (var item in saved)
                {
                    var replay = await recovered.DeliverAsync(item.Request.OperationId, item.Request.Export());
                    Assert.AreEqual(DatabaseCommitOutcome.IdempotentReplay, replay.Members.Single().Receipt.Outcome);
                    Assert.AreEqual(item.Receipt.Members.Single().Receipt.Version, replay.Members.Single().Receipt.Version);
                    var owner = reopened.Single(node => node.Domain.Identity == replay.Members.Single().Identity);
                    Assert.IsTrue(await ((IPartitionTransactionParticipant)owner).CanExecuteOwnerSemanticAsync(
                        item.Request.ForOperation(SemanticEntityWriteOperation.Create().OperationId).Export()),
                        "Source owner coverage must survive reopening.");
                }
            }
            finally
            {
                foreach (var node in reopened)
                {
                    await node.DisposeAsync();
                }
            }
        });
    }

    [TestMethod]
    public async Task Semantic_direct_owner_rechecks_policy_publication_after_probe_and_falls_back_on_later_saves()
    {
        await WithReductionOwners(async (nodes, authority, client) =>
        {
            var captured = await ConfinedSemanticRequest(client, nodes[1]);
            Assert.IsTrue(await ((IPartitionTransactionParticipant)nodes[1]).CanExecuteOwnerSemanticAsync(captured.Export()));
            var local = captured.ForOperation(PartitionOwnerOperation.New(nodes[1].Routing, nodes[1].Domain.Partition));
            var policy = new SemanticGovernanceRuleDefinition(new("urn:test:direct-policy"), SemanticGovernanceRuleKind.Policy,
                new("urn:test:definition"), "1", false);
            var publication = await client.PublishSemanticGovernanceAsync(SemanticEntityWriteOperation.Create().OperationId, nodes[3].Domain.Partition, policy);
            Assert.AreEqual(nodes.Length, publication.Members.Count);
            Assert.IsFalse(await ((IPartitionTransactionParticipant)nodes[1]).CanExecuteOwnerSemanticAsync(captured.Export()));
            var blocked = await Assert.ThrowsAsync<EntityOperationAbortedException>(() => client.DeliverAsync(local.OperationId, local.Export()).AsTask());
            Assert.IsInstanceOfType<EntityForgeDbCommitException>(blocked.InnerException);
            Assert.IsInstanceOfType<NotSupportedException>(blocked.InnerException!.InnerException);
            Assert.AreNotEqual(EntityOperationState.Committed, (await client.ResolveAsync(local.OperationId, default)).State);
            var coordinated = await client.DeliverAsync(captured.OperationId, captured.Export());
            CollectionAssert.AreEquivalent(new[] { nodes[1].Domain.Identity, nodes[3].Domain.Identity }, coordinated.Members.Select(m => m.Identity).ToArray());
        });
    }

    [TestMethod]
    public async Task Semantic_direct_owner_coverage_is_bound_and_late_policy_is_rejected_by_native_planning()
    {
        await WithReductionOwners(async (nodes, authority, client) =>
        {
            var node = nodes[1];
            var request = (await ConfinedSemanticRequest(client, node)).ForOperation(
                PartitionOwnerOperation.New(node.Routing, node.Domain.Partition));
            var native = typeof(PartitionOwnedNode).GetField("_native", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(node)!;
            var database = (EntityForge.Db.Semantic.Engine.SemanticDatabaseEngine)native.GetType().GetProperty("Database",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(native)!;
            var original = await PartitionSemanticPlanning.OriginalTypedAsync(request.ForDatabase(node.Domain.Incarnation));
            var coverage = new EntityForge.Db.Semantic.Durability.SemanticOwnerCoverage(node.Routing.Fingerprint, [node.Domain.Partition.Value]);
            var commit = EntityForge.Db.Semantic.Durability.DurableDatabaseCommit.ForSemanticTransaction([new(original.Session, LocalOwnerCoverage: coverage)]);
            var codec = new SemanticCommitCodec();
            var bytes = codec.Encode(commit);
            var decoded = ((ISemanticCommitDecoder)codec).Decode(default, bytes);
            Assert.AreEqual(coverage, decoded.Contributions.Single().LocalOwnerCoverage);
            CollectionAssert.AreEqual(bytes, codec.Encode(decoded));
            Assert.Throws<InvalidDataException>(() => ((ISemanticCommitDecoder)codec).Decode(default, bytes[..^1]));
            var before = node.Kernel.CurrentVersion;
            var unbound = await Assert.ThrowsAsync<EntityForgeDbCommitException>(async () =>
                await node.Authority.ExecuteAsync(async () =>
                {
                    await node.Kernel.CommitAsync(request.OperationId, bytes);
                    return true;
                }));
            Assert.IsInstanceOfType<InvalidDataException>(unbound.InnerException);
            Assert.AreEqual(before, node.Kernel.CurrentVersion);
            var operations = ((IEntityOperationHost)database).Operations;
            operations.AfterBindingPersisted = () => throw new IOException("Pause after exact native binding.");
            await Assert.ThrowsAsync<IOException>(() => client.DeliverAsync(request.OperationId, request.Export()).AsTask());
            operations.AfterBindingPersisted = null;
            var plan = (await operations.FindAsync(request.OperationId, default))!.Plan;
            var metadata = EntityRecordDeliveryCodec.Decode<SemanticEntityDeliveryCodec.Metadata>(plan).Metadata;
            foreach (var manifest in metadata.Manifests)
            {
                await database.Mappings().RegisterAsync(manifest);
            }

            Assert.AreEqual(coverage, metadata.LocalOwnerCoverage);
            var boundSession = SemanticEntityDeliveryCodec.Session(plan);
            bytes = codec.Encode(EntityForge.Db.Semantic.Durability.DurableDatabaseCommit.ForSemanticTransaction([new(boundSession, LocalOwnerCoverage: coverage)]));
            using (var state = await database.AcquireCurrentDistributedStateAsync(default))
            {
                EntityForge.Db.Semantic.Engine.SemanticPartitionDependencies.Validate(state.State, commit);
            }

            var policy = new SemanticGovernanceRuleDefinition(new("urn:test:late-native-policy"), SemanticGovernanceRuleKind.Policy,
                new("urn:test:definition"), "1", false);
            await client.PublishSemanticGovernanceAsync(SemanticEntityWriteOperation.Create().OperationId, nodes[3].Domain.Partition, policy);
            before = node.Kernel.CurrentVersion;
            await Assert.ThrowsAsync<EntityForge.Db.Kernel.DatabasePartitionConflictException>(async () =>
                await node.Authority.ExecuteAsync(async () =>
                {
                    await node.Kernel.CommitAsync(request.OperationId, bytes);
                    return true;
                }));
            Assert.AreEqual(before, node.Kernel.CurrentVersion);
            Assert.IsNull((await operations.FindAsync(request.OperationId, default))!.Committed);
        });
    }

    [TestMethod]
    public async Task Semantic_direct_owner_preserves_guarded_predecessor_and_wrong_owner_rejection()
    {
        await WithReductionOwners(async (nodes, authority, client) =>
        {
            var node = nodes[2];
            var candidate = await ConfinedSemanticRequest(client, node);
            var original = await PartitionSemanticPlanning.OriginalTypedAsync(candidate);
            string key = ((StringSemanticValue)original.Session.Pages.Single().Changes.Single().Subject.Value).Value;
            var wrong = candidate.ForOperation(PartitionOwnerOperation.New(node.Routing, nodes[0].Domain.Partition));
            var versions = nodes.Select(n => n.Kernel.CurrentVersion).ToArray();
            await Assert.ThrowsAsync<InvalidDataException>(() => client.DeliverAsync(wrong.OperationId, wrong.Export()).AsTask());
            CollectionAssert.AreEqual(versions, nodes.Select(n => n.Kernel.CurrentVersion).ToArray());
            var first = candidate.ForOperation(PartitionOwnerOperation.New(node.Routing, node.Domain.Partition));
            await client.DeliverAsync(first.OperationId, first.Export());
            original = await PartitionSemanticPlanning.OriginalTypedAsync(first);
            using var http = new HttpClient();
            var store = new HttpSemanticEntityClient(http, client.Generation).EntityStore<Item>();
            SemanticEntityRequest Capture(string name) => store.CaptureSave(new(key, name),
                new(PartitionOwnerOperation.New(node.Routing, node.Domain.Partition), DateTimeOffset.UtcNow),
                SemanticSourceProgressPrecondition.After(original.Session.Start.Source.Position));
            var successor = Capture("successor");
            await client.DeliverAsync(successor.OperationId, successor.Export());
            var stale = Capture("must not commit");
            var rejected = await Assert.ThrowsAsync<EntityOperationAbortedException>(() => client.DeliverAsync(stale.OperationId, stale.Export()).AsTask());
            Assert.IsInstanceOfType<SemanticSourceProgressConflictException>(rejected.InnerException);
            StringAssert.Contains(rejected.InnerException!.Message, "predecessor");
            Assert.AreNotEqual(EntityOperationState.Committed, (await client.ResolveAsync(stale.OperationId, default)).State);
            Assert.AreEqual(DatabaseCommitOutcome.IdempotentReplay,
                (await client.DeliverAsync(successor.OperationId, successor.Export())).Members.Single().Receipt.Outcome);
        });
    }

}
