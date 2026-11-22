using EntityForge.Db.Durability;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Distributed.Hosting;

internal static class PartitionRetention
{
    internal static async ValueTask RetainAsync(IPartitionTransactionAuthority authority, IReadOnlyList<PartitionOwnedNode> owners,
        DateTimeOffset retainedSince, CancellationToken token = default)
    {
        long floor = retainedSince.ToUnixTimeMilliseconds();
        if (floor <= 0 || retainedSince > DateTimeOffset.UtcNow)
        {
            throw new ArgumentOutOfRangeException(nameof(retainedSince));
        }

        PartitionGlobalRead.RequireCompleteOwners(owners);
        if (owners.Any(n => n.TransactionAuthorityIdentity != authority.Identity))
        {
            throw new InvalidDataException("Retention authority differs.");
        }

        var handles = new List<PartitionOwnedNode.Maintenance>();
        var failures = new List<Exception>();
        try
        {
            foreach (var node in owners.OrderBy(n => n.Domain.Partition.Value, StringComparer.Ordinal))
            {
                handles.Add(await node.DrainForMaintenanceAsync(token));
            }
            // All receiving callbacks have drained, including preparations that
            // observed authority before waiting for native admission. Decide any
            // abandoned preparations; an already committed outcome always wins.
            var pending = new HashSet<Guid>();
            foreach (var handle in handles)
            {
                foreach (var entry in await handle.ReadPreparationsAsync(token))
                {
                    if (entry.State == DurablePartitionTransactionState.Prepared)
                    {
                        pending.Add(entry.Operation);
                    }
                }
            }

            foreach (Guid operation in pending)
            {
                var plan = await authority.ReadPlanAsync(operation, token) ?? throw new InvalidDataException("Prepared plan is missing.");
                PartitionTransactionDecision decision;
                if (authority is PartitionNativeHomeAuthority homeAuthority)
                {
                    var home = handles.Single(handle => handle.Domain.Partition == PartitionCoordinatorHome.Owner(operation, homeAuthority.Routing));
                    await home.AbortHomeAsync(operation, token);
                    decision = await authority.ReadDecisionAsync(operation, token) ?? throw new InvalidDataException("Missing home maintenance decision.");
                }
                else
                {
                    decision = await authority.DecideAsync(operation, plan.Descriptor, false, token);
                }

                foreach (var member in plan.Members)
                {
                    var owner = handles.SingleOrDefault(h => h.Domain == member.Domain)
                        ?? throw new InvalidDataException("Retention lacks a selected owner.");
                    await owner.ApplyAsync(plan, decision, token);
                }
            }
            foreach (var handle in handles)
            {
                await handle.RetainAsync(retainedSince, token);
            }

            foreach (var handle in handles)
            {
                await handle.VerifyAsync();
            }
            // Native floors and evidence pruning are durable before authority
            // expiry. An interrupted prefix leaves authority evidence available.
            await authority.RetainAsync(floor, token);
            foreach (var clock in owners.Select(o => o.SharedTimestamps).OfType<EntityForge.Db.Kernel.DatabaseSharedTimestampService>().DistinctBy(c => c.Identity))
            {
                await clock.RetireAsync(retainedSince, token);
            }
        }
        catch (Exception failure)
        {
            failures.Add(failure);
        }
        foreach (var handle in Enumerable.Reverse(handles))
        {
            try
            {
                await handle.DisposeAsync();
            }
            catch (Exception failure)
            {
                failures.Add(failure);
            }
        }

        if (failures.Count != 0)
        {
            throw new AggregateException("Partition retention did not complete on every owner.", failures);
        }
    }
}

internal sealed partial class PartitionOwnedNode
{
    internal async ValueTask<Maintenance> DrainForMaintenanceAsync(CancellationToken token)
    {
        await _lifecycle.WaitAsync(token);
        ShardPlacementLeaseSupervisor.LeaseOperation? lease = null;
        try
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            await _proposals.DrainAsync().WaitAsync(token);
            if (_decisionStream is not null)
            {
                await _decisionStream.StopAsync();
            }

            lease = await _supervisor.AcquireOperationAsync(token);
            var seal = await _fence.SealAsync(Enrollment.Claim, lease.Token);
            return new(this, lease, seal);
        }
        catch
        {
            if (lease is not null)
            {
                await lease.DisposeAsync();
            }

            _decisionStream?.Start();
            _lifecycle.Release();
            throw;
        }
    }

    internal sealed class Maintenance(PartitionOwnedNode owner, ShardPlacementLeaseSupervisor.LeaseOperation lease,
        IShardFenceSeal seal) : IAsyncDisposable
    {
        private bool _disposed;
        private bool _resumed;
        private Task? _retention;

        internal bool RetentionStarted => _retention is not null;

        internal async ValueTask BeginRetentionAsync(DateTimeOffset floor, CancellationToken lifetime)
        {
            if (_retention is not null)
            {
                throw new InvalidOperationException("Retention already started on this cut.");
            }

            var captured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            long queued = System.Diagnostics.Stopwatch.GetTimestamp();
            _retention = Task.Run(async () =>
            {
                PartitionRemoteRetention.OwnerPhase("owner-task-queue", queued);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime, lease.Token);
                try
                {
                    await RetainCoreAsync(floor, async () =>
                    {
                        await ResumeAsync();
                        captured.TrySetResult();
                    }, linked.Token);
                    // Non-concurrent storage keeps its fence until the full checkpoint completes.
                    await ResumeAsync();
                    captured.TrySetResult();
                }
                catch (Exception failure)
                {
                    captured.TrySetException(failure);
                    throw;
                }
            }, CancellationToken.None);
            await captured.Task.ConfigureAwait(false);
        }

        internal async ValueTask FinishRetentionAsync()
        {
            if (_retention is null)
            {
                throw new InvalidOperationException("Retention has not started.");
            }

            await _retention.ConfigureAwait(false);
        }

        private async ValueTask ResumeAsync()
        {
            if (_resumed)
            {
                return;
            }

            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            await seal.DisposeAsync();
            PartitionRemoteRetention.OwnerPhase("owner-fence-release", started);
            _resumed = true;
            owner._decisionStream?.Start();
            owner._lifecycle.Release();
        }

        internal PartitionExecutionDomain Domain => owner.Domain;

        internal async ValueTask<IReadOnlyList<DurablePartitionTransaction>> ReadPreparationsAsync(CancellationToken token)
        {
            lease.VerifyLocalAuthority();
            var entries = await owner.Kernel.PartitionTransactions.ReadAsync(token);
            foreach (var entry in entries.Where(IsNativeLocal))
            {
                var application = await owner._transactions!.ReadApplicationAsync(entry.Operation, token)
                    ?? throw new InvalidDataException("Native local retention lost its selection.");
                owner.ValidateNativeLocal(entry, application);
                if (entry.State == DurablePartitionTransactionState.Prepared)
                {
                    await owner.ResolveNativeLocalAsync(application, abort: true, token);
                }
            }
            await lease.VerifyAuthorityAsync();
            return entries.Where(entry => !IsNativeLocal(entry)).ToArray();
        }

        internal async ValueTask AbortHomeAsync(Guid operation, CancellationToken token)
        {
            lease.VerifyLocalAuthority();
            if (owner._transactions is not PartitionNativeHomeAuthority ||
                PartitionCoordinatorHome.Owner(operation, owner.Routing) != Domain.Partition)
            {
                throw new InvalidDataException("Maintenance does not own the immutable home.");
            }

            var selected = await owner.Kernel.PartitionTransactions.ReadCoordinatorAsync(operation, token)
                ?? throw new InvalidDataException("Missing home preparation.");
            _ = PartitionCoordinatorHome.Manifest.Read(selected, owner.TransactionAuthorityIdentity!, owner.Routing, Domain);
            if (selected.State == DurablePartitionTransactionState.Prepared)
            {
                await owner.Kernel.PartitionTransactions.CompleteAsync(operation, selected.Descriptor, false, token);
            }

            owner._homeNotifications.Publish(operation);
            await lease.VerifyAuthorityAsync();
        }

        internal async ValueTask ApplyAsync(PartitionTransactionPlan plan, PartitionTransactionDecision decision, CancellationToken token)
        {
            lease.VerifyLocalAuthority();
            var application = await (owner._transactions ?? throw new InvalidDataException("Missing maintenance authority."))
                .ReadApplicationAsync(plan.Operation, token) ?? throw new InvalidDataException("Missing retained application.");
            application.RequireCoordinatedPlan(plan);
            _ = owner.RequireNativeMember(plan, !application.PlanningEvidence.IsEmpty);
            if (plan.Operation != decision.Operation || plan.Descriptor != decision.Descriptor)
            {
                throw new InvalidDataException("Decision differs from plan.");
            }

            if (!decision.Commit && !(await ReadPreparationsAsync(token)).Any(p => p.Operation == plan.Operation))
            {
                await owner.CompleteAbsentTimestampAsync(plan.Operation, plan.Descriptor, token);
                return;
            }
            await owner.Kernel.PartitionTransactions.CompleteAsync(plan.Operation, plan.Descriptor, decision.Commit, token);
            await lease.VerifyAuthorityAsync();
        }

        internal ValueTask RetainAsync(DateTimeOffset floor, CancellationToken token) => RetainCoreAsync(floor, null, token);

        private async ValueTask RetainCoreAsync(DateTimeOffset floor, Func<ValueTask>? captured, CancellationToken token)
        {
            long phaseStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            void Phase(string name)
            {
                PartitionRemoteRetention.OwnerPhase(name, phaseStarted);
                phaseStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            }
            lease.VerifyLocalAuthority();
            if (owner._native.Database is EntityForge.Db.Mapping.IEntityOperationHost operations)
            {
                await operations.Operations.ReconcileAbandonedOwnersAsync(floor.ToUnixTimeMilliseconds(), token);
            }

            Phase("owner-reconcile-operations");
            await owner.ReconcileTimestampsAsync(token);
            Phase("owner-reconcile-timestamps");
            if (owner.SharedTimestamps is { } shared)
            {
                await shared.MaintainAsync(owner.TimestampParticipantId, owner.Kernel.CurrentVersion.Value, token);
            }

            Phase("owner-maintain-timestamps");
            await owner.RequireTimestampDrainAsync(token);
            Phase("owner-drain-timestamps");
            if (owner.SharedTimestamps is not null)
            {
                await owner.Kernel.PartitionTransactions.ReleaseTimestampHistoryAsync(token);
            }

            Phase("owner-release-history");
            bool historyReleased = owner.SharedTimestamps is not null;
            try
            {
                if (captured is null)
                {
                    await owner.Kernel.PartitionTransactions.RetainAsync(floor, token);
                }
                else
                {
                    await owner.Kernel.PartitionTransactions.RetainConcurrentlyAsync(floor, async () =>
                {
                    Phase("owner-checkpoint-capture");
                    if (historyReleased)
                    {
                        await owner.Kernel.PartitionTransactions.EnableTimestampHistoryAsync(CancellationToken.None);
                        historyReleased = false;
                    }
                    Phase("owner-enable-history");
                    await captured();
                    Phase("owner-captured-callback");
                }, token);
                }

                Phase("owner-checkpoint-finish");
            }
            finally
            {
                if (historyReleased)
                {
                    await owner.Kernel.PartitionTransactions.EnableTimestampHistoryAsync(CancellationToken.None);
                }
            }
            await lease.VerifyAuthorityAsync();
        }

        internal ValueTask VerifyAsync() => lease.VerifyAuthorityAsync();

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                if (_retention is not null)
                {
                    await _retention.ConfigureAwait(false);
                }

                await lease.VerifyAuthorityAsync();
            }
            finally
            {
                try
                {
                    await ResumeAsync();
                }
                finally
                {
                    await lease.DisposeAsync();
                    _disposed = true;
                }
            }
        }
    }
}
