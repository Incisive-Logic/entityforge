using EntityForge.Db.Durability;
using EntityForge.Db.Mapping;
using EntityForge.Db.Distributed.Sharding;
using System.Text.Json;

namespace EntityForge.Db.Distributed.Hosting;

// Owns complete jobs, not durable outcomes. Native selection remains the arbiter
// after this bounded in-memory entry disappears or the process restarts.
internal sealed class PartitionHomeExecution(
    Func<PartitionApplicationBinding, Action<PartitionTransactionDecision>, ValueTask<DatabaseDistributedCommitReceipt>> execute) : IAsyncDisposable
{
    private const int MaximumWaiters = 1024;
    private readonly object _sync = new();
    private readonly Dictionary<Guid, Job> _jobs = [];
    private long _bytes;
    private int _waiters;
    private bool _closed;

    private sealed class Job(PartitionApplicationBinding application)
    {
        internal readonly PartitionApplicationBinding Application = application;
        internal readonly TaskCompletionSource<PartitionTransactionDecision> Commitment = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<DatabaseDistributedCommitReceipt> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal Task<DatabaseDistributedCommitReceipt> ExecuteAsync(PartitionApplicationBinding application, CancellationToken token) =>
        Start(application, token).Readability;

    internal Task<DatabaseDistributedCommitReceipt>? JoinOwned(PartitionApplicationBinding application, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (!_jobs.TryGetValue(application.Operation, out var job))
            {
                return null;
            }

            if (job.Application.Descriptor != application.Descriptor)
            {
                throw new EntityForgeDbIdempotencyException(application.Operation);
            }

            if (_waiters >= MaximumWaiters)
            {
                throw new InvalidOperationException("Home waiter capacity is exhausted.");
            }

            _waiters++;
            return WaitAsync(job, token);
        }
    }

    internal PartitionHomeCommitHandle Start(PartitionApplicationBinding application, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Job job;
        bool start = false;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (_waiters >= MaximumWaiters)
            {
                throw new InvalidOperationException("Home waiter capacity is exhausted.");
            }

            if (_jobs.TryGetValue(application.Operation, out job!))
            {
                if (job.Application.Descriptor != application.Descriptor)
                {
                    throw new EntityForgeDbIdempotencyException(application.Operation);
                }
            }
            else
            {
                long bytes = application.Requests.Sum(request => (long)request.Length);
                if (_jobs.Count >= PartitionOwnedExecution.DefaultMaximumJobs || bytes > PartitionOwnedExecution.DefaultMaximumBytes - _bytes)
                {
                    throw new InvalidOperationException("Home execution capacity is exhausted.");
                }

                job = new(application);
                _jobs.Add(application.Operation, job);
                _bytes += bytes;
                start = true;
            }
            _waiters++;
        }
        if (start)
        {
            _ = RunAsync(job);
        }

        return new(job.Commitment.Task, WaitAsync(job, token));
    }

    private async Task<DatabaseDistributedCommitReceipt> WaitAsync(Job job, CancellationToken token)
    {
        try
        {
            return await job.Completion.Task.WaitAsync(token).ConfigureAwait(false);
        }
        finally
        {
            lock (_sync)
            {
                _waiters--;
            }
        }
    }

    private async Task RunAsync(Job job)
    {
        DatabaseDistributedCommitReceipt? result = null;
        Exception? failure = null;
        try
        {
            result = await execute(job.Application, decision =>
        {
            if (decision.Operation != job.Application.Operation || !decision.Commit)
            {
                throw new InvalidDataException("Invalid durable home commitment notification.");
            }

            job.Commitment.TrySetResult(decision);
        }).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            failure = error;
        }
        lock (_sync)
        {
            _jobs.Remove(job.Application.Operation);
            _bytes -= job.Application.Requests.Sum(request => (long)request.Length);
            if (failure is null)
            {
                job.Completion.TrySetResult(result!);
            }
            else
            {
                job.Completion.TrySetException(failure);
                _ = job.Completion.Task.Exception;
            }
            if (!job.Commitment.Task.IsCompleted)
            {
                job.Commitment.TrySetException(failure ?? new InvalidDataException("Home completed without durable decision evidence."));
                _ = job.Commitment.Task.Exception;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task[] jobs;
        lock (_sync)
        {
            _closed = true;
            jobs = _jobs.Values.Select(job => (Task)job.Completion.Task).ToArray();
        }
        try
        {
            await Task.WhenAll(jobs).ConfigureAwait(false);
        }
        catch
        {

        }
    }
}

// Explicitly distinct promises. Commitment never asserts a complete readable vector.
// The owner retains and drains application work after a commitment waiter returns.
internal sealed record PartitionHomeCommitHandle(Task<PartitionTransactionDecision> Commitment,
    Task<DatabaseDistributedCommitReceipt> Readability);

internal sealed partial class PartitionOwnedNode
{
    private readonly PartitionHomeExecution _homeExecution;

    internal ValueTask<DatabaseDistributedCommitReceipt> ExecuteCoordinatorHomeAsync(string authority,
        PartitionApplicationBinding application, CancellationToken token = default) =>
        new(StartCoordinatorHome(authority, application, token).Readability);

    internal async ValueTask<DatabaseDistributedCommitReceipt> WaitCoordinatorHomeAsync(string authority,
        PartitionApplicationBinding application, CancellationToken token = default)
    {
        if (_transactions is not PartitionNativeHomeAuthority home || authority != home.Identity ||
            PartitionCoordinatorHome.Owner(application.Operation, Routing) != Domain.Partition)
        {
            throw new InvalidDataException("Readability wait belongs to another home authority.");
        }

        ObjectDisposedException.ThrowIf(_closing, this);
        // Joining an existing job never creates a job or applies a mutation.
        if (_homeExecution.JoinOwned(application, token) is { } owned)
        {
            return await owned.ConfigureAwait(false);
        }

        var selected = await home.ReadApplicationAsync(application.Operation, token)
            ?? throw new InvalidOperationException("No retained home application exists.");
        if (selected.Descriptor != application.Descriptor)
        {
            throw new EntityForgeDbIdempotencyException(application.Operation);
        }

        var client = new PartitionApplicationClient(home, Routing, Routing.Shards.Select(home.Participant).ToArray());
        var observation = await client.ResolveAsync(application.Operation, token);
        if (observation.Receipt is { } receipt)
        {
            return receipt;
        }

        if (observation.State == EntityOperationState.Aborted)
        {
            throw new EntityOperationAbortedException(application.Operation);
        }

        var decision = await home.ReadDecisionAsync(application.Operation, token);
        if (decision is { Commit: true })
        {
            throw new PartitionDecisionDeliveryException(decision,
            new IOException("Durable home commitment requires recovery or exact redelivery before readability."));
        }

        throw new InvalidOperationException("Home application is not yet committed.");
    }

    internal PartitionHomeCommitHandle StartCoordinatorHome(string authority,
        PartitionApplicationBinding application, CancellationToken token = default)
    {
        if (_transactions is not PartitionNativeHomeAuthority home || authority != home.Identity ||
            PartitionCoordinatorHome.Owner(application.Operation, Routing) != Domain.Partition ||
            application.Local || application.NativeDescriptor is not null || !application.PlanningEvidence.IsEmpty ||
            Domain.Profile is not (DatabaseProfileId.PropertyGraph or DatabaseProfileId.Rdf))
        {
            throw new NotSupportedException("Complete home execution requires its enrolled scalar transaction domain.");
        }

        ObjectDisposedException.ThrowIf(_closing, this);
        // Reconstruct the complete owner set from frozen input before accepting work.
        var owners = application.Requests.Select(request => PartitionApplicationRouting.Owner(
            Domain.Generation, Domain.Profile, Routing, application.Operation, request)).Distinct().ToArray();
        if (PartitionGraphPlanning.Required(Domain.Profile, Routing, application.Requests) ||
            !owners.Contains(Domain.Partition) || owners.Length != application.Owners.Count ||
            owners.Any(owner => !application.Owners.Contains(home.Participant(owner).Domain.Identity)))
        {
            throw new InvalidDataException("The captured request does not own its complete home participant set.");
        }

        return _homeExecution.Start(application, token);
    }

    private async ValueTask<DatabaseDistributedCommitReceipt> ExecuteHomeJobAsync(PartitionApplicationBinding application, Action<PartitionTransactionDecision> committed)
    {
        PartitionTransactionDecision? known = null;
        void Observe(DurablePartitionTransaction? entry)
        {
            if (entry is null)
            {
                return;
            }

            var manifest = PartitionCoordinatorHome.Manifest.Read(entry, TransactionAuthorityIdentity!, Routing, Domain);
            if (manifest.Application.Descriptor != application.Descriptor)
            {
                throw new EntityForgeDbIdempotencyException(application.Operation);
            }

            if (entry.State == DurablePartitionTransactionState.Committed)
            {
                known = new(entry.Operation, entry.Descriptor, true, Timestamp: entry.Timestamp);
                committed(known);
            }
        }
        try
        {
            return await ExecuteHomeJobCoreAsync(application, Observe);
        }
        catch (Exception failure) when (known is not null && failure is not PartitionDecisionDeliveryException)
        {
            throw new PartitionDecisionDeliveryException(known, failure);
        }
    }

    private async ValueTask<DatabaseDistributedCommitReceipt> ExecuteHomeJobCoreAsync(PartitionApplicationBinding application,
        Action<DurablePartitionTransaction?> observe)
    {
        var authority = (PartitionNativeHomeAuthority)_transactions!;
        Guid operation = application.Operation;
        // Keep network waits outside native/fence exclusion. Maintenance may win
        // between phases; every local phase re-enters fresh placement admission,
        // and kernel selection/floors arbitrate the retained operation.
        var initial = await Authority.ExecuteAsync(async () =>
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            var entry = await Kernel.PartitionTransactions.ReadCoordinatorAsync(operation);
            observe(entry);
            PartitionTransactionMember? local = entry is null ? await BuildApplicationMemberAsync(application, CancellationToken.None) : null;
            return (Entry: entry, Local: local);
        });
        var selected = initial.Entry;
        if (selected is null)
        {
            var owners = application.Owners.Where(owner => owner != Domain.Identity)
                .Select(owner => authority.Participant(new(owner.Partition))).ToArray();
            var members = await PartitionParticipantWork.RunAsync(owners, owner => owner.ProposeApplicationAsync(application, CancellationToken.None));
            var proposal = new PartitionTransactionPlan(operation, Routing.MapVersion, members.Append(initial.Local!).ToArray());
            application.RequireCoordinatedPlan(proposal);
            var local = RequireNativeMember(proposal);
            var evidence = new DurablePartitionCoordinator(authority.Identity, application.Descriptor,
                JsonSerializer.SerializeToUtf8Bytes(PartitionCoordinatorHome.Manifest.From(application, proposal)));
            selected = await Authority.ExecuteAsync(async () =>
            {
                ObjectDisposedException.ThrowIf(_closing, this);
                // The home itself constructed this member from owned input. Preserve
                // its original predicates; the common kernel validates/reserves them
                // and returns the whole retained winner if another selector won.
                var entry = await Kernel.PartitionTransactions.SelectCoordinatorAsync(operation, proposal.Descriptor,
                    local.ReadVersion, local.Payload, evidence);
                observe(entry);
                return entry;
            });
        }
        var manifest = PartitionCoordinatorHome.Manifest.Read(selected, authority.Identity, Routing, Domain);
        if (manifest.Application.Descriptor != application.Descriptor)
        {
            throw new EntityForgeDbIdempotencyException(operation);
        }

        var plan = manifest.Plan;
        var others = plan.Members.Where(member => member.Domain != Domain).Select(member =>
        {
            var participant = authority.Participant(member.Domain.Partition);
            if (participant.Domain != member.Domain)
            {
                throw new InvalidDataException("Selected participant incarnation differs.");
            }

            return participant;
        }).ToArray();
        PartitionTransactionDecision? decision = selected.State == DurablePartitionTransactionState.Prepared ? null :
            new(operation, plan.Descriptor, selected.State == DurablePartitionTransactionState.Committed, Timestamp: selected.Timestamp);
        DatabaseCommitReceipt? homeReceipt = null;
        try
        {
            if (decision is null)
            {
                bool prepared = true;
                DurableTimestampState? agreed = null;
                try
                {
                    if (Kernel.PartitionTransactions.TimestampVisibilityEnabled)
                    {
                        var votes = await PartitionParticipantWork.RunAsync(others, owner => owner.PrepareTimestampNativeAsync(plan, CancellationToken.None));
                        agreed = await Kernel.PartitionTransactions.ChooseTimestampAsync(votes.Append(selected.Timestamp ??
                            throw new InvalidDataException("Home has no durable timestamp vote.")).ToArray(), CancellationToken.None);
                    }
                    else
                    {
                        await PartitionParticipantWork.RunAsync(others, async owner =>
                    {
                        await owner.PrepareNativeAsync(plan, CancellationToken.None);
                        return true;
                    });
                    }
                }
                catch
                {
                    prepared = false;
                }
                // Retain the receipt from this native commitment. Do not enqueue a
                // second home completion command through ordinary terminal delivery.
                (selected, homeReceipt) = await CompleteHomeJobAsync(selected, prepared, agreed);
                observe(selected);
                decision = new(operation, plan.Descriptor, selected.State == DurablePartitionTransactionState.Committed, Timestamp: selected.Timestamp);
            }
            if (decision.Commit && homeReceipt is null)
            {
                homeReceipt = await Authority.ExecuteAsync(() => Kernel.PartitionTransactions.ResolveAsync(operation, plan.Descriptor));
            }

            var remote = await PartitionParticipantWork.RunAsync(others, async owner =>
            {
                var receipt = await owner.ApplyPartitionDecisionAsync(operation, CancellationToken.None);
                if (!decision.Commit)
                {
                    return null;
                }

                if (receipt is null || receipt.TransactionId != operation || receipt.DatabaseIncarnation != owner.Domain.Incarnation ||
                    receipt.Profile != owner.Domain.Profile || receipt.Outcome == DatabaseCommitOutcome.Committed)
                {
                    throw new InvalidDataException("Remote completion does not establish the selected readable member.");
                }

                return new DatabasePartitionCommit(owner.Domain.Identity, receipt);
            });
            if (!decision.Commit)
            {
                throw new EntityOperationAbortedException(operation);
            }

            if (homeReceipt is null || homeReceipt.Outcome == DatabaseCommitOutcome.Committed)
            {
                throw new InvalidDataException("Home commitment is not confirmed readable.");
            }

            return new(operation, remote.OfType<DatabasePartitionCommit>().Append(new(Domain.Identity, homeReceipt)).ToArray());
        }
        catch (EntityOperationCommittedException<DatabaseCommitReceipt> failure)
        {
            throw new PartitionDecisionDeliveryException(new(operation, plan.Descriptor, true), failure);
        }
        catch (Exception failure) when (decision is { Commit: true })
        {
            throw new PartitionDecisionDeliveryException(decision, failure);
        }
    }

    private async ValueTask<(DurablePartitionTransaction Entry, DatabaseCommitReceipt? Receipt)> CompleteHomeJobAsync(
        DurablePartitionTransaction selected, bool commit, DurableTimestampState? time = null)
    {
        DatabaseCommitReceipt? receipt = null;
        try
        {
            return await Authority.ExecuteAsync(async () =>
            {
                ObjectDisposedException.ThrowIf(_closing, this);
                // Terminal contenders (including maintenance) use this same kernel
                // arbitration. An opposite winner is observed, never overwritten.
                try
                {
                    receipt = await Kernel.PartitionTransactions.CompleteTimestampedAsync(selected.Operation, selected.Descriptor, commit, time);
                }
                catch (InvalidDataException)
                {
                    var winner = await Kernel.PartitionTransactions.ReadCoordinatorAsync(selected.Operation);
                    if (winner is null || winner.Descriptor != selected.Descriptor || winner.State == DurablePartitionTransactionState.Prepared)
                    {
                        throw;
                    }

                    if (winner.State == DurablePartitionTransactionState.Committed)
                    {
                        receipt = await Kernel.PartitionTransactions.ResolveAsync(selected.Operation, selected.Descriptor);
                    }

                    return (winner, receipt);
                }
                _homeNotifications.Publish(selected.Operation);
                return (selected.Complete(commit ? DurablePartitionTransactionState.Committed : DurablePartitionTransactionState.Aborted,
                    receipt?.Version, time), receipt);
            });
        }
        catch (Exception failure) when (receipt is not null)
        {
            throw new EntityOperationCommittedException<DatabaseCommitReceipt>(receipt, receipt, failure);
        }
    }
}
