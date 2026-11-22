using EntityForge.Db.Distributed.Sharding;
using System.Text.Json;
using System.Threading.Channels;
using EntityForge.Db.Durability;

namespace EntityForge.Db.Distributed.Hosting;

internal sealed record PartitionQueryJobCommand(Guid Id, PartitionQueryExchangeRequest? Selection = null,
    bool Finish = false, bool Abandon = false,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    PartitionQueryAuthorityBinding? Home = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    int Sequence = 0);

internal sealed record PartitionQueryJobResult(bool Valid,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    PartitionProjectionValue? Value, bool Completed,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    PartitionQueryPendingCompletion? Pending = null);

// One authority interval owns the complete local query, including all pages and
// frontier stages. It borrows the certified native pin; it is not an authority cache.
internal sealed partial class PartitionReadCuts
{
    private sealed partial class Entry
    {
        private readonly Dictionary<Guid, QueryJob> _queryJobs = [];
        private readonly Queue<Guid> _completedQueryJobs = [];
        private const int MaximumQueryJobs = 128;

        private async ValueTask<PartitionProjectionValue> ReadQueryJobAsync(PartitionReadCut cut,
            PartitionReadQuery request, CancellationToken token)
        {
            Require(cut);
            if (TimestampProtection is null || Scope is not null || request.ReadTime is not { } readTime || cut.ClockDomain != readTime.Domain || request.QueryJob is not { } command ||
                command.Id == Guid.Empty || request.Identity != Guid.Empty || request.Subject is not null || request.Label is not null ||
                request.Batch is not null || request.Projection is not null || request.Source is not null ||
                request.SnapshotPrepared is not null || request.QueryExchange is not null ||
                command.Abandon && (!command.Finish || command.Selection is not null) ||
                command.Selection is null && !command.Finish)
            {
                throw new InvalidDataException("Invalid query job association.");
            }

            if (command.Home is not null && (command.Home != _node.QueryHomeBinding || command.Sequence < 1))
            {
                throw new InvalidDataException("Query home capability is unavailable for this owner or snapshot.");
            }

            var owned = command.Selection is { } selection ? PartitionQueryOwnedSelection.Capture(selection) : null;
            if (owned is not null)
            {
                command = command with
                {
                    Selection = owned.Value
                };
            }

            QueryJob job;
            bool created = false;
            lock (_queryJobs)
            {
                if (!_queryJobs.TryGetValue(command.Id, out job!))
                {
                    // An unanswered start is always abandoned using its original ID.
                    if (command.Abandon)
                    {
                        return new(new PartitionQueryJobResult(true, null, true));
                    }

                    if (command.Selection is null || command.Selection.Offset != 0 || Volatile.Read(ref _closing) != 0)
                    {
                        throw new InvalidOperationException("The query job is absent or its pin is closing.");
                    }

                    while (_completedQueryJobs.TryDequeue(out var completed))
                    {
                        _queryJobs.Remove(completed);
                    }

                    if (_queryJobs.Count >= MaximumQueryJobs)
                    {
                        throw new InvalidOperationException("Query job capacity is exhausted.");
                    }

                    job = new(this, cut, command, readTime, owned!);
                    _queryJobs.Add(command.Id, job);
                    created = true;
                }
            }
            if (created)
            {
                return new(await job.StartAsync(token));
            }

            job.Require(command, request.ReadTime, owned);
            return new(await job.ExecuteAsync(command, token));
        }

        private sealed class QueryJob
        {
            private readonly Entry _entry;
            private readonly PartitionReadCut _cut;
            private readonly PartitionQueryJobCommand _first;
            private readonly DurableTimestampState _time;
            private readonly PartitionQueryInterpretation _interpretation;
            private readonly CancellationTokenSource _stop;
            private bool _disposed;
            private int _sequence;
            private readonly HashSet<int> _received = [];
            private Channel<Work>? _work;
            private bool _closed;
            private Work? _initial;
            private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

            internal Task Completion => _completion.Task;

            private Channel<Work> Queue => _work ??= Channel.CreateBounded<Work>(new BoundedChannelOptions(MaximumQueryJobs)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });

            private sealed record Work(PartitionQueryJobCommand Command, TaskCompletionSource<PartitionQueryJobResult> Result);

            internal QueryJob(Entry entry, PartitionReadCut cut, PartitionQueryJobCommand first, DurableTimestampState time, PartitionQueryOwnedSelection owned)
            {
                _entry = entry;
                _cut = cut;
                _first = first;
                _time = time;
                _interpretation = new(owned);
                _interpretation.Require(owned);
                AdmitSequence(first);
                _initial = new(first, new(TaskCreationOptions.RunContinuationsAsynchronously));
                _stop = CancellationTokenSource.CreateLinkedTokenSource(entry._token);
                _ = Completion.ContinueWith(t =>
{
    _ = t.Exception;
}, TaskContinuationOptions.OnlyOnFaulted);
            }

            // Registration publishes a fully associated job before any native work.
            // Its initial command is already admitted, so racing continuations cannot
            // replace it. Even an abandoned request starts and drains this lifetime.
            internal ValueTask<PartitionQueryJobResult> StartAsync(CancellationToken token)
            {
                var initial = _initial ?? throw new InvalidOperationException("Query job already started.");
                _initial = null;
                _ = RunOwnedAsync(initial);
                return AwaitAsync(initial.Result.Task, token);
            }

            private async Task RunOwnedAsync(Work initial)
            {
                Exception? failure = null;
                try
                {
                    await RunAsync(initial);
                }
                catch (Exception error)
                {
                    failure = error;
                }
                // Publish drainage and its registry obligation together. Keep the
                // completed entry until a new identity is admitted, preserving
                // exact-ID interpretation checks without scanning every job.
                lock (_entry._queryJobs)
                {
                    if (failure is null)
                    {
                        _completion.TrySetResult();
                    }
                    else
                    {
                        _completion.TrySetException(failure);
                    }

                    _entry._completedQueryJobs.Enqueue(_first.Id);
                }
            }

            private async ValueTask<PartitionQueryJobResult> AwaitAsync(Task<PartitionQueryJobResult> result, CancellationToken token)
            {
                var value = await result.WaitAsync(token);
                if (value.Completed)
                {
                    await Completion.WaitAsync(token);
                }

                return value;
            }

            private void AdmitSequence(PartitionQueryJobCommand command)
            {
                if (command.Home is null)
                {
                    return;
                }

                if (command.Sequence <= _sequence || command.Sequence > (long)_sequence + MaximumQueryJobs ||
                    _received.Contains(command.Sequence) || command.Finish && (command.Sequence != _sequence + 1 || _received.Count != 0))
                {
                    throw new InvalidDataException("Query command sequence differs.");
                }

                _received.Add(command.Sequence);
                while (_received.Remove(_sequence + 1))
                {
                    _sequence++;
                }
            }

            internal void Require(PartitionQueryJobCommand command, DurableTimestampState? time, PartitionQueryOwnedSelection? owned)
            {
                if (command.Home != _first.Home || time != _time)
                {
                    throw new InvalidDataException("Query job identity was reused with another query or timestamp.");
                }

                if (owned is not null)
                {
                    _interpretation.Require(owned);
                }
            }

            internal async ValueTask<PartitionQueryJobResult> ExecuteAsync(PartitionQueryJobCommand command, CancellationToken token)
            {
                if (command.Abandon)
                {
                    lock (_stop)
                    {
                        if (!_disposed)
                        {
                            _stop.Cancel();
                        }
                    }
                    // Abandon joins cleanup; failure of the read is not completion success.
                    try
                    {
                        await Completion;
                    }
                    catch
                    {

                    }
                    return new(true, null, true);
                }
                var result = new TaskCompletionSource<PartitionQueryJobResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                bool queued;
                lock (_received)
                {
                    if (_closed)
                    {
                        queued = false;
                    }
                    else
                    {
                        AdmitSequence(command);
                        queued = Queue.Writer.TryWrite(new(command, result));
                    }
                }
                if (!queued)
                {
                    if (Completion.IsCompleted)
                    {
                        await Completion;
                    }

                    throw new InvalidOperationException("The query job is complete or its queue is full.");
                }
                return await AwaitAsync(result.Task, token);
            }

            private async Task RunAsync(Work initial)
            {
                Work? active = initial;
                PartitionQueryJobResult? final = null;
                Exception? failure = null;
                try
                {
                    using var capacity = _entry._node.EnterQueryJob();
                    using var trace = DistributedReadTelemetry.Start("partition.query.job");
                    await _entry.UseEpochAsync(_cut, async token =>
                    {
                        async ValueTask<bool> Execute()
                        {
                            using var workspace = new PartitionQueryWorkspace(_entry._node, _interpretation);
                            using var owned = _entry._node.EnterQueryWorkspace(workspace);
                            IDisposable? timestampView = null;
                            try
                            {
                                object view = await _entry._node.Kernel.PartitionTransactions.CaptureTimestampProjectionAsync(
                                    _entry.TimestampProtection!, _time, null, token);
                                timestampView = _entry._node.EnterQueryTimestamp(view, _time);
                                var item = initial;
                                while (true)
                                {
                                    active = item;
                                    PartitionProjectionValue? value = item.Command.Selection is { } selection
                                        ? await _entry._node.ReadProjectionAsync(_cut.Position.Version,
                                            new("query-relation-page", QueryExchange: selection), token, nativeExclusion: false) : null;
                                    bool last = item.Command.Finish && (value is null || value.Native is PartitionQueryExchangePage { NextOffset: null });
                                    var response = new PartitionQueryJobResult(true, value, last);
                                    if (last)
                                    {
                                        final = response;
                                        break;
                                    }
                                    // Private provisional evidence only. QueryRead joins every
                                    // job's authority-checked completion before public success.
                                    item.Result.TrySetResult(response);
                                    active = null;
                                    Channel<Work> queue;
                                    lock (_received)
                                    {
                                        queue = Queue;
                                    }

                                    item = await queue.Reader.ReadAsync(token);
                                }
                                return true;
                            }
                            finally
                            {
                                timestampView?.Dispose();
                            }
                        }
                        return _first.Home is null
                            ? await _entry._node.Authority.ExecuteQueryAsync(Execute)
                            : await _entry._node.Authority.ExecutePendingQueryAsync(Execute);
                    }, _stop.Token);
                    // Workspace and local ownership have drained. Ordinary jobs
                    // also checked fresh authority; home jobs explicitly retain
                    // that outstanding obligation in the pending result below.
                    if (final is null || active is null)
                    {
                        throw new InvalidOperationException("Query ended without completion.");
                    }

                    if (_first.Home is { } binding && final.Valid)
                    {
                        final = final with
                        {
                            Pending = new(_first.Id, _cut.Reference,
                            _interpretation.Fingerprint, active.Command.Sequence,
                            _entry._node.CurrentLease.Placement.Claim, binding, _time)
                        };
                    }

                    active.Result.TrySetResult(final);
                }
                catch (Exception error)
                {
                    failure = error;
                    active?.Result.TrySetException(error);
                    throw;
                }
                finally
                {
                    lock (_stop)
                    {
                        _disposed = true;
                        _stop.Dispose();
                    }
                    lock (_received)
                    {
                        _closed = true;
                        if (_work is not null)
                        {
                            _work.Writer.TryComplete(failure);
                            while (_work.Reader.TryRead(out var pending))
                            {
                                pending.Result.TrySetException(failure ?? new InvalidOperationException("Query already completed."));
                            }
                        }
                    }
                }
            }
        }
    }
}

internal sealed partial class PartitionRemoteRead
{
    internal QueryRead BeginQuery(PartitionQueryHomeAuthority? home = null, bool completeCommands = false,
        PartitionQueryPendingCompletion[]? indexProofs = null) => new(this, _epoch && _cuts.Count <= GrpcEtcdShardPlacementAuthority.MaximumQueryClaims ? home : null, completeCommands, indexProofs);

    internal sealed class QueryRead(PartitionRemoteRead read, PartitionQueryHomeAuthority? home, bool completeCommands,
        PartitionQueryPendingCompletion[]? indexProofs)
    {
        private sealed class OwnerJob(KeyValuePair<IPartitionReadParticipant, PartitionReadCut> pair, PartitionQueryOwnedSelection selection)
        {
            internal readonly Guid Id = Guid.NewGuid();
            internal readonly IPartitionReadParticipant Participant = pair.Key;
            internal readonly PartitionReadCut Cut = pair.Value;
            internal readonly PartitionQueryInterpretation Selection = new(selection);
            internal int Sequence;
            internal int Finished;
            internal PartitionQueryPendingCompletion? Pending;
        }

        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, OwnerJob> _owners = new();

        private bool Commands => completeCommands && home is not null;

        private int Next(OwnerJob owner) => home is null ? 0 : Interlocked.Increment(ref owner.Sequence);

        private void RequireCompletion(string partition, OwnerJob owner, int sequence, PartitionQueryJobResult response)
        {
            if (home is null)
            {
                if (response.Pending is not null)
                {
                    throw new InvalidDataException("Unexpected pending authority completion.");
                }

                return;
            }
            if (!response.Valid || !response.Completed)
            {
                if (response.Pending is not null)
                {
                    throw new InvalidDataException("Premature pending authority completion.");
                }

                return;
            }
            var pending = response.Pending ?? throw new InvalidDataException("Missing pending authority completion.");
            if (pending.Job != owner.Id || pending.Cut != owner.Cut.Reference || pending.Selection != owner.Selection.Fingerprint ||
                pending.Sequence != sequence || pending.Authority != home.Binding || pending.ReadTime != read._logicalTime ||
                pending.Claim.Shard.Value != partition || pending.Claim.Generation.Value != owner.Cut.Position.Identity.Generation ||
                pending.Claim.MapVersion.Value <= 0 || pending.Claim.FencingToken.Value <= 0)
            {
                throw new InvalidDataException("Pending authority completion association differs.");
            }

            if (Commands)
            {
                while (true)
                {
                    var previous = Volatile.Read(ref owner.Pending);
                    if (previous is not null && previous.Claim != pending.Claim)
                    {
                        throw new InvalidDataException("Owner claim changed between complete commands.");
                    }

                    if (previous is not null && previous.Sequence >= sequence)
                    {
                        break;
                    }

                    if (Interlocked.CompareExchange(ref owner.Pending, pending, previous) == previous)
                    {
                        break;
                    }
                }
            }
            else if (Interlocked.CompareExchange(ref owner.Pending, pending, null) is not null)
            {
                throw new InvalidDataException("Duplicate owner completion.");
            }
        }

        internal async ValueTask<JsonElement> ReadAsync(string partition, PartitionQueryExchangeRequest selection, bool finish, CancellationToken token) =>
            (await ReadValueAsync(partition, selection, finish, token)).Element;

        internal async ValueTask<PartitionProjectionValue> ReadValueAsync(string partition, PartitionQueryExchangeRequest selection, bool finish, CancellationToken token)
        {
            var owned = PartitionQueryOwnedSelection.Capture(selection);
            selection = owned.Value;
            var owner = _owners.GetOrAdd(partition, key => new(read._cuts.Single(p => p.Key.Domain.Partition.Value == key), owned));
            owner.Selection.Require(owned);
            int sequence = Next(owner);
            var response = await owner.Participant.ReadQueryJobValueAsync(owner.Cut, new(Commands ? "query-command" : "query-job",
                QueryJob: new(owner.Id, selection, Commands || finish, Home: home?.Binding, Sequence: sequence), ReadTime: read._logicalTime), token);
            RequireCompletion(partition, owner, sequence, response);
            if (!response.Valid)
            {
                if (!response.Completed || response.Value is not null)
                {
                    throw new InvalidDataException("Invalid rejected query job.");
                }

                Volatile.Write(ref owner.Finished, 1);
                throw new PartitionReadEpochChangedException();
            }
            if (response.Value is null || Commands && !response.Completed || !Commands && response.Completed && !finish)
            {
                throw new InvalidDataException("Invalid query page completion.");
            }

            if (response.Completed)
            {
                Volatile.Write(ref owner.Finished, 1);
            }

            return response.Value;
        }

        internal async ValueTask CompleteAsync(bool abandon)
        {
            await RunSnapshotRoundAsync(_owners.Where(p => !Commands && Volatile.Read(ref p.Value.Finished) == 0), async (item, token) =>
            {
                var owner = item.Value;
                int sequence = Next(owner);
                var response = await owner.Participant.ReadQueryJobValueAsync(owner.Cut, new("query-job", ReadTime: read._logicalTime,
                    QueryJob: new(owner.Id, Finish: true, Abandon: abandon,
                        Home: home?.Binding, Sequence: sequence)), token);
                if (response is not { Valid: true, Completed: true, Value: null })
                {
                    throw new InvalidDataException("Query cleanup did not complete.");
                }

                if (!abandon)
                {
                    RequireCompletion(item.Key, owner, sequence, response);
                }

                Volatile.Write(ref owner.Finished, 1);
            }, CancellationToken.None);
            if (!abandon && home is not null)
            {
                var pending = _owners.Values.Select(owner => owner.Pending ?? throw new InvalidDataException("Incomplete query authority set."))
                    .Concat(indexProofs ?? []).GroupBy(p => p.Claim.Shard).Select(group =>
                    {
                        var proof = group.First();
                        if (group.Any(p => p.Claim != proof.Claim || p.Authority != proof.Authority || p.ReadTime != read._logicalTime))
                        {
                            throw new InvalidDataException("Root-index and data authority claims differ.");
                        }
                        // Keep the earliest expiry as the complete claim's obligation.
                        return group.OrderBy(p => p.Cut.Expires).First();
                    }).ToArray();
                if (pending.Length == 0)
                {
                    throw new InvalidDataException("Incomplete query authority set.");
                }

                var remaining = read._cuts.Values.Min(c => c.Expires) - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    throw new InvalidOperationException("Query snapshot deadline elapsed.");
                }

                using var deadline = new CancellationTokenSource(remaining);
                await home.ValidateAsync(pending, deadline.Token);
            }
        }
    }
}
