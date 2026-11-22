using EntityForge.Db.Distributed.Sharding;
using Google.Protobuf;
using System.Text.Json;

namespace EntityForge.Db.Distributed.Hosting;

internal sealed partial class GrpcEtcdPartitionTransactionAuthority
{
    private const int MaximumBindingBatch = 8;
    private const int MaximumQueuedBindings = 256;
    private const int MaximumQueuedBindingBytes = 64 * 1024 * 1024;
    private const int MaximumBindingBatchBodyBytes = MaximumEncodedPlanBytes / 2;
    private const int MaximumBindingBatchAttempts = 4;
    private readonly object _bindingQueueSync = new();
    private readonly Queue<QueuedApplicationBinding> _bindingQueue = new();
    private long _queuedBindingBytes;
    private bool _bindingWorker;
    private int _activeBindings;

    private sealed record QueuedApplicationBinding(PartitionApplicationBinding Binding, byte[] Bytes, CancellationToken Token,
        PartitionTransactionPlan? Plan = null, byte[]? PlanBytes = null)
    {
        internal int BodyBytes => Bytes.Length + (PlanBytes?.Length ?? 0);

        internal PartitionTransactionPlan? SelectedPlan;
        internal readonly TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly DistributedReadTelemetry.Measurement Waiting = DistributedReadTelemetry.Measure("partition.binding.queue");
    }

    private async ValueTask BindWithinCapacityAsync(PartitionApplicationBinding binding, byte[] bytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var item = new QueuedApplicationBinding(binding, bytes, token);
        await EnqueueBindingAsync(item);
    }

    private async ValueTask EnqueueBindingAsync(QueuedApplicationBinding item)
    {
        var token = item.Token;
        token.ThrowIfCancellationRequested();
        bool start;
        lock (_bindingQueueSync)
        {
            if (_bindingQueue.Count >= MaximumQueuedBindings || item.BodyBytes > MaximumQueuedBindingBytes - _queuedBindingBytes)
            {
                item.Waiting.Dispose();
                throw new InvalidOperationException("Application binding waiting capacity is exhausted.");
            }
            _bindingQueue.Enqueue(item);
            _queuedBindingBytes += item.BodyBytes;
            DistributedReadTelemetry.Queue("binding", _bindingQueue.Count, _activeBindings);
            start = !_bindingWorker;
            _bindingWorker = true;
        }
        if (start)
        {
            _ = DrainApplicationBindingsAsync();
        }
        // Cancellation of the wait does not erase selected work or authorize a new
        // request. The worker skips cancelled queued jobs, but owns selected jobs.
        await item.Completion.Task.WaitAsync(token).ConfigureAwait(false);
    }

    private async Task DrainApplicationBindingsAsync()
    {
        while (true)
        {
            var batch = new List<QueuedApplicationBinding>(MaximumBindingBatch);
            var operations = new HashSet<Guid>();
            int bytes = 0;
            lock (_bindingQueueSync)
            {
                while (batch.Count < MaximumBindingBatch && _bindingQueue.TryPeek(out var item))
                {
                    if (!item.Token.IsCancellationRequested && batch.Count != 0 &&
                        (bytes + item.BodyBytes > MaximumBindingBatchBodyBytes || operations.Contains(item.Binding.Operation)))
                    {
                        break;
                    }

                    _bindingQueue.Dequeue();
                    _queuedBindingBytes -= item.BodyBytes;
                    if (item.Token.IsCancellationRequested)
                    {
                        item.Waiting.Dispose();
                        item.Completion.TrySetCanceled(item.Token);
                    }
                    else
                    {
                        batch.Add(item);
                        bytes += item.BodyBytes;
                        operations.Add(item.Binding.Operation);
                    }
                }
                _activeBindings = batch.Count;
                DistributedReadTelemetry.Queue("binding", _bindingQueue.Count, _activeBindings);
                if (batch.Count == 0 && _bindingQueue.Count == 0)
                {
                    _bindingWorker = false;
                    return;
                }
            }
            if (batch.Count == 0)
            {
                continue;
            }
            // Share the same local capacity gate with native-plan binding. Remote
            // authorities and retention still arbitrate through the exact CAS.
            using (DistributedReadTelemetry.Measure("partition.binding.capacity-wait"))
            {
                await _capacityBindingGate.WaitAsync().ConfigureAwait(false);
            }

            foreach (var item in batch)
            {
                item.Waiting.Dispose();
            }

            try
            {
                using var activity = DistributedReadTelemetry.Start("partition.binding.cohort");
                activity?.SetTag("entityforge.operations", batch.Select(item => item.Binding.Operation.ToString("N")).ToArray());
                using var measured = DistributedReadTelemetry.Measure("partition.binding.service");
                if ((batch.Count > 1 || batch[0].Plan is not null) && await TryBindApplicationBatchAsync(batch).ConfigureAwait(false))
                {
                    foreach (var item in batch)
                    {
                        item.SelectedPlan = item.Plan;
                        item.Completion.TrySetResult();
                    }
                    continue;
                }
                // Only pre-submission ineligibility or definite CAS rejection can
                // reach this fallback. An unknown write outcome fails the cohort.
                foreach (var item in batch)
                {
                    try
                    {
                        if (item.Plan is not null)
                        {
                            item.SelectedPlan = await BindCombinedCoreAsync(item).ConfigureAwait(false);
                        }
                        else
                        {
                            await BindWithinCapacityCoreAsync(item.Binding, item.Bytes, CancellationToken.None).ConfigureAwait(false);
                        }

                        item.Completion.TrySetResult();
                    }
                    catch (Exception failure)
                    {
                        item.Completion.TrySetException(failure);
                    }
                }
            }
            catch (Exception failure)
            {
                foreach (var item in batch)
                {
                    item.Completion.TrySetException(failure);
                }
            }
            finally
            {
                _capacityBindingGate.Release();
                lock (_bindingQueueSync)
                {
                    _activeBindings = 0;
                    DistributedReadTelemetry.Queue("binding", _bindingQueue.Count, 0);
                }
            }
        }
    }

    private async ValueTask<bool> TryBindApplicationBatchAsync(IReadOnlyList<QueuedApplicationBinding> batch)
    {
        using var deadline = new CancellationTokenSource(RequestTimeout);
        for (int attempt = 0; attempt < MaximumBindingBatchAttempts; attempt++)
        {
            var read = Guarded();
            read.Success.Add(Get(_prefix + "floor"));
            read.Success.Add(Get(UsageKey));
            foreach (var item in batch)
            {
                // Existence is enough to choose the scalar exact-retry path. Do
                // not pull up to eight unrelated large retained bodies here.
                var lookup = Get(ApplicationKey(item.Binding.Operation));
                lookup.RequestRange.KeysOnly = true;
                read.Success.Add(lookup);
            }
            if (read.CalculateSize() > MaximumEncodedPlanBytes)
            {
                return false;
            }

            var observed = await ExecuteAsync(read, deadline.Token).ConfigureAwait(false);
            if (!observed.Succeeded)
            {
                throw new InvalidDataException("The application authority domain was replaced.");
            }

            var floor = DecodeFloor(observed.Responses[0].ResponseRange);
            // Isolate exact retries, conflicts and expired members in the ordinary
            // path, without making one invalid member reject unrelated requests.
            for (int i = 0; i < batch.Count; i++)
            {
                if (EntityOperationIds.IsBefore(batch[i].Binding.Operation, floor.Value) ||
                    observed.Responses[i + 2].ResponseRange.Kvs.Count != 0)
                {
                    return false;
                }
            }

            foreach (var item in batch)
            {
                EntityOperationIds.RequireRetained(item.Binding.Operation, floor.Value);
            }

            var usage = DecodeUsage(observed.Responses[1].ResponseRange);
            var proposed = usage.Value.Add(batch.Count, batch.Sum(item => (long)item.Bytes.Length),
                batch.Count(item => item.Plan is not null), batch.Sum(item => (long)(item.PlanBytes?.Length ?? 0)));
            try
            {
                proposed.RequireAvailable();
            }
            catch (PartitionApplicationCapacityException)
            {
                return false;
            }
            var write = Guarded();
            write.Compare.Add(FloorComparison(floor));
            write.Compare.Add(UsageComparison(usage));
            foreach (var item in batch)
            {
                write.Compare.Add(Version(ApplicationKey(item.Binding.Operation), 0));
                write.Success.Add(PutSelection(ApplicationKey(item.Binding.Operation), item.Bytes));
                if (item.Plan is not null)
                {
                    write.Compare.Add(Version(PlanKey(item.Binding.Operation), 0));
                    write.Compare.Add(Version(DecisionKey(item.Binding.Operation), 0));
                    write.Success.Add(PutSelection(PlanKey(item.Binding.Operation), item.PlanBytes!));
                }
            }
            write.Success.Add(Put(UsageKey, JsonSerializer.SerializeToUtf8Bytes(proposed)));
            if (write.CalculateSize() > MaximumEncodedPlanBytes)
            {
                return false;
            }
            // A lost response is propagated. Never retry uncertain submission as
            // a scalar job; exact caller retry observes the original owned bytes.
            if ((await ExecuteAsync(write, deadline.Token).ConfigureAwait(false)).Succeeded)
            {
                DistributedReadTelemetry.Work("partition.binding.cohort", batch.Count);
                return true;
            }
            DistributedReadTelemetry.Work("partition.binding.cas-retry", 1);
        }
        return false;
    }
}
