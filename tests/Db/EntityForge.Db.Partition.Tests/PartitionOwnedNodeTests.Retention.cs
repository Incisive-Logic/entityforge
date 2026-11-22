using EntityForge.Db.Distributed.Hosting;
using EntityForge.Db.Distributed.Sharding;
using EntityForge.Db.Semantic;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [TestMethod]
    public async Task Semantic_checkpoint_diagnostic_separates_persistence_from_retention_publication()
    {
        var samples = new System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Concurrent.ConcurrentBag<double>>();
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, owner) =>
        {
            if (instrument.Name is "entityforge.db.kernel.checkpoint.phase.duration" or "entityforge.db.storage.checkpoint.phase.duration")
            {
                owner.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            string phase = "";
            foreach (var tag in tags)
            {
                if (tag.Key == "entityforge.db.phase")
                {
                    phase = (string)tag.Value!;
                }
            }

            samples.GetOrAdd(instrument.Name + "/" + phase, _ => new()).Add(value);
        });
        listener.Start();
        await AuditSemanticRetainedReopenAsync(true, 8);
        const string kernel = "entityforge.db.kernel.checkpoint.phase.duration/";
        Assert.IsTrue(samples[kernel + "persistence"].Count > 0);
        Assert.AreEqual(samples[kernel + "save"].Count, samples[kernel + "persistence"].Count);
        Assert.AreEqual(samples[kernel + "save"].Count, samples[kernel + "retention_publication"].Count);
        Assert.IsTrue(samples.Values.All(values => values.All(value => value >= 0)));
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(samples.ToDictionary(pair => pair.Key,
            pair => new { Count = pair.Value.Count, MeanMs = pair.Value.Average(), MaximumMs = pair.Value.Max() })));
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Semantic_two_owner_retention_reopen_classifies_background_version_changes(bool expireOperations)
        => await AuditSemanticRetainedReopenAsync(expireOperations, 8);

    [TestMethod]
    public async Task Semantic_recovery_subphase_diagnostic()
    {
        if (Environment.GetEnvironmentVariable("ENTITYFORGE_RECOVERY_SUBPHASE_DIAGNOSTIC") != "1")
        {
            Assert.Inconclusive("Opt-in recovery cost diagnostic.");
        }

        const int diagnosticRoots = 1024;
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, active) =>
        {
            if (instrument.Name is "entityforge.db.kernel.recovery.phase.duration" or "entityforge.db.semantic.query.stage.duration")
            {
                active.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, state) =>
        {
            string? phase = null;
            foreach (var tag in tags)
            {
                if (tag.Key is "entityforge.db.phase" or "entityforge.db.stage")
                {
                    phase = tag.Value?.ToString();
                }
            }

            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
            {
                RecoveryPhase = phase,
                Milliseconds = value
            }));
        });
        listener.Start();
        await AuditSemanticRetainedReopenAsync(true, diagnosticRoots);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Concurrent_semantic_checkpoint_reopens_full_values_and_preserves_retention(bool expireOperations)
        => await AuditSemanticRetainedReopenAsync(expireOperations, 8, concurrent: true);

    private async Task AuditSemanticRetainedReopenAsync(bool expireOperations, int roots, bool concurrent = false)
    {
        const int semanticCommitKindOffset = 0;
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
        await using var decisions = FilePartitionTransactionAuthority.Open(Path.Combine(_root, "authority"));
        string aPath = Path.Combine(_root, "a"), bPath = Path.Combine(_root, "b");
        var first = await PartitionOwnedNode.CreateAsync(aPath, DatabaseProfileId.Semantic,
            a.Lease, routing, a, Options, transactionAuthority: decisions, timestampClock: ReadClock);
        var second = await PartitionOwnedNode.CreateAsync(bPath, DatabaseProfileId.Semantic,
            b.Lease, routing, b, Options, transactionAuthority: decisions, timestampClock: ReadClock);
        try
        {
            var client = new PartitionApplicationClient(decisions, routing, [first, second], ReadClock);
            using var http = new HttpClient();
            var store = new HttpSemanticEntityClient(http, client.Generation).EntityStore<Item>();
            const int captureWidth = 8;
            const int retentionBatch = 256;
            const int operationBatchSpacingSeconds = 2;
            const int retentionFloorAdvanceSeconds = 1;
            var operationStart = DateTimeOffset.UtcNow.AddMinutes(-2);
            PartitionIndependentWrite Capture(string key, int batch = 0)
            {
                var operation = EntityOperationIds.New(expireOperations
                    ? operationStart.AddSeconds(batch * operationBatchSpacingSeconds) : DateTimeOffset.UtcNow);
                var request = store.CaptureSave(new(key, "retained"),
                    new SemanticEntityWriteOperation(operation, DateTimeOffset.UtcNow));
                return new(operation, [request.Export()]);
            }
            var seed = Capture("seed");
            await client.DeliverAsync(seed.Operation, seed.Requests);
            var writes = Enumerable.Range(0, roots).Select(i => Capture(i.ToString(), i / retentionBatch)).ToArray();
            var receipts = new List<DatabaseDistributedCommitReceipt>();
            int completedBatch = 0;
            foreach (var batch in writes.Chunk(retentionBatch))
            {
                foreach (var wave in batch.Chunk(captureWidth))
                {
                    receipts.AddRange(await Task.WhenAll(wave.Select(write => client.DeliverAsync(write.Operation, write.Requests).AsTask())));
                }

                var floor = expireOperations ? operationStart.AddSeconds(completedBatch * operationBatchSpacingSeconds + retentionFloorAdvanceSeconds) : DateTimeOffset.UtcNow.AddMinutes(-1);
                await PartitionRemoteRetention.RetainAsync(decisions, [first, second], routing, floor, captured: concurrent ? static () => ValueTask.CompletedTask : null);
                completedBatch++;
            }
            await PartitionRemoteRetention.RetainAsync(decisions, [first, second], routing, DateTimeOffset.UtcNow.AddMinutes(-1), captured: concurrent ? static () => ValueTask.CompletedTask : null);
            var versions = new[] { first.Kernel.CurrentVersion, second.Kernel.CurrentVersion };
            var domains = new[] { first.Domain, second.Domain };
            Console.WriteLine($"Retained native versions: {versions[0]}, {versions[1]}; durable: {first.Kernel.DurableVersion}, {second.Kernel.DurableVersion}");
            async Task<EntityForge.Db.Durability.DurableTransactionEnvelope[]> CaptureDurableKinds(string ownerPath, string label)
            {
                string audit = Path.Combine(_root, label + "-audit");
                Directory.CreateDirectory(audit);
                string native = Path.Combine(ownerPath, PartitionOwnedNode.NativeDirectory);
                string checkpointPath = Path.Combine(audit, "entityforge.checkpoint");
                string walPath = Path.Combine(audit, "entityforge.wal");
                File.Copy(Path.Combine(native, "entityforge.checkpoint"), checkpointPath);
                File.Copy(Path.Combine(native, "entityforge.wal"), walPath);
                var checkpoint = await new EntityForge.Db.Storage.Local.FileDatabaseCheckpointStore(checkpointPath).LoadAsync();
                await using var wal = await EntityForge.Db.Storage.Pages.GroupedRedoDatabaseCommitLog.OpenAsync(walPath);
                var transactions = await wal.RecoverAsync();
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
                {
                    Owner = label,
                    CheckpointVersion = checkpoint!.Version.Value,
                    CheckpointKinds = checkpoint.CommittedTransactions.Select(t => new { Version = t.Version.Value, Kind = t.ChangeKind.ToString() }),
                    WalKinds = transactions.Select(t => new
                    {
                        Version = t.Version.Value,
                        Kind = t.ChangeKind.ToString(),
                        SemanticKind = t.Payload.CodecId == SemanticProfileCommitLog.SemanticPayloadCodecId && !t.Payload.Content.IsEmpty
                            ? ((EntityForge.Db.Semantic.Durability.DurableDatabaseCommitKind)t.Payload.Content.Span[semanticCommitKindOffset]).ToString() : null
                    })
                }));
                return transactions.ToArray();
            }
            await CaptureDurableKinds(aPath, "a");
            await CaptureDurableKinds(bPath, "b");
            await first.DisposeAsync();
            await second.DisposeAsync();
            await CaptureDurableKinds(aPath, "a-closed");
            await CaptureDurableKinds(bPath, "b-closed");
            first = await PartitionOwnedNode.OpenAsync(aPath, DatabaseProfileId.Semantic,
                a.Lease, routing, a, Options, transactionAuthority: decisions, timestampClock: ReadClock);
            second = await PartitionOwnedNode.OpenAsync(bPath, DatabaseProfileId.Semantic,
                b.Lease, routing, b, Options, transactionAuthority: decisions, timestampClock: ReadClock);
            Console.WriteLine($"Reopened native versions: {first.Kernel.CurrentVersion}, {second.Kernel.CurrentVersion}");
            CollectionAssert.AreEqual(domains, new[] { first.Domain, second.Domain });
            Assert.IsTrue(first.Kernel.CurrentVersion.Value >= versions[0].Value);
            Assert.IsTrue(second.Kernel.CurrentVersion.Value >= versions[1].Value);
            var recovered = new PartitionApplicationClient(decisions, routing, [first, second], ReadClock);
            if (expireOperations)
            {
                await Assert.ThrowsExactlyAsync<EntityOperationExpiredException>(() =>
                    recovered.DeliverAsync(writes[0].Operation, writes[0].Requests).AsTask());
            }
            else
            {
                for (int index = 0; index < writes.Length; index++)
                {
                    var replay = await recovered.DeliverAsync(writes[index].Operation, writes[index].Requests);
                    CollectionAssert.AreEqual(receipts[index].Members.Select(m => m.Receipt.Version).ToArray(),
                        replay.Members.Select(m => m.Receipt.Version).ToArray());
                }
            }

            await first.DisposeAsync();
            await second.DisposeAsync();
            foreach (var (path, label, version) in new[] { (aPath, "a-final", versions[0]), (bPath, "b-final", versions[1]) })
            {
                foreach (var transaction in await CaptureDurableKinds(path, label))
                {
                    if (transaction.Version.Value > version.Value && transaction.ChangeKind is not
                        (EntityForge.Db.Durability.DurableCommittedChangeKind.TimestampEvidence or
                         EntityForge.Db.Durability.DurableCommittedChangeKind.PartitionEvidence))
                    {
                        Assert.AreEqual(EntityForge.Db.Durability.DurableCommittedChangeKind.System, transaction.ChangeKind);
                        Assert.AreEqual(SemanticProfileCommitLog.SemanticPayloadCodecId, transaction.Payload.CodecId);
                        Assert.AreEqual(EntityForge.Db.Semantic.Durability.DurableDatabaseCommitKind.PhysicalMaintenance,
                            (EntityForge.Db.Semantic.Durability.DurableDatabaseCommitKind)transaction.Payload.Content.Span[semanticCommitKindOffset]);
                    }
                }
            }
        }
        finally
        {
            await first.DisposeAsync();
            await second.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task Semantic_retained_checkpoint_reopen_preserves_native_version()
    {
        var placement = new Authority();
        await using var decisions = FilePartitionTransactionAuthority.Open(Path.Combine(_root, "authority"));
        string path = Path.Combine(_root, "node");
        var node = await PartitionOwnedNode.CreateAsync(path, DatabaseProfileId.Semantic,
            placement.Lease, Routing, placement, Options, transactionAuthority: decisions, timestampClock: ReadClock);
        try
        {
            var adapter = await node.CreateSemanticExecutionAsync();
            Guid operation = EntityOperationIds.New(DateTimeOffset.UtcNow.AddMinutes(-2));
            var request = adapter.CaptureSave(new Item("one", "retained"),
                new SemanticEntityWriteOperation(operation, DateTimeOffset.UtcNow));
            await adapter.ExecuteAsync(node.Domain, operation, request.Export());
            await PartitionRetention.RetainAsync(decisions, [node], DateTimeOffset.UtcNow.AddMinutes(-1));
            var version = node.Kernel.CurrentVersion;
            var domain = node.Domain;
            await node.DisposeAsync();
            node = await PartitionOwnedNode.OpenAsync(path, DatabaseProfileId.Semantic,
                placement.Lease, Routing, placement, Options, transactionAuthority: decisions, timestampClock: ReadClock);
            Assert.AreEqual(domain, node.Domain);
            Assert.AreEqual(version, node.Kernel.CurrentVersion);
            var recovered = await node.CreateSemanticExecutionAsync();
            Assert.AreEqual("retained", (await recovered.ReadAsync<Item>(node.Domain, "one"))!.Value.Name);
        }
        finally
        {
            await node.DisposeAsync();
        }
    }

    [TestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph)]
    [DataRow(DatabaseProfileId.Rdf)]
    [DataRow(DatabaseProfileId.Semantic)]
    public async Task Reopened_abandoned_owner_selections_do_not_block_repeated_retention(DatabaseProfileId profile)
    {
        var placement = new Authority();
        await using var decisions = FilePartitionTransactionAuthority.Open(Path.Combine(_root, "authority"));
        string path = Path.Combine(_root, "node");
        var node = await PartitionOwnedNode.CreateAsync(path, profile, placement.Lease, Routing, placement, Options, transactionAuthority: decisions);
        try
        {
            var start = DateTimeOffset.UtcNow.AddMinutes(-5);
            for (int epoch = 0; epoch < 3; epoch++)
            {
                Guid operation = PartitionOwnerOperation.New(Routing, new("a"), start.AddSeconds(epoch * 2));
                var binding = new EntityForge.Db.Mapping.EntityOperationBinding(operation, new string('A', 64), [1]);
                byte[] payload = EntityForge.Db.Mapping.EntityOperationCodec.Encode(binding);
                if (profile == DatabaseProfileId.Semantic)
                {
                    payload = [(byte)EntityForge.Db.Semantic.Durability.DurableDatabaseCommitKind.ApplicationOperation, .. payload];
                }

                Guid metadataId = new(System.Security.Cryptography.SHA256.HashData(payload).AsSpan(0, 16));
                await node.Kernel.CommitWithReceiptAsync(metadataId, payload);
                await node.DisposeAsync();
                node = await PartitionOwnedNode.OpenAsync(path, profile, placement.Lease, Routing, placement, Options, transactionAuthority: decisions);
                await PartitionRetention.RetainAsync(decisions, [node], start.AddSeconds(epoch * 2 + 1));
                await Assert.ThrowsExactlyAsync<EntityOperationExpiredException>(() => node.ResolveApplicationAsync(operation, false, default).AsTask());
            }
        }
        finally
        {
            await node.DisposeAsync();
        }
    }

    [DataTestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph, false)]
    [DataRow(DatabaseProfileId.Rdf, false)]
    [DataRow(DatabaseProfileId.Semantic, false)]
    [DataRow(DatabaseProfileId.PropertyGraph, true)]
    [DataRow(DatabaseProfileId.Rdf, true)]
    [DataRow(DatabaseProfileId.Semantic, true)]
    public async Task Local_mapped_retention_preserves_data_and_allows_new_saves(DatabaseProfileId profile, bool ownerIdentity)
    {
        var placement = new Authority();
        await using var decisions = FilePartitionTransactionAuthority.Open(Path.Combine(_root, "authority"));
        await using var node = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "node"), profile,
            placement.Lease, Routing, placement, Options, transactionAuthority: decisions);
        Func<Item, Guid, byte[]> capture;
        Func<Guid, byte[], ValueTask<PartitionCommitResult>> deliver;
        Func<ValueTask<string>> read;
        if (profile == DatabaseProfileId.PropertyGraph)
        {
            var adapter = await node.CreateGraphExecutionAsync();
            capture = (item, id) => adapter.CaptureSave(item, id).Export();
            deliver = (id, bytes) => adapter.ExecuteAsync(node.Domain, id, bytes);
            read = async () => (await adapter.ReadAsync<Item>(node.Domain, "one"))!.Value.Name;
        }
        else if (profile == DatabaseProfileId.Rdf)
        {
            var adapter = await node.CreateRdfExecutionAsync();
            capture = (item, id) => adapter.CaptureSave(item, id).Export();
            deliver = (id, bytes) => adapter.ExecuteAsync(node.Domain, id, bytes);
            read = async () => (await adapter.ReadAsync<Item>(node.Domain, "one"))!.Value.Name;
        }
        else
        {
            var adapter = await node.CreateSemanticExecutionAsync();
            capture = (item, id) => adapter.CaptureSave(item, new SemanticEntityWriteOperation(id, DateTimeOffset.UtcNow)).Export();
            deliver = (id, bytes) => adapter.ExecuteAsync(node.Domain, id, bytes);
            read = async () => (await adapter.ReadAsync<Item>(node.Domain, "one"))!.Value.Name;
        }
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-1);
        Guid old = ownerIdentity ? PartitionOwnerOperation.New(Routing, new("a"), cutoff.AddMinutes(-1)) : EntityOperationIds.New(cutoff.AddMinutes(-1));
        byte[] original = capture(new("one", "before"), old);
        await deliver(old, original);
        await PartitionRetention.RetainAsync(decisions, [node], cutoff);
        await Assert.ThrowsExactlyAsync<EntityOperationExpiredException>(() => deliver(old, original).AsTask());
        Assert.AreEqual("before", await read());
        Guid fresh = ownerIdentity ? PartitionOwnerOperation.New(Routing, new("a")) : EntityOperationIds.New();
        await deliver(fresh, capture(new("one", "after"), fresh));
        Assert.AreEqual("after", await read());
    }

    [TestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph)]
    [DataRow(DatabaseProfileId.Rdf)]
    [DataRow(DatabaseProfileId.Semantic)]
    public async Task Established_partition_rejects_an_empty_replacement_before_lease_renewal(DatabaseProfileId profile)
    {
        var placement = new Authority();
        await using var decisions = FilePartitionTransactionAuthority.Open(Path.Combine(_root, "authority"));
        await using var first = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "first"), profile,
            placement.Lease, Routing, placement, Options, transactionAuthority: decisions);
        int renewals = placement.Renewals;
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => PartitionOwnedNode.CreateAsync(Path.Combine(_root, "empty"),
            profile, placement.Lease, Routing, placement, Options, transactionAuthority: decisions).AsTask());
        Assert.AreEqual(renewals, placement.Renewals);
        await first.Authority.ExecuteAsync(() => ValueTask.FromResult(true));
    }

    [DataTestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph)]
    [DataRow(DatabaseProfileId.Rdf)]
    public async Task Retention_drains_abandoned_preparation_expires_replay_and_recovers_lost_reply(DatabaseProfileId profile)
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
        await using var files = FilePartitionTransactionAuthority.Open(Path.Combine(_root, "authority"));
        var decisions = new LostRetentionReply(files);
        string aPath = Path.Combine(_root, "a"), bPath = Path.Combine(_root, "b");
        await using var first = await PartitionOwnedNode.CreateAsync(aPath, profile, a.Lease, routing, a, Options, transactionAuthority: decisions);
        await using var second = await PartitionOwnedNode.CreateAsync(bPath, profile, b.Lease, routing, b, Options, transactionAuthority: decisions);
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-1);
        Guid operation = EntityOperationIds.New(cutoff.AddMinutes(-1));
        var plan = new PartitionTransactionPlan(operation, routing.MapVersion,
            [new(first.Domain, new(0), NativePayload(profile, routing, new("a"), operation)),
                new(second.Domain, new(0), NativePayload(profile, routing, new("b"), operation))]);
        await decisions.BindAsync(plan);
        await first.PrepareNativeAsync(plan);
        decisions.FailReply = true;
        await Assert.ThrowsExactlyAsync<AggregateException>(() => PartitionRetention.RetainAsync(decisions, [first, second], cutoff).AsTask());
        await PartitionRetention.RetainAsync(decisions, [first, second], cutoff);
        await Assert.ThrowsExactlyAsync<EntityOperationExpiredException>(() => decisions.BindAsync(plan).AsTask());
        await Assert.ThrowsExactlyAsync<EntityOperationExpiredException>(() => first.PrepareNativeAsync(plan).AsTask());
        Assert.AreEqual(0, (await first.Kernel.CreateCheckpointAsync()).PartitionTransactions.Count);
        Assert.AreEqual(0, (await second.Kernel.CreateCheckpointAsync()).PartitionTransactions.Count);
        Assert.AreEqual(0L, first.Kernel.CurrentVersion.Value, "An abandoned preparation is aborted, not committed by retention.");
        await first.DisposeAsync();
        await second.DisposeAsync();
        await using var reopenedA = await PartitionOwnedNode.OpenAsync(aPath, profile, a.Lease, routing, a, Options, transactionAuthority: decisions);
        await using var reopenedB = await PartitionOwnedNode.OpenAsync(bPath, profile, b.Lease, routing, b, Options, transactionAuthority: decisions);
        Guid fresh = EntityOperationIds.New();
        var freshPlan = new PartitionTransactionPlan(fresh, routing.MapVersion,
            [new(reopenedA.Domain, new(0), NativePayload(profile, routing, new("a"), fresh)),
                new(reopenedB.Domain, new(0), NativePayload(profile, routing, new("b"), fresh))]);
        var result = await new PartitionTransactionCoordinator(decisions).ExecuteAsync(freshPlan, [reopenedA, reopenedB]);
        Assert.IsTrue(result.Decision.Commit);
        Assert.IsTrue(result.Members.All(m => m.Receipt.Version.Value == 1));
    }

    private sealed class LostRetentionReply(IPartitionTransactionAuthority inner) : IPartitionTransactionAuthority
    {
        internal bool FailReply;
        internal int RetentionCalls;

        public string Identity => inner.Identity;

        public ValueTask<PartitionTransactionDecision> AbortApplicationAsync(Guid operation, string descriptor, CancellationToken token = default) => inner.AbortApplicationAsync(operation, descriptor, token);

        public ValueTask BindApplicationAsync(PartitionApplicationBinding binding, CancellationToken token = default) => inner.BindApplicationAsync(binding, token);

        public ValueTask<PartitionApplicationBinding?> ReadApplicationAsync(Guid operation, CancellationToken token = default) => inner.ReadApplicationAsync(operation, token);

        public ValueTask RequireUnenrolledAsync(PartitionAuthorityEnrollment partition, CancellationToken token = default) => inner.RequireUnenrolledAsync(partition, token);

        public ValueTask EnrollAsync(PartitionExecutionDomain domain, bool create, CancellationToken token = default) => inner.EnrollAsync(domain, create, token);

        public ValueTask BindAsync(PartitionTransactionPlan plan, CancellationToken token = default) => inner.BindAsync(plan, token);

        public ValueTask<PartitionTransactionPlan?> ReadPlanAsync(Guid id, CancellationToken token = default) => inner.ReadPlanAsync(id, token);

        public ValueTask<PartitionTransactionDecision?> ReadDecisionAsync(Guid id, CancellationToken token = default) => inner.ReadDecisionAsync(id, token);

        public ValueTask<PartitionTransactionDecision> DecideAsync(Guid id, string descriptor, bool commit, CancellationToken token = default) => inner.DecideAsync(id, descriptor, commit, token);

        public async ValueTask RetainAsync(long floor, CancellationToken token = default)
        {
            RetentionCalls++;
            await inner.RetainAsync(floor, token);
            if (FailReply)
            {
                FailReply = false;
                throw new IOException("Lost completed retention response.");
            }
        }
    }
}
