using System.Reflection;
using EntityForge.Db.Distributed.Hosting;
using EntityForge.Db.Kernel;
using EntityForge.Db.Semantic;
using EntityForge.Db.Semantic.Engine;
using EntityForge.Db.Mapping;
using EntityForge.Db.Storage.Pages;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    private static Guid[] PlannedOwnerOperations(DatabaseKernel kernel)
    {
        var runtime = typeof(DatabaseKernel).GetField("_profile", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(kernel)!;
        var plans = (System.Collections.IDictionary)runtime.GetType().GetField("_plannedCommits", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(runtime)!;
        return plans.Keys.Cast<Guid>().ToArray();
    }

    private static object? OwnerPlanningBasis(SemanticDatabaseEngine database, string field)
    {
        var profile = typeof(SemanticDatabaseEngine).GetField("_profileState", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(database)!;
        return profile.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(profile);
    }

    [TestMethod]
    [DataRow(false, false, false)]
    [DataRow(true, false, false)]
    [DataRow(false, true, false)]
    [DataRow(false, false, true)]
    public async Task Semantic_owner_writes_group_under_contention_and_retain_each_binding(bool fail, bool failAfterDurable, bool cancelCaller)
    {
        await WithProfileFragments(DatabaseProfileId.Semantic, async (_, client, _, remote, http, nodes, authority) =>
        {
            var node = nodes[0];
            var store = new HttpSemanticEntityClient(http, client.Generation).EntityStore<Item>();
            var keys = Enumerable.Range(0, 128).Select(i => "group-owner-" + i)
                .Where(key => PartitionSemanticOwnerPlan.Owner(store.CaptureSave(new Item(key, "seed"), SemanticEntityWriteOperation.Create()), node.Routing) == node.Domain.Partition)
                .Take(8).ToArray();
            Assert.AreEqual(8, keys.Length);
            foreach (var key in keys)
            {
                await remote.SaveSemanticAsync(new Item(key, "seed"));
            }

            var native = typeof(PartitionOwnedNode).GetField("_native", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(node)!;
            var database = (SemanticDatabaseEngine)native.GetType().GetProperty("Database", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(native)!;
            var gate = (SemanticCommitPreparationCoordinator)typeof(SemanticDatabaseEngine).GetField("_commitGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(database)!;
            var requests = keys.Select(key => store.CaptureSave(new Item(key, "grouped"),
                new SemanticEntityWriteOperation(PartitionOwnerOperation.New(node.Routing, node.Domain.Partition), DateTimeOffset.UtcNow))).ToArray();
            // Bind the exact public requests before holding the data commit gate: durable
            // request binding itself also acquires that gate and must be allowed to finish.
            foreach (var request in requests.Select(r => r.ForDatabase(node.Domain.Incarnation)))
            {
                await ((IEntityOperationHost)database).Operations.GetOrBindOperationAsync(
                    request.OperationId, request.CanonicalIdentity(),
                    () => SemanticEntityDeliveryCodec.WithLocalCoverage(request.PrepareCore(database).Export(),
                        new EntityForge.Db.Semantic.Durability.SemanticOwnerCoverage(node.Routing.Fingerprint, [node.Domain.Partition.Value])),
                    default, request._metadata.DatabaseIncarnation);
            }

            var boundDigests = new Dictionary<Guid, string>();
            foreach (var request in requests)
            {
                var binding = (await ((IEntityOperationHost)database).Operations.FindAsync(request.OperationId, default))!;
                var metadata = EntityRecordDeliveryCodec.Decode<SemanticEntityDeliveryCodec.Metadata>(binding.Plan).Metadata;
                Assert.IsNotNull(metadata.LocalOwnerCoverage);
                Assert.AreEqual(node.Routing.Fingerprint, metadata.LocalOwnerCoverage.Routing);
                boundDigests.Add(request.OperationId, EntityOperationCommitted.Digest(binding.Plan));
            }
            using var callerCancellation = new CancellationTokenSource();
            var queued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var drain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int admissions = 0;
            database.BeforeOrderedCommitAdmission = () =>
            {
                if (Interlocked.Increment(ref admissions) == requests.Length)
                {
                    queued.TrySetResult();
                }

                return ValueTask.CompletedTask;
            };
            database.BeforeOrderedCommitDrain = () => new ValueTask(drain.Task);
            var log = typeof(DatabaseKernel).GetField("_commitLog", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(node.Kernel)!;
            var stream = typeof(GroupedRedoDatabaseCommitLog).GetField("_stream", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(log)!;
            using var pause = new PausedReadAdmissionWal { Armed = true, FailOnRelease = fail };
            int selectedCount = 0;
            pause.ShouldPause = () =>
            {
                var ids = PlannedOwnerOperations(node.Kernel);
                selectedCount = ids?.Count(id => requests.Any(r => r.OperationId == id)) ?? 0;
                return selectedCount > 1;
            };
            stream.GetType().GetField("_observer", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(stream, pause);
            await gate.WaitAsync(default);
            bool held = true;
            Task<DatabaseDistributedCommitReceipt>[] writes = [];
            try
            {
                writes = requests.Select(r => client.DeliverAsync(r.OperationId, r.Export(), callerCancellation.Token).AsTask()).ToArray();
                await queued.Task.WaitAsync(TimeSpan.FromSeconds(10));
                if (cancelCaller)
                {
                    callerCancellation.Cancel();
                    Assert.IsTrue(writes.All(write => !write.IsCompleted), "Cancellation cannot acknowledge an accepted write while the commit gate is held.");
                }
                gate.Release();
                held = false;
                drain.TrySetResult();
                await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.AreEqual(8, selectedCount, "All independent owner requests must share the selected physical append.");
                var stagedBasis = OwnerPlanningBasis(database, "_stagedPlanningEvidence");
                var publishedBasis = OwnerPlanningBasis(database, "_planningEvidence");
                Assert.IsNotNull(stagedBasis);
                Assert.IsNotNull(publishedBasis);
                Assert.AreNotSame(publishedBasis, stagedBasis);
                Assert.IsTrue(((SemanticPlanningEvidence)stagedBasis).Version.Value > ((SemanticPlanningEvidence)publishedBasis).Version.Value);
                Assert.IsTrue(writes.All(w => !w.IsCompleted), "No acknowledgment before the held WAL completes.");
                if (failAfterDurable)
                {
                    var profile = typeof(SemanticDatabaseEngine).GetField("_profileState", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(database)!;
                    profile.GetType().GetField("_observer", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .SetValue(profile, new FailOwnerGroupPublication());
                }

            }
            finally
            {
                if (held)
                {
                    gate.Release();
                }

                drain.TrySetResult();
                pause.Release.Set();
                database.BeforeOrderedCommitAdmission = null;
                database.BeforeOrderedCommitDrain = null;
                if (fail || failAfterDurable)
                {
                    try
                    {
                        await Task.WhenAll(writes).WaitAsync(TimeSpan.FromSeconds(15));
                    }
                    catch (Exception) when (writes.All(write => write.IsCompleted))
                    {

                    }
                }
                else
                {
                    await Task.WhenAll(writes).WaitAsync(TimeSpan.FromSeconds(15));
                }
            }
            Assert.IsNull(OwnerPlanningBasis(database, "_stagedPlanningEvidence"), "Success and failed append must discard private preparation evidence.");
            if (fail || failAfterDurable)
            {
                Assert.IsTrue(writes.All(write => write.IsFaulted), "Every member must report the failed physical group, with no successful acknowledgment.");
                Assert.AreEqual(DatabaseKernelState.RecoveryRequired, node.Kernel.State);
            }
            if (!fail && !failAfterDurable)
            {
                foreach (var request in requests)
                {
                    var binding = await ((IEntityOperationHost)database).Operations.FindAsync(request.OperationId, default);
                    Assert.IsNotNull(binding);
                    Assert.IsNotNull(binding.Committed);
                    Assert.AreEqual(boundDigests[request.OperationId], binding.Committed.EnvelopeDigest);
                    Assert.AreEqual(DatabaseCommitOutcome.IdempotentReplay,
                        (await client.DeliverAsync(request.OperationId, request.Export())).Members.Single().Receipt.Outcome);
                }
            }

            if (!fail && !failAfterDurable)
            {
                foreach (var key in keys)
                {
                    Assert.AreEqual("grouped", (await remote.ReadSemanticAsync<Item>(key)).Value!.Name);
                }
            }

            if (failAfterDurable)
            {
                for (int i = 0; i < writes.Length; i++)
                {
                    var known = writes[i].Exception!.Flatten().InnerExceptions.OfType<EntityOperationCommittedException<DatabaseCommitReceipt>>().Single();
                    Assert.AreEqual(requests[i].OperationId, known.NativeReceipt.TransactionId);
                    Assert.AreEqual(node.Domain.Incarnation, known.NativeReceipt.DatabaseIncarnation);
                }
            }

            await client.CloseReadEpochAsync();
            var lease = node.CurrentLease;
            await node.DisposeAsync();
            await using var reopened = await PartitionOwnedNode.OpenAsync(Path.Combine(_root, node.Domain.Partition.Value),
                DatabaseProfileId.Semantic, lease, node.Routing, new Authority { Lease = lease }, Options,
                transactionAuthority: authority, timestampClock: ReadClock);
            var reopenedNative = typeof(PartitionOwnedNode).GetField("_native", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(reopened)!;
            var reopenedDatabase = (SemanticDatabaseEngine)reopenedNative.GetType().GetProperty("Database", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(reopenedNative)!;
            var recovered = new PartitionApplicationClient(authority, node.Routing, [reopened, nodes[1]], ReadClock);
            var recoveredStore = ((ISemanticDatabase)reopenedDatabase).EntityStore<Item>();
            foreach (var key in keys)
            {
                Assert.AreEqual(fail ? "seed" : "grouped", (await recoveredStore.ReadAsync(key))!.Value.Name,
                    "Reopening must expose the entire acknowledged group, or none of a group that failed before tail publication.");
            }

            foreach (var request in requests)
            {
                var binding = await ((IEntityOperationHost)reopenedDatabase).Operations.FindAsync(request.OperationId, default);
                Assert.IsNotNull(binding);
                if (fail)
                {
                    Assert.IsNull(binding.Committed);
                }
                else
                {
                    Assert.IsNotNull(binding.Committed);
                    Assert.AreEqual(boundDigests[request.OperationId], binding.Committed.EnvelopeDigest);
                    var replay = await recovered.DeliverAsync(request.OperationId, request.Export());
                    Assert.AreEqual(DatabaseCommitOutcome.IdempotentReplay, replay.Members.Single().Receipt.Outcome);
                    var original = writes[Array.IndexOf(requests, request)];
                    var originalVersion = failAfterDurable
                        ? original.Exception!.Flatten().InnerExceptions.OfType<EntityOperationCommittedException<DatabaseCommitReceipt>>().Single().NativeReceipt.Version
                        : original.Result.Members.Single().Receipt.Version;
                    Assert.AreEqual(originalVersion,
                        replay.Members.Single().Receipt.Version);
                }
            }
        });
    }

    [DataTestMethod]
    [DataRow(100)]
    [DataRow(350)]
    public async Task Semantic_automatic_public_saves_form_data_groups_after_a_held_append(int holdMilliseconds)
    {
        await WithProfileFragments(DatabaseProfileId.Semantic, async (_, client, _, remote, http, nodes, authority) =>
        {
            var node = nodes[0];
            var store = new HttpSemanticEntityClient(http, client.Generation).EntityStore<Item>();
            var keys = Enumerable.Range(0, 128).Select(i => "automatic-group-" + i)
                .Where(key => PartitionSemanticOwnerPlan.Owner(store.CaptureSave(new Item(key, "seed"), SemanticEntityWriteOperation.Create()), node.Routing) == node.Domain.Partition)
                .Take(9).ToArray();
            Assert.AreEqual(9, keys.Length);
            foreach (var key in keys)
            {
                await remote.SaveSemanticAsync(new Item(key, "seed"));
            }

            var resources = keys.Select(key => SemanticReadResources.Entity(
                SemanticEntityReadDecoder.SemanticIdentity(store.DescribePartitionRead(key).Item2.Identity))).ToHashSet();
            var log = typeof(DatabaseKernel).GetField("_commitLog", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(node.Kernel)!;
            var stream = typeof(GroupedRedoDatabaseCommitLog).GetField("_stream", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(log)!;
            using var observer = new AutomaticOwnerGroupObserver(node.Kernel, resources);
            stream.GetType().GetField("_observer", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(stream, observer);
            var first = Task.Run(async () => await remote.SaveSemanticAsync(new Item(keys[0], "updated")));
            Task[] backlog = [];
            try
            {
                await observer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                backlog = keys.Skip(1).Select(key => Task.Run(async () => await remote.SaveSemanticAsync(new Item(key, "updated")))).ToArray();
                await Task.Delay(holdMilliseconds);
                Assert.IsFalse(first.IsCompleted, "The initial public save cannot acknowledge before WAL publication.");
                Assert.IsTrue(backlog.All(t => !t.IsCompleted));
            }
            finally
            {
                observer.Release.Set();
                await Task.WhenAll(backlog.Append(first)).WaitAsync(TimeSpan.FromSeconds(15));
            }
            var groups = observer.DataGroups.ToArray();
            var sizes = groups.Select(ids => ids.Length).ToArray();
            Assert.AreEqual(9, groups.Sum(ids => ids.Length), "Every data operation appears exactly once.");
            Assert.AreEqual(9, groups.SelectMany(ids => ids).Distinct().Count(), "Every public save must appear among the actual selected data operations.");
            Assert.IsTrue(sizes.Any(size => size > 1), "Automatic public saves must actually use a grouped data append: " + string.Join(",", sizes));
            foreach (var key in keys)
            {
                Assert.AreEqual("updated", (await remote.ReadSemanticAsync<Item>(key)).Value!.Name);
            }
        });
    }

    [TestMethod]
    public async Task Semantic_queued_owner_overlap_splits_physical_groups_and_preserves_bindings()
    {
        await WithProfileFragments(DatabaseProfileId.Semantic, async (_, client, _, remote, http, nodes, authority) =>
        {
            var node = nodes[0];
            var store = new HttpSemanticEntityClient(http, client.Generation).EntityStore<Item>();
            var keys = Enumerable.Range(0, 128).Select(i => "group-owner-" + i)
                .Where(key => PartitionSemanticOwnerPlan.Owner(store.CaptureSave(new Item(key, "seed"), SemanticEntityWriteOperation.Create()), node.Routing) == node.Domain.Partition)
                .Take(4).SelectMany(key => new[] { key, key }).ToArray();
            Assert.AreEqual(8, keys.Length);
            foreach (var key in keys)
            {
                await remote.SaveSemanticAsync(new Item(key, "seed"));
            }

            var native = typeof(PartitionOwnedNode).GetField("_native", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(node)!;
            var database = (SemanticDatabaseEngine)native.GetType().GetProperty("Database", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(native)!;
            var gate = (SemanticCommitPreparationCoordinator)typeof(SemanticDatabaseEngine).GetField("_commitGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(database)!;
            var requests = keys.Select(key => store.CaptureSave(new Item(key, "grouped"),
                new SemanticEntityWriteOperation(PartitionOwnerOperation.New(node.Routing, node.Domain.Partition), DateTimeOffset.UtcNow))).ToArray();
            // Bind the exact public requests before holding the data commit gate: durable
            // request binding itself also acquires that gate and must be allowed to finish.
            foreach (var request in requests.Select(r => r.ForDatabase(node.Domain.Incarnation)))
            {
                await ((IEntityOperationHost)database).Operations.GetOrBindOperationAsync(
                    request.OperationId, request.CanonicalIdentity(),
                    () => SemanticEntityDeliveryCodec.WithLocalCoverage(request.PrepareCore(database).Export(),
                        new EntityForge.Db.Semantic.Durability.SemanticOwnerCoverage(node.Routing.Fingerprint, [node.Domain.Partition.Value])),
                    default, request._metadata.DatabaseIncarnation);
            }

            var boundDigests = new Dictionary<Guid, string>();
            foreach (var request in requests)
            {
                var binding = (await ((IEntityOperationHost)database).Operations.FindAsync(request.OperationId, default))!;
                var metadata = EntityRecordDeliveryCodec.Decode<SemanticEntityDeliveryCodec.Metadata>(binding.Plan).Metadata;
                Assert.IsNotNull(metadata.LocalOwnerCoverage);
                Assert.AreEqual(node.Routing.Fingerprint, metadata.LocalOwnerCoverage.Routing);
                boundDigests.Add(request.OperationId, EntityOperationCommitted.Digest(binding.Plan));
            }
            var queued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var drain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int admissions = 0;
            database.BeforeOrderedCommitAdmission = () =>
            {
                if (Interlocked.Increment(ref admissions) == requests.Length)
                {
                    queued.TrySetResult();
                }

                return ValueTask.CompletedTask;
            };
            database.BeforeOrderedCommitDrain = () => new ValueTask(drain.Task);
            var operationKeys = requests.Select((request, i) => (request.OperationId, Key: keys[i])).ToDictionary(x => x.OperationId, x => x.Key);
            var log = typeof(DatabaseKernel).GetField("_commitLog", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(node.Kernel)!;
            var stream = typeof(GroupedRedoDatabaseCommitLog).GetField("_stream", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(log)!;
            var observer = new OwnerOverlapObserver(node.Kernel, operationKeys);
            stream.GetType().GetField("_observer", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(stream, observer);
            await gate.WaitAsync(default);
            bool held = true;
            Task<DatabaseDistributedCommitReceipt>[] writes = [];
            try
            {
                writes = requests.Select(r => client.DeliverAsync(r.OperationId, r.Export()).AsTask()).ToArray();
                await queued.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.IsTrue(writes.All(write => !write.IsCompleted));
                gate.Release();
                held = false;
                drain.TrySetResult();
                await Task.WhenAll(writes).WaitAsync(TimeSpan.FromSeconds(15));
            }
            finally
            {
                if (held)
                {
                    gate.Release();
                }

                drain.TrySetResult();
                database.BeforeOrderedCommitAdmission = null;
                database.BeforeOrderedCommitDrain = null;
                await Task.WhenAll(writes).WaitAsync(TimeSpan.FromSeconds(15));
            }
            var groups = observer.Groups.ToArray();
            CollectionAssert.AreEquivalent(requests.Select(r => r.OperationId).ToArray(), groups.SelectMany(g => g).ToArray());
            Assert.IsTrue(groups.Length >= 2, "Overlapping writes cannot share one independent physical group.");
            foreach (var group in groups)
            {
                Assert.AreEqual(group.Length, group.Select(id => operationKeys[id]).Distinct().Count());
            }

            foreach (var request in requests)
            {
                var binding = (await ((IEntityOperationHost)database).Operations.FindAsync(request.OperationId, default))!;
                Assert.IsNotNull(binding.Committed);
                Assert.AreEqual(boundDigests[request.OperationId], binding.Committed.EnvelopeDigest);
                var replay = await client.DeliverAsync(request.OperationId, request.Export());
                Assert.AreEqual(DatabaseCommitOutcome.IdempotentReplay, replay.Members.Single().Receipt.Outcome);
                Assert.AreEqual(writes[Array.IndexOf(requests, request)].Result.Members.Single().Receipt.Version, replay.Members.Single().Receipt.Version);
            }
            foreach (var key in keys.Distinct())
            {
                Assert.AreEqual("grouped", (await remote.ReadSemanticAsync<Item>(key)).Value!.Name);
            }
        });
    }

    [DataTestMethod]
    [DataRow(8, false)]
    [DataRow(17, false)]
    [DataRow(8, true)]
    public async Task Semantic_owner_queue_selection_at_admission_includes_later_arrivals(int requestCount, bool shutdown)
    {
        await WithProfileFragments(DatabaseProfileId.Semantic, async (_, client, _, remote, http, nodes, authority) =>
        {
            var node = nodes[0];
            var store = new HttpSemanticEntityClient(http, client.Generation).EntityStore<Item>();
            var keys = Enumerable.Range(0, 128).Select(i => "group-owner-" + i)
                .Where(key => PartitionSemanticOwnerPlan.Owner(store.CaptureSave(new Item(key, "seed"), SemanticEntityWriteOperation.Create()), node.Routing) == node.Domain.Partition)
                .Take(requestCount).ToArray();
            Assert.AreEqual(requestCount, keys.Length);
            foreach (var key in keys)
            {
                await remote.SaveSemanticAsync(new Item(key, "seed"));
            }

            var native = typeof(PartitionOwnedNode).GetField("_native", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(node)!;
            var database = (SemanticDatabaseEngine)native.GetType().GetProperty("Database", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(native)!;
            var gate = (SemanticCommitPreparationCoordinator)typeof(SemanticDatabaseEngine).GetField("_commitGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(database)!;
            var requests = keys.Select(key => store.CaptureSave(new Item(key, "grouped"),
                new SemanticEntityWriteOperation(PartitionOwnerOperation.New(node.Routing, node.Domain.Partition), DateTimeOffset.UtcNow))).ToArray();
            // Bind the exact public requests before holding the data commit gate: durable
            // request binding itself also acquires that gate and must be allowed to finish.
            foreach (var request in requests.Select(r => r.ForDatabase(node.Domain.Incarnation)))
            {
                await ((IEntityOperationHost)database).Operations.GetOrBindOperationAsync(
                    request.OperationId, request.CanonicalIdentity(),
                    () => SemanticEntityDeliveryCodec.WithLocalCoverage(request.PrepareCore(database).Export(),
                        new EntityForge.Db.Semantic.Durability.SemanticOwnerCoverage(node.Routing.Fingerprint, [node.Domain.Partition.Value])),
                    default, request._metadata.DatabaseIncarnation);
            }

            var boundDigests = new Dictionary<Guid, string>();
            foreach (var request in requests)
            {
                var binding = (await ((IEntityOperationHost)database).Operations.FindAsync(request.OperationId, default))!;
                var metadata = EntityRecordDeliveryCodec.Decode<SemanticEntityDeliveryCodec.Metadata>(binding.Plan).Metadata;
                Assert.IsNotNull(metadata.LocalOwnerCoverage);
                Assert.AreEqual(node.Routing.Fingerprint, metadata.LocalOwnerCoverage.Routing);
                boundDigests.Add(request.OperationId, EntityOperationCommitted.Digest(binding.Plan));
            }
            var queued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var drain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int admissions = 0;
            database.BeforeOrderedCommitAdmission = () =>
            {
                if (Interlocked.Increment(ref admissions) == requests.Length)
                {
                    queued.TrySetResult();
                }

                return ValueTask.CompletedTask;
            };
            database.BeforeOrderedCommitDrain = () => ValueTask.CompletedTask;
            var operationKeys = requests.Select((request, i) => (request.OperationId, Key: keys[i])).ToDictionary(x => x.OperationId, x => x.Key);
            var log = typeof(DatabaseKernel).GetField("_commitLog", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(node.Kernel)!;
            var stream = typeof(GroupedRedoDatabaseCommitLog).GetField("_stream", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(log)!;
            var observer = new OwnerOverlapObserver(node.Kernel, operationKeys);
            stream.GetType().GetField("_observer", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(stream, observer);
            await gate.WaitAsync(default);
            bool held = true;
            Task<DatabaseDistributedCommitReceipt>[] writes = [];
            try
            {
                writes = [client.DeliverAsync(requests[0].OperationId, requests[0].Export()).AsTask()];
                var waiting = System.Diagnostics.Stopwatch.StartNew();
                var queueField = typeof(SemanticDatabaseEngine).GetField("_orderedCommits", BindingFlags.NonPublic | BindingFlags.Instance)!;
                var queueLock = typeof(SemanticDatabaseEngine).GetField("_orderedCommitGate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(database)!;
                while (true)
                {
                    int count;
                    lock (queueLock)
                    {
                        var queue = queueField.GetValue(database)!;
                        count = (int)queue.GetType().GetProperty("Count")!.GetValue(queue)!;
                    }
                    if (Volatile.Read(ref admissions) == 1 && count == 1 && gate.Snapshot().WaitingPublications > 0)
                    {
                        break;
                    }

                    if (waiting.Elapsed > TimeSpan.FromSeconds(5))
                    {
                        Assert.Fail("First request did not remain queued while waiting for publication admission.");
                    }

                    await Task.Delay(1);
                }
                for (int index = 1; index < requests.Length; index++)
                {
                    var request = requests[index];
                    writes = writes.Append(client.DeliverAsync(request.OperationId, request.Export()).AsTask()).ToArray();
                    waiting.Restart();
                    while (true)
                    {
                        int count;
                        lock (queueLock)
                        {
                            var queue = queueField.GetValue(database)!;
                            count = (int)queue.GetType().GetProperty("Count")!.GetValue(queue)!;
                        }
                        if (count == index + 1)
                        {
                            break;
                        }

                        if (waiting.Elapsed > TimeSpan.FromSeconds(5))
                        {
                            Assert.Fail("Request did not enter FIFO queue.");
                        }

                        await Task.Delay(1);
                    }
                }
                await queued.Task.WaitAsync(TimeSpan.FromSeconds(10));
                if (shutdown)
                {
                    Task[] pendingTasks;
                    lock (queueLock)
                    {
                        pendingTasks = ((System.Collections.IEnumerable)queueField.GetValue(database)!).Cast<object>()
                            .Select(item => item.GetType().GetProperty("Completion")!.GetValue(item)!)
                            .Select(completion => (Task)completion.GetType().GetProperty("Task")!.GetValue(completion)!).ToArray();
                    }

                    var stop = (CancellationTokenSource)typeof(SemanticDatabaseEngine)
                        .GetField("_orderedCommitShutdown", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(database)!;
                    stop.Cancel();
                    try
                    {
                        await Task.WhenAll(pendingTasks).WaitAsync(TimeSpan.FromSeconds(5));
                    }
                    catch (OperationCanceledException)
                    {

                    }
                    Assert.IsTrue(pendingTasks.All(task => task.IsCanceled));
                    Assert.AreEqual(0, observer.Groups.Count);
                    Assert.AreEqual(1, gate.Snapshot().ActivePublications, "Canceled waiter must not release the fixture's gate.");
                    lock (queueLock)
                    {
                        Assert.AreEqual(0, (int)queueField.GetValue(database)!.GetType().GetProperty("Count")!.GetValue(queueField.GetValue(database))!);
                    }

                    return;
                }
                Assert.IsTrue(writes.All(write => !write.IsCompleted));
                gate.Release();
                held = false;
                drain.TrySetResult();
                await Task.WhenAll(writes).WaitAsync(TimeSpan.FromSeconds(15));
            }
            finally
            {
                if (held)
                {
                    gate.Release();
                }

                drain.TrySetResult();
                database.BeforeOrderedCommitAdmission = null;
                database.BeforeOrderedCommitDrain = null;
                try
                {
                    await Task.WhenAll(writes).WaitAsync(TimeSpan.FromSeconds(15));
                }
                catch (Exception) when (shutdown && writes.All(write => write.IsCompleted && !write.IsCompletedSuccessfully))
                {

                }
            }
            var groups = observer.Groups.ToArray();
            CollectionAssert.AreEquivalent(requests.Select(r => r.OperationId).ToArray(), groups.SelectMany(g => g).ToArray());
            Assert.AreEqual(Math.Min(16, requestCount), groups[0].Length);
            Assert.AreEqual((requestCount + 15) / 16, groups.Length);
            Assert.IsTrue(groups.All(group => group.Length <= 16));
            CollectionAssert.AreEqual(requests.Select(request => request.OperationId).ToArray(), groups.SelectMany(group => group).ToArray());
            foreach (var group in groups)
            {
                Assert.AreEqual(group.Length, group.Select(id => operationKeys[id]).Distinct().Count());
            }

            foreach (var request in requests)
            {
                var binding = (await ((IEntityOperationHost)database).Operations.FindAsync(request.OperationId, default))!;
                Assert.IsNotNull(binding.Committed);
                Assert.AreEqual(boundDigests[request.OperationId], binding.Committed.EnvelopeDigest);
                var replay = await client.DeliverAsync(request.OperationId, request.Export());
                Assert.AreEqual(DatabaseCommitOutcome.IdempotentReplay, replay.Members.Single().Receipt.Outcome);
                Assert.AreEqual(writes[Array.IndexOf(requests, request)].Result.Members.Single().Receipt.Version, replay.Members.Single().Receipt.Version);
            }
            foreach (var key in keys.Distinct())
            {
                Assert.AreEqual("grouped", (await remote.ReadSemanticAsync<Item>(key)).Value!.Name);
            }
        });
    }

    [TestMethod]
    public async Task Semantic_owner_group_planning_reuses_staged_predecessor_evidence()
    {
        const int requestCount = 8;
        await WithProfileFragments(DatabaseProfileId.Semantic, async (_, client, _, remote, http, nodes, authority) =>
        {
            var node = nodes[0];
            var store = new HttpSemanticEntityClient(http, client.Generation).EntityStore<Item>();
            var keys = Enumerable.Range(0, 128).Select(i => "group-owner-" + i)
                .Where(key => PartitionSemanticOwnerPlan.Owner(store.CaptureSave(new Item(key, "seed"), SemanticEntityWriteOperation.Create()), node.Routing) == node.Domain.Partition)
                .Take(requestCount).ToArray();
            Assert.AreEqual(requestCount, keys.Length);
            foreach (var key in keys)
            {
                await remote.SaveSemanticAsync(new Item(key, "seed"));
            }

            var native = typeof(PartitionOwnedNode).GetField("_native", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(node)!;
            var database = (SemanticDatabaseEngine)native.GetType().GetProperty("Database", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(native)!;
            var gate = (SemanticCommitPreparationCoordinator)typeof(SemanticDatabaseEngine).GetField("_commitGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(database)!;
            var requests = keys.Select(key => store.CaptureSave(new Item(key, "grouped"),
                new SemanticEntityWriteOperation(PartitionOwnerOperation.New(node.Routing, node.Domain.Partition), DateTimeOffset.UtcNow))).ToArray();
            // Bind the exact public requests before holding the data commit gate: durable
            // request binding itself also acquires that gate and must be allowed to finish.
            foreach (var request in requests.Select(r => r.ForDatabase(node.Domain.Incarnation)))
            {
                await ((IEntityOperationHost)database).Operations.GetOrBindOperationAsync(
                    request.OperationId, request.CanonicalIdentity(),
                    () => SemanticEntityDeliveryCodec.WithLocalCoverage(request.PrepareCore(database).Export(),
                        new EntityForge.Db.Semantic.Durability.SemanticOwnerCoverage(node.Routing.Fingerprint, [node.Domain.Partition.Value])),
                    default, request._metadata.DatabaseIncarnation);
            }

            var boundDigests = new Dictionary<Guid, string>();
            foreach (var request in requests)
            {
                var binding = (await ((IEntityOperationHost)database).Operations.FindAsync(request.OperationId, default))!;
                var metadata = EntityRecordDeliveryCodec.Decode<SemanticEntityDeliveryCodec.Metadata>(binding.Plan).Metadata;
                Assert.IsNotNull(metadata.LocalOwnerCoverage);
                Assert.AreEqual(node.Routing.Fingerprint, metadata.LocalOwnerCoverage.Routing);
                boundDigests.Add(request.OperationId, EntityOperationCommitted.Digest(binding.Plan));
            }
            var queued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int admissions = 0;
            database.BeforeOrderedCommitAdmission = () =>
            {
                if (Interlocked.Increment(ref admissions) == requests.Length)
                {
                    queued.TrySetResult();
                }

                return ValueTask.CompletedTask;
            };
            var operationKeys = requests.Select((request, i) => (request.OperationId, Key: keys[i])).ToDictionary(x => x.OperationId, x => x.Key);
            var log = typeof(DatabaseKernel).GetField("_commitLog", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(node.Kernel)!;
            var stream = typeof(GroupedRedoDatabaseCommitLog).GetField("_stream", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(log)!;
            var observer = new OwnerOverlapObserver(node.Kernel, operationKeys);
            stream.GetType().GetField("_observer", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(stream, observer);
            var profile = typeof(SemanticDatabaseEngine).GetField("_profileState", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(database)!;
            int reused = 0;
            SemanticCommittedChangePlanner.ReusedPlanningEvidence.Value = () => Interlocked.Increment(ref reused);
            await gate.WaitAsync(default);
            bool held = true;
            Task<DatabaseDistributedCommitReceipt>[] writes = [];
            try
            {
                writes = [client.DeliverAsync(requests[0].OperationId, requests[0].Export()).AsTask()];
                var waiting = System.Diagnostics.Stopwatch.StartNew();
                var queueField = typeof(SemanticDatabaseEngine).GetField("_orderedCommits", BindingFlags.NonPublic | BindingFlags.Instance)!;
                var queueLock = typeof(SemanticDatabaseEngine).GetField("_orderedCommitGate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(database)!;
                while (true)
                {
                    int count;
                    lock (queueLock)
                    {
                        var queue = queueField.GetValue(database)!;
                        count = (int)queue.GetType().GetProperty("Count")!.GetValue(queue)!;
                    }
                    if (Volatile.Read(ref admissions) == 1 && count == 1 && gate.Snapshot().WaitingPublications > 0)
                    {
                        break;
                    }

                    if (waiting.Elapsed > TimeSpan.FromSeconds(5))
                    {
                        Assert.Fail("First request did not remain queued while waiting for publication admission.");
                    }

                    await Task.Delay(1);
                }
                for (int index = 1; index < requests.Length; index++)
                {
                    var request = requests[index];
                    writes = writes.Append(client.DeliverAsync(request.OperationId, request.Export()).AsTask()).ToArray();
                    waiting.Restart();
                    while (true)
                    {
                        int count;
                        lock (queueLock)
                        {
                            var queue = queueField.GetValue(database)!;
                            count = (int)queue.GetType().GetProperty("Count")!.GetValue(queue)!;
                        }
                        if (count == index + 1)
                        {
                            break;
                        }

                        if (waiting.Elapsed > TimeSpan.FromSeconds(5))
                        {
                            Assert.Fail("Request did not enter FIFO queue.");
                        }

                        await Task.Delay(1);
                    }
                }
                await queued.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.IsTrue(writes.All(write => !write.IsCompleted));
                gate.Release();
                held = false;
                await Task.WhenAll(writes).WaitAsync(TimeSpan.FromSeconds(15));
            }
            finally
            {
                if (held)
                {
                    gate.Release();
                }

                SemanticCommittedChangePlanner.ReusedPlanningEvidence.Value = null;
                database.BeforeOrderedCommitAdmission = null;
                database.BeforeOrderedCommitDrain = null;
                await Task.WhenAll(writes).WaitAsync(TimeSpan.FromSeconds(15));
            }
            Assert.AreEqual(requestCount, reused, "Every member must reuse its exact native staged predecessor.");
            var groups = observer.Groups.ToArray();
            CollectionAssert.AreEquivalent(requests.Select(r => r.OperationId).ToArray(), groups.SelectMany(g => g).ToArray());
            Assert.AreEqual(Math.Min(16, requestCount), groups[0].Length);
            Assert.AreEqual((requestCount + 15) / 16, groups.Length);
            Assert.IsTrue(groups.All(group => group.Length <= 16));
            CollectionAssert.AreEqual(requests.Select(request => request.OperationId).ToArray(), groups.SelectMany(group => group).ToArray());
            foreach (var group in groups)
            {
                Assert.AreEqual(group.Length, group.Select(id => operationKeys[id]).Distinct().Count());
            }

            foreach (var request in requests)
            {
                var binding = (await ((IEntityOperationHost)database).Operations.FindAsync(request.OperationId, default))!;
                Assert.IsNotNull(binding.Committed);
                Assert.AreEqual(boundDigests[request.OperationId], binding.Committed.EnvelopeDigest);
                var replay = await client.DeliverAsync(request.OperationId, request.Export());
                Assert.AreEqual(DatabaseCommitOutcome.IdempotentReplay, replay.Members.Single().Receipt.Outcome);
                Assert.AreEqual(writes[Array.IndexOf(requests, request)].Result.Members.Single().Receipt.Version, replay.Members.Single().Receipt.Version);
            }
            foreach (var key in keys.Distinct())
            {
                Assert.AreEqual("grouped", (await remote.ReadSemanticAsync<Item>(key)).Value!.Name);
            }
        });
    }

    [TestMethod]
    public async Task Semantic_owner_group_preparation_rejection_falls_back_under_owned_admission()
    {
        const int requestCount = 8;
        await WithProfileFragments(DatabaseProfileId.Semantic, async (_, client, _, remote, http, nodes, authority) =>
        {
            var node = nodes[0];
            var store = new HttpSemanticEntityClient(http, client.Generation).EntityStore<Item>();
            var keys = Enumerable.Range(0, 128).Select(i => "group-owner-" + i)
                .Where(key => PartitionSemanticOwnerPlan.Owner(store.CaptureSave(new Item(key, "seed"), SemanticEntityWriteOperation.Create()), node.Routing) == node.Domain.Partition)
                .Take(requestCount).ToArray();
            Assert.AreEqual(requestCount, keys.Length);
            foreach (var key in keys)
            {
                await remote.SaveSemanticAsync(new Item(key, "seed"));
            }

            var native = typeof(PartitionOwnedNode).GetField("_native", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(node)!;
            var database = (SemanticDatabaseEngine)native.GetType().GetProperty("Database", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(native)!;
            var gate = (SemanticCommitPreparationCoordinator)typeof(SemanticDatabaseEngine).GetField("_commitGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(database)!;
            var requests = keys.Select(key => store.CaptureSave(new Item(key, "grouped"),
                new SemanticEntityWriteOperation(PartitionOwnerOperation.New(node.Routing, node.Domain.Partition), DateTimeOffset.UtcNow))).ToArray();
            // Bind the exact public requests before holding the data commit gate: durable
            // request binding itself also acquires that gate and must be allowed to finish.
            foreach (var request in requests.Select(r => r.ForDatabase(node.Domain.Incarnation)))
            {
                await ((IEntityOperationHost)database).Operations.GetOrBindOperationAsync(
                    request.OperationId, request.CanonicalIdentity(),
                    () => SemanticEntityDeliveryCodec.WithLocalCoverage(request.PrepareCore(database).Export(),
                        new EntityForge.Db.Semantic.Durability.SemanticOwnerCoverage(node.Routing.Fingerprint, [node.Domain.Partition.Value])),
                    default, request._metadata.DatabaseIncarnation);
            }

            var boundDigests = new Dictionary<Guid, string>();
            foreach (var request in requests)
            {
                var binding = (await ((IEntityOperationHost)database).Operations.FindAsync(request.OperationId, default))!;
                var metadata = EntityRecordDeliveryCodec.Decode<SemanticEntityDeliveryCodec.Metadata>(binding.Plan).Metadata;
                Assert.IsNotNull(metadata.LocalOwnerCoverage);
                Assert.AreEqual(node.Routing.Fingerprint, metadata.LocalOwnerCoverage.Routing);
                boundDigests.Add(request.OperationId, EntityOperationCommitted.Digest(binding.Plan));
            }
            var queued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var drain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int admissions = 0;
            database.BeforeOrderedCommitAdmission = () =>
            {
                if (Interlocked.Increment(ref admissions) == requests.Length)
                {
                    queued.TrySetResult();
                }

                return ValueTask.CompletedTask;
            };
            database.BeforeOrderedCommitDrain = () => ValueTask.CompletedTask;
            var operationKeys = requests.Select((request, i) => (request.OperationId, Key: keys[i])).ToDictionary(x => x.OperationId, x => x.Key);
            var log = typeof(DatabaseKernel).GetField("_commitLog", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(node.Kernel)!;
            var stream = typeof(GroupedRedoDatabaseCommitLog).GetField("_stream", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(log)!;
            var observer = new OwnerOverlapObserver(node.Kernel, operationKeys);
            stream.GetType().GetField("_observer", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(stream, observer);
            var optionsField = typeof(GroupedRedoDatabaseCommitLog).GetField("_options", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var originalOptions = (RedoDurabilityOptions)optionsField.GetValue(log)!;
            await gate.WaitAsync(default);
            optionsField.SetValue(log, new RedoDurabilityOptions(1, originalOptions.MaximumGroupBytes, originalOptions.MaximumGroupDelay));
            bool held = true;
            Task<DatabaseDistributedCommitReceipt>[] writes = [];
            try
            {
                writes = [client.DeliverAsync(requests[0].OperationId, requests[0].Export()).AsTask()];
                var waiting = System.Diagnostics.Stopwatch.StartNew();
                var queueField = typeof(SemanticDatabaseEngine).GetField("_orderedCommits", BindingFlags.NonPublic | BindingFlags.Instance)!;
                var queueLock = typeof(SemanticDatabaseEngine).GetField("_orderedCommitGate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(database)!;
                while (true)
                {
                    int count;
                    lock (queueLock)
                    {
                        var queue = queueField.GetValue(database)!;
                        count = (int)queue.GetType().GetProperty("Count")!.GetValue(queue)!;
                    }
                    if (Volatile.Read(ref admissions) == 1 && count == 1 && gate.Snapshot().WaitingPublications > 0)
                    {
                        break;
                    }

                    if (waiting.Elapsed > TimeSpan.FromSeconds(5))
                    {
                        Assert.Fail("First request did not remain queued while waiting for publication admission.");
                    }

                    await Task.Delay(1);
                }
                for (int index = 1; index < requests.Length; index++)
                {
                    var request = requests[index];
                    writes = writes.Append(client.DeliverAsync(request.OperationId, request.Export()).AsTask()).ToArray();
                    waiting.Restart();
                    while (true)
                    {
                        int count;
                        lock (queueLock)
                        {
                            var queue = queueField.GetValue(database)!;
                            count = (int)queue.GetType().GetProperty("Count")!.GetValue(queue)!;
                        }
                        if (count == index + 1)
                        {
                            break;
                        }

                        if (waiting.Elapsed > TimeSpan.FromSeconds(5))
                        {
                            Assert.Fail("Request did not enter FIFO queue.");
                        }

                        await Task.Delay(1);
                    }
                }
                await queued.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.IsTrue(writes.All(write => !write.IsCompleted));
                gate.Release();
                held = false;
                drain.TrySetResult();
                await Task.WhenAll(writes).WaitAsync(TimeSpan.FromSeconds(15));
            }
            finally
            {
                if (held)
                {
                    gate.Release();
                }

                drain.TrySetResult();
                database.BeforeOrderedCommitAdmission = null;
                database.BeforeOrderedCommitDrain = null;
                try
                {
                    await Task.WhenAll(writes).WaitAsync(TimeSpan.FromSeconds(15));
                }
                finally
                {
                    optionsField.SetValue(log, originalOptions);
                }
            }
            var groups = observer.Groups.ToArray();
            CollectionAssert.AreEquivalent(requests.Select(r => r.OperationId).ToArray(), groups.SelectMany(g => g).ToArray());
            Assert.IsNull(OwnerPlanningBasis(database, "_stagedPlanningEvidence"), "Group validation fallback must restore published evidence before singles.");
            Assert.AreEqual(8, groups.Length);
            Assert.IsTrue(groups.All(group => group.Length == 1));
            CollectionAssert.AreEqual(requests.Select(request => request.OperationId).ToArray(), groups.SelectMany(group => group).ToArray());
            foreach (var group in groups)
            {
                Assert.AreEqual(group.Length, group.Select(id => operationKeys[id]).Distinct().Count());
            }

            foreach (var request in requests)
            {
                var binding = (await ((IEntityOperationHost)database).Operations.FindAsync(request.OperationId, default))!;
                Assert.IsNotNull(binding.Committed);
                Assert.AreEqual(boundDigests[request.OperationId], binding.Committed.EnvelopeDigest);
                var replay = await client.DeliverAsync(request.OperationId, request.Export());
                Assert.AreEqual(DatabaseCommitOutcome.IdempotentReplay, replay.Members.Single().Receipt.Outcome);
                Assert.AreEqual(writes[Array.IndexOf(requests, request)].Result.Members.Single().Receipt.Version, replay.Members.Single().Receipt.Version);
            }
            foreach (var key in keys.Distinct())
            {
                Assert.AreEqual("grouped", (await remote.ReadSemanticAsync<Item>(key)).Value!.Name);
            }
        });
    }

    [TestMethod]
    public async Task Semantic_owner_group_preserves_every_member_version_in_retained_history()
    {
        await WithProfileFragments(DatabaseProfileId.Semantic, async (_, client, _, remote, http, nodes, authority) =>
        {
            var node = nodes[0];
            var store = new HttpSemanticEntityClient(http, client.Generation).EntityStore<Item>();
            var keys = Enumerable.Range(0, 128).Select(i => "group-owner-" + i)
                .Where(key => PartitionSemanticOwnerPlan.Owner(store.CaptureSave(new Item(key, "seed"), SemanticEntityWriteOperation.Create()), node.Routing) == node.Domain.Partition)
                .Take(8).ToArray();
            Assert.AreEqual(8, keys.Length);
            foreach (var key in keys)
            {
                await remote.SaveSemanticAsync(new Item(key, "seed"));
            }

            var native = typeof(PartitionOwnedNode).GetField("_native", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(node)!;
            var database = (SemanticDatabaseEngine)native.GetType().GetProperty("Database", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(native)!;
            var gate = (SemanticCommitPreparationCoordinator)typeof(SemanticDatabaseEngine).GetField("_commitGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(database)!;
            var requests = keys.Select(key => store.CaptureSave(new Item(key, "grouped"),
                new SemanticEntityWriteOperation(PartitionOwnerOperation.New(node.Routing, node.Domain.Partition), DateTimeOffset.UtcNow))).ToArray();
            // Bind the exact public requests before holding the data commit gate: durable
            // request binding itself also acquires that gate and must be allowed to finish.
            foreach (var request in requests.Select(r => r.ForDatabase(node.Domain.Incarnation)))
            {
                await ((IEntityOperationHost)database).Operations.GetOrBindOperationAsync(
                    request.OperationId, request.CanonicalIdentity(),
                    () => SemanticEntityDeliveryCodec.WithLocalCoverage(request.PrepareCore(database).Export(),
                        new EntityForge.Db.Semantic.Durability.SemanticOwnerCoverage(node.Routing.Fingerprint, [node.Domain.Partition.Value])),
                    default, request._metadata.DatabaseIncarnation);
            }

            var boundDigests = new Dictionary<Guid, string>();
            foreach (var request in requests)
            {
                var binding = (await ((IEntityOperationHost)database).Operations.FindAsync(request.OperationId, default))!;
                var metadata = EntityRecordDeliveryCodec.Decode<SemanticEntityDeliveryCodec.Metadata>(binding.Plan).Metadata;
                Assert.IsNotNull(metadata.LocalOwnerCoverage);
                Assert.AreEqual(node.Routing.Fingerprint, metadata.LocalOwnerCoverage.Routing);
                boundDigests.Add(request.OperationId, EntityOperationCommitted.Digest(binding.Plan));
            }
            var lease = await database.AcquireSnapshotLeaseAsync(TimeSpan.FromMinutes(5));
            try
            {
                var queued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var drain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                int admissions = 0;
                database.BeforeOrderedCommitAdmission = () =>
                {
                    if (Interlocked.Increment(ref admissions) == requests.Length)
                    {
                        queued.TrySetResult();
                    }

                    return ValueTask.CompletedTask;
                };
                database.BeforeOrderedCommitDrain = () => new ValueTask(drain.Task);
                var operationKeys = requests.Select((request, i) => (request.OperationId, Key: keys[i])).ToDictionary(x => x.OperationId, x => x.Key);
                var log = typeof(DatabaseKernel).GetField("_commitLog", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(node.Kernel)!;
                var stream = typeof(GroupedRedoDatabaseCommitLog).GetField("_stream", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(log)!;
                var observer = new OwnerOverlapObserver(node.Kernel, operationKeys);
                stream.GetType().GetField("_observer", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(stream, observer);
                await gate.WaitAsync(default);
                bool held = true;
                Task<DatabaseDistributedCommitReceipt>[] writes = [];
                try
                {
                    writes = requests.Select(r => client.DeliverAsync(r.OperationId, r.Export()).AsTask()).ToArray();
                    await queued.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    Assert.IsTrue(writes.All(write => !write.IsCompleted));
                    gate.Release();
                    held = false;
                    drain.TrySetResult();
                    await Task.WhenAll(writes).WaitAsync(TimeSpan.FromSeconds(15));
                }
                finally
                {
                    if (held)
                    {
                        gate.Release();
                    }

                    drain.TrySetResult();
                    database.BeforeOrderedCommitAdmission = null;
                    database.BeforeOrderedCommitDrain = null;
                    await Task.WhenAll(writes).WaitAsync(TimeSpan.FromSeconds(15));
                }
                var groups = observer.Groups.ToArray();
                CollectionAssert.AreEquivalent(requests.Select(r => r.OperationId).ToArray(), groups.SelectMany(g => g).ToArray());
                Assert.AreEqual(1, groups.Length, "History fixture must exercise one actual eight-member physical group.");
                foreach (var group in groups)
                {
                    Assert.AreEqual(group.Length, group.Select(id => operationKeys[id]).Distinct().Count());
                }

                foreach (var request in requests)
                {
                    var binding = (await ((IEntityOperationHost)database).Operations.FindAsync(request.OperationId, default))!;
                    Assert.IsNotNull(binding.Committed);
                    Assert.AreEqual(boundDigests[request.OperationId], binding.Committed.EnvelopeDigest);
                    var replay = await client.DeliverAsync(request.OperationId, request.Export());
                    Assert.AreEqual(DatabaseCommitOutcome.IdempotentReplay, replay.Members.Single().Receipt.Outcome);
                    Assert.AreEqual(writes[Array.IndexOf(requests, request)].Result.Members.Single().Receipt.Version, replay.Members.Single().Receipt.Version);
                }
                foreach (var key in keys.Distinct())
                {
                    Assert.AreEqual("grouped", (await remote.ReadSemanticAsync<Item>(key)).Value!.Name);
                }

                var manifest = store.Manifests[0];
                var property = new EntityForge.Semantics.SemanticTerm(new Uri(manifest.ProfileBindings.Single(binding => binding.MemberIdentity ==
                    manifest.Members.Single(member => member.ClrName == "Name").Identity).StoredBinding), EntityForge.Semantics.SemanticTermRole.Property);
                var receipts = writes.Select(write => write.Result.Members.Single().Receipt).ToArray();
                Assert.AreEqual(8, receipts.Select(receipt => receipt.Version).Distinct().Count());
                foreach (var version in receipts.Select(receipt => receipt.Version).Prepend(lease.SnapshotVersion))
                {
                    int committed = receipts.Count(receipt => receipt.Version.Value <= version.Value);
                    Assert.AreEqual(committed, (await database.QueryAsync(new(property,
                        new EntityForge.Semantics.StringSemanticValue("grouped")), version)).Count);
                    Assert.AreEqual(8 - committed, (await database.QueryAsync(new(property,
                        new EntityForge.Semantics.StringSemanticValue("seed")), version)).Count);
                }
            }
            finally
            {
                await database.ReleaseSnapshotLeaseAsync(lease.LeaseId);
            }
        });
    }

    [TestMethod]
    public async Task Semantic_queued_owner_writes_revalidate_policy_published_before_drain()
    {
        await WithProfileFragments(DatabaseProfileId.Semantic, async (_, client, _, remote, http, nodes, authority) =>
        {
            var node = nodes[0];
            var store = new HttpSemanticEntityClient(http, client.Generation).EntityStore<Item>();
            var keys = Enumerable.Range(0, 128).Select(i => "group-owner-" + i)
                .Where(key => PartitionSemanticOwnerPlan.Owner(store.CaptureSave(new Item(key, "seed"), SemanticEntityWriteOperation.Create()), node.Routing) == node.Domain.Partition)
                .Take(8).ToArray();
            Assert.AreEqual(8, keys.Length);
            foreach (var key in keys)
            {
                await remote.SaveSemanticAsync(new Item(key, "seed"));
            }

            var native = typeof(PartitionOwnedNode).GetField("_native", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(node)!;
            var database = (SemanticDatabaseEngine)native.GetType().GetProperty("Database", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(native)!;
            var gate = (SemanticCommitPreparationCoordinator)typeof(SemanticDatabaseEngine).GetField("_commitGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(database)!;
            var requests = keys.Select(key => store.CaptureSave(new Item(key, "grouped"),
                new SemanticEntityWriteOperation(PartitionOwnerOperation.New(node.Routing, node.Domain.Partition), DateTimeOffset.UtcNow))).ToArray();
            // Bind the exact public requests before holding the data commit gate: durable
            // request binding itself also acquires that gate and must be allowed to finish.
            foreach (var request in requests.Select(r => r.ForDatabase(node.Domain.Incarnation)))
            {
                await ((IEntityOperationHost)database).Operations.GetOrBindOperationAsync(
                    request.OperationId, request.CanonicalIdentity(),
                    () => SemanticEntityDeliveryCodec.WithLocalCoverage(request.PrepareCore(database).Export(),
                        new EntityForge.Db.Semantic.Durability.SemanticOwnerCoverage(node.Routing.Fingerprint, [node.Domain.Partition.Value])),
                    default, request._metadata.DatabaseIncarnation);
            }

            var boundDigests = new Dictionary<Guid, string>();
            foreach (var request in requests)
            {
                var binding = (await ((IEntityOperationHost)database).Operations.FindAsync(request.OperationId, default))!;
                var metadata = EntityRecordDeliveryCodec.Decode<SemanticEntityDeliveryCodec.Metadata>(binding.Plan).Metadata;
                Assert.IsNotNull(metadata.LocalOwnerCoverage);
                Assert.AreEqual(node.Routing.Fingerprint, metadata.LocalOwnerCoverage.Routing);
                boundDigests.Add(request.OperationId, EntityOperationCommitted.Digest(binding.Plan));
            }
            var queued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var drain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int admissions = 0;
            database.BeforeOrderedCommitAdmission = () =>
            {
                if (Interlocked.Increment(ref admissions) == requests.Length)
                {
                    queued.TrySetResult();
                }

                return ValueTask.CompletedTask;
            };
            database.BeforeOrderedCommitDrain = () => new ValueTask(drain.Task);
            var operationKeys = requests.Select((request, i) => (request.OperationId, Key: keys[i])).ToDictionary(x => x.OperationId, x => x.Key);
            var log = typeof(DatabaseKernel).GetField("_commitLog", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(node.Kernel)!;
            var stream = typeof(GroupedRedoDatabaseCommitLog).GetField("_stream", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(log)!;
            var observer = new OwnerOverlapObserver(node.Kernel, operationKeys);
            stream.GetType().GetField("_observer", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(stream, observer);
            await gate.WaitAsync(default);
            bool held = true;
            Task<DatabaseDistributedCommitReceipt>[] writes = [];
            try
            {
                writes = requests.Select(r => client.DeliverAsync(r.OperationId, r.Export()).AsTask()).ToArray();
                await queued.Task.WaitAsync(TimeSpan.FromSeconds(10));
                gate.Release();
                held = false;
                var policy = new SemanticGovernanceRuleDefinition(new("urn:test:queued-owner-policy"),
                    SemanticGovernanceRuleKind.Policy, new("urn:test:definition"), "1", false);
                await client.PublishSemanticGovernanceAsync(SemanticEntityWriteOperation.Create().OperationId,
                    nodes[1].Domain.Partition, policy).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
                Assert.IsTrue(writes.All(write => !write.IsCompleted), "The queue remains held until the new policy is durable.");
            }
            finally
            {
                if (held)
                {
                    gate.Release();
                }

                drain.TrySetResult();
                database.BeforeOrderedCommitAdmission = null;
                database.BeforeOrderedCommitDrain = null;
                try
                {
                    await Task.WhenAll(writes).WaitAsync(TimeSpan.FromSeconds(15));
                }
                catch (Exception) when (writes.All(write => write.IsCompleted))
                {

                }
            }
            Assert.AreEqual(8, writes.Length);
            foreach (var write in writes)
            {
                Assert.IsTrue(write.IsFaulted);
                var aborted = write.Exception!.Flatten().InnerExceptions.OfType<EntityOperationAbortedException>().Single();
                Assert.IsInstanceOfType<EntityForgeDbCommitException>(aborted.InnerException);
                Assert.IsInstanceOfType<NotSupportedException>(aborted.InnerException!.InnerException);
                Assert.AreEqual("Semantic dependencies require coordinated planning.", aborted.InnerException.InnerException!.Message);
            }
            Assert.AreEqual(0, observer.Groups.Count, "Rejected owner requests must never reach a data WAL append.");
            foreach (var request in requests)
            {
                var binding = (await ((IEntityOperationHost)database).Operations.FindAsync(request.OperationId, default))!;
                Assert.IsNull(binding.Committed);
                Assert.IsTrue(binding.Aborted);
                Assert.AreEqual(boundDigests[request.OperationId], binding.AbortedPlanDigest);
                await Assert.ThrowsAsync<EntityOperationAbortedException>(() => client.DeliverAsync(request.OperationId, request.Export()).AsTask());
            }
            foreach (var key in keys)
            {
                Assert.AreEqual("seed", (await ((ISemanticDatabase)database).EntityStore<Item>().ReadAsync(key))!.Value.Name);
            }
        });
    }

    private sealed class OwnerOverlapObserver(DatabaseKernel kernel, Dictionary<Guid, string> keys) : IRedoDurabilityObserver
    {
        internal readonly System.Collections.Concurrent.ConcurrentQueue<Guid[]> Groups = new();

        public void Reached(RedoDurabilityStage stage)
        {
            if (stage != RedoDurabilityStage.BeforeTailPublication)
            {
                return;
            }

            var ids = PlannedOwnerOperations(kernel);
            var selected = ids.Where(keys.ContainsKey).ToArray();
            if (selected.Length > 0)
            {
                Groups.Enqueue(selected);
            }
        }
    }

    private sealed class FailOwnerGroupPublication : IEntityForgeCommitObserver
    {
        public void Reached(EntityForgeCommitStage stage)
        {
            if (stage == EntityForgeCommitStage.AfterDurableCommit)
            {
                throw new IOException("Injected failure after the owner data group became durable.");
            }
        }
    }

    private sealed class AutomaticOwnerGroupObserver(DatabaseKernel kernel, HashSet<string> resources)
        : EntityForge.Db.Storage.Pages.IRedoDurabilityObserver, IDisposable
    {
        internal readonly System.Collections.Concurrent.ConcurrentQueue<Guid[]> DataGroups = new();
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly ManualResetEventSlim Release = new();
        private bool held;

        public void Reached(EntityForge.Db.Storage.Pages.RedoDurabilityStage stage)
        {
            if (stage != EntityForge.Db.Storage.Pages.RedoDurabilityStage.BeforeTailPublication)
            {
                return;
            }

            var runtime = typeof(DatabaseKernel).GetField("_profile", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(kernel)!;
            var planned = (Dictionary<Guid, EntityForge.Db.Semantic.Durability.DurableDatabaseCommit>)runtime.GetType()
                .GetField("_plannedCommits", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(runtime)!;
            var isolation = (IDatabasePreparedAppendReadIsolation)runtime;
            var data = planned.Where(pair => PartitionOwnerOperation.IsOwnerLocal(pair.Key) &&
                pair.Value.Kind == EntityForge.Db.Semantic.Durability.DurableDatabaseCommitKind.SemanticTransaction &&
                isolation.DescribePreparedAppendWrites(pair.Key).WritesAny(resources)).Select(pair => pair.Key).ToArray();
            if (data.Length == 0)
            {
                return;
            }

            DataGroups.Enqueue(data);
            if (held)
            {
                return;
            }

            held = true;
            Entered.TrySetResult();
            if (!Release.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("Automatic owner backlog WAL was not released.");
            }
        }

        public void Dispose() => Release.Dispose();
    }
}
