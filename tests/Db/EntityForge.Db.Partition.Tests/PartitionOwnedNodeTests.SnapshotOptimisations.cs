using System.Collections.Concurrent;
using System.Text.Json;
using EntityForge.Db.Durability;
using EntityForge.Db.Kernel;
using EntityForge.Db.Distributed.Hosting;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [TestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph)]
    [DataRow(DatabaseProfileId.Rdf)]
    public async Task Snapshot_projection_joins_native_commit_and_preserves_contradiction_checks(DatabaseProfileId profile)
    {
        var opened = await OpenReservationStore(profile, Path.Combine(_root, "snapshot-projection"));
        await using var database = opened.Database;
        var kernel = opened.Kernel;
        Guid subject = Guid.NewGuid(), seed = Guid.NewGuid(), operation = Guid.NewGuid();
        await kernel.CommitAsync(seed, ReservationPayload(profile, seed, [subject], null, create: true));
        await kernel.PartitionTransactions.PrepareAsync(operation, ReservationDescriptor, kernel.CurrentVersion,
            ReservationPayload(profile, operation, [subject], kernel.CurrentVersion));
        var entries = (Dictionary<Guid, DurablePartitionTransaction>)typeof(DatabaseKernel)
            .GetField("_partitionTransactions", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(kernel)!;
        var prepared = entries[operation];
        await kernel.PartitionTransactions.CompleteAsync(operation, ReservationDescriptor, true);
        var terminal = entries[operation];
        using var expected = await kernel.PartitionTransactions.CaptureSnapshotAsync();
        try
        {
            // Reproduce native commitment before its separate terminal projection.
            entries[operation] = prepared;
            using var projected = await kernel.PartitionTransactions.CaptureSnapshotAsync();
            Assert.AreEqual(expected.Evidence.Protocol, projected.Evidence.Protocol);
            CollectionAssert.AreEqual(expected.Evidence.Committed.ToArray(), projected.Evidence.Committed.ToArray());
            Assert.AreEqual(0, projected.Evidence.Prepared.Count);
            var changed = prepared.Payload.ToArray();
            changed[0] ^= 1;
            entries[operation] = new(operation, prepared.Descriptor, prepared.ReadVersion, changed, prepared.State);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => kernel.PartitionTransactions.CaptureSnapshotAsync().AsTask());
            entries[operation] = prepared.Complete(DurablePartitionTransactionState.Aborted);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => kernel.PartitionTransactions.CaptureSnapshotAsync().AsTask());
        }
        finally
        {
            entries[operation] = terminal;
        }
    }

    [TestMethod]
    public async Task Parallel_snapshot_failure_joins_unanswered_sibling_before_abandonment()
    {
        var (routing, a, b) = SnapshotParticipants();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rejected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool siblingCancelled = false;
        a.Acquiring = async token =>
        {
            await entered.Task.WaitAsync(token);
            rejected.SetResult();
            throw new IOException("Rejected identity");
        };
        b.Acquiring = async token =>
        {
            entered.SetResult();
            using var registration = token.Register(() => siblingCancelled = true);
            await release.Task.WaitAsync(token);
        };
        var read = PartitionRemoteRead.ExecuteAsync([a, b], routing, (_, _) => ValueTask.FromResult(true), timeout.Token, useSnapshots: true).AsTask();
        await rejected.Task.WaitAsync(timeout.Token);
        Assert.IsFalse(read.IsCompleted);
        Assert.AreEqual(0, a.Abandoned + b.Abandoned);
        release.SetResult();
        await Assert.ThrowsExactlyAsync<AggregateException>(() => read);
        Assert.IsFalse(siblingCancelled);
        Assert.AreEqual(2, a.Abandoned + b.Abandoned);
        Assert.AreEqual(0, a.Active.Count + b.Active.Count);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Parallel_snapshot_validation_joins_before_retry_or_failure(bool throwFailure)
    {
        var (routing, a, b) = SnapshotParticipants();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rejected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        a.Validating = async token =>
        {
            await entered.Task.WaitAsync(token);
            rejected.SetResult();
            if (throwFailure)
            {
                throw new IOException("Validation response lost");
            }

            return false;
        };
        b.Validating = async token =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(token);
            return true;
        };
        b.Abandoning = () =>
        {
            timeout.Cancel();
            return ValueTask.CompletedTask;
        };
        bool projected = false;
        var read = PartitionRemoteRead.ExecuteAsync([a, b], routing, (_, _) =>
        {
            projected = true;
            return ValueTask.FromResult(true);
        }, timeout.Token, useSnapshots: true).AsTask();
        await rejected.Task.WaitAsync(timeout.Token);
        Assert.IsFalse(read.IsCompleted);
        Assert.AreEqual(2, a.Active.Count + b.Active.Count);
        release.SetResult();
        if (throwFailure)
        {
            await Assert.ThrowsExactlyAsync<AggregateException>(() => read);
        }
        else
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() => read);
        }

        Assert.IsFalse(projected);
        Assert.AreEqual(2, a.Abandoned + b.Abandoned);
        Assert.AreEqual(0, a.Active.Count + b.Active.Count);
    }

    [TestMethod]
    public async Task Parallel_snapshot_round_is_bounded_and_validates_only_after_all_captures()
    {
        const int ownerCount = 16, maximumConcurrent = 8;
        var routing = new StableHashDistributedShardRouter(new(new(1), Enumerable.Range(0, ownerCount).Select(i => new LogicalShardId(i.ToString())).ToArray())).Binding;
        Guid generation = Guid.NewGuid();
        var owners = routing.Shards.Select(shard => new SnapshotParticipant(new(generation, shard, routing.Fingerprint,
            DatabaseProfileId.PropertyGraph, Guid.NewGuid()))).ToArray();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int active = 0, captured = 0, validations = 0;
        foreach (var owner in owners)
        {
            owner.Acquiring = async token =>
            {
                int running = Interlocked.Increment(ref active);
                Assert.IsTrue(running <= maximumConcurrent);
                if (running == maximumConcurrent)
                {
                    release.TrySetResult();
                }

                await release.Task.WaitAsync(token);
                Interlocked.Increment(ref captured);
                Interlocked.Decrement(ref active);
            };
            owner.Validating = _ =>
            {
                Assert.AreEqual(ownerCount, Volatile.Read(ref captured));
                Interlocked.Increment(ref validations);
                return ValueTask.FromResult(true);
            };
        }
        var result = await PartitionRemoteRead.ExecuteAsync(owners, routing, (_, _) =>
        {
            Assert.AreEqual(ownerCount, validations);
            return ValueTask.FromResult(true);
        }, timeout.Token, useSnapshots: true);
        Assert.IsTrue(result.Value);
        Assert.IsTrue(owners.All(owner => owner.Active.IsEmpty));
    }

    [TestMethod]
    public async Task Parallel_snapshot_external_cancellation_drains_and_abandons_attempted_pins()
    {
        var (routing, a, b) = SnapshotParticipants();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int arrivals = 0;
        async ValueTask Acquire(CancellationToken token)
        {
            if (Interlocked.Increment(ref arrivals) == 2)
            {
                entered.SetResult();
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }
        a.Acquiring = Acquire;
        b.Acquiring = Acquire;
        var read = PartitionRemoteRead.ExecuteAsync([a, b], routing, (_, _) => ValueTask.FromResult(true), timeout.Token, useSnapshots: true).AsTask();
        await entered.Task.WaitAsync(timeout.Token);
        timeout.Cancel();
        await Assert.ThrowsExactlyAsync<AggregateException>(() => read);
        Assert.AreEqual(2, a.Abandoned + b.Abandoned);
        Assert.AreEqual(0, a.Active.Count + b.Active.Count);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Partial_snapshot_retry_releases_pins_and_never_hides_cleanup_failure(bool failCleanup)
    {
        Guid operation = Guid.NewGuid();
        var (routing, a, b) = SnapshotParticipants();
        a.Evidence = new(new(1), new string('A', 64), [operation], []);
        b.Evidence = new(new(2), new string('B', 64), [], [operation]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        a.Abandoning = () => failCleanup ? ValueTask.FromException(new IOException("Cleanup failed")) : ValueTask.CompletedTask;
        b.Abandoning = () =>
        {
            if (b.Abandoned == 3)
            {
                timeout.Cancel();
            }

            return ValueTask.CompletedTask;
        };
        bool projected = false;
        var read = PartitionRemoteRead.ExecuteAsync([a, b], routing, (_, _) =>
        {
            projected = true;
            return ValueTask.FromResult(true);
        }, timeout.Token, useSnapshots: true).AsTask();
        if (failCleanup)
        {
            await Assert.ThrowsExactlyAsync<AggregateException>(() => read);
        }
        else
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() => read);
        }

        Assert.IsFalse(projected);
        Assert.AreEqual(failCleanup ? 2 : 3, a.Abandoned);
        Assert.AreEqual(failCleanup ? 1 : 3, b.Abandoned);
        Assert.AreEqual(0, a.Active.Count + b.Active.Count);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Parallel_completion_joins_siblings_before_success_or_rollback(bool fail)
    {
        var (routing, a, b) = SnapshotParticipants();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int arrivals = 0;
        async ValueTask Complete(CancellationToken token)
        {
            if (Interlocked.Increment(ref arrivals) == 2)
            {
                both.TrySetResult();
            }

            await both.Task.WaitAsync(token);
        }
        a.Completing = async token =>
        {
            await Complete(token);
            if (fail)
            {
                throw new IOException("Completion failed");
            }
        };
        b.Completing = async token =>
        {
            await Complete(token);
            await release.Task.WaitAsync(token);
        };
        var read = PartitionRemoteRead.ExecuteAsync([a, b], routing, (_, _) => ValueTask.FromResult(true), timeout.Token, useSnapshots: true).AsTask();
        await both.Task.WaitAsync(timeout.Token);
        Assert.IsFalse(read.IsCompleted);
        Assert.AreEqual(0, a.Abandoned + b.Abandoned);
        release.SetResult();
        if (fail)
        {
            await Assert.ThrowsExactlyAsync<AggregateException>(() => read);
            Assert.AreEqual(2, a.Abandoned + b.Abandoned);
        }
        else
        {
            Assert.IsTrue((await read).Value);
        }

        Assert.AreEqual(0, a.Active.Count + b.Active.Count);
    }

    private static (DistributedRoutingBinding Routing, SnapshotParticipant A, SnapshotParticipant B) SnapshotParticipants()
    {
        var routing = new StableHashDistributedShardRouter(new(new(1), [new("a"), new("b")])).Binding;
        Guid generation = Guid.NewGuid();
        return (routing, new(new(generation, new("a"), routing.Fingerprint, DatabaseProfileId.PropertyGraph, Guid.NewGuid())),
            new(new(generation, new("b"), routing.Fingerprint, DatabaseProfileId.PropertyGraph, Guid.NewGuid())));
    }

    private sealed class SnapshotParticipant(PartitionExecutionDomain domain) : IPartitionReadParticipant
    {
        public PartitionExecutionDomain Domain => domain;

        internal ConcurrentDictionary<Guid, byte> Active { get; } = new();

        internal int Abandoned;
        internal Guid? ClockDomain;
        internal DateTimeOffset HistoryExpires;
        internal DatabasePartitionSnapshotEvidence Evidence = new(new(0), new string('A', 64), [], []);
        internal Func<CancellationToken, ValueTask>? Acquiring;
        internal Func<CancellationToken, ValueTask<bool>>? Validating;
        internal Func<PartitionReadQuery, CancellationToken, ValueTask<byte[]>>? Reading;
        internal Func<ValueTask>? Abandoning;
        internal Func<CancellationToken, ValueTask>? Settling;
        internal Func<CancellationToken, ValueTask>? Completing;

        public ValueTask<PartitionReadCut> AcquireCutAsync(Guid id, DateTimeOffset expires, CancellationToken token = default) =>
            throw new AssertFailedException("MVCC work must not acquire a frozen cut.");

        public async ValueTask<PartitionReadCut> AcquireHistoryProtectionAsync(Guid id, DateTimeOffset expires, CancellationToken token = default)
        {
            HistoryExpires = expires;
            Active.TryAdd(id, 0);
            if (Acquiring is not null)
            {
                await Acquiring(token);
            }

            return new(id, new(domain.Identity, Evidence.Version), expires, ClockDomain: ClockDomain, HistoryProtection: true);
        }

        public async ValueTask<PartitionReadCut> AcquireSnapshotCutAsync(Guid id, DateTimeOffset expires, CancellationToken token = default)
        {
            Active.TryAdd(id, 0);
            if (Acquiring is not null)
            {
                await Acquiring(token);
            }

            return new(id, new(domain.Identity, Evidence.Version), expires, SnapshotEvidence: JsonSerializer.Serialize(Evidence.Summarize()), ClockDomain: ClockDomain);
        }

        public async ValueTask<byte[]> ReadCutAsync(PartitionReadCut cut, PartitionReadQuery query, CancellationToken token = default)
        {
            if (query.Kind != "settle-snapshot")
            {
                return Reading is not null ? await Reading(query, token) : JsonSerializer.SerializeToUtf8Bytes(Validating is null || await Validating(token));
            }

            try
            {
                if (Settling is not null)
                {
                    await Settling(token);
                }

                return JsonSerializer.SerializeToUtf8Bytes(true);
            }
            finally
            {
                await AbandonCutAsync(cut.Id, CancellationToken.None);
            }
        }

        public async ValueTask CompleteCutAsync(PartitionReadCut cut, CancellationToken token = default)
        {
            if (Completing is not null)
            {
                await Completing(token);
            }

            Assert.IsTrue(Active.TryRemove(cut.Id, out _));
        }

        public async ValueTask AbandonCutAsync(Guid id, CancellationToken token = default)
        {
            Active.TryRemove(id, out _);
            Interlocked.Increment(ref Abandoned);
            if (Abandoning is not null)
            {
                await Abandoning();
            }
        }
    }
}
