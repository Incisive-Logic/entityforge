using EntityForge.Db.Mapping;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Distributed.Hosting;

internal sealed partial class PartitionApplicationClient
{
    private async ValueTask<DatabaseDistributedCommitReceipt?> TryDeliverCombinedAsync(Guid operation,
        IReadOnlyList<ReadOnlyMemory<byte>> owned, CancellationToken token)
    {
        var selected = await TrySelectCombinedAsync(operation, owned, token);
        if (selected is null)
        {
            return null;
        }

        var owners = selected.Members.Select(member => _owners[member.Domain.Partition]).ToArray();
        var result = await new PartitionTransactionCoordinator(_authority).ExecuteSelectedAsync(selected, owners, token);
        if (!result.Decision.Commit)
        {
            throw new EntityOperationAbortedException(operation);
        }

        return result.Completion;
    }

    private async ValueTask<PartitionTransactionPlan?> TrySelectCombinedAsync(Guid operation,
        IReadOnlyList<ReadOnlyMemory<byte>> owned, CancellationToken token)
    {
        if (CanExecuteLocalSemantic(owned) || _authority is not IPartitionCombinedSelectionAuthority combined)
        {
            return null;
        }

        if (Profile == DatabaseProfileId.Semantic)
        {
            if (!combined.SupportsSemanticEvidence)
            {
                return null;
            }

            if (await _authority.ReadApplicationAsync(operation, token) is not null)
            {
                return null;
            }

            using var measured = DistributedReadTelemetry.Measure("partition.application.semantic-combined-selection");
            using var interpretation = new PartitionSemanticPlanning.InterpretationScope();
            var semanticApplication = (await SelectSemanticQueuedAsync(operation, owned, token)).SelectNativeLocal();
            if (semanticApplication.Local)
            {
                try
                {
                    await _authority.BindApplicationAsync(semanticApplication, token);
                }
                catch (EntityForgeDbIdempotencyException)
                {
                    // A competing capture may already have selected a different
                    // cut. Reuse only an observed winner, as for coordinated selection.
                    if (await _authority.ReadApplicationAsync(operation, token) is null)
                    {
                        throw;
                    }
                }
                return null;
            }
            // The retained expansion already contains each native proposal. No
            // owner RPC or current-state reconstruction is needed to copy it.
            // Native preparation still validates current dependencies atomically.
            PartitionTransactionPlan proposal;
            using (DistributedReadTelemetry.Measure("partition.semantic-planning.build-selected-plan"))
            {
                proposal = PartitionSemanticPlanning.Plan(semanticApplication, _routing, _owners.Values.Select(o => o.Domain).ToArray());
            }

            try
            {
                var semanticSelected = await combined.SelectCombinedAsync(semanticApplication, proposal, token);
                if (semanticSelected is null)
                {
                    await RetainDeclinedApplicationAsync(semanticApplication, token);
                }

                return semanticSelected;
            }
            catch (EntityForgeDbIdempotencyException)
            {
                // Concurrent exact captures may observe different planning cuts.
                // Only a definite selected winner permits the normal replay path;
                // unknown submission failures must propagate without a retry.
                if (await _authority.ReadApplicationAsync(operation, token) is null)
                {
                    throw;
                }

                return null;
            }
        }
        if (Profile is not (DatabaseProfileId.PropertyGraph or DatabaseProfileId.Rdf))
        {
            return null;
        }

        if (PartitionGraphPlanning.Required(Profile, _routing, owned))
        {
            return null;
        }

        var owners = owned.Select(request => _owners[PartitionApplicationRouting.Owner(Generation, Profile, _routing, operation, request)])
            .DistinctBy(owner => owner.Domain.Partition).OrderBy(owner => owner.Domain.Partition.Value, StringComparer.Ordinal).ToArray();
        if (owners.Length < 2 || owners.Any(owner => !owner.SupportsApplicationProposal))
        {
            return null;
        }
        // An observed selection is reused before speculative planning. Concurrent
        // proposals still arbitrate atomically and execute only the whole winner.
        if (await _authority.ReadApplicationAsync(operation, token) is not null)
        {
            return null;
        }

        var application = new PartitionApplicationBinding(operation, owners.Select(owner => owner.Domain.Identity).ToArray(), owned);
        PartitionTransactionPlan? selected;
        using (DistributedReadTelemetry.Measure("partition.application.combined-selection"))
        {
            var members = await PartitionParticipantWork.RunAsync(owners, owner => owner.ProposeApplicationAsync(application, token));
            selected = await combined.SelectCombinedAsync(application, new(operation, _routing.MapVersion, members), token);
            if (selected is null)
            {
                await RetainDeclinedApplicationAsync(application, token);
            }
        }
        return selected;
    }

    private async ValueTask RetainDeclinedApplicationAsync(PartitionApplicationBinding application, CancellationToken token)
    {
        // Null is a definite pre-submission size/capability decline. The captured
        // application and its complete planning cut are already valid proposals;
        // preserve them for ordinary selection instead of expanding a second cut.
        // The fallback still observes the authoritative winner and validates fresh
        // native state. Unknown write outcomes propagate without another attempt.
        try
        {
            await _authority.BindApplicationAsync(application, token);
        }
        catch (EntityForgeDbIdempotencyException)
        {
            if (await _authority.ReadApplicationAsync(application.Operation, token) is null)
            {
                throw;
            }
        }
    }
}
