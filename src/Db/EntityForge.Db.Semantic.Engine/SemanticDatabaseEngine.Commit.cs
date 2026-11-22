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

internal sealed partial class SemanticDatabaseEngine
{
    private async ValueTask<SessionCommitResult> CommitSingleCoreAsync(
        EntityForgeWriteTransaction write,
        DatabaseVersion? authoritativeVersion,
        CancellationToken cancellationToken,
        Action<IReadOnlyList<SemanticEntityChange>>? validateCompleteChanges = null,
        Func<EntityForgeWriteTransaction, SemanticOwnerCoverage>? ownerCoverage = null,
        bool admissionOwned = false)
    {
        using EntityForgeDbOperation operation = _telemetry.StartOperation("db.commit", write.Session.Start.SessionId);
        bool gateEntered = false;
        bool durable = false;
        bool preparationOwned = false;
        SessionCommitResult? committedResult = null;
        DatabaseCommitReceipt? committedReceipt = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!admissionOwned)
            {
                await _commitGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                gateEntered = true;
            }
            if (_profileState.IsContributionRevisionManaged(write.Session.Start.Source))
            {
                DurableDatabaseCommit replayCommit = DurableDatabaseCommit.ForSemanticSession(
                    write.Session, write.IsInitialBulkLoad,
                    usesAuthoritativeVersion: authoritativeVersion is not null);
                DatabaseCommitReceipt? replay = await _log.ResolveReplayReceiptAsync(
                    replayCommit, cancellationToken).ConfigureAwait(false);
                if (replay is not null)
                {
                    if (authoritativeVersion is not null && replay.Version != authoritativeVersion.Value)
                    {
                        throw new SemanticSessionValidationException(
                            "Session replay version differs from the authoritative commit version.");
                    }

                    committedReceipt = replay;
                    committedResult = Result(write.Session, replay);
                    if (write.ResumableTransactionId is not null)
                    {
                        await _resumableWrites.CloseAsync(write.ResumableTransactionId.Value).ConfigureAwait(false);
                    }

                    operation.SetOutcome("idempotent_replay");
                    return committedResult;
                }
                RejectRevisionManagedOrdinaryWrite(write.Session.Start);
            }
            if (validateCompleteChanges is not null)
            {
                // Complete source-snapshot closure is revalidated under the same local
                // admission that publishes it. The session's exact predecessor still fences writers.
                write = await BeginWriteAsync(write.Session, cancellationToken).ConfigureAwait(false);
                validateCompleteChanges(write.Changes);
            }
            SemanticSourcePartitionGuard.Validate(_state.Current, write.Session.Start, write.Changes);
            _router.Validate(write.Participants);
            LocalShardTopology.RequireLocalParticipant(write.Participants);
            SemanticSessionEnvelope session = write.Session;
            await _transactionQueries.EnforceApplicableGovernanceAsync(write, cancellationToken)
                .ConfigureAwait(false);
            if (_state.Current.AppliedSessions.TryGetValue(session.Start.SessionId, out AppliedSession? applied))
            {
                if (applied.ContentHash != session.Close.ReplayHash)
                {
                    throw new SemanticSessionValidationException("Session replay content differs from the durable session.");
                }

                if (authoritativeVersion is not null && applied.Version != authoritativeVersion.Value)
                {
                    throw new SemanticSessionValidationException(
                        "Session replay version differs from the authoritative commit version.");
                }

                operation.SetOutcome("idempotent_replay");
                DatabaseCommitReceipt replayReceipt = authoritativeVersion is null
                    ? await _log.CommitWithReceiptAsync(DurableDatabaseCommit.ForSemanticSession(
                        session, write.IsInitialBulkLoad), cancellationToken).ConfigureAwait(false)
                    : await _log.ApplyAuthoritativeCommitWithReceiptAsync(
                        DurableDatabaseCommit.ForSemanticSession(session, write.IsInitialBulkLoad,
                            usesAuthoritativeVersion: true), authoritativeVersion.Value, cancellationToken)
                        .ConfigureAwait(false);
                committedReceipt = replayReceipt;
                committedResult = Result(session, replayReceipt);
                if (write.ResumableTransactionId is not null)
                {
                    await _resumableWrites.CloseAsync(write.ResumableTransactionId.Value).ConfigureAwait(false);
                }

                return committedResult;
            }

            ValidateSingleCommitConflicts(write, operation);

            _profileState.Prepare(session.Start.SessionId, write.OwnershipWrites, write.PredicateReads.ToArray(),
                write.PredicateWrites);
            preparationOwned = true;
            _commitObserver.Reached(EntityForgeCommitStage.BeforeDurableCommit);
            DurableDatabaseCommit commit = ownerCoverage is null
                ? DurableDatabaseCommit.ForSemanticSession(session, write.IsInitialBulkLoad,
                    usesAuthoritativeVersion: authoritativeVersion is not null)
                : DurableDatabaseCommit.ForSemanticTransaction([new(session, LocalOwnerCoverage: ownerCoverage(write))]);
            DatabaseCommitReceipt receipt = authoritativeVersion is null
                ? await _log.CommitWithReceiptAsync(commit, cancellationToken).ConfigureAwait(false)
                : await _log.ApplyAuthoritativeCommitWithReceiptAsync(commit, authoritativeVersion.Value, cancellationToken)
                    .ConfigureAwait(false);
            durable = true;
            committedReceipt = receipt;
            committedResult = Result(session, receipt);
            if (!write.IsInitialBulkLoad)
            {
                _storageAdvisor.RecordWrites(write.Changes);
            }

            if (session.Start.Kind is SemanticChangeSessionKind.SourceSnapshot)
            {
                long observed = session.Pages.Sum(page => page.Changes.Count);
                _telemetry.RecordSourceSnapshot(observed, write.Changes.Count - observed);
            }
            if (write.ResumableTransactionId is not null)
            {
                await _resumableWrites.CloseAsync(write.ResumableTransactionId.Value).ConfigureAwait(false);
            }

            operation.SetOutcome("applied");
            return committedResult;
        }
        catch (Exception failure) when (committedResult is not null)
        {
            throw new EntityOperationCommittedException<SessionCommitResult>(committedResult, committedReceipt!, failure);
        }
        catch (EntityOperationCommittedException<DatabaseCommitReceipt> failure)
        {
            throw new EntityOperationCommittedException<SessionCommitResult>(
                Result(write.Session, failure.Receipt), failure.NativeReceipt, failure);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            operation.SetOutcome("cancelled");
            throw;
        }
        catch (Exception exception)
        {
            operation.RecordException(exception, exception is EntityForgeWriteConflictException ? "conflict" : "error");
            if (exception is EntityForgeDbException or SemanticContributionRevisionException)
            {
                throw;
            }

            throw new EntityForgeDbCommitException("The semantic transaction commit failed.", exception);
        }
        finally
        {
            // Only this admitted attempt may discharge its preparation, including cancellation.
            // A rejected duplicate must never remove another attempt's entry by session ID.
            if (preparationOwned && !durable)
            {
                _profileState.CancelPreparation(write.Session.Start.SessionId);
            }

            if (gateEntered)
            {
                _commitGate.Release();
            }
        }
    }

    private void ValidateSingleCommitConflicts(EntityForgeWriteTransaction write, EntityForgeDbOperation operation)
    {
        SemanticSessionEnvelope session = write.Session;
        ValidateSourceProgress(_state.Current, session.Start);

        TransactionConflict? conflict;
        try
        {
            conflict = _validation.Validate(write.ReadVersion, write.OwnershipWrites,
                write.PredicateReads, write.PredicateWrites);
        }
        catch (ValidationHistoryUnavailableException exception)
        {
            throw new EntityForgeValidationHistoryUnavailableException(
                exception.ReadVersion, exception.RecoveryFloor, exception);
        }
        if (conflict is not null)
        {
            operation.SetOutcome("conflict");
            throw new EntityForgeWriteConflictException(
                write.ReadVersion, conflict.ConflictingVersion, MapConflictKind(conflict.Kind));
        }

        if (session.Start.Kind is SemanticChangeSessionKind.SourceSnapshot &&
            write.ReadVersion != _state.Current.Version)
        {
            operation.SetOutcome("conflict");
            throw new EntityForgeWriteConflictException(write.ReadVersion, _state.Current.Version,
                EntityForgeConflictKind.ReadAfterWrite);
        }

    }
}
