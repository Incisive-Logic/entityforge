using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Distributed.Hosting;

internal sealed record PartitionTransactionResult(PartitionTransactionDecision Decision,
    IReadOnlyList<PartitionCommitResult> Members, Exception? PreparationFailure = null)
{
    internal DatabaseDistributedCommitReceipt Completion => Decision.Commit
        ? new(Decision.Operation, Members.Select(m => m.Member).ToArray())
        : throw new InvalidOperationException("An aborted decision has no committed completion.");
}

internal sealed class PartitionDecisionDeliveryException(PartitionTransactionDecision decision, Exception failure)
    : Exception("The partition decision is durable, but delivery has not completed on every owner.", failure)
{
    internal PartitionTransactionDecision Decision => decision;
}

internal sealed class PartitionTransactionCoordinator(IPartitionTransactionAuthority authority)
{
    internal async ValueTask<PartitionTransactionResult> ExecuteAsync(PartitionTransactionPlan proposal,
        IReadOnlyList<IPartitionTransactionParticipant> owners, CancellationToken token = default)
    {
        if (owners.Count != proposal.Members.Count || owners.Select(n => n.Domain).Distinct().Count() != owners.Count ||
            owners.Any(n => n.TransactionAuthorityIdentity != authority.Identity || !proposal.Members.Any(m => m.Domain == n.Domain)))
        {
            throw new InvalidDataException("The exact selected participant set and authority are required.");
        }
        // Arbitration precedes preparation: a losing proposal must never publish
        // an abort for the winning proposal's operation identity.
        await authority.BindAsync(proposal, token);
        var plan = await authority.ReadPlanAsync(proposal.Operation, token) ?? throw new InvalidDataException("Bound plan disappeared.");
        return await ExecuteSelectedAsync(plan, owners, token);
    }

    // Used only after authority selection has returned the complete winner. Every
    // receiver independently validates that selection before durable preparation.
    internal async ValueTask<PartitionTransactionResult> ExecuteSelectedAsync(PartitionTransactionPlan plan,
        IReadOnlyList<IPartitionTransactionParticipant> owners, CancellationToken token = default)
    {
        if (owners.Count != plan.Members.Count || owners.Select(n => n.Domain).Distinct().Count() != owners.Count ||
            owners.Any(n => n.TransactionAuthorityIdentity != authority.Identity || !plan.Members.Any(m => m.Domain == n.Domain)))
        {
            throw new InvalidDataException("The exact selected participant set and authority are required.");
        }

        PartitionTransactionDecision? decision;
        using (DistributedReadTelemetry.Start("partition.coordination.observe-decision"))
        {
            decision = await authority.ReadDecisionAsync(plan.Operation, token);
        }

        Exception? preparationFailure = null;
        if (decision is null)
        {
            EntityForge.Db.Durability.DurableTimestampState? commitTime = null;
            try
            {
                using var preparing = DistributedReadTelemetry.Start("partition.coordination.prepare");
                var preparingOwners = authority is PartitionNativeHomeAuthority
                    ? owners.Where(owner => owner.Domain.Partition != PartitionCoordinatorHome.Owner(plan.Operation,
                        ((PartitionNativeHomeAuthority)authority).Routing)).ToArray() : owners;
                var votes = await PartitionParticipantWork.RunAsync(preparingOwners,
                    owner => owner.PrepareWithTimestampAsync(plan, token));
                if (authority is PartitionNativeHomeAuthority nativeHome)
                {
                    votes = votes.Append(await nativeHome.ReadTimestampVoteAsync(plan.Operation, token)).ToArray();
                }

                if (votes.Any(vote => vote is not null))
                {
                    if (votes.Any(vote => vote is null))
                    {
                        throw new InvalidDataException("Timestamp participation must include every owner.");
                    }

                    var maximum = votes[0]!;
                    foreach (var vote in votes)
                    {
                        if (vote!.CompareTo(maximum) > 0)
                        {
                            maximum = vote;
                        }
                    }

                    if (!maximum.Mvcc)
                    {
                        throw new InvalidDataException("Timestamp visibility vote is required.");
                    }

                    commitTime = new(maximum.Domain, maximum.Physical, checked(maximum.Logical + 1), 0, true);
                }
            }
            catch (Exception failure)
            {
                preparationFailure = failure;
            }
            // Once preparation may be durable, caller cancellation cannot discard
            // the obligation. The first authoritative outcome wins; unknown write
            // outcomes remain prepared and are resolved by an exact later retry.
            using (DistributedReadTelemetry.Start("partition.coordination.decide"))
            {
                decision = preparationFailure is null && commitTime is not null
                    ? await authority.DecideTimestampedAsync(plan.Operation, plan.Descriptor, commitTime, CancellationToken.None)
                    : await authority.DecideAsync(plan.Operation, plan.Descriptor, preparationFailure is null, CancellationToken.None);
            }
        }
        var receipts = new List<PartitionCommitResult>();
        try
        {
            using var applying = DistributedReadTelemetry.Start("partition.coordination.apply");
            var results = await PartitionParticipantWork.RunAsync(owners, async owner =>
            {
                var receipt = await owner.ApplyPartitionDecisionAsync(plan.Operation, CancellationToken.None);
                return receipt is null ? null : new PartitionCommitResult(receipt, new(owner.Domain, receipt.Version));
            });
            receipts.AddRange(results.OfType<PartitionCommitResult>());
        }
        catch (Exception failure)
        {
            throw new PartitionDecisionDeliveryException(decision, failure);
        }
        if (decision.Commit && receipts.Count != owners.Count)
        {
            throw new InvalidDataException("Committed delivery omitted a native result.");
        }

        return new(decision, receipts.AsReadOnly(), preparationFailure);
    }
}
