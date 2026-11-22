using EntityForge.Db.Durability;
using EntityForge.Db.Mapping;
using EntityForge.Db.Storage;
using System.Diagnostics;

namespace EntityForge.Db.Kernel;

public sealed partial class DatabaseKernel
{
    private const int MaximumPartitionCohort = 8;
    private const int MaximumQueuedPartitionCommands = 256;
    private const long MaximumQueuedPartitionBytes = 64L * 1024 * 1024;
    private readonly object _partitionWorkSync = new();
    private readonly Queue<PartitionCommand> _partitionWork = new();
    private Task? _partitionWorker;
    private long _partitionQueuedBytes;
    private bool _partitionWorkClosed;

    private sealed record PartitionCommand(Guid Operation, string Descriptor,
        DurablePartitionTransaction? Preparation, bool Commit, CancellationToken Token, DurableTimestampState? Timestamp = null)
    {
        internal TaskCompletionSource<DatabaseCommitReceipt?> Completion
        {
            get;
        }
        =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal long Bytes => Preparation?.EvidenceBytes ?? 0;

        internal long QueuedAt { get; } = Stopwatch.GetTimestamp();
    }

    internal async ValueTask PreparePartitionTransactionAsync(Guid operation, string descriptor,
        DatabaseVersion readVersion, ReadOnlyMemory<byte> payload, CancellationToken token = default)
    {
        RequirePartitionTransactions();
        var selected = new DurablePartitionTransaction(operation, descriptor, readVersion, payload,
            DurablePartitionTransactionState.Prepared);
        await EnqueuePartitionCommand(new(operation, descriptor, selected, false, token)).ConfigureAwait(false);
    }

    internal async ValueTask<DurablePartitionTransaction> SelectPartitionCoordinatorAsync(Guid operation, string descriptor,
        DatabaseVersion readVersion, ReadOnlyMemory<byte> payload, DurablePartitionCoordinator coordinator, CancellationToken token)
    {
        RequirePartitionTransactions();
        ArgumentNullException.ThrowIfNull(coordinator);
        var selected = new DurablePartitionTransaction(operation, descriptor, readVersion, payload,
            DurablePartitionTransactionState.Prepared, coordinator: coordinator);
        await EnqueuePartitionCommand(new(operation, descriptor, selected, false, token)).ConfigureAwait(false);
        return await ReadPartitionCoordinatorAsync(operation, CancellationToken.None).ConfigureAwait(false)
            ?? throw new InvalidDataException("Durable home selection was not published.");
    }

    internal async ValueTask<DatabaseCommitReceipt?> CompletePartitionTransactionAsync(Guid operation,
        string descriptor, bool commit, CancellationToken token = default, DurableTimestampState? commitTime = null)
    {
        RequirePartitionTransactions();
        if (commit && commitTime is not null)
        {
            if (!TimestampMvccEnabled || commitTime.Domain != TimestampCostOptions!.Domain || !commitTime.Mvcc)
            {
                throw new InvalidDataException("Commit time belongs to another timestamp interpretation.");
            }
            // Preserve the exact decision time while allowing its uncertainty
            // interval to overlap native persistence. PublishCommitAsync retains
            // the final clock barrier and known-commit failure classification.
            if (!TimestampCostOptions!.Healthy())
            {
                throw new InvalidOperationException("Clock is unhealthy.");
            }
        }
        object? timestamp = TimestampAdmission is { } clock ? await clock.EnterAsync(operation, descriptor, token).ConfigureAwait(false) : null;
        var receipt = await EnqueuePartitionCommand(new(operation, descriptor, null, commit, token, commitTime)).ConfigureAwait(false);
        if (timestamp is not null)
        {
            try
            {
                await TimestampAdmission!.AppliedAsync(timestamp, receipt, CurrentVersion, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception failure) when (receipt is not null)
            {
                throw new EntityOperationCommittedException<DatabaseCommitReceipt>(receipt, receipt, failure);
            }
        }
        return receipt;
    }

    private Task<DatabaseCommitReceipt?> EnqueuePartitionCommand(PartitionCommand command)
    {
        command.Token.ThrowIfCancellationRequested();
        lock (_partitionWorkSync)
        {
            ObjectDisposedException.ThrowIf(_partitionWorkClosed, this);
            if (_partitionWork.Count >= MaximumQueuedPartitionCommands ||
                command.Bytes > MaximumQueuedPartitionBytes - _partitionQueuedBytes)
            {
                throw new InvalidOperationException("Partition command admission capacity is exhausted.");
            }

            _partitionWork.Enqueue(command);
            _partitionQueuedBytes += command.Bytes;
            _partitionWorker ??= Task.Run(RunPartitionWorkAsync);
        }
        return command.Completion.Task;
    }

    private async Task DrainPartitionWorkAsync()
    {
        Task? worker;
        lock (_partitionWorkSync)
        {
            _partitionWorkClosed = true;
            worker = _partitionWorker;
        }
        if (worker is not null)
        {
            await worker.ConfigureAwait(false);
        }
    }

    private async Task RunPartitionWorkAsync()
    {
        while (true)
        {
            // Collect only after obtaining ownership. Requests arriving during the
            // previous force are then eligible for the next durable cohort.
            await _localWriteAuthority.WaitAsync().ConfigureAwait(false);
            List<PartitionCommand> cohort = [];
            long started = 0;
            try
            {
                lock (_partitionWorkSync)
                {
                    HashSet<Guid> operations = [];
                    string stop = "empty";
                    while (_partitionWork.TryPeek(out var next) && cohort.Count < MaximumPartitionCohort)
                    {
                        if (next.Token.IsCancellationRequested)
                        {
                            _partitionWork.Dequeue();
                            _partitionQueuedBytes -= next.Bytes;
                            next.Completion.TrySetCanceled(next.Token);
                            continue;
                        }
                        if (cohort.Count != 0 && ((next.Preparation is null) != (cohort[0].Preparation is null) ||
                            next.Commit != cohort[0].Commit || !operations.Add(next.Operation) ||
                            _commitLog is not IGroupedPhysicalDatabaseCommitLog))
                        {
                            stop = (next.Preparation is null) != (cohort[0].Preparation is null) || next.Commit != cohort[0].Commit
                            ? "phase" : "identity-or-log";
                            break;
                        }
                        operations.Add(next.Operation);
                        cohort.Add(_partitionWork.Dequeue());
                        _partitionQueuedBytes -= next.Bytes;
                    }
                    if (cohort.Count == 0)
                    {
                        _partitionWorker = null;
                        return;
                    }
                    _telemetry.PartitionCohortStop(Profile, cohort.Count == MaximumPartitionCohort ? "limit" : stop);
                }
                // Selection transfers ownership. Caller cancellation cannot abandon
                // a force, vote or terminal outcome after this point.
                using var activity = _telemetry.Start("partition.native.cohort", Profile);
                activity?.SetTag("entityforge.operations", cohort.Select(command => command.Operation.ToString("N")).ToArray());
                activity?.SetTag("entityforge.phase", cohort[0].Preparation is null ? "complete" : "prepare");
                started = Stopwatch.GetTimestamp();
                foreach (var command in cohort)
                {
                    _telemetry.CommitPhase(Profile, "partition_queue", Stopwatch.GetElapsedTime(command.QueuedAt), 0);
                }

                if (cohort[0].Preparation is not null)
                {
                    await PreparePartitionCohortAsync(cohort).ConfigureAwait(false);
                }
                else
                {
                    await CompletePartitionCohortAsync(cohort).ConfigureAwait(false);
                }
            }
            catch (Exception failure)
            {
                foreach (var command in cohort)
                {
                    command.Completion.TrySetException(failure);
                }
            }
            finally
            {
                if (started != 0)
                {
                    _telemetry.CommitPhase(Profile, "partition_service", Stopwatch.GetElapsedTime(started), 0);
                }

                _localWriteAuthority.Release();
                // New preparations cannot settle an already captured preparation.
                // Completion (including failed publication) may do so; recovery
                // and disposal independently wake every observer.
                if (cohort.Count != 0 && cohort[0].Preparation is null)
                {
                    NotifySnapshotProgress();
                }
            }
        }
    }

    private async Task PreparePartitionCohortAsync(IReadOnlyList<PartitionCommand> cohort)
    {
        await _commitCoordinator.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            List<(PartitionCommand Command, DatabaseTransactionFootprint Footprint)> accepted = [];
            long bytes = _partitionTransactions.Values.Sum(value => value.EvidenceBytes);
            foreach (var command in cohort)
            {
                bool admittedHome = false;
                var proposed = command.Preparation!;
                try
                {
                    EnsureReady();
                    EntityOperationIds.RequireRetained(command.Operation, _localOperationFloor);
                    if (_partitionTransactions.TryGetValue(command.Operation, out var existing))
                    {
                        if (proposed.Coordinator is { } home)
                        {
                            if (existing.Coordinator is not { } winner || winner.Authority != home.Authority ||
                                winner.ApplicationDescriptor != home.ApplicationDescriptor)
                            {
                                throw new EntityForgeDbIdempotencyException(command.Operation);
                            }
                        }
                        else
                        {
                            RequireSamePreparation(existing, proposed);
                        }

                        command.Completion.TrySetResult(null);
                        continue;
                    }
                    if (_partitionTransactions.Count + accepted.Count >= DurablePartitionTransaction.MaximumTransactions ||
                        proposed.EvidenceBytes > DurablePartitionTransaction.MaximumPayloadBytes - bytes)
                    {
                        throw new InvalidOperationException("Partition transaction evidence capacity is exhausted.");
                    }

                    if (_committedTransactions.ContainsKey(command.Operation))
                    {
                        throw new EntityForgeDbIdempotencyException(command.Operation);
                    }

                    if (proposed.ReadVersion.Value > CurrentVersion.Value)
                    {
                        throw new ArgumentOutOfRangeException(nameof(proposed.ReadVersion));
                    }

                    if (TimestampMvccEnabled)
                    {
                        proposed = new(proposed.Operation, proposed.Descriptor, proposed.ReadVersion, proposed.Payload,
                            proposed.State, proposed.LocalVersion, proposed.Coordinator, NextTimestamp());
                    }

                    admittedHome = proposed.Coordinator is not null;
                    var footprint = DescribeReservation(command.Operation, proposed.Payload, proposed.ReadVersion);
                    if ((!footprint.IsScopedPreparation && CurrentVersion != proposed.ReadVersion) ||
                        proposed.ReadVersion.Value > CurrentVersion.Value || _committedTransactions.ContainsKey(command.Operation) ||
                        _partitionReservations.Values.Any(footprint.ConflictsWith) ||
                        accepted.Any(value => value.Command.Preparation!.State == DurablePartitionTransactionState.Prepared &&
                            footprint.ConflictsWith(value.Footprint)))
                    {
                        throw new DatabasePartitionConflictException();
                    }

                    PreparedKernelCommit? planned = null;
                    try
                    {
                        planned = await PlanCommitAsync(command.Operation, proposed.Payload, new(checked(_durableVersion + 1)),
                            CancellationToken.None, partitionAdmissionOwned: true).ConfigureAwait(false);
                        planned = await PreparePhysicalMutationsAsync(planned, CancellationToken.None).ConfigureAwait(false);
                        if (_retainLogicalIntents)
                        {
                            (_profile as IDatabaseProfileLogicalPreparation)?.CaptureLogicalEffects(command.Operation, proposed.Payload);
                        }

                        RetainPhysicalPreparation(planned, cohort.Count == 1, footprint);
                    }
                    finally
                    {
                        if (planned?.PhysicalPlanner is { } planner && !ReferenceEquals(planned, _retainedPhysicalPreparation))
                        {
                            try
                            {
                                await planner.AbandonUnsubmittedPhysicalMutationsAsync(command.Operation, CancellationToken.None).ConfigureAwait(false);
                            }
                            catch
                            {
                                RequireRecovery("partition_prepare_cleanup");
                                throw;
                            }
                        }
                    }
                    accepted.Add((command with
                    {
                        Preparation = proposed
                    }, footprint));
                    bytes += proposed.EvidenceBytes;
                }
                catch (Exception failure)
                {
                    (_profile as IDatabaseProfileLogicalPreparation)?.ReleaseLogicalPreparation(command.Operation);
                    if (admittedHome)
                    {
                        // Validation failed before a native write was submitted. Do
                        // not strand a frozen home manifest; atomically retain its
                        // terminal rejection. Cleanup uncertainty forbids this path.
                        try
                        {
                            EnsureReady();
                            // Preserve the assigned visibility-clock association on
                            // rejected homes too; recovery validates every record.
                            var rejected = proposed.Complete(DurablePartitionTransactionState.Aborted);
                            accepted.Add((command with
                            {
                                Preparation = rejected
                            }, DatabaseTransactionFootprint.WholePartition));
                            bytes += rejected.EvidenceBytes;
                            continue;
                        }
                        catch
                        {
                            /* Recovery-required state must preserve the original failure. */
                        }
                    }
                    command.Completion.TrySetException(failure);
                }
            }
            if (accepted.Count == 0)
            {
                return;
            }

            EnsureReady();
            EnsureDatabaseIncarnation();
            var commits = accepted.Select(value => new DatabasePhysicalCommit(
                PartitionEvidenceEnvelope(value.Command.Preparation!), [])).ToArray();
            if (_commitLog is IGroupedPhysicalDatabaseCommitLog grouped)
            {
                // Bounds rejection has not submitted anything. Retain individual
                // compatibility with compositions whose group limits are smaller.
                try
                {
                    grouped.ValidateGroup(commits);
                }
                catch (Exception failure) when (failure is ArgumentException or NotSupportedException)
                {
                    foreach (var value in accepted)
                    {
                        await AppendPartitionEvidenceAsync(value.Command.Preparation!, CancellationToken.None).ConfigureAwait(false);
                        PublishPreparation(value.Command, value.Footprint);
                    }
                    return;
                }
                _telemetry.PartitionCohort(Profile, "prepare", accepted.Count);
                try
                {
                    await grouped.CommitGroupAsync(commits, CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    RequireRecovery("partition_evidence_wal");
                    throw;
                }
            }
            else
            {
                await AppendPartitionEvidenceAsync(accepted.Single().Command.Preparation!, CancellationToken.None).ConfigureAwait(false);
            }

            foreach (var value in accepted)
            {
                PublishPreparation(value.Command, value.Footprint);
            }
        }
        catch (Exception failure)
        {
            try
            {
                await DiscardPhysicalPreparationAsync().ConfigureAwait(false);
            }
            catch (Exception cleanup)
            {
                EntityFailureDiagnostics.Attach(failure, cleanup);
            }
            throw;
        }
        finally
        {
            _commitCoordinator.Release();
        }
    }

    private void PublishPreparation(PartitionCommand command, DatabaseTransactionFootprint footprint)
    {
        try
        {
            PublishPartitionTransaction(command.Preparation!);
            if (command.Preparation!.State == DurablePartitionTransactionState.Prepared)
            {
                _partitionReservations.Add(command.Operation, footprint);
                if (_retainLogicalIntents)
                {
                    (_profile as IDatabaseProfileLogicalPreparation)?.RetainLogicalPreparation(command.Operation, command.Preparation.Payload);
                }
            }
        }
        catch
        {
            RequireRecovery("partition_evidence_publication");
            throw;
        }
        using (var trace = _telemetry.Start("db.partition.prepared_footprint", _profile.Profile))
        {
            if (trace is not null)
            {
                trace.SetTag("entityforge.operation", command.Operation.ToString("N"));
                trace.SetTag("entityforge.incarnation", _databaseIncarnation.ToString("N"));
                footprint.TraceWrites(trace);
            }
        }

        command.Completion.TrySetResult(null);
    }

    private async Task CompletePartitionCohortAsync(IReadOnlyList<PartitionCommand> cohort)
    {
        if (TimestampMvccEnabled || cohort.Any(command => command.Timestamp is not null))
        {
            await _commitCoordinator.WaitAsync();
            try
            {
                EnsureReady();
                foreach (var command in cohort)
                {
                    if (command.Commit)
                    {
                        RequireCommitTime(command.Operation, command.Timestamp);
                    }
                }
            }
            finally
            {
                _commitCoordinator.Release();
            }
        }
        // Replays, aborts and unsupported profiles use the same owned individual
        // completion path. Only fresh, scoped reservations enter native grouping.
        bool eligible = cohort.Count > 1 && cohort[0].Commit &&
            _commitLog is IGroupedPhysicalDatabaseCommitLog && _profile is IDatabaseProfileOrderedPreparationRuntime &&
            _profile is not IDatabaseProfileGroupedPhysicalMutationPlanner { SupportsAtomicGroupPublication: false } &&
            cohort.All(command => _partitionTransactions.TryGetValue(command.Operation, out var entry) &&
                entry.State == DurablePartitionTransactionState.Prepared &&
                StringComparer.OrdinalIgnoreCase.Equals(entry.Descriptor, command.Descriptor) &&
                _partitionReservations.TryGetValue(command.Operation, out var footprint) && footprint.IsScopedPreparation);
        if (eligible)
        {
            try
            {
                var receipts = await CommitGroupCoreAsync(cohort.Select(command => new DatabaseCommitRequest(command.Operation,
                    _partitionTransactions[command.Operation].Payload)).ToArray(), null, CancellationToken.None,
                    partitionAdmissionOwned: true).ConfigureAwait(false);
                _telemetry.PartitionCohort(Profile, "commit", cohort.Count);
                try
                {
                    await _commitCoordinator.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                    try
                    {
                        for (int index = 0; index < cohort.Count; index++)
                        {
                            var command = cohort[index];
                            var receipt = receipts[index];
                            PublishPartitionTransaction(_partitionTransactions[command.Operation]
                                .Complete(DurablePartitionTransactionState.Committed, receipt.Version, _committedTransactions[command.Operation].Timestamp));
                            _partitionReservations.Remove(command.Operation);
                            _partitionCommitTimes.Remove(command.Operation);
                            (_profile as IDatabaseProfileLogicalPreparation)?.ReleaseLogicalPreparation(command.Operation);
                        }
                    }
                    finally
                    {
                        _commitCoordinator.Release();
                    }
                }
                catch (Exception failure)
                {
                    RequireRecovery("partition_group_terminal_publication");
                    throw new EntityOperationGroupDurableException(receipts, failure);
                }
                for (int index = 0; index < cohort.Count; index++)
                {
                    cohort[index].Completion.TrySetResult(receipts[index]);
                }

                return;
            }
            catch (DatabaseCommitGroupPreparationException)
            {
                /* Definitely unsubmitted and cleaned: individual fallback. */
            }
            catch (EntityOperationGroupDurableException failure)
            {
                foreach (var command in cohort)
                {
                    var receipt = failure.Receipts.Single(value => value.TransactionId == command.Operation);
                    command.Completion.TrySetException(new EntityOperationCommittedException<DatabaseCommitReceipt>(receipt, receipt, failure));
                }
                return;
            }
        }
        foreach (var command in cohort)
        {
            _telemetry.PartitionCohort(Profile, command.Commit ? "individual_commit" : "individual_abort", 1);
            try
            {
                command.Completion.TrySetResult(await CompletePartitionTransactionIndividuallyAsync(command.Operation,
                command.Descriptor, command.Commit, CancellationToken.None).ConfigureAwait(false));
            }
            catch (Exception failure)
            {
                command.Completion.TrySetException(failure);
            }
        }
    }
}
