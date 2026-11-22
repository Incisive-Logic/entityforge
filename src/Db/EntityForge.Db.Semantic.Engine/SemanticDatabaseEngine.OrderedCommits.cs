using EntityForge.Db.Semantic.Durability;
using EntityForge.Db.Semantic;
using EntityForge.Db.Semantic.Core;
using EntityForge.Db.Query;
using EntityForge.Db.Routing;
using EntityForge.Db.Storage;
using EntityForge.Db;
using EntityForge.Db.Telemetry;
using EntityForge.Semantics.Protobuf;
using EntityForge.Query.Projection;
using EntityForge.Query.Querying;
using EntityForge.Semantics;
using EntityForge.Values;
using EntityForge.Db.Kernel;
using EntityForge.Db.Mapping;

namespace EntityForge.Db.Semantic.Engine;

// The queue owns FIFO selection; the commit gate owns validation, preparation and
// publication. The runner acquires the gate before selecting at most 16 independent
// writes. Group and single fallback paths borrow that ownership and never release it.
internal sealed partial class SemanticDatabaseEngine
{
    private ValueTask<SessionCommitResult> CommitConvenienceAsync(EntityForgeWriteTransaction write,
        Action<IReadOnlyList<SemanticEntityChange>> refresh, CancellationToken token,
        Func<EntityForgeWriteTransaction, SemanticOwnerCoverage>? ownerCoverage = null)
    {
        // A lone save has nothing to group with. Contended callers join the bounded queue;
        // the admission decision is still made under the commit gate in either path.
        lock (_orderedCommitGate)
        {
            if (!_orderedCommitRunner && _commitGate.Snapshot().ActivePublications == 0)
            {
                return CommitSingleCoreAsync(write, null, token, refresh, ownerCoverage);
            }
        }

        return EnqueueCommitAsync(write, null, token, refresh, ownerCoverage);
    }

    private async ValueTask<SessionCommitResult> EnqueueCommitAsync(
        EntityForgeWriteTransaction write, DatabaseVersion? authoritativeVersion,
        CancellationToken cancellationToken, Action<IReadOnlyList<SemanticEntityChange>>? refresh = null,
        Func<EntityForgeWriteTransaction, SemanticOwnerCoverage>? ownerCoverage = null)
    {
        ThrowIfDisposed();
        if (BeforeOrderedCommitAdmission is not null)
        {
            await BeforeOrderedCommitAdmission().ConfigureAwait(false);
        }

        var pending = new PendingOrderedCommit(write, authoritativeVersion, cancellationToken, refresh, ownerCoverage);
        bool run;
        lock (_orderedCommitGate)
        {
            ThrowIfDisposed();
            if (_orderedCommitRunner && _orderedCommits.Count >= _maximumOrderedCommitQueue)
            {
                _orderedCommitsRejected++;
                throw new EntityForgeCommitAdmissionException(_maximumOrderedCommitQueue);
            }
            _orderedCommits.Enqueue(pending);
            _orderedCommitsAdmitted++;
            run = !_orderedCommitRunner;
            if (run)
            {
                _orderedCommitRunner = true;
            }

            if (run)
            {
                Task runner = RunOrderedCommitsAsync();
                _orderedCommitTasks.Add(runner);
                _ = runner.ContinueWith(completed =>
                {
                    lock (_orderedCommitGate)
                    {
                        _orderedCommitTasks.Remove(completed);
                    }
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        return await pending.Completion.Task.ConfigureAwait(false);
    }

    private async Task RunOrderedCommitsAsync()
    {
        while (true)
        {
            await Task.Yield();
            if (BeforeOrderedCommitDrain is not null)
            {
                await BeforeOrderedCommitDrain().ConfigureAwait(false);
            }

            PendingOrderedCommit[] group = [];
            bool gateEntered = false;
            try
            {
                // Select only after admission: arrivals during a stalled publication
                // can join the same bounded FIFO prefix without an extra append round.
                await _commitGate.WaitAsync(_orderedCommitShutdown.Token).ConfigureAwait(false);
                gateEntered = true;
                lock (_orderedCommitGate)
                {
                    if (_orderedCommits.Count == 0)
                    {
                        _orderedCommitRunner = false;
                        return;
                    }
                    group = PartitionIndependent(_orderedCommits.Take(MaximumOrderedCommitGroupCount).ToArray())[0];
                    for (int index = 0; index < group.Length; index++)
                    {
                        _orderedCommits.Dequeue();
                    }
                }
                if (group.Length > 1)
                {
                    await CommitIndependentGroupUnderAdmissionAsync(group).ConfigureAwait(false);
                }
                else
                {
                    await CompleteSingleUnderAdmissionAsync(group[0]).ConfigureAwait(false);
                }
            }
            catch (Exception exception)
            {
                void Fail(PendingOrderedCommit pending)
                {
                    if (exception is OperationCanceledException && _orderedCommitShutdown.IsCancellationRequested)
                    {
                        pending.Completion.TrySetCanceled(_orderedCommitShutdown.Token);
                    }
                    else
                    {
                        pending.Completion.TrySetException(exception);
                    }
                }
                foreach (var pending in group)
                {
                    Fail(pending);
                }

                lock (_orderedCommitGate)
                {
                    while (_orderedCommits.TryDequeue(out var pending))
                    {
                        Fail(pending);
                    }

                    _orderedCommitRunner = false;
                }
                return;
            }
            finally
            {
                if (gateEntered)
                {
                    _commitGate.Release();
                }
            }
        }
    }

    private async Task CompleteSingleUnderAdmissionAsync(PendingOrderedCommit item)
    {
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                item.CancellationToken, _orderedCommitShutdown.Token);
            item.Completion.TrySetResult(await CommitSingleCoreAsync(
                item.Write, item.AuthoritativeVersion, linked.Token, item.Refresh, item.OwnerCoverage, admissionOwned: true).ConfigureAwait(false));
        }
        catch (Exception exception)
        {
            item.Completion.TrySetException(exception);
        }
    }

    // The owner validator runs again under commit admission, after source replacement
    // refresh. Its coverage is persisted in the same native commit as the data.
    internal async ValueTask<SessionCommitResult> ApplyOwnerMappedSessionAsync(
        SemanticSessionEnvelope session, Action<IReadOnlyList<SemanticEntityChange>> validateComplete,
        Func<SemanticDatabaseState, IReadOnlyList<SemanticEntityChange>, SemanticOwnerCoverage> validateOwner,
        CancellationToken token)
    {
        var write = await BeginWriteAsync(session, token);
        return await CommitConvenienceAsync(write, validateComplete, token,
            current => validateOwner(_state.Current, current.Changes));
    }

    private async Task CommitIndependentGroupUnderAdmissionAsync(PendingOrderedCommit[] group)
    {
        var accepted = new List<PendingOrderedCommit>(group.Length);
        var deferred = new List<PendingOrderedCommit>();
        try
        {
            foreach (PendingOrderedCommit pending in group)
            {
                try
                {
                    pending.CancellationToken.ThrowIfCancellationRequested();
                    if (pending.Refresh is not null)
                    {
                        pending.Write = await BeginWriteAsync(pending.Write.Session, pending.CancellationToken).ConfigureAwait(false);
                        pending.Refresh(pending.Write.Changes);
                    }
                    EntityForgeWriteTransaction write = pending.Write;
                    // Refresh may add a previous source-owned closure. Recheck independence.
                    if (!CanGroup(pending) || accepted.Any(item => Overlaps(item.Write, write)))
                    {
                        deferred.Add(pending);
                        continue;
                    }
                    if (_profileState.IsContributionRevisionManaged(write.Session.Start.Source))
                    {
                        DatabaseCommitReceipt? replay = await _log.ResolveReplayReceiptAsync(
                            DurableDatabaseCommit.ForSemanticSession(write.Session, write.IsInitialBulkLoad),
                            pending.CancellationToken).ConfigureAwait(false);
                        if (replay is not null)
                        {
                            pending.Completion.TrySetResult(Result(write.Session, replay));
                            continue;
                        }
                        RejectRevisionManagedOrdinaryWrite(write.Session.Start);
                    }
                    SemanticSourcePartitionGuard.Validate(_state.Current, write.Session.Start, write.Changes);
                    _router.Validate(write.Participants);
                    LocalShardTopology.RequireLocalParticipant(write.Participants);
                    await _transactionQueries.EnforceApplicableGovernanceAsync(write, pending.CancellationToken)
                        .ConfigureAwait(false);
                    SemanticSessionEnvelope session = write.Session;
                    if (_state.Current.AppliedSessions.TryGetValue(session.Start.SessionId,
                            out AppliedSession? applied))
                    {
                        if (applied.ContentHash != session.Close.ReplayHash)
                        {
                            throw new SemanticSessionValidationException(
                                "Session replay content differs from the durable session.");
                        }

                        DatabaseCommitReceipt replayReceipt = await _log.CommitWithReceiptAsync(
                            DurableDatabaseCommit.ForSemanticSession(session, write.IsInitialBulkLoad),
                            pending.CancellationToken).ConfigureAwait(false);
                        pending.Completion.TrySetResult(Result(session, replayReceipt));
                        continue;
                    }
                    ValidateSourceProgress(_state.Current, session.Start);
                    TransactionConflict? conflict;
                    try
                    {
                        conflict = _validation.Validate(write.ReadVersion,
                            write.OwnershipWrites, write.PredicateReads, write.PredicateWrites);
                    }
                    catch (ValidationHistoryUnavailableException exception)
                    {
                        throw new EntityForgeValidationHistoryUnavailableException(
                            exception.ReadVersion, exception.RecoveryFloor, exception);
                    }
                    if (conflict is not null)
                    {
                        throw new EntityForgeWriteConflictException(write.ReadVersion,
                            conflict.ConflictingVersion, MapConflictKind(conflict.Kind));
                    }
                    // Refresh, independence and ownership are checked under the same admission.
                    // Retain each member's owner evidence in its own durable payload.
                    pending.Commit = pending.OwnerCoverage is null
                        ? DurableDatabaseCommit.ForSemanticSession(session, write.IsInitialBulkLoad)
                        : DurableDatabaseCommit.ForSemanticTransaction([new(session, LocalOwnerCoverage: pending.OwnerCoverage(write))]);
                    _profileState.Prepare(session.Start.SessionId, write.OwnershipWrites,
                        write.PredicateReads.ToArray(), write.PredicateWrites);
                    accepted.Add(pending);
                }
                catch (OperationCanceledException) when (pending.CancellationToken.IsCancellationRequested)
                {
                    pending.Completion.TrySetCanceled(pending.CancellationToken);
                }
                catch (Exception exception)
                {
                    pending.Completion.TrySetException(exception);
                }
            }
            if (accepted.Count != 0)
            {
                _commitObserver.Reached(EntityForgeCommitStage.BeforeDurableCommit);
                IReadOnlyList<DatabaseCommitReceipt> receipts = accepted.Count == 1
                    ? [await _log.CommitWithReceiptAsync(accepted[0].Commit!, CancellationToken.None)
                        .ConfigureAwait(false)]
                    : await _log.CommitGroupWithReceiptsAsync(accepted.Select(item => item.Commit!).ToArray(),
                        _orderedCommitShutdown.Token).ConfigureAwait(false);
                for (int index = 0; index < accepted.Count; index++)
                {
                    PendingOrderedCommit pending = accepted[index];
                    if (!pending.Write.IsInitialBulkLoad)
                    {
                        _storageAdvisor.RecordWrites(pending.Write.Changes);
                    }

                    pending.Completion.TrySetResult(Result(pending.Write.Session, receipts[index]));
                }
            }
        }
        catch (EntityOperationGroupDurableException failure)
        {
            for (int index = 0; index < accepted.Count; index++)
            {
                var pending = accepted[index];
                var receipt = failure.Receipts[index];
                _profileState.CancelPreparation(pending.Write.Session.Start.SessionId);
                pending.Completion.TrySetException(new EntityOperationCommittedException<SessionCommitResult>(
                    Result(pending.Write.Session, receipt), receipt, failure));
            }
        }
        catch (EntityOperationCommittedException<DatabaseCommitReceipt> failure) when (accepted.Count == 1)
        {
            var pending = accepted[0];
            _profileState.CancelPreparation(pending.Write.Session.Start.SessionId);
            pending.Completion.TrySetException(new EntityOperationCommittedException<SessionCommitResult>(
                Result(pending.Write.Session, failure.Receipt), failure.NativeReceipt, failure));
        }
        catch (DatabaseCommitGroupPreparationException)
        {
            // The kernel has abandoned every staged member before signalling this failure.
            // An aggregate byte limit must not reject otherwise admissible individual writes.
            foreach (var pending in accepted)
            {
                _profileState.CancelPreparation(pending.Write.Session.Start.SessionId);
            }

            deferred.AddRange(accepted);
        }
        catch (Exception exception)
        {
            Exception reported = exception is EntityForgeDbException or OperationCanceledException
                ? exception
                : new EntityForgeDbCommitException("The semantic transaction group commit failed.", exception);
            foreach (PendingOrderedCommit pending in accepted)
            {
                _profileState.CancelPreparation(pending.Write.Session.Start.SessionId);
                if (reported is OperationCanceledException)
                {
                    pending.Completion.TrySetCanceled(_orderedCommitShutdown.Token);
                }
                else
                {
                    pending.Completion.TrySetException(reported);
                }
            }
            foreach (PendingOrderedCommit pending in group.Except(accepted)
                         .Where(pending => !pending.Completion.Task.IsCompleted && !deferred.Contains(pending)))
            {
                if (reported is OperationCanceledException)
                {
                    pending.Completion.TrySetCanceled(_orderedCommitShutdown.Token);
                }
                else
                {
                    pending.Completion.TrySetException(reported);
                }
            }
        }
        foreach (var pending in deferred)
        {
            await CompleteSingleUnderAdmissionAsync(pending).ConfigureAwait(false);
        }
    }

    private IReadOnlyList<PendingOrderedCommit[]> PartitionIndependent(PendingOrderedCommit[] pending)
    {
        var result = new List<PendingOrderedCommit[]>();
        var current = new List<PendingOrderedCommit>();
        foreach (PendingOrderedCommit candidate in pending)
        {
            if (current.Count == MaximumOrderedCommitGroupCount || candidate.AuthoritativeVersion is not null ||
                !CanGroup(candidate) ||
                current.Any(item => Overlaps(item.Write, candidate.Write)))
            {
                if (current.Count != 0)
                {
                    result.Add(current.ToArray());
                    current.Clear();
                }
                if (candidate.AuthoritativeVersion is not null || !CanGroup(candidate))
                {
                    result.Add([candidate]);
                    continue;
                }
            }
            current.Add(candidate);
        }
        if (current.Count != 0)
        {
            result.Add(current.ToArray());
        }

        return result;
    }

    // First bounded grouping capability: scalar state only. Existing relationships can
    // introduce target dependencies during replacement, so retain individual admission.
    private bool CanGroup(PendingOrderedCommit pending) =>
        _state.Current.AssertionStatistics.RelationshipCount == 0 &&
        pending.AuthoritativeVersion is null && !pending.Write.IsInitialBulkLoad &&
        (pending.Write.Session.Start.Kind is not SemanticChangeSessionKind.SourceSnapshot || pending.Refresh is not null) &&
        pending.Write.ResumableTransactionId is null && pending.Write.Governance.Rules.Count == 0 &&
        pending.Write.Changes.All(change => change.Relationships.Count == 0) &&
        pending.Write.PredicateReads.Count == 0;

    private static bool Overlaps(EntityForgeWriteTransaction left, EntityForgeWriteTransaction right) =>
        (left.Session.Start.Source.SourceSystem == right.Session.Start.Source.SourceSystem &&
         left.Session.Start.Source.SourcePartition == right.Session.Start.Source.SourcePartition) ||
        left.Changes.Select(change => change.Subject).Intersect(right.Changes.Select(change => change.Subject)).Any() ||
        left.OwnershipWrites.Intersect(right.OwnershipWrites).Any() ||
        left.PredicateReads.Any(leftRead => right.PredicateWrites.Any(leftRead.Overlaps)) ||
        right.PredicateReads.Any(rightRead => left.PredicateWrites.Any(rightRead.Overlaps));

    private static SessionCommitResult Result(SemanticSessionEnvelope session,
        DatabaseCommitReceipt receipt) => new(
        session.Start.SessionId,
        receipt.Version,
        receipt.Outcome is DatabaseCommitOutcome.IdempotentReplay
            ? SessionCommitOutcome.IdempotentReplay
            : SessionCommitOutcome.Applied,
        receipt.DatabaseIncarnation,
        receipt.TransactionId,
        receipt.ChangeCursor,
        receipt.EffectCount ?? throw new InvalidDataException("A native commit requires its effect count."),
        new SemanticSourceCommitProgress(session.Start.Source.SourceSystem,
            session.Start.Source.SourcePartition, session.Start.Source.Position, receipt.Version));

    private void RejectRevisionManagedOrdinaryWrite(SemanticChangeSessionStart start)
    {
        if (_profileState.IsContributionRevisionManaged(start.Source))
        {
            throw new SemanticContributionRevisionException(start.SessionId,
                "This source scope is revision-managed and cannot accept ordinary Semantic writes.");
        }
    }

    internal async ValueTask<SessionCommitResult?> ResolvePreparedReplayAsync(
        SemanticSessionEnvelope session, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            DatabaseCommitReceipt? receipt = await ResolveOrdinaryReplayAsync(session, false, cancellationToken).ConfigureAwait(false);
            return receipt is null ? null : Result(session, receipt);
        }
        catch (EntityForgeDbIdempotencyException exception)
        {
            throw new SemanticSessionValidationException(
                "Session replay content differs from the durable session.", exception);
        }
    }

    private async ValueTask<DatabaseCommitReceipt?> ResolveOrdinaryReplayAsync(
        SemanticSessionEnvelope session, bool isInitialBulkLoad, CancellationToken cancellationToken)
    {
        Validate(session);
        return await _log.ResolveReplayReceiptAsync(
            DurableDatabaseCommit.ForSemanticSession(session, isInitialBulkLoad), cancellationToken)
            .ConfigureAwait(false);
    }

    private static void ValidateSourceProgress(SemanticDatabaseState state,
        SemanticChangeSessionStart start)
    {
        SemanticSourceProgressPrecondition? precondition = start.ProgressPrecondition;
        if (precondition is null)
        {
            return;
        }

        state.SourceProgress.TryGetValue(new(start.Source.SourceSystem, start.Source.SourcePartition),
            out SourcePartitionProgress? actualProgress);
        SemanticSourcePosition? actual = actualProgress is null ? null : new(
            actualProgress.GenerationId, actualProgress.SourceVersion, actualProgress.SourcePosition);
        bool matches = precondition.Expectation switch
        {
            SemanticSourceProgressExpectation.NoPriorProgress => actual is null,
            SemanticSourceProgressExpectation.ExactPredecessor => actual == precondition.Predecessor,
            _ => false
        };
        if (!matches)
        {
            throw new SemanticSourceProgressConflictException(precondition, actual);
        }
    }

    private sealed class PendingOrderedCommit(
        EntityForgeWriteTransaction write, DatabaseVersion? authoritativeVersion,
        CancellationToken cancellationToken, Action<IReadOnlyList<SemanticEntityChange>>? refresh,
        Func<EntityForgeWriteTransaction, SemanticOwnerCoverage>? ownerCoverage)
    {
        public EntityForgeWriteTransaction Write { get; set; } = write;

        public Func<EntityForgeWriteTransaction, SemanticOwnerCoverage>? OwnerCoverage { get; } = ownerCoverage;

        public DurableDatabaseCommit? Commit
        {
            get; set;
        }

        public Action<IReadOnlyList<SemanticEntityChange>>? Refresh { get; } = refresh;

        public DatabaseVersion? AuthoritativeVersion { get; } = authoritativeVersion;

        public CancellationToken CancellationToken { get; } = cancellationToken;

        public TaskCompletionSource<SessionCommitResult> Completion
        {
            get;
        }
        =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

}
