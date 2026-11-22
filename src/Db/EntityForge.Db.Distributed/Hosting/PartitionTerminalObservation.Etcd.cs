using System.Text.Json;
using Grpc.Core;

namespace EntityForge.Db.Distributed.Hosting;

internal sealed partial class GrpcEtcdPartitionTransactionAuthority
{
    public async ValueTask<IReadOnlyList<PartitionTerminalObservation>> ReadTerminalAsync(
        IReadOnlyList<Guid> operations, CancellationToken token = default, IReadOnlyDictionary<Guid, string>? selected = null)
    {
        PartitionTerminalObservation.RequireBatch(operations);
        // Copy before an await: callers cannot change the association with response slots.
        var owned = operations.ToArray();
        var selections = PartitionTerminalObservation.CopySelections(owned, selected);
        var slots = new Dictionary<Guid, int>(owned.Length);
        var request = Guarded();
        request.Success.Add(Get(_prefix + "floor"));
        foreach (var operation in owned)
        {
            slots.Add(operation, request.Success.Count);
            if (!selections.ContainsKey(operation))
            {
                request.Success.Add(Get(ApplicationKey(operation)));
                request.Success.Add(Get(PlanKey(operation)));
            }
            request.Success.Add(Get(DecisionKey(operation)));
        }
        Etcdserverpb.TxnResponse response;
        try
        {
            response = await ExecuteAsync(request, token);
        }
        catch (RpcException failure) when (failure.StatusCode == StatusCode.ResourceExhausted && owned.Length > 1)
        {
            // Large valid bodies can exceed the channel's receive limit. Split only
            // a read, bounded down to singleton; never retry a mutation this way.
            int middle = owned.Length / 2;
            var firstIds = owned[..middle];
            var secondIds = owned[middle..];
            var first = await ReadTerminalAsync(firstIds, token, selections.Where(pair => firstIds.Contains(pair.Key)).ToDictionary());
            var second = await ReadTerminalAsync(secondIds, token, selections.Where(pair => secondIds.Contains(pair.Key)).ToDictionary());
            return first.Concat(second).ToArray();
        }
        if (!response.Succeeded)
        {
            throw new InvalidDataException("The enrolled partition authority domain was replaced.");
        }

        if (response.Responses.Count != request.Success.Count)
        {
            throw new InvalidDataException("Incomplete terminal observation.");
        }

        var floor = DecodeFloor(response.Responses[0].ResponseRange);
        var results = new List<PartitionTerminalObservation>(owned.Length);
        for (int index = 0; index < owned.Length; index++)
        {
            Guid operation = owned[index];
            try
            {
                EntityOperationIds.RequireRetained(operation, floor.Value);
                int slot = slots[operation];
                if (selections.TryGetValue(operation, out var descriptor))
                {
                    var values = response.Responses[slot].ResponseRange.Kvs;
                    PartitionTransactionDecision? terminal = null;
                    if (values.Count != 0)
                    {
                        const int MaximumTerminalBytes = 1024;
                        if (values.Count != 1 || values[0].Lease != 0 || values[0].Value.Length > MaximumTerminalBytes)
                        {
                            throw new InvalidDataException("Invalid partition decision.");
                        }

                        terminal = JsonSerializer.Deserialize<PartitionTransactionDecision>(values[0].Value.Span);
                        if (terminal is null || terminal.Operation != operation || terminal.ApplicationOnly || terminal.Descriptor != descriptor)
                        {
                            throw new InvalidDataException("Decision differs from durable native selection.");
                        }
                    }
                    results.Add(new(operation, null, null, terminal));
                    continue;
                }
                var applications = response.Responses[slot].ResponseRange.Kvs;
                var plans = response.Responses[slot + 1].ResponseRange.Kvs;
                var decisions = response.Responses[slot + 2].ResponseRange.Kvs;
                PartitionApplicationBinding? application = null;
                PartitionTransactionPlan? plan = null;
                PartitionTransactionDecision? decision = null;
                if (applications.Count != 0)
                {
                    if (applications.Count != 1 || applications[0].Lease != 0 || applications[0].Value.Length > MaximumEncodedPlanBytes)
                    {
                        throw new InvalidDataException("Invalid retained application binding.");
                    }

                    application = PartitionSelectionCodec.DecodeApplication(PartitionSelectionStorage.Decode(applications[0].Value.Span));
                    RequireApplicationDomain(application);
                    if (application.Operation != operation)
                    {
                        throw new InvalidDataException("Application identity mismatch.");
                    }
                }
                if (plans.Count != 0)
                {
                    if (plans.Count != 1 || plans[0].Lease != 0 || plans[0].Value.Length > MaximumEncodedPlanBytes)
                    {
                        throw new InvalidDataException("Invalid retained partition plan.");
                    }

                    plan = PartitionSelectionCodec.DecodePlan(PartitionSelectionStorage.Decode(plans[0].Value.Span), application);
                    RequireDomain(plan);
                    if (plan.Operation != operation)
                    {
                        throw new InvalidDataException("Partition plan identity mismatch.");
                    }
                }
                if (decisions.Count != 0)
                {
                    const int MaximumDecisionBytes = 1024;
                    if (decisions.Count != 1 || decisions[0].Lease != 0 || decisions[0].Value.Length > MaximumDecisionBytes)
                    {
                        throw new InvalidDataException("Invalid partition decision.");
                    }

                    decision = JsonSerializer.Deserialize<PartitionTransactionDecision>(decisions[0].Value.Span);
                    var expected = decision is { ApplicationOnly: true } ? application?.Descriptor : plan?.Descriptor;
                    if (decision is null || expected is null || decision.Operation != operation || decision.Descriptor != expected ||
                        (decision.ApplicationOnly && (application!.Local || plan is not null || decision.Commit)))
                    {
                        throw new InvalidDataException("Decision and selected state differ.");
                    }
                }
                results.Add(new(operation, application, plan, decision));
            }
            catch (Exception failure)
            {
                results.Add(new(operation, null, null, null, failure));
            }
        }
        return results;
    }
}
