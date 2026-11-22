using System.Diagnostics;
using System.Text.Json;
using EntityForge.Db.Durability;
using EntityForge.Db.Semantic;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [EntityForge.Db.Mapping.EntityType("partition-node-tests", "semantic-reference-parent")]
    public sealed class SemanticReferenceParent
    {
        public string Id { get; set; } = "";

        public string Name { get; set; } = "";

        [EntityForge.Db.Mapping.EntityReference]
        public PartitionSemanticLeaf? Peer
        {
            get; set;
        }
    }

    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task Semantic_scopes_protect_removed_relationship_targets(bool alreadyPrepared, bool deleteTarget)
    {
        await WithProfileFragments(DatabaseProfileId.Semantic, async (_, client, _, _, http, nodes, authority) =>
        {
            var trees = new HttpSemanticEntityClient(http, client.Generation).EntityStore<SemanticReferenceParent>();
            var leaves = new HttpSemanticEntityClient(http, client.Generation).EntityStore<PartitionSemanticLeaf>();
            var parentProbe = trees.CaptureSave(new()
            {
                Id = "parent",
                Name = "probe"
            }, SemanticEntityWriteOperation.Create());
            var parentSubject = (await PartitionSemanticPlanning.OriginalTypedAsync(parentProbe)).Session.Pages.SelectMany(p => p.Changes).Single().Subject;
            var parentOwner = nodes[0].Routing.Route(DistributedDatabaseProfile.Semantic, ProfilePartitionRouting.SemanticOwner(parentSubject));
            string childId = Enumerable.Range(0, 1000).Select(i => "child-" + i).First(id =>
                PartitionSemanticOwnerPlan.Owner(leaves.CaptureSave(new(id, "probe"), SemanticEntityWriteOperation.Create()), nodes[0].Routing) != parentOwner);
            PartitionOwnedNode[] Participants(PartitionTransactionPlan plan) => nodes.Where(node => plan.Members.Any(m => m.Domain == node.Domain)).ToArray();
            var childSeed = leaves.CaptureSave(new(childId, "before"), SemanticEntityWriteOperation.Create());
            await client.DeliverAsync(childSeed.OperationId, childSeed.Export());
            var seed = trees.CaptureSave(new()
            {
                Id = "parent",
                Name = "before",
                Peer = new(childId, "before")
            }, SemanticEntityWriteOperation.Create());
            var parentIdentity = PartitionSemanticCodec.Original(await PartitionSemanticPlanning.OriginalAsync(seed.ForDatabase(nodes[0].Domain.Incarnation)))
                .Session.Pages.SelectMany(p => p.Changes).Single().Subject;
            var childIdentity = PartitionSemanticCodec.Original(await PartitionSemanticPlanning.OriginalAsync(childSeed.ForDatabase(nodes[0].Domain.Incarnation)))
                .Session.Pages.SelectMany(p => p.Changes).Single().Subject;
            Assert.AreNotEqual(nodes[0].Routing.Route(DistributedDatabaseProfile.Semantic, ProfilePartitionRouting.SemanticOwner(parentIdentity)),
                nodes[0].Routing.Route(DistributedDatabaseProfile.Semantic, ProfilePartitionRouting.SemanticOwner(childIdentity)),
                "The regression schedule must cross physical owners.");
            await client.DeliverAsync(seed.OperationId, seed.Export());
            async Task<PartitionTransactionPlan> Select(SemanticEntityRequest request)
            {
                var selection = await PartitionSemanticPlanning.SelectAsync(client.Generation, request.OperationId, nodes[0].Routing,
                    nodes.Cast<IPartitionTransactionParticipant>().ToArray(), [request.Export()], default);
                await authority.BindApplicationAsync(selection);
                var plan = PartitionSemanticPlanning.Plan(selection, nodes[0].Routing, nodes.Select(n => n.Domain).ToArray());
                await authority.BindAsync(plan);
                return plan;
            }
            var removal = await Select(trees.CaptureSave(new()
            {
                Id = "parent",
                Name = "after"
            }, SemanticEntityWriteOperation.Create()));
            var child = await Select(deleteTarget ? leaves.CaptureDelete(childId, SemanticEntityWriteOperation.Create()) :
                leaves.CaptureSave(new(childId, "changed"), SemanticEntityWriteOperation.Create()));
            var decoder = (ISemanticCommitDecoder)new SemanticCommitCodec();
            var removalEntities = removal.Members.SelectMany(m => decoder.Decode(m.ReadVersion, m.Payload.Span).PartitionRead!.Entities).ToHashSet();
            var childSubjects = child.Members.SelectMany(m => decoder.Decode(m.ReadVersion, m.Payload.Span).Session!.Pages)
                .SelectMany(p => p.Changes).Select(c => c.Subject).ToArray();
            Assert.IsTrue(childSubjects.Length > 0 && childSubjects.All(removalEntities.Contains),
                "The removed target must be included even though the new parent has no reference.");
            if (alreadyPrepared)
            {
                foreach (var node in Participants(removal))
                {
                    await node.PrepareNativeAsync(removal);
                }

                var rejected = await new PartitionTransactionCoordinator(authority).ExecuteAsync(child, Participants(child));
                Assert.IsFalse(rejected.Decision.Commit);
                Assert.IsInstanceOfType<EntityForge.Db.Kernel.DatabasePartitionConflictException>(rejected.PreparationFailure);
                Assert.IsTrue((await new PartitionTransactionCoordinator(authority).ExecuteAsync(removal, Participants(removal))).Decision.Commit);
            }
            else
            {
                Assert.IsTrue((await new PartitionTransactionCoordinator(authority).ExecuteAsync(child, Participants(child))).Decision.Commit);
                var rejected = await new PartitionTransactionCoordinator(authority).ExecuteAsync(removal, Participants(removal));
                Assert.IsFalse(rejected.Decision.Commit);
                Assert.IsInstanceOfType<EntityForge.Db.Kernel.DatabasePartitionConflictException>(rejected.PreparationFailure);
            }
            foreach (var node in nodes)
            {
                Assert.IsTrue((await node.Kernel.PartitionTransactions.ReadAsync())
                .All(p => p.State != DurablePartitionTransactionState.Prepared));
            }
        }, commands: true);
    }

    [TestMethod]
    public async Task Semantic_cross_owner_target_reservation_survives_reopen()
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
        await using var lifetime = await ApplicationAuthorityLifetime.OpenAsync(Path.Combine(_root, "authority"),
            a.Lease.Placement.Claim.Generation.Value, DatabaseProfileId.Semantic, routing, false);
        var authority = lifetime.Authority;
        var first = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "a"), DatabaseProfileId.Semantic,
            a.Lease, routing, a, Options, transactionAuthority: authority, timestampClock: ReadClock);
        var second = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "b"), DatabaseProfileId.Semantic,
            b.Lease, routing, b, Options, transactionAuthority: authority, timestampClock: ReadClock);
        using var http = new HttpClient();
        var leaves = new HttpSemanticEntityClient(http, first.Domain.Generation).EntityStore<PartitionSemanticLeaf>();
        var parents = new HttpSemanticEntityClient(http, first.Domain.Generation).EntityStore<SemanticReferenceParent>();
        var client = new PartitionApplicationClient(authority, routing, [first, second], ReadClock);
        async Task<PartitionTransactionPlan> Select(SemanticEntityRequest request)
        {
            var selection = await PartitionSemanticPlanning.SelectAsync(first.Domain.Generation, request.OperationId, routing,
                [first, second], [request.Export()], default);
            await authority.BindApplicationAsync(selection);
            var plan = PartitionSemanticPlanning.Plan(selection, routing, [first.Domain, second.Domain]);
            await authority.BindAsync(plan);
            return plan;
        }
        string? parentKey = null;
        var childProbe = await PartitionSemanticPlanning.OriginalTypedAsync(leaves.CaptureSave(new("child", "before"), SemanticEntityWriteOperation.Create()));
        var childOwner = routing.Route(DistributedDatabaseProfile.Semantic, ProfilePartitionRouting.SemanticOwner(childProbe.Session.Pages.Single().Changes.Single().Subject));
        for (int i = 0; i < 512; i++)
        {
            var parentProbe = await PartitionSemanticPlanning.OriginalTypedAsync(parents.CaptureSave(new()
            {
                Id = "cross-parent-" + i
            }, SemanticEntityWriteOperation.Create()));
            if (routing.Route(DistributedDatabaseProfile.Semantic, ProfilePartitionRouting.SemanticOwner(parentProbe.Session.Pages.Single().Changes.Single().Subject)) != childOwner)
            {
                parentKey = "cross-parent-" + i;
                break;
            }
        }
        Assert.IsNotNull(parentKey, "No cross-owner parent candidate.");
        try
        {
            var seed = leaves.CaptureSave(new("child", "before"), SemanticEntityWriteOperation.Create());
            await client.DeliverAsync(seed.OperationId, seed.Export());
            seed = parents.CaptureSave(new()
            {
                Id = parentKey,
                Peer = new("child", "before")
            }, SemanticEntityWriteOperation.Create());
            await client.DeliverAsync(seed.OperationId, seed.Export());
            var parent = await Select(parents.CaptureSave(new()
            {
                Id = parentKey
            }, SemanticEntityWriteOperation.Create()));
            var child = await Select(leaves.CaptureDelete("child", SemanticEntityWriteOperation.Create()));
            foreach (var node in new[] { first, second })
            {
                await node.PrepareNativeAsync(parent);
            }

            await first.DisposeAsync();
            await second.DisposeAsync();
            first = await PartitionOwnedNode.OpenAsync(Path.Combine(_root, "a"), DatabaseProfileId.Semantic,
                a.Lease, routing, a, Options, transactionAuthority: authority, timestampClock: ReadClock);
            second = await PartitionOwnedNode.OpenAsync(Path.Combine(_root, "b"), DatabaseProfileId.Semantic,
                b.Lease, routing, b, Options, transactionAuthority: authority, timestampClock: ReadClock);
            var rejected = await new PartitionTransactionCoordinator(authority).ExecuteAsync(child, new[] { first, second }.Where(n => child.Members.Any(m => m.Domain == n.Domain)).ToArray());
            Assert.IsFalse(rejected.Decision.Commit);
            Assert.IsInstanceOfType<EntityForge.Db.Kernel.DatabasePartitionConflictException>(rejected.PreparationFailure);
            Assert.IsTrue((await new PartitionTransactionCoordinator(authority).ExecuteAsync(parent, [first, second])).Decision.Commit);
            foreach (var node in new[] { first, second })
            {
                Assert.IsTrue((await node.Kernel.PartitionTransactions.ReadAsync())
                .All(p => p.State != DurablePartitionTransactionState.Prepared));
            }
        }
        finally
        {
            await first.DisposeAsync();
            await second.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task Semantic_revision_ownership_is_rechecked_under_native_admission()
    {
        await WithProfileFragments(DatabaseProfileId.Semantic, async (_, client, _, _, http, nodes, authority) =>
        {
            var store = new HttpSemanticEntityClient(http, client.Generation).EntityStore<Item>();
            var request = store.CaptureSave(new("revision", "value"), SemanticEntityWriteOperation.Create());
            var selection = await PartitionSemanticPlanning.SelectAsync(client.Generation, request.OperationId, nodes[0].Routing,
                nodes.Cast<IPartitionTransactionParticipant>().ToArray(), [request.Export()], default);
            await authority.BindApplicationAsync(selection);
            var member = await nodes[0].PlanApplicationAsync(request.OperationId);
            var sourceSession = PartitionSemanticCodec.Original(await PartitionSemanticPlanning.OriginalAsync(
                request.ForDatabase(nodes[0].Domain.Incarnation))).Session;
            var revision = EntityForge.Db.Semantic.Durability.DurableDatabaseCommit.ForContributionRevision(new(
                EntityForge.Db.Semantic.Durability.SemanticContributionRevisionChangeKind.Begin,
                sourceSession.Start.SessionId, Start: sourceSession.Start));
            await nodes[0].Kernel.CommitAsync(Guid.NewGuid(), new SemanticCommitCodec().Encode(revision));
            await Assert.ThrowsExactlyAsync<EntityForge.Db.Kernel.DatabasePartitionConflictException>(() =>
                nodes[0].Kernel.PartitionTransactions.PrepareAsync(request.OperationId, ReservationDescriptor,
                    member.ReadVersion, member.Payload).AsTask());
            await client.AbortAsync(request.OperationId, default);
            Assert.IsTrue((await nodes[0].Kernel.PartitionTransactions.ReadAsync())
                .All(p => p.State != DurablePartitionTransactionState.Prepared));
        }, commands: true);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Semantic_scopes_protect_catalogue_dependencies(bool alreadyPrepared)
    {
        await WithProfileFragments(DatabaseProfileId.Semantic, async (_, client, _, _, http, nodes, authority) =>
        {
            var store = new HttpSemanticEntityClient(http, client.Generation).EntityStore<Item>();
            var request = store.CaptureSave(new("catalogue", "value"), SemanticEntityWriteOperation.Create());
            var selection = await PartitionSemanticPlanning.SelectAsync(client.Generation, request.OperationId, nodes[0].Routing,
                nodes.Cast<IPartitionTransactionParticipant>().ToArray(), [request.Export()], default);
            await authority.BindApplicationAsync(selection);
            var plan = PartitionSemanticPlanning.Plan(selection, nodes[0].Routing, nodes.Select(n => n.Domain).ToArray());
            var participants = nodes.Where(node => plan.Members.Any(member => member.Domain == node.Domain)).ToArray();
            await authority.BindAsync(plan);
            var publication = new PartitionApplicationClient(authority, nodes[0].Routing, nodes, ReadClock);
            var policy = new SemanticGovernanceRuleDefinition(new("urn:test:dependency-policy"),
                SemanticGovernanceRuleKind.Policy, new("urn:test:definition"), "1", false);
            if (alreadyPrepared)
            {
                foreach (var node in participants)
                {
                    await node.PrepareNativeAsync(plan);
                }

                var failure = await Assert.ThrowsExactlyAsync<EntityForge.Db.Mapping.EntityOperationAbortedException>(() =>
                    publication.PublishSemanticGovernanceAsync(SemanticEntityWriteOperation.Create().OperationId, nodes[0].Domain.Partition, policy).AsTask());
                Assert.IsInstanceOfType<EntityForge.Db.Kernel.DatabasePartitionConflictException>(failure.InnerException);
            }
            else
            {
                await publication.PublishSemanticGovernanceAsync(SemanticEntityWriteOperation.Create().OperationId, nodes[0].Domain.Partition, policy);
            }

            var outcome = await new PartitionTransactionCoordinator(authority).ExecuteAsync(plan, participants);
            Assert.AreEqual(alreadyPrepared, outcome.Decision.Commit);
            if (!alreadyPrepared)
            {
                Assert.IsInstanceOfType<EntityForge.Db.Kernel.DatabasePartitionConflictException>(outcome.PreparationFailure);
            }

            foreach (var node in nodes)
            {
                Assert.IsTrue((await node.Kernel.PartitionTransactions.ReadAsync())
                .All(p => p.State != DurablePartitionTransactionState.Prepared));
            }
        }, commands: true);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Semantic_scoped_preparations_recover_independently_and_retain(bool live)
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
        await using var lifetime = await ApplicationAuthorityLifetime.OpenAsync(Path.Combine(_root, "authority"),
            a.Lease.Placement.Claim.Generation.Value, DatabaseProfileId.Semantic, routing, live);
        var authority = lifetime.Authority;
        var first = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "a"), DatabaseProfileId.Semantic,
            a.Lease, routing, a, Options, transactionAuthority: authority, timestampClock: ReadClock);
        var second = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "b"), DatabaseProfileId.Semantic,
            b.Lease, routing, b, Options, transactionAuthority: authority, timestampClock: ReadClock);
        using var http = new HttpClient();
        var store = new HttpSemanticEntityClient(http, first.Domain.Generation).EntityStore<Item>();
        var client = new PartitionApplicationClient(authority, routing, [first, second], ReadClock);
        foreach (var key in new[] { "left", "right" })
        {
            var seed = store.CaptureSave(new(key, "before"), SemanticEntityWriteOperation.Create());
            await client.DeliverAsync(seed.OperationId, seed.Export());
        }
        async Task<PartitionTransactionPlan> Select(string key)
        {
            var request = store.CaptureSave(new(key, key + "-after"), SemanticEntityWriteOperation.Create());
            var primaryOwner = PartitionSemanticOwnerPlan.Owner(request, routing);
            var other = primaryOwner == first.Domain.Partition ? second : first;
            var companion = (await ConfinedSemanticRequest(client, other)).ForOperation(request.OperationId);
            // Distinct companion source for each independent transaction.
            if (key == "right")
            {
                bool found = false;
                for (int i = 0; i < 512; i++)
                {
                    var candidate = store.CaptureSave(new("independent-companion-" + i, "value"), new(request.OperationId, DateTimeOffset.UtcNow));
                    if (PartitionSemanticOwnerPlan.Owner(candidate, routing) == other.Domain.Partition)
                    {
                        companion = candidate;
                        found = true;
                        break;
                    }
                }
                Assert.IsTrue(found, "No independent cross-owner companion candidate.");
            }
            var selection = await PartitionSemanticPlanning.SelectAsync(first.Domain.Generation, request.OperationId, routing,
                [first, second], [request.Export(), companion.Export()], default);
            await authority.BindApplicationAsync(selection);
            var plan = PartitionSemanticPlanning.Plan(selection, routing, [first.Domain, second.Domain]);
            await authority.BindAsync(plan);
            return plan;
        }
        try
        {
            var left = await Select("left");
            var right = await Select("right");
            foreach (var node in new[] { first, second })
            {
                await node.PrepareNativeAsync(left);
                await node.PrepareNativeAsync(right);
            }
            await SelectTimestampDecisionAsync(authority, left, first, second);
            await first.ApplyPartitionDecisionAsync(left.Operation); // crash between owner applications
            await first.DisposeAsync();
            await second.DisposeAsync();
            first = await PartitionOwnedNode.OpenAsync(Path.Combine(_root, "a"), DatabaseProfileId.Semantic,
                a.Lease, routing, a, Options, transactionAuthority: authority, timestampClock: ReadClock);
            second = await PartitionOwnedNode.OpenAsync(Path.Combine(_root, "b"), DatabaseProfileId.Semantic,
                b.Lease, routing, b, Options, transactionAuthority: authority, timestampClock: ReadClock);
            Assert.IsTrue((await new PartitionTransactionCoordinator(authority).ExecuteAsync(right, [first, second])).Decision.Commit);
            var replay = await new PartitionTransactionCoordinator(authority).ExecuteAsync(left, [first, second]);
            Assert.IsTrue(replay.Members.All(m => m.Receipt.Outcome == DatabaseCommitOutcome.IdempotentReplay));
            client = new(authority, routing, [first, second], ReadClock);
            using var certificate = PartitionCertificate();
            using var readHttp = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert?.Thumbprint == certificate.Thumbprint });
            await using var service = new PartitionApplicationHttpService(client);
            await using var host = await StartApplicationHostAsync(service, certificate);
            var remote = new HttpPartitionEntityClient(readHttp, PartitionAddress(host), PartitionToken, first.Domain.Generation, DatabaseProfileId.Semantic);
            Assert.AreEqual("left-after", (await remote.ReadSemanticAsync<Item>("left")).Value!.Name);
            Assert.AreEqual("right-after", (await remote.ReadSemanticAsync<Item>("right")).Value!.Name);
            await host.StopAsync();
            const int identityPrecisionDrainMilliseconds = 2;
            await Task.Delay(TimeSpan.FromMilliseconds(identityPrecisionDrainMilliseconds));
            await PartitionRetention.RetainAsync(authority, [first, second], DateTimeOffset.UtcNow);
            foreach (var node in new[] { first, second })
            {
                Assert.AreEqual(0, (await node.Kernel.PartitionTransactions.ReadAsync()).Count);
            }

            await Assert.ThrowsExactlyAsync<EntityOperationExpiredException>(() => first.PrepareNativeAsync(left).AsTask());
        }
        finally
        {
            await first.DisposeAsync();
            await second.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task Semantic_scoped_save_performance_screen()
    {
        string? output = Environment.GetEnvironmentVariable("ENTITYFORGE_SEMANTIC_SCOPE_OUTPUT");
        if (string.IsNullOrEmpty(output))
        {
            Assert.Inconclusive("Opt-in bounded save screen.");
            return;
        }
        const int records = 64, windows = 2;
        var results = new List<object>();
        await WithProfileFragments(DatabaseProfileId.Semantic, async (_, _, _, client, _, _, _) =>
        {
            for (int i = 0; i < records; i++)
            {
                await client.SaveSemanticAsync(new Item(i.ToString(), "seed"));
            }

            foreach (int concurrency in new[] { 1, 32 })
            {
                for (int window = 0; window < windows; window++)
                {
                    var errors = new System.Collections.Concurrent.ConcurrentBag<string>();
                    var watch = Stopwatch.StartNew();
                    await Task.WhenAll(Enumerable.Range(0, concurrency).Select(async worker =>
                    {
                        for (int i = worker; i < records; i += concurrency)
                        {
                            try
                            {
                                await client.SaveSemanticAsync(new Item(i.ToString(), $"value-{concurrency}-{window}-{i}"));
                            }
                            catch (Exception failure)
                            {
                                errors.Add(failure.ToString());
                            }
                        }
                    }));
                    watch.Stop();
                    results.Add(new
                    {
                        Concurrency = concurrency,
                        Window = window,
                        Attempted = records,
                        Succeeded = records - errors.Count,
                        Seconds = watch.Elapsed.TotalSeconds,
                        PerSecond = (records - errors.Count) / watch.Elapsed.TotalSeconds,
                        Errors = errors.ToArray()
                    });
                    Directory.CreateDirectory(output);
                    await File.WriteAllTextAsync(Path.Combine(output, "semantic-scoped-saves.json"),
                        JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
                    Assert.AreEqual(0, errors.Count, string.Join("\n", errors));
                    for (int i = 0; i < records; i++)
                    {
                        Assert.AreEqual($"value-{concurrency}-{window}-{i}",
                        (await client.ReadSemanticAsync<Item>(i.ToString())).Value!.Name);
                    }
                }
            }
        }, commands: true);
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "semantic-scoped-saves.json"),
            JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
    }
}
