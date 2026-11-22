using EntityForge.Db.Durability;
using EntityForge.Db.Mapping;

namespace EntityForge.Db.Distributed.Hosting;

internal sealed partial class PartitionOwnedNode
{
    // Shared by every profile with a receiver-verifiable, one-owner planning cut.
    // The immutable external binding selects this protocol; only this native WAL
    // may then arbitrate commit versus abort. No external decision can compete.
    private const string NativeLocalAuthority = "entityforge.selected-owner.v1:";

    private static bool IsNativeLocal(DurablePartitionTransaction entry) =>
        entry.Coordinator?.Authority.StartsWith(NativeLocalAuthority, StringComparison.Ordinal) == true;

    private void ValidateNativeLocal(DurablePartitionTransaction entry, PartitionApplicationBinding application)
    {
        if (!application.NativeLocal || application.Owners[0] != Domain.Identity ||
            entry.Operation != application.Operation || entry.Coordinator is not { } coordinator ||
            coordinator.Authority != NativeLocalAuthority + TransactionAuthorityIdentity ||
            coordinator.ApplicationDescriptor != application.Descriptor ||
            !coordinator.Manifest.Span.SequenceEqual(PartitionSelectionCodec.EncodeApplication(application)))
        {
            throw new InvalidDataException("Native local selection differs from its authority binding.");
        }

        var plan = new PartitionTransactionPlan(entry.Operation, Routing.MapVersion,
            [new(Domain, entry.ReadVersion, entry.Payload)]);
        if (plan.Descriptor != entry.Descriptor)
        {
            throw new InvalidDataException("Native local member differs from its durable descriptor.");
        }

        if (Domain.Profile == DatabaseProfileId.Semantic && !PartitionSemanticGovernance.Is(application) && !PartitionQueryUpdate.Is(application) &&
            PartitionSemanticPlanning.Plan(application, Routing, [Domain]).Descriptor != plan.Descriptor)
        {
            throw new InvalidDataException("Native local payload differs from frozen Semantic evidence.");
        }

        _ = RequireNativeMember(plan, graphEvidence: true);
    }

    // Caller holds placement admission. A retained selection wins before any
    // reconstruction, including after its original snapshot has been reclaimed.
    private async ValueTask<DurablePartitionTransaction> SelectNativeLocalAsync(
        PartitionApplicationBinding application, CancellationToken token, bool validateCurrent = true)
    {
        if (!application.NativeLocal || application.Owners[0] != Domain.Identity)
        {
            throw new InvalidDataException("Not an evidence-backed local selection.");
        }

        var existing = await Kernel.PartitionTransactions.ReadCoordinatorAsync(application.Operation, token);
        if (existing is not null)
        {
            ValidateNativeLocal(existing, application);
            return existing;
        }
        PartitionTransactionMember member;
        if (!validateCurrent)
        {
            member = await BuildApplicationMemberAsync(application, token, validateEvidence: false);
        }
        else if (PartitionQueryUpdate.Is(application))
        {
            await ValidateQueryUpdateEvidenceAsync(application, token);
            member = await BuildApplicationMemberAsync(application, token);
        }
        else if (Domain.Profile == DatabaseProfileId.Semantic && !PartitionSemanticGovernance.Is(application))
        {
            member = await ValidateSemanticEvidenceAsync(application, token);
        }
        else
        {
            member = await BuildApplicationMemberAsync(application, token);
        }

        var plan = new PartitionTransactionPlan(application.Operation, Routing.MapVersion, [member]);
        _ = RequireNativeMember(plan, graphEvidence: true);
        var coordinator = new DurablePartitionCoordinator(NativeLocalAuthority + TransactionAuthorityIdentity,
            application.Descriptor, PartitionSelectionCodec.EncodeApplication(application));
        var selected = await Kernel.PartitionTransactions.SelectCoordinatorAsync(application.Operation,
            plan.Descriptor, member.ReadVersion, member.Payload, coordinator, token);
        ValidateNativeLocal(selected, application);
        return selected;
    }

    private async ValueTask<PartitionCommitResult> ExecuteNativeLocalAsync(
        PartitionApplicationBinding application, CancellationToken token)
    {
        DatabaseCommitReceipt? receipt = null;
        try
        {
            receipt = await Authority.ExecuteAsync(async () =>
            {
                ObjectDisposedException.ThrowIf(_closing, this);
                var selected = await SelectNativeLocalAsync(application, token);
                if (selected.State == DurablePartitionTransactionState.Aborted)
                {
                    throw new EntityOperationAbortedException(application.Operation);
                }

                if (selected.State == DurablePartitionTransactionState.Committed)
                {
                    return receipt = await Kernel.PartitionTransactions.ResolveAsync(selected.Operation, selected.Descriptor, token)
                        ?? throw new InvalidDataException("Native local commitment lost its replay receipt.");
                }
                // Native selection transfers ownership. Cancellation cannot abandon
                // an in-flight durable outcome or the final readability barrier.
                DurableTimestampState? time = Kernel.PartitionTransactions.TimestampVisibilityEnabled
                    ? await Kernel.PartitionTransactions.ChooseTimestampAsync([selected.Timestamp ??
                        throw new InvalidDataException("Native local selection lost its timestamp vote.")], CancellationToken.None)
                    : null;
                try
                {
                    return receipt = await Kernel.PartitionTransactions.CompleteTimestampedAsync(selected.Operation,
                        selected.Descriptor, true, time, CancellationToken.None)
                        ?? throw new InvalidDataException("Native local commitment returned no receipt.");
                }
                catch (InvalidDataException)
                {
                    var winner = await Kernel.PartitionTransactions.ReadCoordinatorAsync(selected.Operation, CancellationToken.None);
                    if (winner is null)
                    {
                        throw;
                    }

                    ValidateNativeLocal(winner, application);
                    if (winner.State == DurablePartitionTransactionState.Aborted)
                    {
                        throw new EntityOperationAbortedException(application.Operation);
                    }

                    if (winner.State == DurablePartitionTransactionState.Committed)
                    {
                        return receipt = await Kernel.PartitionTransactions.ResolveAsync(selected.Operation, selected.Descriptor, CancellationToken.None)
                            ?? throw new InvalidDataException("Native local winner lost its receipt.");
                    }

                    throw;
                }
            });
            return new(receipt, new(Domain, receipt.Version));
        }
        catch (Exception failure) when (receipt is not null)
        {
            throw new EntityOperationCommittedException<DatabaseCommitReceipt>(receipt, receipt, failure);
        }
    }

    private async ValueTask<PartitionMemberResolution> ResolveNativeLocalAsync(
        PartitionApplicationBinding application, bool abort, CancellationToken token)
    {
        var entry = await Kernel.PartitionTransactions.ReadCoordinatorAsync(application.Operation, token);
        if (entry is null && abort)
        {
            entry = await SelectNativeLocalAsync(application, token, validateCurrent: false);
        }

        if (entry is null)
        {
            return new(EntityOperationState.PlanBound);
        }

        ValidateNativeLocal(entry, application);
        if (abort && entry.State == DurablePartitionTransactionState.Prepared)
        {
            try
            {
                await Kernel.PartitionTransactions.CompleteAsync(entry.Operation, entry.Descriptor, false, token);
            }
            catch (InvalidDataException)
            {
                var winner = await Kernel.PartitionTransactions.ReadCoordinatorAsync(entry.Operation, CancellationToken.None);
                if (winner is null || winner.State == DurablePartitionTransactionState.Prepared)
                {
                    throw;
                }

                ValidateNativeLocal(winner, application);
            }
            entry = await Kernel.PartitionTransactions.ReadCoordinatorAsync(entry.Operation, CancellationToken.None)
                ?? throw new InvalidDataException("Native local outcome disappeared.");
            ValidateNativeLocal(entry, application);
        }
        if (entry.State == DurablePartitionTransactionState.Aborted)
        {
            return new(EntityOperationState.Aborted);
        }

        if (entry.State == DurablePartitionTransactionState.Prepared)
        {
            return new(EntityOperationState.PlanBound);
        }

        var receipt = await Kernel.PartitionTransactions.ResolveAsync(entry.Operation, entry.Descriptor, token);
        return new(receipt is null ? EntityOperationState.CommittedPendingReadability : EntityOperationState.Committed, receipt);
    }
}
