using EntityForge.Db.Mapping;
using EntityForge.Db.Distributed.Sharding;
using System.Text.Json;

namespace EntityForge.Db.Distributed.Hosting;

// Optional authority capability. Null means bounded pre-submission ineligibility,
// never an uncertain write. Legacy/file compositions keep their existing protocol.
internal interface IPartitionCombinedSelectionAuthority
{
    bool SupportsSemanticEvidence => false;

    ValueTask<PartitionTransactionPlan?> SelectCombinedAsync(PartitionApplicationBinding application,
        PartitionTransactionPlan proposal, CancellationToken token = default);
}

internal sealed partial class GrpcEtcdPartitionTransactionAuthority : IPartitionCombinedSelectionAuthority
{
    public bool SupportsSemanticEvidence => true;

    public async ValueTask<PartitionTransactionPlan?> SelectCombinedAsync(PartitionApplicationBinding application,
        PartitionTransactionPlan proposal, CancellationToken token = default)
    {
        RequireApplicationDomain(application);
        RequireDomain(proposal);
        application.RequireCoordinatedPlan(proposal);
        if (application.NativeDescriptor is not null)
        {
            throw new NotSupportedException("Combined selection requires a mapped proposal.");
        }

        if (!application.PlanningEvidence.IsEmpty)
        {
            if (_domain.Profile != DatabaseProfileId.Semantic)
            {
                throw new NotSupportedException("Combined planning evidence requires Semantic closure.");
            }

            if (PartitionSemanticGovernance.Is(application) || PartitionQueryUpdate.Is(application))
            {
                throw new NotSupportedException("Combined selection requires mapped Semantic source evidence.");
            }
        }
        else if (_domain.Profile == DatabaseProfileId.Semantic)
        {
            throw new InvalidDataException("Semantic selection requires complete planning evidence.");
        }

        byte[] applicationBytes, planBytes;
        using (DistributedReadTelemetry.Measure("partition.selection.encode"))
        {
            applicationBytes = PartitionSelectionCodec.EncodeApplication(application);
            planBytes = PartitionSelectionCodec.EncodeSelectedPlan(proposal, application);
        }
        if ((long)applicationBytes.Length + planBytes.Length > MaximumBindingBatchBodyBytes)
        {
            return null;
        }

        var item = new QueuedApplicationBinding(application, applicationBytes, token, proposal, planBytes);
        // This includes queueing, authority reads/CAS and response processing, not
        // just network time. Keep it disjoint from local selection encoding.
        using (DistributedReadTelemetry.Measure("partition.selection.authority-wait"))
        {
            await EnqueueBindingAsync(item);
        }

        return item.SelectedPlan ?? throw new InvalidDataException("Combined selection omitted its native plan.");
    }

    private async ValueTask<PartitionTransactionPlan> BindCombinedCoreAsync(QueuedApplicationBinding item)
    {
        using var deadline = new CancellationTokenSource(RequestTimeout);
        while (true)
        {
            var read = Guarded();
            read.Success.Add(Get(_prefix + "floor"));
            read.Success.Add(Get(UsageKey));
            read.Success.Add(Get(ApplicationKey(item.Binding.Operation)));
            read.Success.Add(Get(PlanKey(item.Binding.Operation)));
            read.Success.Add(Get(DecisionKey(item.Binding.Operation)));
            var observed = await ExecuteAsync(read, deadline.Token);
            if (!observed.Succeeded)
            {
                throw new InvalidDataException("The application authority domain was replaced.");
            }

            var floor = DecodeFloor(observed.Responses[0].ResponseRange);
            EntityOperationIds.RequireRetained(item.Binding.Operation, floor.Value);
            var applications = observed.Responses[2].ResponseRange.Kvs;
            var plans = observed.Responses[3].ResponseRange.Kvs;
            if (applications.Count != 0)
            {
                if (applications.Count != 1 || applications[0].Lease != 0 || applications[0].Value.Length > MaximumEncodedPlanBytes)
                {
                    throw new InvalidDataException("Invalid retained application selection.");
                }

                var selected = PartitionSelectionCodec.DecodeApplication(PartitionSelectionStorage.Decode(applications[0].Value.Span));
                RequireApplicationDomain(selected);
                if (selected.Descriptor != item.Binding.Descriptor)
                {
                    throw new EntityForgeDbIdempotencyException(item.Binding.Operation);
                }

                if (plans.Count != 0)
                {
                    if (plans.Count != 1 || plans[0].Lease != 0 || plans[0].Value.Length > MaximumEncodedPlanBytes)
                    {
                        throw new InvalidDataException("Invalid retained native selection.");
                    }

                    var winner = PartitionSelectionCodec.DecodePlan(PartitionSelectionStorage.Decode(plans[0].Value.Span), selected);
                    RequireDomain(winner);
                    selected.RequireCoordinatedPlan(winner);
                    return winner; // One whole winning plan, even if this retry observed newer state.
                }
            }
            else if (plans.Count != 0)
            {
                throw new InvalidDataException("Native selection has no application owner.");
            }

            if (observed.Responses[4].ResponseRange.Kvs.Count != 0)
            {
                throw new EntityOperationAbortedException(item.Binding.Operation);
            }

            var usage = DecodeUsage(observed.Responses[1].ResponseRange);
            bool fresh = applications.Count == 0;
            var proposed = usage.Value.Add(fresh ? 1 : 0, fresh ? item.Bytes.Length : 0, 1, item.PlanBytes!.Length);
            proposed.RequireAvailable();
            var write = Guarded();
            write.Compare.Add(FloorComparison(floor));
            write.Compare.Add(UsageComparison(usage));
            write.Compare.Add(fresh ? Version(ApplicationKey(item.Binding.Operation), 0) : Revision(ApplicationKey(item.Binding.Operation), applications[0].ModRevision));
            write.Compare.Add(Version(PlanKey(item.Binding.Operation), 0));
            write.Compare.Add(Version(DecisionKey(item.Binding.Operation), 0));
            if (fresh)
            {
                write.Success.Add(PutSelection(ApplicationKey(item.Binding.Operation), item.Bytes));
            }

            write.Success.Add(PutSelection(PlanKey(item.Binding.Operation), item.PlanBytes));
            write.Success.Add(Put(UsageKey, JsonSerializer.SerializeToUtf8Bytes(proposed)));
            // Atomic selection is the first durable acceptance of a fresh proposal.
            // Unknown CAS results propagate; only a definite rejection is retried.
            if ((await ExecuteAsync(write, deadline.Token)).Succeeded)
            {
                return item.Plan!;
            }

            DistributedReadTelemetry.Work("partition.combined-selection.cas-retry", 1);
        }
    }
}
