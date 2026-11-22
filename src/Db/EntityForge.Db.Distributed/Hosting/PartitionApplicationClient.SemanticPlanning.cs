using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Distributed.Hosting;

internal sealed partial class PartitionApplicationClient
{
    private const int MaximumQueuedSemanticPlans = 64;
    private const long MaximumQueuedSemanticBytes = 8L * PartitionApplicationBinding.MaximumRequestBytes;
    private readonly object _semanticPlanningSync = new();
    private readonly Queue<SemanticPlanningJob> _semanticPlanningQueue = new();
    private long _semanticPlanningBytes;
    private bool _semanticPlanningWorker;
    private readonly PartitionSemanticMappingAdmission _semanticMappings = new();

    private sealed record SemanticPlanningJob(PartitionIndependentWrite Write, CancellationToken Token,
        PartitionSemanticPlanning.InterpretationScope? Interpretation)
    {
        internal long Bytes => Write.Requests.Sum(request => (long)request.Length);

        internal TaskCompletionSource<PartitionApplicationBinding> Completion
        {
            get;
        }
        =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private async ValueTask<PartitionApplicationBinding> SelectSemanticQueuedAsync(Guid operation,
        IReadOnlyList<ReadOnlyMemory<byte>> requests, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        PartitionSemanticRequests.Validate(Generation, operation, requests);
        // Capture again at the queue boundary; callers and later fallback cannot
        // mutate buffers owned by another pending planning job.
        var job = new SemanticPlanningJob(new(operation, requests.Select(r => (ReadOnlyMemory<byte>)r.ToArray()).ToArray()), token,
            PartitionSemanticPlanning.InterpretationScope.Current);
        bool start;
        lock (_semanticPlanningSync)
        {
            if (_semanticPlanningQueue.Count >= MaximumQueuedSemanticPlans || job.Bytes > MaximumQueuedSemanticBytes - _semanticPlanningBytes)
            {
                throw new InvalidOperationException("Semantic planning waiting capacity is exhausted.");
            }

            _semanticPlanningQueue.Enqueue(job);
            _semanticPlanningBytes += job.Bytes;
            start = !_semanticPlanningWorker;
            _semanticPlanningWorker = true;
        }
        if (start)
        {
            _ = DrainSemanticPlanningAsync();
        }
        // Cancellation does not cancel other captures sharing this read cut.
        // The worker owns and drains started reads; no durable selection occurs here.
        return await job.Completion.Task.WaitAsync(token).ConfigureAwait(false);
    }

    private async Task DrainSemanticPlanningAsync()
    {
        while (true)
        {
            var jobs = new List<SemanticPlanningJob>();
            long bytes = 0;
            var operations = new HashSet<Guid>();
            lock (_semanticPlanningSync)
            {
                while (jobs.Count < PartitionSemanticPlanning.MaximumPlanningCohort && _semanticPlanningQueue.TryPeek(out var job))
                {
                    if (!job.Token.IsCancellationRequested && jobs.Count != 0 &&
                        (bytes + job.Bytes > PartitionApplicationBinding.MaximumRequestBytes || operations.Contains(job.Write.Operation)))
                    {
                        break;
                    }

                    _semanticPlanningQueue.Dequeue();
                    _semanticPlanningBytes -= job.Bytes;
                    if (job.Token.IsCancellationRequested)
                    {
                        job.Completion.TrySetCanceled(job.Token);
                    }
                    else
                    {
                        jobs.Add(job);
                        bytes += job.Bytes;
                        operations.Add(job.Write.Operation);
                    }
                }
                if (jobs.Count == 0 && _semanticPlanningQueue.Count == 0)
                {
                    _semanticPlanningWorker = false;
                    return;
                }
            }
            if (jobs.Count == 0)
            {
                continue;
            }

            using var deadline = new CancellationTokenSource(PartitionReadCuts.MaximumDuration);
            try
            {
                // Activate the submitting invocation explicitly: a queue worker may
                // have inherited another job's execution context. Cohorts own a fresh scope.
                using var interpretation = jobs.Count == 1 && jobs[0].Interpretation is { } submitted
                    ? submitted.Activate() : new PartitionSemanticPlanning.InterpretationScope();
                using var measured = DistributedReadTelemetry.Measure("partition.semantic-planning.cohort");
                var owners = _owners.Values.ToArray();
                var results = jobs.Count == 1
                    ? new[]
                    {
                        new PartitionSemanticPlanning.PlanningResult(await PartitionSemanticPlanning.SelectOwnerOrDistributedAsync(
                        Generation, jobs[0].Write.Operation, _routing, owners, jobs[0].Write.Requests, deadline.Token, _semanticMappings), null)
                    }
                    : await PartitionSemanticPlanning.SelectCohortAsync(Generation, _routing, owners,
                        jobs.Select(j => j.Write).ToArray(), deadline.Token, _semanticMappings, preferOwner: true);
                DistributedReadTelemetry.Work("partition.semantic-planning.operations", jobs.Count);
                for (int index = 0; index < jobs.Count; index++)
                {
                    var job = jobs[index];
                    var result = results[index];
                    if (job.Token.IsCancellationRequested)
                    {
                        job.Completion.TrySetCanceled(job.Token);
                    }
                    else if (result.Failure is not null)
                    {
                        job.Completion.TrySetException(result.Failure);
                    }
                    else
                    {
                        job.Completion.TrySetResult(result.Binding!);
                    }
                }
            }
            catch (Exception failure)
            {
                foreach (var job in jobs)
                {
                    job.Completion.TrySetException(failure);
                }
            }
        }
    }
}
