namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [DataTestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph, false)]
    [DataRow(DatabaseProfileId.Rdf, false)]
    [DataRow(DatabaseProfileId.Semantic, false)]
    [DataRow(DatabaseProfileId.PropertyGraph, true)]
    [DataRow(DatabaseProfileId.Rdf, true)]
    [DataRow(DatabaseProfileId.Semantic, true)]
    public async Task Remote_retention_drains_concurrent_owners_before_expiry_or_failure_cleanup(DatabaseProfileId profile, bool fail)
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
        await using var first = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "a"), profile, a.Lease, routing, a, Options, transactionAuthority: decisions);
        await using var second = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "b"), profile, b.Lease, routing, b, Options, transactionAuthority: decisions);
        var left = new PausedRetention(first, fail);
        var right = new PausedRetention(second, false);
        left.BeforeAcquire = right.BeforeAcquire = () => Assert.IsTrue(left.Prepared && right.Prepared);
        var task = PartitionRemoteRetention.RetainAsync(decisions, [left, right], routing, DateTimeOffset.UtcNow.AddMinutes(-1)).AsTask();
        try
        {
            await Task.WhenAll(left.Started.Task, right.Started.Task).WaitAsync(TimeSpan.FromSeconds(5));
            left.Release.TrySetResult();
            await left.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(task.IsCompleted);
            Assert.AreEqual(0, decisions.RetentionCalls);
            Assert.AreEqual(0, left.Abandoned + right.Abandoned);
            Assert.AreEqual(0, left.Completed + right.Completed);
        }
        finally
        {
            left.Release.TrySetResult();
            right.Release.TrySetResult();
        }
        if (fail)
        {
            await Assert.ThrowsExactlyAsync<AggregateException>(() => task);
            Assert.AreEqual(0, decisions.RetentionCalls);
            Assert.AreEqual(1, left.Abandoned);
            Assert.AreEqual(1, right.Abandoned);
        }
        else
        {
            await task;
            Assert.AreEqual(1, decisions.RetentionCalls);
            Assert.AreEqual(1, left.Completed);
            Assert.AreEqual(1, right.Completed);
        }
        Assert.AreEqual(0, first.UnreleasedReadCutCount + second.UnreleasedReadCutCount);
    }

    private sealed class PausedRetention(PartitionOwnedNode inner, bool fail) : IPartitionMaintenanceParticipant
    {
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Abandoned, Completed;
        internal bool Prepared;
        internal Action? BeforeAcquire;

        public async ValueTask PrepareMaintenanceAsync(CancellationToken token = default)
        {
            await inner.PrepareMaintenanceAsync(token);
            Prepared = true;
        }

        public PartitionExecutionDomain Domain => inner.Domain;

        public string? TransactionAuthorityIdentity => inner.TransactionAuthorityIdentity;

        public ValueTask<PartitionReadCut> AcquireMaintenanceAsync(Guid id, DateTimeOffset expires, CancellationToken token = default)
        {
            BeforeAcquire?.Invoke();
            return inner.AcquireMaintenanceAsync(id, expires, token);
        }

        public ValueTask<PartitionReadCut> AcquireCutAsync(Guid id, DateTimeOffset expires, CancellationToken token = default) => inner.AcquireCutAsync(id, expires, token);

        public ValueTask<byte[]> ReadCutAsync(PartitionReadCut cut, PartitionReadQuery query, CancellationToken token = default) => inner.ReadCutAsync(cut, query, token);

        public async ValueTask<byte[]> MaintainAsync(PartitionReadCut cut, PartitionMaintenanceCommand command, CancellationToken token = default)
        {
            if (command.Action != "retain")
            {
                return await inner.MaintainAsync(cut, command, token);
            }

            Started.TrySetResult();
            try
            {
                await Release.Task.WaitAsync(token);
                if (fail)
                {
                    throw new IOException("Injected checkpoint failure.");
                }

                return await inner.MaintainAsync(cut, command, token);
            }
            finally
            {
                Finished.TrySetResult();
            }
        }

        public async ValueTask CompleteCutAsync(PartitionReadCut cut, CancellationToken token = default)
        {
            Completed++;
            await inner.CompleteCutAsync(cut, token);
        }

        public async ValueTask AbandonCutAsync(Guid id, CancellationToken token = default)
        {
            Abandoned++;
            await inner.AbandonCutAsync(id, token);
        }
    }
}
