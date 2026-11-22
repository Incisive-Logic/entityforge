using EntityForge.Db.Mapping;
using EntityForge.Db.Semantic;
using EntityForge.Db.Semantic.Durability;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [TestMethod]
    public async Task Semantic_registration_hints_remove_repeat_admission_without_changing_plans_or_values()
    {
        await WithReductionOwners(async (nodes, authority, client) =>
        {
            var request = await ConfinedSemanticRequest(client, nodes[0]);
            int admissions = 0;
            var previous = DistributedReadTelemetry.ObserveElapsedTicks;
            DistributedReadTelemetry.ObserveElapsedTicks = (stage, ticks) =>
            {
                previous?.Invoke(stage, ticks);
                if (stage == "partition.semantic-planning.mapping-initialize")
                {
                    Interlocked.Increment(ref admissions);
                }
            };
            try
            {
                var hints = new PartitionSemanticMappingAdmission();
                async Task<PartitionApplicationBinding> Select(PartitionSemanticMappingAdmission? cache) =>
                    await PartitionSemanticPlanning.SelectAsync(client.Generation, request.OperationId,
                        nodes[0].Routing, nodes, [request.Export()], default, cache);
                var first = await Select(hints);
                Assert.AreEqual(nodes.Length, admissions);
                var warm = await Select(hints);
                Assert.AreEqual(nodes.Length, admissions, "Warm planning must not repeat registration RPCs.");
                var oracle = await Select(null);
                var domains = nodes.Select(n => n.Domain).ToArray();
                Assert.AreEqual(PartitionSemanticPlanning.Plan(oracle, nodes[0].Routing, domains).Descriptor,
                    PartitionSemanticPlanning.Plan(warm, nodes[0].Routing, domains).Descriptor);
                CollectionAssert.AreEqual(first.Owners.ToArray(), warm.Owners.ToArray());
                var result = await client.DeliverAsync(request.OperationId, request.Export());
                Assert.AreEqual(1, result.Members.Count);
                var replay = await client.DeliverAsync(request.OperationId, request.Export());
                Assert.AreEqual(DatabaseCommitOutcome.IdempotentReplay, replay.Members.Single().Receipt.Outcome);
                using var http = new HttpClient();
                var store = new HttpSemanticEntityClient(http, client.Generation).EntityStore<Item>();
                var second = store.CaptureSave(new("warm-next", "complete second value"), SemanticEntityWriteOperation.Create());
                int before = admissions;
                await client.DeliverAsync(second.OperationId, second.Export());
                var secondOriginal = await PartitionSemanticPlanning.OriginalTypedAsync(second);
                var firstOriginal = await PartitionSemanticPlanning.OriginalTypedAsync(request);
                var routing = nodes[0].Routing;
                bool sameOwner = ProfilePartitionRouting.SemanticHome(routing, secondOriginal.Session.Start.Source.SourceSystem, secondOriginal.Session.Start.Source.SourcePartition)
                    == ProfilePartitionRouting.SemanticHome(routing, firstOriginal.Session.Start.Source.SourceSystem, firstOriginal.Session.Start.Source.SourcePartition);
                Assert.AreEqual(before + (sameOwner ? 0 : 1), admissions, "Registration hints are specific to participating owners.");
                before = admissions;
                var repeated = second.ForOperation(SemanticEntityWriteOperation.Create().OperationId);
                await client.DeliverAsync(repeated.OperationId, repeated.Export());
                Assert.AreEqual(before, admissions, "Repeated owner planning reuses the registration hint.");
                var different = new HttpSemanticEntityClient(http, client.Generation).EntityStore<PartitionSemanticLeaf>()
                    .CaptureSave(new("leaf", "new mapping"), SemanticEntityWriteOperation.Create());
                await client.DeliverAsync(different.OperationId, different.Export());
                Assert.AreEqual(before + 1, admissions, "A new complete mapping is registered on its actual owner.");
                using var cancelled = new CancellationTokenSource();
                cancelled.Cancel();
                await Assert.ThrowsAsync<OperationCanceledException>(() => hints.InitializeAsync(nodes[0], request.Export(), cancelled.Token).AsTask());
                var invalid = store.CaptureSave(new("invalid-new", "value"), SemanticEntityWriteOperation.Create());
                var malformed = invalid.Export()[..^1];
                await Assert.ThrowsAsync<InvalidDataException>(() => client.DeliverAsync(invalid.OperationId, malformed).AsTask());
                Assert.IsNull(await authority.ReadApplicationAsync(invalid.OperationId));
                // A well-framed capture with an already-known mapping must still
                // undergo full value/session validation at expansion.
                var tooSmall = store.CaptureSave(new("too-small", "not one byte"), SemanticEntityWriteOperation.Create());
                byte[] bounded = EntityRecordDeliveryCodec.Encode(tooSmall._metadata with
                {
                    Limits = new SemanticEntityStoreLimits(bytes: 1)
                }, tooSmall._payload);
                _ = SemanticEntityRequest.Restore(bounded);
                before = admissions;
                var beforeBoundedVersions = nodes.Select(node => node.Kernel.CurrentVersion).ToArray();
                Exception? boundedFailure = null;
                try
                {
                    await client.DeliverAsync(tooSmall.OperationId, bounded);
                }
                catch (Exception failure)
                {
                    boundedFailure = failure;
                }
                Assert.IsNotNull(boundedFailure, "Known mapping must not bypass captured value limits.");
                // Lightweight discovery does not serialize a session to check its
                // exact byte limit. A cold owner may be called, but its compiler
                // rejects before mapping registration or any native publication.
                Assert.IsTrue(admissions >= before && admissions <= before + 1);
                CollectionAssert.AreEqual(beforeBoundedVersions, nodes.Select(node => node.Kernel.CurrentVersion).ToArray());
                Assert.IsNull(await authority.ReadApplicationAsync(tooSmall.OperationId));
                Assert.IsNull(await authority.ReadPlanAsync(tooSmall.OperationId));
                // A new client has no registration hints, but exact durable replay is unchanged.
                var fresh = new PartitionApplicationClient(authority, nodes[0].Routing, nodes, ReadClock);
                Assert.AreEqual(DatabaseCommitOutcome.IdempotentReplay,
                    (await fresh.DeliverAsync(request.OperationId, request.Export())).Members.Single().Receipt.Outcome);
                using var certificate = PartitionCertificate();
                using var reader = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, observed, _, _) => observed?.Thumbprint == certificate.Thumbprint });
                await using var service = new PartitionApplicationHttpService(client);
                await using var gateway = await StartApplicationHostAsync(service, certificate);
                var remote = new HttpPartitionEntityClient(reader, PartitionAddress(gateway), PartitionToken, client.Generation, DatabaseProfileId.Semantic);
                Assert.AreEqual("complete second value", (await remote.ReadSemanticAsync<Item>("warm-next")).Value!.Name);
                Assert.AreEqual("new mapping", (await remote.ReadSemanticAsync<PartitionSemanticLeaf>("leaf")).Value!.Name);
            }
            finally
            {
                DistributedReadTelemetry.ObserveElapsedTicks = previous;
            }
        });
    }

    [TestMethod]
    public async Task Semantic_registration_hint_cannot_hide_changed_mapping_at_an_omitted_owner()
    {
        await WithReductionOwners(async (nodes, authority, client) =>
        {
            var request = await ConfinedSemanticRequest(client, nodes[0]);
            var hints = new PartitionSemanticMappingAdmission();
            var binding = await PartitionSemanticPlanning.SelectAsync(client.Generation, request.OperationId,
                nodes[0].Routing, nodes, [request.Export()], default, hints);
            Assert.AreEqual(1, binding.Owners.Count);
            var original = (await PartitionSemanticPlanning.OriginalTypedAsync(request)).Metadata.Manifests.Single();
            var added = original.Members.First(m => m.Kind != EntityMemberKind.Identity) with
            {
                Identity = "additional",
                ClrName = "Additional",
                IsNullable = true
            };
            var addedBinding = original.ProfileBindings.First(b => b.MemberIdentity == original.Members.First(m => m.Kind != EntityMemberKind.Identity).Identity)
                with
            {
                MemberIdentity = added.Identity,
                StoredBinding = "urn:entityforge:test:additional"
            };
            var upgraded = new EntityMappingManifest(original.Model, original.Convention, original.EntityType,
                original.Profile, original.ProfileTypeBinding, [.. original.Members, added], [.. original.ProfileBindings, addedBinding]);
            var commit = DurableDatabaseCommit.ForMappingRegistration(new(upgraded, original.Fingerprint));
            byte[] payload = new SemanticCommitCodec().Encode(commit);
            await nodes[3].Authority.ExecuteAsync(async () =>
            {
                await nodes[3].Kernel.CommitWithReceiptAsync(SemanticCommitCodec.TransactionId(commit, payload), payload);
                return true;
            });
            Exception? rejection = null;
            try
            {
                await PartitionSemanticPlanning.SelectAsync(client.Generation, request.OperationId,
                nodes[0].Routing, nodes, [request.Export()], default, hints);
            }
            catch (Exception failure)
            {
                rejection = failure;
            }
            bool IsMappingFailure(Exception? failure) => failure is EntityMappingMismatchException ||
                failure is AggregateException aggregate && aggregate.InnerExceptions.Any(IsMappingFailure) ||
                failure?.InnerException is { } inner && IsMappingFailure(inner);
            Assert.IsTrue(IsMappingFailure(rejection), rejection?.ToString() ?? "Stale mapping was accepted.");
            Assert.IsNull(await authority.ReadApplicationAsync(request.OperationId));
        });
    }

    [TestMethod]
    public async Task Semantic_registration_hints_are_bounded_incarnation_specific_and_never_remember_failures()
    {
        using var http = new HttpClient();
        var generation = Guid.NewGuid();
        var capture = new HttpSemanticEntityClient(http, generation).EntityStore<Item>()
            .CaptureSave(new("bounded", "value"), SemanticEntityWriteOperation.Create()).Export();
        var hints = new PartitionSemanticMappingAdmission();
        var owner = new MappingAdmissionProbe(new(generation, new("a"), "routing", DatabaseProfileId.Semantic, Guid.NewGuid()));
        owner.Fail = true;
        await Assert.ThrowsAsync<IOException>(() => hints.InitializeAsync(owner, capture, default).AsTask());
        owner.Fail = false;
        await hints.InitializeAsync(owner, capture, default);
        await hints.InitializeAsync(owner, capture, default);
        Assert.AreEqual(2, owner.Calls);
        for (int i = 0; i < PartitionSemanticMappingAdmission.MaximumEntries; i++)
        {
            var replacement = new MappingAdmissionProbe(new(generation, new("a"), "routing", DatabaseProfileId.Semantic, Guid.NewGuid()));
            await hints.InitializeAsync(replacement, capture, default);
            Assert.AreEqual(1, replacement.Calls, "A new native incarnation must not reuse admission.");
        }
        await hints.InitializeAsync(owner, capture, default);
        Assert.AreEqual(3, owner.Calls, "Eviction must restore ordinary admission.");
        var foreign = new MappingAdmissionProbe(new(Guid.NewGuid(), new("a"), "routing", DatabaseProfileId.Semantic, Guid.NewGuid()));
        await Assert.ThrowsAsync<InvalidDataException>(() => hints.InitializeAsync(foreign, capture, default).AsTask());
        Assert.AreEqual(0, foreign.Calls);
    }

    private sealed class MappingAdmissionProbe(PartitionExecutionDomain domain) : IPartitionTransactionParticipant
    {
        public PartitionExecutionDomain Domain => domain;

        public string TransactionAuthorityIdentity => "test";

        internal int Calls;
        internal bool Fail;

        public ValueTask InitializeApplicationMappingAsync(ReadOnlyMemory<byte> captured, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Calls++;
            return Fail ? ValueTask.FromException(new IOException("Admission failed.")) : ValueTask.CompletedTask;
        }

        public ValueTask<EntityForge.Db.Durability.DurableTimestampState?> PrepareWithTimestampAsync(PartitionTransactionPlan plan, CancellationToken token) => throw new NotSupportedException();

        public ValueTask<PartitionCommitResult> ExecuteMappedAsync(Guid operation, ReadOnlyMemory<byte> captured, CancellationToken token) => throw new NotSupportedException();

        public ValueTask<PartitionCommitResult> ExecuteApplicationAsync(Guid operation, CancellationToken token) => throw new NotSupportedException();

        public ValueTask<PartitionTransactionMember> PlanApplicationAsync(Guid operation, CancellationToken token) => throw new NotSupportedException();

        public ValueTask PrepareLocalApplicationAsync(Guid operation, CancellationToken token) => throw new NotSupportedException();

        public ValueTask<PartitionMemberResolution> ResolveApplicationAsync(Guid operation, bool abort, CancellationToken token) => throw new NotSupportedException();

        public ValueTask PrepareNativeAsync(PartitionTransactionPlan plan, CancellationToken token) => throw new NotSupportedException();

        public ValueTask<DatabaseCommitReceipt?> ApplyPartitionDecisionAsync(Guid operation, CancellationToken token) => throw new NotSupportedException();
    }
}
