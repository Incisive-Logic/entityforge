using EntityForge.Db.Mapping;
using System.Text.Json;
using Etcdserverpb;
using Google.Protobuf;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Distributed.Hosting;

// Charges all selections, including abandoned ones, until their enforced retry
// horizon is reclaimed. Limits describe retained application bodies, not etcd's
// physical MVCC history; operator compaction remains a separate responsibility.
// The format-7 etcd authority charges uncompressed binary records, including
// their integrity frame; reclamation subtracts those same bytes. Limits are not
// based on the smaller compressed representation.
internal sealed record PartitionApplicationUsage(long Count, long Bytes, long PlanCount = 0, long PlanBytes = 0)
{
    internal const long MaximumCount = 16_384;
    internal const long MaximumBytes = 64 * 1024 * 1024;

    internal PartitionApplicationUsage Add(long count, long bytes, long planCount = 0, long planBytes = 0)
    {
        var result = new PartitionApplicationUsage(checked(Count + count), checked(Bytes + bytes),
            checked(PlanCount + planCount), checked(PlanBytes + planBytes));
        if (result.Count < 0 || result.Bytes < 0 || (result.Count == 0) != (result.Bytes == 0) ||
            result.PlanCount < 0 || result.PlanBytes < 0 || (result.PlanCount == 0) != (result.PlanBytes == 0))
        {
            throw new InvalidDataException("Application capacity accounting is inconsistent.");
        }

        return result;
    }

    internal void RequireAvailable()
    {
        if (Count > MaximumCount || Bytes > MaximumBytes || PlanCount > MaximumCount || PlanBytes > MaximumBytes)
        {
            throw new PartitionApplicationCapacityException();
        }
    }
}

/// <summary>The retained exact-request budget is full. Advance the retry horizon through coordinated retention before admitting new identities.</summary>
public sealed class PartitionApplicationCapacityException() : InvalidOperationException("Retained application selection capacity is exhausted.");

internal sealed partial class GrpcEtcdPartitionTransactionAuthority
{
    // One local writer owns the shared capacity revision at a time. Other authority
    // instances still arbitrate through the unchanged etcd comparisons.
    private readonly SemaphoreSlim _capacityBindingGate = new(1, 1);

    private string UsageKey => _prefix + "application-usage";

    private sealed record UsageState(PartitionApplicationUsage Value, long Revision);

    private static UsageState DecodeUsage(RangeResponse response)
    {
        if (response.Kvs.Count == 0)
        {
            throw new InvalidDataException("The authority has lost its application capacity record.");
        }

        if (response.Kvs.Count != 1 || response.Kvs[0].Lease != 0 || response.Kvs[0].Value.Length > 256)
        {
            throw new InvalidDataException("Invalid application capacity record.");
        }

        var value = JsonSerializer.Deserialize<PartitionApplicationUsage>(response.Kvs[0].Value.Span)
            ?? throw new InvalidDataException("Missing application capacity record.");
        _ = value.Add(0, 0);
        value.RequireAvailable();
        return new(value, response.Kvs[0].ModRevision);
    }

    private Compare UsageComparison(UsageState usage) => usage.Revision == 0 ? Version(UsageKey, 0) : Revision(UsageKey, usage.Revision);

    private async ValueTask BindPlanWithinCapacityAsync(PartitionTransactionPlan plan, byte[] bytes, CancellationToken token)
    {
        await DistributedReadTelemetry.WaitAsync(_capacityBindingGate, "partition.plan-binding.queue", token).ConfigureAwait(false);
        try
        {
            using var measured = DistributedReadTelemetry.Measure("partition.plan-binding.service");
            await BindPlanWithinCapacityCoreAsync(plan, bytes, token).ConfigureAwait(false);
        }
        finally
        {
            _capacityBindingGate.Release();
        }
    }

    private async ValueTask BindWithinCapacityCoreAsync(PartitionApplicationBinding binding, byte[] bytes, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(RequestTimeout);
        while (true)
        {
            var read = Guarded();
            read.Success.Add(Get(_prefix + "floor"));
            read.Success.Add(Get(UsageKey));
            read.Success.Add(Get(ApplicationKey(binding.Operation)));
            var observed = await ExecuteAsync(read, deadline.Token);
            if (!observed.Succeeded)
            {
                throw new InvalidDataException("The application authority domain was replaced.");
            }

            var floor = DecodeFloor(observed.Responses[0].ResponseRange);
            EntityOperationIds.RequireRetained(binding.Operation, floor.Value);
            var previous = observed.Responses[2].ResponseRange.Kvs;
            if (previous.Count != 0)
            {
                if (previous.Count != 1 || previous[0].Lease != 0 || previous[0].Value.Length > MaximumEncodedPlanBytes)
                {
                    throw new InvalidDataException("Invalid retained application selection.");
                }

                var selected = PartitionSelectionCodec.DecodeApplication(PartitionSelectionStorage.Decode(previous[0].Value.Span));
                if (selected.Descriptor != binding.Descriptor)
                {
                    throw new EntityForgeDbIdempotencyException(binding.Operation);
                }

                return; // exact retry remains admissible even at capacity
            }
            var usage = DecodeUsage(observed.Responses[1].ResponseRange);
            var proposed = usage.Value.Add(1, bytes.Length);
            proposed.RequireAvailable();
            var write = Guarded();
            write.Compare.Add(FloorComparison(floor));
            write.Compare.Add(UsageComparison(usage));
            write.Compare.Add(Version(ApplicationKey(binding.Operation), 0));
            write.Success.Add(PutSelection(ApplicationKey(binding.Operation), bytes));
            write.Success.Add(Put(UsageKey, JsonSerializer.SerializeToUtf8Bytes(proposed)));
            // A lost CAS reply remains uncertain. Exact resubmission reads the
            // selected body; it neither reserves again nor chooses another plan.
            if ((await ExecuteAsync(write, deadline.Token)).Succeeded)
            {
                return;
            }

            DistributedReadTelemetry.Work("partition.binding.cas-retry", 1);
        }
    }

    private async ValueTask BindPlanWithinCapacityCoreAsync(PartitionTransactionPlan plan, byte[] bytes, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(RequestTimeout);
        while (true)
        {
            var read = Guarded();
            read.Success.Add(Get(_prefix + "floor"));
            read.Success.Add(Get(UsageKey));
            read.Success.Add(Get(PlanKey(plan.Operation)));
            read.Success.Add(Get(DecisionKey(plan.Operation)));
            var observed = await ExecuteAsync(read, deadline.Token);
            if (!observed.Succeeded)
            {
                throw new InvalidDataException("The authority domain was replaced.");
            }

            var floor = DecodeFloor(observed.Responses[0].ResponseRange);
            EntityOperationIds.RequireRetained(plan.Operation, floor.Value);
            var prior = observed.Responses[2].ResponseRange.Kvs;
            if (prior.Count != 0)
            {
                if (prior.Count != 1 || prior[0].Lease != 0 || prior[0].Value.Length > MaximumEncodedPlanBytes)
                {
                    throw new InvalidDataException("Invalid selected plan.");
                }

                byte[] retainedPlan = PartitionSelectionStorage.Decode(prior[0].Value.Span);
                var application = PartitionSelectionCodec.IsReferencedPlan(retainedPlan)
                    ? await ReadApplicationAsync(plan.Operation, deadline.Token) : null;
                var selected = PartitionSelectionCodec.DecodePlan(retainedPlan, application);
                if (selected.Descriptor != plan.Descriptor)
                {
                    throw new InvalidDataException("Another plan owns this partition operation.");
                }

                return;
            }
            if (observed.Responses[3].ResponseRange.Kvs.Count != 0)
            {
                throw new EntityOperationAbortedException(plan.Operation);
            }

            var usage = DecodeUsage(observed.Responses[1].ResponseRange);
            var proposed = usage.Value.Add(0, 0, 1, bytes.Length);
            proposed.RequireAvailable();
            var write = Guarded();
            write.Compare.Add(FloorComparison(floor));
            write.Compare.Add(UsageComparison(usage));
            write.Compare.Add(Version(PlanKey(plan.Operation), 0));
            write.Compare.Add(Version(DecisionKey(plan.Operation), 0));
            write.Success.Add(PutSelection(PlanKey(plan.Operation), bytes));
            write.Success.Add(Put(UsageKey, JsonSerializer.SerializeToUtf8Bytes(proposed)));
            if ((await ExecuteAsync(write, deadline.Token)).Succeeded)
            {
                return;
            }

            DistributedReadTelemetry.Work("partition.plan-binding.cas-retry", 1);
        }
    }

    private async ValueTask ReclaimBodiesAsync(FloorState floor, bool plans, CancellationToken token)
    {
        const int pageSize = 16;
        string prefix = _prefix + (plans ? "plans/" : "applications/");
        var next = ByteString.CopyFromUtf8(prefix);
        var end = ByteString.CopyFromUtf8(prefix[..^1] + "0");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(RequestTimeout);
        while (true)
        {
            var scan = Guarded();
            scan.Compare.Add(FloorComparison(floor));
            scan.Success.Add(new RequestOp { RequestRange = new RangeRequest { Key = next, RangeEnd = end, Limit = pageSize } });
            scan.Success.Add(Get(UsageKey));
            var response = await ExecuteAsync(scan, deadline.Token);
            if (!response.Succeeded)
            {
                throw new InvalidOperationException("Retention epoch changed during application reclamation.");
            }

            var range = response.Responses[0].ResponseRange;
            if (range.Kvs.Count == 0)
            {
                return;
            }

            var usage = DecodeUsage(response.Responses[1].ResponseRange);
            var write = Guarded();
            write.Compare.Add(FloorComparison(floor));
            write.Compare.Add(UsageComparison(usage));
            long count = 0, bytes = 0;
            foreach (var value in range.Kvs)
            {
                if (!Guid.TryParseExact(value.Key.ToStringUtf8()[prefix.Length..], "N", out var id) || value.Lease != 0 || value.Value.Length > MaximumEncodedPlanBytes)
                {
                    throw new InvalidDataException("Invalid application reclamation record.");
                }

                if (!EntityOperationIds.IsBefore(id, floor.Value))
                {
                    continue;
                }

                write.Compare.Add(Revision(value.Key.ToStringUtf8(), value.ModRevision));
                write.Success.Add(new RequestOp { RequestDeleteRange = new DeleteRangeRequest { Key = value.Key } });
                count++;
                bytes += PartitionSelectionStorage.Decode(value.Value.Span).Length;
            }
            if (count != 0)
            {
                var reduced = plans ? usage.Value.Add(0, 0, -count, -bytes) : usage.Value.Add(-count, -bytes);
                write.Success.Add(Put(UsageKey, JsonSerializer.SerializeToUtf8Bytes(reduced)));
                if (!(await ExecuteAsync(write, deadline.Token)).Succeeded)
                {
                    continue; // reread this page and its exact charge
                }
            }
            next = ByteString.CopyFrom(range.Kvs[^1].Key.ToByteArray().Concat(new byte[] { 0 }).ToArray());
            if (!range.More)
            {
                return;
            }
        }
    }
}
