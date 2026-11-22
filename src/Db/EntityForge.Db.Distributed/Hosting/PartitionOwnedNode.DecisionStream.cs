using EntityForge.Db.Durability;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Distributed.Hosting;

internal sealed partial class PartitionOwnedNode
{
    private DecisionStream? _decisionStream;

    // Explicit experimental composition, never a fallback after submission.
    internal async ValueTask EnableDecisionNotificationsAsync(CancellationToken token = default)
    {
        await _lifecycle.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            if (_recoveringHome)
            {
                throw new InvalidOperationException("Participant recovery must finish before notification admission.");
            }

            if (_transactions is not IPartitionDecisionNotifications source)
            {
                throw new NotSupportedException("The enrolled authority has no decision notification source.");
            }

            _decisionStream ??= new(this, source);
            _decisionStream.Start();
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private sealed class DecisionStream(PartitionOwnedNode owner, IPartitionDecisionNotifications source)
    {
        private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(250);
        private readonly object _sync = new();
        private readonly Dictionary<Guid, Task> _jobs = [];
        private CancellationTokenSource? _lifetime, _session;
        private Task? _run;

        internal Exception? LastFailure
        {
            get; private set;
        }

        internal void Start()
        {
            lock (_sync)
            {
                if (_run is not null)
                {
                    return;
                }

                _lifetime = new();
                _run = Task.Run(() => RunAsync(_lifetime.Token));
            }
        }

        internal void Reconcile()
        {
            lock (_sync)
            {
                if (_session is not null)
                {
                    _ = _session.CancelAsync();
                }
            }
        }

        private async Task RunAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                using var session = CancellationTokenSource.CreateLinkedTokenSource(token);
                lock (_sync)
                {
                    _session = session;
                }

                try
                {
                    await foreach (var notification in source.ReadNotificationsAsync(session.Token))
                    {
                        if (notification.Reset)
                        {
                            DistributedReadTelemetry.Work("partition.stream.reset", 1);
                            var pending = (await owner.Kernel.PartitionTransactions.ReadAsync(session.Token))
                                .Where(entry => entry.State == DurablePartitionTransactionState.Prepared && !IsNativeLocal(entry)).ToArray();
                            foreach (var batch in pending.Chunk(PartitionTerminalObservation.MaximumBatch))
                            {
                                var observed = await owner._transactions!.ReadTerminalAsync(batch.Select(e => e.Operation).ToArray(),
                                    session.Token, batch.ToDictionary(e => e.Operation, e => e.Descriptor));
                                foreach (var result in observed)
                                {
                                    if (result.Failure is { } failure)
                                    {
                                        throw failure;
                                    }

                                    if (result.Decision is not null)
                                    {
                                        await ScheduleAsync(result.Operation, session.Token);
                                    }
                                }
                            }
                        }
                        else
                        {
                            DistributedReadTelemetry.Work("partition.stream.notification", 1);
                            // A global decision prefix has no recipient list. Native
                            // selection filters irrelevant notifications; it grants no
                            // decision authority. The inbox executor validates that afresh.
                            var selected = await owner.Kernel.PartitionTransactions.ReadSelectedDescriptorsAsync(
                                [notification.Operation], session.Token);
                            if (selected.ContainsKey(notification.Operation))
                            {
                                await ScheduleAsync(notification.Operation, session.Token);
                            }
                        }
                    }
                    throw new IOException("Decision notification source ended.");
                }
                catch (OperationCanceledException) when (session.IsCancellationRequested)
                {

                }
                catch (Exception failure)
                {
                    LastFailure = failure;
                }
                finally
                {
                    lock (_sync)
                    {
                        _session = null;
                    }
                }
                if (!token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(RetryDelay, token);
                    }
                    catch (OperationCanceledException)
                    {

                    }
                }
            }
        }

        private async Task ScheduleAsync(Guid operation, CancellationToken token)
        {
            while (true)
            {
                Task[] busy;
                lock (_sync)
                {
                    if (_jobs.ContainsKey(operation))
                    {
                        return;
                    }

                    if (_jobs.Count < PartitionTerminalInbox.MaximumJobs)
                    {
                        // Start behind an asynchronous boundary so dictionary ownership
                        // is installed before a synchronously completed replay can exit.
                        _jobs.Add(operation, Task.Run(() => ApplyAsync(operation)));
                        return;
                    }
                    busy = _jobs.Values.ToArray();
                }
                await Task.WhenAny(busy).WaitAsync(token);
            }
        }

        private async Task ApplyAsync(Guid operation)
        {
            try
            {
                await owner.ApplyPartitionDecisionAsync(operation);
            }
            catch (Exception failure)
            {
                LastFailure = failure;
                Reconcile();
            }
            finally
            {
                lock (_sync)
                {
                    _jobs.Remove(operation);
                }
            }
        }

        internal async ValueTask StopAsync()
        {
            Task? run;
            lock (_sync)
            {
                run = _run;
                if (_lifetime is not null)
                {
                    _ = _lifetime.CancelAsync();
                }
            }
            if (run is not null)
            {
                await run;
            }

            Task[] jobs;
            lock (_sync)
            {
                jobs = _jobs.Values.ToArray();
            }

            await Task.WhenAll(jobs);
            lock (_sync)
            {
                _run = null;
                _lifetime?.Dispose();
                _lifetime = null;
            }
        }
    }
}
