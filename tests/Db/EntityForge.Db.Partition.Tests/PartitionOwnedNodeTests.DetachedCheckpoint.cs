using System.Reflection;
using EntityForge.Db.Semantic;
using EntityForge.Db.Durability;
using EntityForge.Db.Kernel;
using EntityForge.Db.Storage;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    private sealed class PausedCheckpointStore(IDatabaseCheckpointStore inner, bool failBefore = false, bool failAfter = false)
        : IDatabaseCheckpointStore, IConcurrentDatabaseCheckpointStore
    {
        private bool FailBefore => failBefore;

        private bool FailAfter => failAfter;

        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool SupportsConcurrentPublication => true;

        public bool SupportsDeferredProfileContent => ((IConcurrentDatabaseCheckpointStore)inner).SupportsDeferredProfileContent;

        public ValueTask SaveAsync(DurableCheckpointEnvelope checkpoint, CancellationToken token = default) => inner.SaveAsync(checkpoint, token);

        public ValueTask<DurableCheckpointEnvelope?> LoadAsync(CancellationToken token = default) => inner.LoadAsync(token);

        public ValueTask DisposeAsync() => inner.DisposeAsync();

        public async ValueTask<IDatabaseCheckpointPublication> PreparePublicationAsync(DurableCheckpointEnvelope checkpoint, CancellationToken token = default) =>
            new Publication(this, await ((IConcurrentDatabaseCheckpointStore)inner).PreparePublicationAsync(checkpoint, token));

        private sealed class Publication(PausedCheckpointStore owner, IDatabaseCheckpointPublication inner) : IDatabaseDeferredCheckpointPublication
        {
            public ValueTask PublishAsync(CancellationToken token = default) => PublishCoreAsync(null, token);

            public ValueTask PublishAsync(DurableProfilePayload profile, CancellationToken token = default) => PublishCoreAsync(profile, token);

            private async ValueTask PublishCoreAsync(DurableProfilePayload? profile, CancellationToken token)
            {
                owner.Started.TrySetResult();
                await owner.Release.Task.WaitAsync(token);
                if (owner.FailBefore)
                {
                    throw new IOException("Before detached publication");
                }

                if (profile is null)
                {
                    await inner.PublishAsync(token);
                }
                else
                {
                    await ((IDatabaseDeferredCheckpointPublication)inner).PublishAsync(profile, token);
                }

                if (owner.FailAfter)
                {
                    throw new IOException("After detached publication");
                }
            }
        }
    }

    private static PausedCheckpointStore PauseCheckpoint(DatabaseKernel kernel, bool before = false, bool after = false)
    {
        var field = typeof(DatabaseKernel).GetField("_checkpointStore", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var store = new PausedCheckpointStore((IDatabaseCheckpointStore)field.GetValue(kernel)!, before, after);
        field.SetValue(kernel, store);
        return store;
    }

    [TestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph)]
    [DataRow(DatabaseProfileId.Rdf)]
    public async Task Deferred_profile_encoding_releases_write_admission_before_encoding(DatabaseProfileId profile)
    {
        string path = Path.Combine(_root, "deferred-encoding");
        var opened = await OpenReservationStore(profile, path);
        await using (opened.Database)
        {
            var kernel = opened.Kernel;
            Guid subject = Guid.NewGuid(), seed = Guid.NewGuid(), update = EntityOperationIds.New();
            await kernel.CommitAsync(seed, ReservationPayload(profile, seed, [subject], null, true));
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            DatabaseKernel.BeforeDeferredCheckpointEncoding.Value = async token =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            };
            Task retention = kernel.PartitionTransactions.RetainConcurrentlyAsync(DateTimeOffset.UtcNow.AddMinutes(-1),
                static () => ValueTask.CompletedTask).AsTask();
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await kernel.PartitionTransactions.PrepareAsync(update, ReservationDescriptor, kernel.CurrentVersion,
                    ReservationPayload(profile, update, [subject], kernel.CurrentVersion)).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                await kernel.PartitionTransactions.CompleteAsync(update, ReservationDescriptor, true).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                Assert.IsFalse(retention.IsCompleted);
            }
            finally
            {
                release.TrySetResult();
                DatabaseKernel.BeforeDeferredCheckpointEncoding.Value = null;
            }
            await retention;
            Assert.AreEqual(2L, kernel.CurrentVersion.Value);
        }
        opened = await OpenReservationStore(profile, path);
        await using (opened.Database)
        {
            Assert.AreEqual(2L, opened.Kernel.CurrentVersion.Value);
        }
    }

    [TestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph, "success")]
    [DataRow(DatabaseProfileId.Rdf, "success")]
    [DataRow(DatabaseProfileId.PropertyGraph, "before")]
    [DataRow(DatabaseProfileId.Rdf, "before")]
    [DataRow(DatabaseProfileId.PropertyGraph, "after")]
    [DataRow(DatabaseProfileId.Rdf, "after")]
    [DataRow(DatabaseProfileId.PropertyGraph, "cancel")]
    [DataRow(DatabaseProfileId.Rdf, "cancel")]
    public async Task Detached_checkpoint_allows_commits_and_recovers_the_complete_post_capture_tail(DatabaseProfileId profile, string failure)
    {
        string path = Path.Combine(_root, "detached");
        Guid subject = Guid.NewGuid(), seed = Guid.NewGuid(), update = EntityOperationIds.New();
        var opened = await OpenReservationStore(profile, path);
        await using (opened.Database)
        {
            var kernel = opened.Kernel;
            await kernel.CommitAsync(seed, ReservationPayload(profile, seed, [subject], null, true));
            await kernel.CreateCheckpointAsync();
            var paused = PauseCheckpoint(kernel, failure == "before", failure == "after");
            using var cancel = new CancellationTokenSource();
            bool captured = false;
            DatabasePartitionSnapshot? foregroundSnapshot = null;
            Task retention = kernel.PartitionTransactions.RetainConcurrentlyAsync(DateTimeOffset.UtcNow.AddMinutes(-1),
                () =>
                {
                    captured = true;
                    return ValueTask.CompletedTask;
                }, cancel.Token).AsTask();
            try
            {
                await paused.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.IsTrue(captured);
                await kernel.PartitionTransactions.PrepareAsync(update, ReservationDescriptor, kernel.CurrentVersion,
                    ReservationPayload(profile, update, [subject], kernel.CurrentVersion)).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                var receipt = await kernel.PartitionTransactions.CompleteAsync(update, ReservationDescriptor, true).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                Assert.AreEqual(2L, receipt!.Version.Value);
                foregroundSnapshot = await kernel.PartitionTransactions.CaptureSnapshotAsync();
                Assert.IsFalse(retention.IsCompleted, "Commit must finish while checkpoint publication remains paused.");
                if (failure == "cancel")
                {
                    cancel.Cancel();
                }
            }
            finally
            {
                paused.Release.TrySetResult();
            }
            try
            {
                if (failure == "success")
                {
                    await retention;
                    Assert.IsTrue(await kernel.PartitionTransactions.ValidateSnapshotAsync(foregroundSnapshot!.Evidence));
                }
                else
                {
                    await Assert.ThrowsAsync<Exception>(() => retention);
                }
            }
            finally
            {
                foregroundSnapshot?.Dispose();
            }
        }
        opened = await OpenReservationStore(profile, path);
        await using (opened.Database)
        {
            Assert.AreEqual(2L, opened.Kernel.CurrentVersion.Value);
            var replay = await opened.Kernel.PartitionTransactions.CompleteAsync(update, ReservationDescriptor, true);
            Assert.AreEqual(DatabaseCommitOutcome.IdempotentReplay, replay!.Outcome);
            Assert.AreEqual(2L, replay.Version.Value);
        }
    }

    [TestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph)]
    [DataRow(DatabaseProfileId.Rdf)]
    public async Task Detached_checkpoint_preserves_new_preparation_at_the_same_native_version(DatabaseProfileId profile)
    {
        string path = Path.Combine(_root, "post-cut-preparation");
        Guid subject = Guid.NewGuid(), seed = Guid.NewGuid(), operation = EntityOperationIds.New();
        var opened = await OpenReservationStore(profile, path);
        await using (opened.Database)
        {
            var kernel = opened.Kernel;
            await kernel.CommitAsync(seed, ReservationPayload(profile, seed, [subject], null, true));
            var paused = PauseCheckpoint(kernel);
            Task retention = kernel.PartitionTransactions.RetainConcurrentlyAsync(DateTimeOffset.UtcNow.AddMinutes(-1),
                static () => ValueTask.CompletedTask).AsTask();
            try
            {
                await paused.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await kernel.PartitionTransactions.PrepareAsync(operation, ReservationDescriptor, kernel.CurrentVersion,
                    ReservationPayload(profile, operation, [subject], kernel.CurrentVersion)).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                Assert.AreEqual(1L, kernel.CurrentVersion.Value);
            }
            finally
            {
                paused.Release.TrySetResult();
            }
            await retention;
        }
        opened = await OpenReservationStore(profile, path);
        await using (opened.Database)
        {
            Assert.AreEqual(DurablePartitionTransactionState.Prepared,
                (await opened.Kernel.PartitionTransactions.ReadAsync()).Single(p => p.Operation == operation).State);
            var receipt = await opened.Kernel.PartitionTransactions.CompleteAsync(operation, ReservationDescriptor, true);
            Assert.AreEqual(2L, receipt!.Version.Value);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Semantic_post_capture_values_survive_reopen_even_when_publication_fails(bool fail)
    {
        var placement = new Authority();
        await using var decisions = FilePartitionTransactionAuthority.Open(Path.Combine(_root, "authority"));
        string path = Path.Combine(_root, "semantic-detached");
        var node = await PartitionOwnedNode.CreateAsync(path, DatabaseProfileId.Semantic,
            placement.Lease, Routing, placement, Options, transactionAuthority: decisions, timestampClock: ReadClock);
        try
        {
            var adapter = await node.CreateSemanticExecutionAsync();
            var seed = adapter.CaptureSave(new Item("before", "original complete value"), SemanticEntityWriteOperation.Create());
            await adapter.ExecuteAsync(node.Domain, seed.OperationId, seed.Export());
            await PartitionRemoteRetention.RetainAsync(decisions, [node], Routing, DateTimeOffset.UtcNow.AddMinutes(-1));
            var paused = PauseCheckpoint(node.Kernel, before: fail);
            var captured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task retention = PartitionRemoteRetention.RetainAsync(decisions, [node], Routing, DateTimeOffset.UtcNow.AddMinutes(-1),
                captured: () =>
                {
                    captured.TrySetResult();
                    return ValueTask.CompletedTask;
                }).AsTask();
            try
            {
                await paused.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await captured.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var next = adapter.CaptureSave(new Item("during", "complete value committed during publication"), SemanticEntityWriteOperation.Create());
                await adapter.ExecuteAsync(node.Domain, next.OperationId, next.Export()).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                Assert.IsFalse(retention.IsCompleted);
            }
            finally
            {
                paused.Release.TrySetResult();
            }
            if (fail)
            {
                await Assert.ThrowsExactlyAsync<AggregateException>(() => retention);
            }
            else
            {
                await retention;
            }

            await node.DisposeAsync();
            node = await PartitionOwnedNode.OpenAsync(path, DatabaseProfileId.Semantic,
                placement.Lease, Routing, placement, Options, transactionAuthority: decisions, timestampClock: ReadClock);
            var recovered = await node.CreateSemanticExecutionAsync();
            Assert.AreEqual("original complete value", (await recovered.ReadAsync<Item>(node.Domain, "before"))!.Value.Name);
            Assert.AreEqual("complete value committed during publication", (await recovered.ReadAsync<Item>(node.Domain, "during"))!.Value.Name);
        }
        finally
        {
            await node.DisposeAsync();
        }
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task Semantic_remote_retention_releases_owners_before_publication_and_defers_authority_expiry(bool fail, bool shared)
    {
        using var timestamps = shared ? new FileTimestampStore(Path.Combine(_root, "detached-clock"), "detached") : null;
        await WithReductionOwners(async (nodes, authority, client) =>
        {
            var seed = await ConfinedSemanticRequest(client, nodes[0]);
            await client.DeliverAsync(seed.OperationId, seed.Export());
            var paused = nodes.Select((n, index) => PauseCheckpoint(n.Kernel, fail && index == 0)).ToArray();
            var observedAuthority = new LostRetentionReply(authority);
            var captured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task retention = PartitionRemoteRetention.RetainAsync(observedAuthority, nodes, nodes[0].Routing,
                DateTimeOffset.UtcNow.AddMinutes(-1), captured: () =>
                {
                    captured.TrySetResult();
                    return ValueTask.CompletedTask;
                }).AsTask();
            try
            {
                await Task.WhenAll(paused.Select(p => p.Started.Task)).WaitAsync(TimeSpan.FromSeconds(15));
                await captured.Task.WaitAsync(TimeSpan.FromSeconds(5));
                using var http = new HttpClient();
                var store = new HttpSemanticEntityClient(http, client.Generation).EntityStore<Item>();
                var request = store.CaptureSave(new("foreground-during-checkpoint", "complete post-cut value"), SemanticEntityWriteOperation.Create());
                var result = await client.DeliverAsync(request.OperationId, request.Export()).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
                Assert.IsTrue(result.Members.Count > 0);
                Assert.IsFalse(retention.IsCompleted);
                Assert.AreEqual(0, observedAuthority.RetentionCalls);
            }
            finally
            {
                foreach (var p in paused)
                {
                    p.Release.TrySetResult();
                }
            }
            if (fail)
            {
                await Assert.ThrowsExactlyAsync<AggregateException>(() => retention);
            }
            else
            {
                await retention;
            }

            Assert.AreEqual(fail ? 0 : 1, observedAuthority.RetentionCalls);
            Assert.AreEqual(0, nodes.Sum(n => n.UnreleasedReadCutCount));
        }, timestamps is null ? null : new DatabaseSharedTimestampService(timestamps));
    }
}
