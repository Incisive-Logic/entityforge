using EntityForge.Db.Semantic;
using EntityForge.Db.Semantic.Durability;
using EntityForge.Db.Kernel;
using EntityForge.Semantics;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    private async Task WithReductionOwners(Func<PartitionOwnedNode[], FilePartitionTransactionAuthority, PartitionApplicationClient, Task> action,
        DatabaseSharedTimestampService? timestamps = null)
    {
        var routing = new StableHashDistributedShardRouter(new(new(1), [new("a"), new("b"), new("c"), new("d")])).Binding;
        var initial = new Authority();
        await using var authority = FilePartitionTransactionAuthority.Open(Path.Combine(_root, "authority"));
        var nodes = new List<PartitionOwnedNode>();
        try
        {
            foreach (var shard in routing.Shards)
            {
                var placement = new Authority
                {
                    Lease = initial.Lease with
                    {
                        Placement = initial.Lease.Placement with
                        {
                            Claim = initial.Lease.Placement.Claim with
                            {
                                Shard = shard,
                                Owner = new("owner-" + shard.Value)
                            }
                        }
                    }
                };
                nodes.Add(await PartitionOwnedNode.CreateAsync(Path.Combine(_root, shard.Value), DatabaseProfileId.Semantic,
                    placement.Lease, routing, placement, Options, transactionAuthority: authority, timestamps: timestamps,
                    timestampClock: timestamps is null ? ReadClock : null));
            }
            await action(nodes.ToArray(), authority, new(authority, routing, nodes, timestamps is null ? ReadClock : null));
        }
        finally
        {
            foreach (var node in nodes)
            {
                await node.DisposeAsync();
            }
        }
    }

    private static async Task<SemanticEntityRequest> ConfinedSemanticRequest(PartitionApplicationClient client, PartitionOwnedNode node)
    {
        using var http = new HttpClient();
        var store = new HttpSemanticEntityClient(http, client.Generation).EntityStore<Item>();
        const int maximumCandidates = 512;
        for (int i = 0; i < maximumCandidates; i++)
        {
            var request = store.CaptureSave(new("reduce-" + i, "complete value"), SemanticEntityWriteOperation.Create());
            var original = await PartitionSemanticPlanning.OriginalTypedAsync(request);
            if (ProfilePartitionRouting.SemanticHome(node.Routing, original.Session.Start.Source.SourceSystem,
                    original.Session.Start.Source.SourcePartition) == node.Domain.Partition &&
                original.Session.Pages.SelectMany(p => p.Changes).All(c => node.Routing.Route(DistributedDatabaseProfile.Semantic,
                    ProfilePartitionRouting.SemanticOwner(c.Subject)) == node.Domain.Partition))
            {
                return request;
            }
        }
        throw new AssertFailedException("No deterministic single-owner test candidate.");
    }

    private static async Task<string> CrossOwnerSemanticKeyAsync(PartitionOwnedNode node)
    {
        using var http = new HttpClient();
        var store = new HttpSemanticEntityClient(http, node.Domain.Generation).EntityStore<Item>();
        const int maximumCandidates = 512;
        for (int i = 0; i < maximumCandidates; i++)
        {
            string key = "cross-owner-" + i;
            var original = await PartitionSemanticPlanning.OriginalTypedAsync(store.CaptureSave(new(key, "value"), SemanticEntityWriteOperation.Create()));
            if (ProfilePartitionRouting.SemanticHome(node.Routing, original.Session.Start.Source.SourceSystem, original.Session.Start.Source.SourcePartition) != node.Domain.Partition ||
                original.Session.Pages.SelectMany(p => p.Changes).Any(c => node.Routing.Route(DistributedDatabaseProfile.Semantic,
                    ProfilePartitionRouting.SemanticOwner(c.Subject)) != node.Domain.Partition))
            {
                return key;
            }
        }
        throw new AssertFailedException("Missing cross-owner candidate.");
    }

    [TestMethod]
    public async Task Semantic_directory_certificate_rejects_an_omitted_relationship_dependency_owner()
    {
        await WithReductionOwners(async (nodes, authority, client) =>
        {
            using var http = new HttpClient();
            var store = new HttpSemanticEntityClient(http, client.Generation).EntityStore<SemanticReferenceParent>();
            var routing = nodes[0].Routing;
            SemanticEntityRequest? request = null;
            string? targetOwner = null;
            const int MaximumCandidates = 512;
            for (int i = 0; i < MaximumCandidates; i++)
            {
                var candidate = store.CaptureSave(new()
                {
                    Id = "parent-" + i,
                    Name = "parent",
                    Peer = new("peer-" + i, "peer")
                }, SemanticEntityWriteOperation.Create());
                var original = await PartitionSemanticPlanning.OriginalTypedAsync(candidate);
                var changes = original.Session.Pages.SelectMany(page => page.Changes).ToArray();
                var baseOwners = changes.Select(change => routing.Route(DistributedDatabaseProfile.Semantic, ProfilePartitionRouting.SemanticOwner(change.Subject)).Value)
                    .Append(PartitionSemanticGovernance.Home(routing).Value)
                    .Append(ProfilePartitionRouting.SemanticHome(routing, original.Session.Start.Source.SourceSystem, original.Session.Start.Source.SourcePartition).Value).ToHashSet();
                targetOwner = changes.SelectMany(change => change.Relationships).Select(change => change.Target)
                    .OfType<EntityForge.Semantics.SemanticEntityIdentity>()
                    .Select(target => routing.Route(DistributedDatabaseProfile.Semantic, ProfilePartitionRouting.SemanticOwner(target)).Value)
                    .FirstOrDefault(owner => !baseOwners.Contains(owner));
                if (targetOwner is not null)
                {
                    request = candidate;
                    break;
                }
            }
            Assert.IsNotNull(request);
            Assert.IsNotNull(targetOwner);
            var binding = await PartitionSemanticPlanning.SelectAsync(client.Generation, request.OperationId, routing, nodes, [request.Export()], default);
            var evidence = PartitionSemanticPlanning.Read(binding, routing);
            Assert.IsTrue(evidence.Expansions.Any(entry => entry.Owner.Partition == targetOwner));
            var retained = evidence.Expansions.Where(entry => entry.Owner.Partition != targetOwner).ToArray();
            var damaged = new PartitionSemanticPlanning.Evidence(new(evidence.Position.Positions.Where(position => position.Identity.Partition != targetOwner).ToArray()), retained);
            var forged = new PartitionApplicationBinding(binding.Operation, binding.Owners.Where(owner => owner.Partition != targetOwner).ToArray(), binding.Requests,
                planningEvidence: PartitionSelectionCodec.EncodeEvidence(damaged));
            Assert.ThrowsExactly<InvalidDataException>(() => PartitionSemanticPlanning.ReadValidated(forged, routing));
        });
    }

    [TestMethod]
    public async Task Semantic_source_directory_recovers_prior_child_owners_for_snapshot_removal()
    {
        await WithReductionOwners(async (nodes, authority, client) =>
        {
            using var http = new HttpClient();
            var store = new HttpSemanticEntityClient(http, client.Generation).EntityStore<PartitionSemanticTree>();
            var tree = new PartitionSemanticTree
            {
                Id = "directory-parent",
                Name = "before",
                Children = Enumerable.Range(0, 8).Select(i => new PartitionSemanticLeaf("child-" + i, "value-" + i)).ToList()
            };
            var seed = store.CaptureSave(tree, SemanticEntityWriteOperation.Create());
            var original = await PartitionSemanticPlanning.OriginalTypedAsync(seed);
            var oldSubjects = original.Session.Pages.SelectMany(page => page.Changes).Select(change => change.Subject).ToArray();
            var routing = nodes[0].Routing;
            var oldOwners = oldSubjects.Select(subject => routing.Route(DistributedDatabaseProfile.Semantic,
                ProfilePartitionRouting.SemanticOwner(subject)).Value).ToHashSet();
            Assert.IsTrue(oldOwners.Count > 1, "The fixture must span actual child owners.");
            await client.DeliverAsync(seed.OperationId, seed.Export());
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
                    reopened.Add(await PartitionOwnedNode.OpenAsync(
                    Path.Combine(_root, nodes[i].Domain.Partition.Value), DatabaseProfileId.Semantic, leases[i], routing,
                    new Authority { Lease = leases[i] }, Options, transactionAuthority: authority, timestampClock: ReadClock));
                }

                tree.Children.Clear();
                tree.Name = "after";
                var removal = store.CaptureSave(tree, SemanticEntityWriteOperation.Create());
                var binding = await PartitionSemanticPlanning.SelectAsync(client.Generation, removal.OperationId, routing,
                    reopened, [removal.Export()], default);
                var evidence = PartitionSemanticPlanning.ReadValidated(binding, routing);
                Assert.IsTrue(oldOwners.IsSubsetOf(evidence.Expansions.Select(item => item.Owner.Partition).ToHashSet()));
                var removed = evidence.Expansions.SelectMany(item => item.Contributions).SelectMany(c => c.Session.Pages)
                    .SelectMany(page => page.Changes).Where(change => change.Kind == EntityForge.Semantics.SemanticEntityChangeKind.RetractSourceEntity).ToArray();
                var nextOriginal = await PartitionSemanticPlanning.OriginalTypedAsync(removal);
                var remainingSubjects = nextOriginal.Session.Pages.SelectMany(page => page.Changes).Select(change => change.Subject).ToArray();
                CollectionAssert.AreEquivalent(oldSubjects.Except(remainingSubjects).ToArray(), removed.Select(change => change.Subject).ToArray(),
                    "Every absent child and owned-slot entity must be retracted at its owner.");
                var recoveredClient = new PartitionApplicationClient(authority, routing, reopened, ReadClock);
                await recoveredClient.DeliverAsync(removal.OperationId, removal.Export());
                Assert.IsTrue(reopened.All(node => node.UnreleasedReadCutCount == 0));
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
    public async Task Semantic_durable_owner_discovery_expands_only_covered_owners_and_keeps_fresh_mapping_validation()
    {
        await WithReductionOwners(async (nodes, authority, client) =>
        {
            var request = await ConfinedSemanticRequest(client, nodes[0]);
            var expansions = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
            var mappings = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
            var peers = nodes.Select(owner => new PhaseParticipant(owner)
            {
                BeforeRead = (query, token) =>
                {
                    if ((query.Kind == "validate-project" ? query.Projection?.Kind : query.Kind) == "semantic-expansion")
                    {
                        expansions.AddOrUpdate(owner.Domain.Partition.Value, 1, (_, count) => count + 1);
                    }

                    if (query.Kind == "semantic-mapping-validation")
                    {
                        mappings.AddOrUpdate(owner.Domain.Partition.Value, 1, (_, count) => count + 1);
                    }

                    return ValueTask.CompletedTask;
                }
            }).ToArray();
            var binding = await PartitionSemanticPlanning.SelectAsync(client.Generation, request.OperationId, nodes[0].Routing,
                peers, [request.Export()], default);
            CollectionAssert.AreEqual(new[] { nodes[0].Domain.Partition.Value }, expansions.Keys.ToArray());
            CollectionAssert.AreEquivalent(nodes.Skip(1).Select(node => node.Domain.Partition.Value).ToArray(), mappings.Keys.ToArray());
            var evidence = PartitionSemanticPlanning.ReadValidated(binding, nodes[0].Routing);
            Assert.AreEqual(1, evidence.Expansions.Length);
            var coverage = evidence.Expansions.Single().Contributions.Single().Read!.OwnerCoverage!;
            CollectionAssert.AreEqual(new[] { nodes[0].Domain.Partition.Value }, coverage.Owners.ToArray());
            Assert.AreEqual(nodes[0].Routing.Fingerprint, coverage.Routing);
            await client.DeliverAsync(request.OperationId, request.Export());
            Assert.IsTrue(nodes.All(node => node.UnreleasedReadCutCount == 0));
            // Current source progress must carry the directory through a native checkpoint.
            var native = typeof(PartitionOwnedNode).GetField("_native", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(nodes[0])!;
            var database = (EntityForge.Db.Semantic.Engine.SemanticDatabaseEngine)native.GetType().GetProperty("Database", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(native)!;
            using var retained = await database.AcquireCurrentDistributedStateAsync(default);
            var original = await PartitionSemanticPlanning.OriginalTypedAsync(request);
            var source = original.Session.Start.Source;
            Assert.AreEqual(coverage, retained.State.SourceProgress[new(source.SourceSystem, source.SourcePartition)].Coverage);
        });
    }

    [TestMethod]
    public async Task Semantic_mapped_source_and_subject_share_owner_without_unrelated_native_writes()
    {
        await WithReductionOwners(async (nodes, authority, client) =>
        {
            foreach (var owner in nodes)
            {
                var request = await ConfinedSemanticRequest(client, owner);
                await client.PrepareAsync(request.OperationId, [request.Export()], default);
                var binding = (await authority.ReadApplicationAsync(request.OperationId))!;
                CollectionAssert.AreEquivalent(new[] { owner.Domain.Identity }, binding.Owners.ToArray());
                var omitted = nodes.Where(node => node != owner).ToDictionary(node => node, node => node.Kernel.CurrentVersion);
                Assert.AreEqual(1, (await client.DeliverAsync(request.OperationId, request.Export())).Members.Count);
                foreach (var pair in omitted)
                {
                    Assert.AreEqual(pair.Value, pair.Key.Kernel.CurrentVersion);
                    Assert.IsFalse((await pair.Key.Kernel.PartitionTransactions.ReadAsync()).Any(t => t.Operation == request.OperationId));
                }
            }
        });
    }

    [TestMethod]
    public async Task Semantic_multiowner_reduction_omits_native_writes_and_replays_exact_selection()
    {
        await WithReductionOwners(async (nodes, authority, client) =>
        {
            var request = await ConfinedSemanticRequest(client, nodes[0]);
            await client.PrepareAsync(request.OperationId, [request.Export()], default);
            var binding = (await authority.ReadApplicationAsync(request.OperationId))!;
            CollectionAssert.AreEqual(new[] { nodes[0].Domain.Identity }, binding.Owners.ToArray());
            Assert.IsTrue(binding.NativeLocal);
            Assert.IsNull(await authority.ReadPlanAsync(request.OperationId));
            Assert.IsNull(await authority.ReadDecisionAsync(request.OperationId));
            var before = nodes.Select(n => n.Kernel.CurrentVersion.Value).ToArray();
            var result = await client.DeliverAsync(request.OperationId, request.Export());
            Assert.IsNull(await authority.ReadPlanAsync(request.OperationId));
            Assert.IsNull(await authority.ReadDecisionAsync(request.OperationId));
            Assert.AreEqual(1, result.Members.Count);
            Assert.AreEqual(before[0] + 1, nodes[0].Kernel.CurrentVersion.Value);
            for (int i = 1; i < nodes.Length; i++)
            {
                Assert.AreEqual(before[i], nodes[i].Kernel.CurrentVersion.Value);
                Assert.IsFalse((await nodes[i].Kernel.PartitionTransactions.ReadAsync()).Any(t => t.Operation == request.OperationId));
            }
            var replay = await client.DeliverAsync(request.OperationId, request.Export());
            Assert.AreEqual(DatabaseCommitOutcome.IdempotentReplay, replay.Members.Single().Receipt.Outcome);
            Assert.AreEqual(result.Members.Single().Receipt.Version, replay.Members.Single().Receipt.Version);
            var original = await PartitionSemanticPlanning.OriginalTypedAsync(request);
            string id = ((StringSemanticValue)original.Session.Pages.SelectMany(p => p.Changes).Single().Subject.Value).Value;
            var reopened = new List<PartitionOwnedNode>();
            try
            {
                foreach (var node in nodes)
                {
                    var lease = node.CurrentLease;
                    await node.DisposeAsync();
                    var placement = new Authority { Lease = lease };
                    reopened.Add(await PartitionOwnedNode.OpenAsync(Path.Combine(_root, node.Domain.Partition.Value),
                        DatabaseProfileId.Semantic, lease, node.Routing, placement, Options,
                        transactionAuthority: authority, timestampClock: ReadClock));
                }
                var recoveredClient = new PartitionApplicationClient(authority, nodes[0].Routing, reopened, ReadClock);
                using var certificate = PartitionCertificate();
                using var http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, observed, _, _) => observed?.Thumbprint == certificate.Thumbprint });
                await using var service = new PartitionApplicationHttpService(recoveredClient);
                await using var gateway = await StartApplicationHostAsync(service, certificate);
                var remote = new HttpPartitionEntityClient(http, PartitionAddress(gateway), PartitionToken, client.Generation, DatabaseProfileId.Semantic);
                Assert.AreEqual("complete value", (await remote.ReadSemanticAsync<Item>(id)).Value!.Name);
                var recoveredReplay = await recoveredClient.DeliverAsync(request.OperationId, request.Export());
                Assert.AreEqual(1, recoveredReplay.Members.Count);
                Assert.AreEqual(DatabaseCommitOutcome.IdempotentReplay, recoveredReplay.Members.Single().Receipt.Outcome);
                var store = new HttpSemanticEntityClient(http, client.Generation).EntityStore<Item>();
                var guarded = store.CaptureSave(new(id, "guarded successor"), SemanticEntityWriteOperation.Create(),
                    SemanticSourceProgressPrecondition.After(original.Session.Start.Source.Position));
                Assert.AreEqual(1, (await recoveredClient.DeliverAsync(guarded.OperationId, guarded.Export())).Members.Count);
                Assert.AreEqual("guarded successor", (await remote.ReadSemanticAsync<Item>(id)).Value!.Name);
                var stale = store.CaptureSave(new(id, "never"), SemanticEntityWriteOperation.Create(),
                    SemanticSourceProgressPrecondition.After(original.Session.Start.Source.Position));
                var rejected = await Assert.ThrowsAsync<AggregateException>(() => recoveredClient.DeliverAsync(stale.OperationId, stale.Export()).AsTask());
                Console.WriteLine(rejected);
                Assert.IsTrue(rejected.Flatten().InnerExceptions.Any(e => e is InvalidOperationException &&
                    e.Message.Contains("predecessor", StringComparison.Ordinal)));
                Assert.AreEqual("guarded successor", (await remote.ReadSemanticAsync<Item>(id)).Value!.Name);
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

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Semantic_policy_publication_at_omitted_owner_is_atomic_with_reduced_preparation(bool alreadyPrepared)
    {
        await WithReductionOwners(async (nodes, authority, client) =>
        {
            var request = await ConfinedSemanticRequest(client, nodes[0]);
            var selection = await PartitionSemanticPlanning.SelectAsync(client.Generation, request.OperationId,
                nodes[0].Routing, nodes, [request.Export()], default);
            Assert.AreEqual(1, selection.Owners.Count);
            await authority.BindApplicationAsync(selection);
            var plan = PartitionSemanticPlanning.Plan(selection, nodes[0].Routing, nodes.Select(n => n.Domain).ToArray());
            await authority.BindAsync(plan);
            if (alreadyPrepared)
            {
                await nodes[0].PrepareNativeAsync(plan);
            }

            var policy = new SemanticGovernanceRuleDefinition(new("urn:test:new-remote-policy"),
                SemanticGovernanceRuleKind.Policy, new("urn:test:definition"), "1", false);
            Guid publication = SemanticEntityWriteOperation.Create().OperationId;
            if (alreadyPrepared)
            {
                var failure = await Assert.ThrowsAsync<EntityForge.Db.Mapping.EntityOperationAbortedException>(() =>
                    client.PublishSemanticGovernanceAsync(publication, nodes[3].Domain.Partition, policy).AsTask());
                Assert.IsInstanceOfType<DatabasePartitionConflictException>(failure.InnerException);
                Assert.IsFalse((await authority.ReadDecisionAsync(publication))!.Commit);
            }
            else
            {
                var published = await client.PublishSemanticGovernanceAsync(publication, nodes[3].Domain.Partition, policy);
                Assert.AreEqual(nodes.Length, published.Members.Count);
                var repeated = await client.PublishSemanticGovernanceAsync(publication, nodes[3].Domain.Partition, policy);
                Assert.IsTrue(repeated.Members.All(m => m.Receipt.Outcome == DatabaseCommitOutcome.IdempotentReplay));
            }
            var outcome = await new PartitionTransactionCoordinator(authority).ExecuteAsync(plan, [nodes[0]]);
            Assert.AreEqual(alreadyPrepared, outcome.Decision.Commit);
            if (!alreadyPrepared)
            {
                Assert.IsInstanceOfType<DatabasePartitionConflictException>(outcome.PreparationFailure);
            }

            foreach (var node in nodes)
            {
                Assert.IsFalse((await node.Kernel.PartitionTransactions.ReadAsync()).Any(t =>
                t.State == EntityForge.Db.Durability.DurablePartitionTransactionState.Prepared));
            }

            await Assert.ThrowsAsync<NotSupportedException>(() => nodes[3].PublishSemanticGovernanceAsync(policy).AsTask());
        });
    }

    [TestMethod]
    public async Task Semantic_policy_preparation_and_partial_decision_delivery_recover_with_remote_enforcement()
    {
        await WithReductionOwners(async (nodes, authority, client) =>
        {
            var request = await ConfinedSemanticRequest(client, nodes[0]);
            var original = await PartitionSemanticPlanning.OriginalTypedAsync(request);
            var policy = new SemanticGovernanceRuleDefinition(new("urn:test:recovered-policy"), SemanticGovernanceRuleKind.Policy,
                new("urn:test:definition"), "1", true,
                new(original.Session.Pages.SelectMany(p => p.Changes).Select(c => c.Subject.Concept).Distinct().ToArray(),
                    SemanticGovernanceEnforcement.RejectViolation));
            Guid publication = SemanticEntityWriteOperation.Create().OperationId;
            byte[] capture = PartitionSemanticGovernance.Encode(nodes[3].Domain.Partition, policy);
            var members = nodes.Select(node => new PartitionTransactionMember(node.Domain,
                node.Kernel.CurrentVersion, PartitionSemanticGovernance.Payload(capture, publication, node.Domain.Partition, node.Routing, 0))).ToArray();
            var plan = new PartitionTransactionPlan(publication, nodes[0].Routing.MapVersion, members);
            var binding = new PartitionApplicationBinding(publication, members.Select(m => m.Domain.Identity).ToArray(),
                [capture], planningEvidence: PartitionSelectionCodec.EncodePlan(plan));
            _ = PartitionSemanticGovernance.Plan(binding, nodes[0].Routing);
            var incomplete = new PartitionTransactionPlan(publication, nodes[0].Routing.MapVersion, [members[1]]);
            var incompleteBinding = new PartitionApplicationBinding(publication, [members[1].Domain.Identity], [capture],
                planningEvidence: PartitionSelectionCodec.EncodePlan(incomplete));
            Assert.Throws<InvalidDataException>(() => PartitionSemanticGovernance.Plan(incompleteBinding, nodes[0].Routing));
            await authority.BindApplicationAsync(binding);
            await authority.BindAsync(plan);
            foreach (var node in nodes)
            {
                await node.PrepareNativeAsync(plan);
            }

            var leases = nodes.Select(n => n.CurrentLease).ToArray();
            foreach (var node in nodes)
            {
                await node.DisposeAsync();
            }

            var reopened = new List<PartitionOwnedNode>();
            async Task OpenAll()
            {
                for (int i = 0; i < nodes.Length; i++)
                {
                    reopened.Add(await PartitionOwnedNode.OpenAsync(Path.Combine(_root, nodes[i].Domain.Partition.Value),
                        DatabaseProfileId.Semantic, leases[i], nodes[i].Routing, new Authority { Lease = leases[i] }, Options,
                        transactionAuthority: authority, timestampClock: ReadClock));
                }
            }
            try
            {
                await OpenAll();
                Assert.IsNull(await authority.ReadDecisionAsync(publication));
                foreach (var node in reopened)
                {
                    Assert.AreEqual(
                    EntityForge.Db.Durability.DurablePartitionTransactionState.Prepared,
                    (await node.Kernel.PartitionTransactions.ReadAsync()).Single(t => t.Operation == publication).State);
                }

                await SelectTimestampDecisionAsync(authority, plan, reopened.ToArray());
                await reopened[0].ApplyPartitionDecisionAsync(publication);
                foreach (var node in reopened)
                {
                    await node.DisposeAsync();
                }

                reopened.Clear();
                await OpenAll();
                var recoveredClient = new PartitionApplicationClient(authority, nodes[0].Routing, reopened, ReadClock);
                Assert.AreEqual(nodes.Length, (await recoveredClient.PublishSemanticGovernanceAsync(publication, nodes[3].Domain.Partition, policy)).Members.Count);
                var selection = await PartitionSemanticPlanning.SelectAsync(client.Generation, request.OperationId, nodes[0].Routing,
                    reopened, [request.Export()], default);
                CollectionAssert.AreEquivalent(new[] { nodes[0].Domain.Identity, nodes[3].Domain.Identity }, selection.Owners.ToArray());
                await Assert.ThrowsAsync<EntityForge.Db.Mapping.EntityOperationAbortedException>(() =>
                    recoveredClient.DeliverAsync(request.OperationId, request.Export()).AsTask());
                Assert.IsFalse((await authority.ReadDecisionAsync(request.OperationId))!.Commit,
                    "An omitted mutation owner still enforces its recovered policy, including missing evaluators.");
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
    public void Semantic_reduction_rejects_old_unfenced_enrollment()
    {
        var routing = Routing;
        var authority = new Authority();
        var domain = new PartitionExecutionDomain(authority.Lease.Placement.Claim.Generation.Value, routing.Shards[0],
            routing.Fingerprint, DatabaseProfileId.Semantic, Guid.NewGuid());
        var metadata = PartitionNodeMetadata.Create(domain, authority.Lease.Placement.Claim);
        metadata.Require(authority.Lease.Placement.Claim, routing);
        Assert.AreEqual(PartitionNodeMetadata.CurrentSemanticFormat, metadata.Format);
        Assert.AreNotEqual(PartitionNodeMetadata.CurrentFormat, metadata.Format, "Older binaries must reject reduced-participation stores too.");
        Assert.Throws<InvalidDataException>(() => (metadata with { Format = PartitionNodeMetadata.CurrentFormat }).Require(authority.Lease.Placement.Claim, routing));
        Assert.Throws<InvalidDataException>(() => (metadata with { SemanticParticipationVersion = null }).Require(authority.Lease.Placement.Claim, routing));
    }

}
