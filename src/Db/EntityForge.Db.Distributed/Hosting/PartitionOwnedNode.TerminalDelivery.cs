using System.Runtime.ExceptionServices;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Distributed.Hosting;

internal sealed partial class PartitionOwnedNode
{
    // Every enqueued caller already owns its supervised fence callback, and keeps
    // it until its own fresh completion placement check. Fence sealing therefore
    // drains queued as well as executing work before maintenance or transfer.
    private sealed class TerminalDelivery(PartitionOwnedNode owner) : IAsyncDisposable
    {
        private const int MaximumQueued = 256;
        private readonly object _sync = new();
        private readonly Queue<Job> _pending = new();
        private Task? _worker;
        private bool _closed;
        private int _active;
        private readonly HashSet<Task> _timestampApplications = [];

        private sealed record Job(Guid Operation, CancellationToken Token)
        {
            internal TaskCompletionSource<DatabaseCommitReceipt?> Completion
            {
                get;
            }
            =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            internal DatabaseCommitReceipt? Receipt;
            internal Exception? Failure;
        }

        internal Task<DatabaseCommitReceipt?> Enqueue(Guid operation, CancellationToken token)
        {
            if (operation == Guid.Empty)
            {
                throw new ArgumentException("Missing terminal operation identity.");
            }

            token.ThrowIfCancellationRequested();
            var job = new Job(operation, token);
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_closed, this);
                if (_pending.Count + (owner.SharedTimestamps is null ? 0 : _active) >= MaximumQueued)
                {
                    throw new InvalidOperationException("Terminal delivery capacity is exhausted.");
                }

                _pending.Enqueue(job);
                DistributedReadTelemetry.Queue("terminal", _pending.Count, _active);
                _worker ??= Task.Run(DrainAsync);
            }
            return job.Completion.Task;
        }

        private async Task DrainAsync()
        {
            while (true)
            {
                List<Job> jobs = [];
                lock (_sync)
                {
                    HashSet<Guid> selected = [];
                    while (_pending.TryPeek(out var next) && jobs.Count < PartitionTerminalObservation.MaximumBatch)
                    {
                        if (next.Token.IsCancellationRequested)
                        {
                            _pending.Dequeue();
                            next.Completion.TrySetCanceled(next.Token);
                            continue;
                        }
                        if (!selected.Add(next.Operation))
                        {
                            break;
                        }

                        jobs.Add(_pending.Dequeue());
                    }
                    _active = owner.SharedTimestamps is null ? jobs.Count : _active + jobs.Count;
                    DistributedReadTelemetry.Queue("terminal", _pending.Count, _active);
                    if (jobs.Count == 0)
                    {
                        _worker = null;
                        return;
                    }
                }
                using var activity = DistributedReadTelemetry.Start("partition.terminal.cohort");
                activity?.SetTag("entityforge.operations", jobs.Select(job => job.Operation.ToString("N")).ToArray());
                using var service = DistributedReadTelemetry.Measure("partition.terminal.service");
                DistributedReadTelemetry.Work("partition.terminal.members", jobs.Count);
                try
                {
                    var authority = owner._transactions ?? throw new NotSupportedException("No partition decision authority is enrolled.");
                    var ids = jobs.Select(job => job.Operation).ToArray();
                    IReadOnlyDictionary<Guid, string> selections;
                    using (DistributedReadTelemetry.Measure("partition.terminal.descriptors"))
                    {
                        selections = await owner.Kernel.PartitionTransactions.ReadSelectedDescriptorsAsync(ids);
                    }

                    DistributedReadTelemetry.Work("partition.terminal.compact", selections.Count);
                    IReadOnlyList<PartitionTerminalObservation> observations;
                    using (DistributedReadTelemetry.Measure("partition.terminal.observe"))
                    {
                        observations = await authority.ReadTerminalAsync(ids, CancellationToken.None, selections);
                    }

                    if (observations.Count != jobs.Count || observations.Where((value, index) => value.Operation != jobs[index].Operation).Any())
                    {
                        throw new InvalidDataException("Terminal observation association differs.");
                    }

                    if (owner.SharedTimestamps is not null)
                    {
                        // Timestamp order and HTTP arrival order can differ. Do not keep the
                        // observation lane behind a later timestamp while its predecessor is queued.
                        // These bounded jobs remain owned by disposal and their original fence callbacks.
                        Task? application = null;
                        lock (_sync)
                        {
                            application = Task.Run(async () =>
                            {
                                try
                                {
                                    await Task.WhenAll(jobs.Select((job, index) => ApplyAsync(job, observations[index], selections.GetValueOrDefault(job.Operation))));
                                }
                                catch (Exception failure)
                                {
                                    foreach (var job in jobs)
                                    {
                                        job.Failure ??= failure;
                                    }
                                }
                                finally
                                {
                                    lock (_sync)
                                    {
                                        _active -= jobs.Count;
                                        _timestampApplications.Remove(application!);
                                    }
                                    Complete(jobs);
                                }
                            });
                            _timestampApplications.Add(application);
                        }
                        continue;
                    }
                    // Start all selected native completions before awaiting any. Native
                    // admission, grouping, recovery and per-member receipts stay in the kernel.
                    using (DistributedReadTelemetry.Measure("partition.terminal.native"))
                    {
                        await Task.WhenAll(jobs.Select((job, index) => ApplyAsync(job, observations[index], selections.GetValueOrDefault(job.Operation))));
                    }
                }
                catch (Exception failure)
                {
                    foreach (var job in jobs)
                    {
                        job.Failure ??= failure;
                    }
                }
                lock (_sync)
                {
                    _active -= jobs.Count;
                    DistributedReadTelemetry.Queue("terminal", _pending.Count, _active);
                }
                Complete(jobs);
            }
        }

        private static void Complete(IEnumerable<Job> jobs)
        {
            foreach (var job in jobs)
            {
                if (job.Failure is { } failure)
                {
                    job.Completion.TrySetException(failure);
                }
                else
                {
                    job.Completion.TrySetResult(job.Receipt);
                }
            }
        }

        private async Task ApplyAsync(Job job, PartitionTerminalObservation observed, string? selectedDescriptor)
        {
            try
            {
                if (observed.Failure is { } failure)
                {
                    ExceptionDispatchInfo.Capture(failure).Throw();
                }

                if (selectedDescriptor is null)
                {
                    var plan = observed.Plan ?? throw new InvalidDataException("Missing selected partition plan.");
                    var application = observed.Application ?? throw new InvalidDataException("Missing application selection.");
                    application.RequireCoordinatedPlan(plan);
                    _ = owner.RequireNativeMember(plan, !application.PlanningEvidence.IsEmpty);
                    selectedDescriptor = plan.Descriptor;
                }
                // Preparation already validated the entire local closure and stored the
                // descriptor durably. Startup recovery revalidates it against the full
                // authority plan before exposing the node. Only the terminal decision
                // needs a fresh authority observation for that selected native member.
                var decision = observed.Decision ?? throw new InvalidOperationException("The transaction is not decided.");
                if (decision.ApplicationOnly || decision.Operation != job.Operation || decision.Descriptor != selectedDescriptor)
                {
                    throw new InvalidDataException("Decision and selected plan differ.");
                }

                if (!decision.Commit && !(await owner.Kernel.PartitionTransactions.ReadAsync()).Any(p => p.Operation == job.Operation))
                {
                    await owner.CompleteAbsentTimestampAsync(job.Operation, decision.Descriptor, CancellationToken.None);
                    return;
                }
                // Selection owns completion even if the originating HTTP request is
                // cancelled. No vote or durable result is abandoned after this point.
                job.Receipt = await owner.Kernel.PartitionTransactions.CompleteTimestampedAsync(job.Operation, decision.Descriptor, decision.Commit, decision.Timestamp);
            }
            catch (Exception failure)
            {
                job.Failure = failure;
            }
        }

        public async ValueTask DisposeAsync()
        {
            Task? worker;
            lock (_sync)
            {
                _closed = true;
                worker = _worker;
            }
            if (worker is not null)
            {
                await worker;
            }

            Task[] applications;
            lock (_sync)
            {
                applications = _timestampApplications.ToArray();
            }

            await Task.WhenAll(applications);
        }
    }
}
