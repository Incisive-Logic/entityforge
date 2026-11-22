using System.Diagnostics;
using System.Security.Cryptography;
using EntityForge.Db.Mapping;
using EntityForge.Db.Storage;

namespace EntityForge.Db.Kernel;

public sealed partial class DatabaseKernel
{
    async ValueTask<IReadOnlyList<DatabaseCommitReceipt>> IEntityOperationAtomicCommitAuthority.CommitBoundGroupAsync(
        IReadOnlyList<EntityOperationBoundCommit> requests, CancellationToken cancellationToken)
    {
        long encodingStarted = Stopwatch.GetTimestamp();
        long encodingAllocated = GC.GetTotalAllocatedBytes();
        var records = requests.SelectMany(value => new DatabaseCommitRequest[]
        {
            new(value.BindingTransactionId, value.BindingRecord),
            new(value.Binding.OperationId, value.Payload)
        }).ToArray();
        RecordPhase("bound_record_encoding", encodingStarted, encodingAllocated);
        if (_telemetry.MeasureCommitPhases)
        {
            foreach (var request in requests)
            {
                _telemetry.BoundRecordBytes(_profile.Profile, "binding", request.BindingRecord.Length);
                _telemetry.BoundRecordBytes(_profile.Profile, "native", request.Payload.Length);
            }
        }

        try
        {
            var receipts = await CommitGroupCoreAsync(records, requests, cancellationToken).ConfigureAwait(false);
            return receipts.Where((_, index) => index % 2 == 1).ToArray();
        }
        catch (EntityOperationGroupDurableException failure)
        {
            throw new EntityOperationGroupDurableException(
                failure.Receipts.Where((_, index) => index % 2 == 1).ToArray(), failure);
        }
        catch (EntityOperationGroupCompletionException failure)
        {
            throw new EntityOperationGroupCompletionException(
                failure.Receipts.Where((_, index) => index % 2 == 1).ToArray(), failure.Failures);
        }
    }

    async ValueTask<DatabaseCommitReceipt> IEntityOperationAtomicCommitAuthority.CommitBoundAsync(
        EntityOperationBoundCommit request, CancellationToken cancellationToken)
    {
        var binding = request.Binding;
        var nativePayload = request.Payload;
        if (_commitLog is not IGroupedPhysicalDatabaseCommitLog log ||
            _profile is not IEntityOperationAtomicCommitProfile profile)
        {
            throw new NotSupportedException("This profile cannot atomically submit an application binding and native commit.");
        }

        if (binding.Aborted || binding.Committed is not null || binding.AbortedPlanDigest is not null)
        {
            throw new ArgumentException("Only an initial operation binding can accompany native execution.", nameof(binding));
        }

        var metadata = request.Metadata;
        EntityRecordDeliveryCodec.Validate(metadata, _profile.Profile, _profile.CodecVersion);
        if (metadata.OperationId != binding.OperationId || metadata.DatabaseIncarnation != _databaseIncarnation)
        {
            throw new EntityForgeDbIdempotencyException(binding.OperationId);
        }

        await EnterLocalWriteAuthorityAsync(cancellationToken, allowReservations: true).ConfigureAwait(false);
        try
        {
            await _commitCoordinator.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _localWriteAuthority.Release();
            throw;
        }
        long started = Stopwatch.GetTimestamp();
        PreparedKernelCommit? native = null;
        bool bindingDurable = false;
        bool bindingPublished = false;
        bool nativeDurable = false;
        bool nativePublished = false;
        DatabaseCommitReceipt? durableReceipt = null;
        Exception? primary = null;
        try
        {
            EnsureReady();
            Guid bindingId = request.BindingTransactionId;
            if (profile.OperationCatalog.Get(binding.OperationId) is not null ||
                _committedTransactions.ContainsKey(binding.OperationId) ||
                _committedTransactions.ContainsKey(bindingId) || bindingId == binding.OperationId)
            {
                throw new EntityForgeDbIdempotencyException(binding.OperationId);
            }

            PreparedKernelCommit initial = await PlanCommitAsync(bindingId, request.BindingRecord,
                new(checked(_durableVersion + 1)), cancellationToken).ConfigureAwait(false);
            try
            {
                profile.ValidateBoundPayload(metadata, nativePayload);
                native = await PlanCommitAsync(binding.OperationId, nativePayload,
                    new(checked(initial.Version.Value + 1)), cancellationToken).ConfigureAwait(false);
                native = await PreparePhysicalMutationsAsync(native, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception admissionFailure)
            {
                // Reject the native write without losing its selected application plan. No native
                // publication or log submission has occurred. Cleanup must succeed first.
                if (_profile is IDatabaseProfilePhysicalMutationPlanner planner)
                {
                    try
                    {
                        await planner.AbandonPhysicalMutationsAsync(binding.OperationId, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception cleanup)
                    {
                        RequireRecovery("bound_preparation_cleanup");
                        throw new AggregateException(admissionFailure, cleanup);
                    }
                }
                native = null;
                var append = await AppendDurablyAsync(initial, CancellationToken.None).ConfigureAwait(false);
                bindingDurable = true;
                RecordPhase(DurableAppendPhase, append.Started, append.Allocated);
                await PublishBoundMemberAsync(initial, started).ConfigureAwait(false);
                bindingPublished = true;
                throw;
            }

            durableReceipt = PreparedReceipt(native);
            DatabasePhysicalCommit[] group = [new(initial.Transaction, []), new(native.Transaction, native.PhysicalMutations)];
            bool fits = true;
            try
            {
                log.ValidateGroup(group);
            }
            catch (ArgumentOutOfRangeException)
            {
                fits = false;
            }
            if (fits)
            {
                long appendStarted = Stopwatch.GetTimestamp();
                long allocated = GC.GetTotalAllocatedBytes();
                bool readWindow = OpenAppendReadWindow([initial, native]);
                try
                {
                    await log.CommitGroupAsync(group, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    if (readWindow)
                    {
                        CloseAppendReadWindow();
                    }
                }
                bindingDurable = nativeDurable = true;
                RecordPhase(DurableAppendPhase, appendStarted, allocated);
            }
            else
            {
                // Individually valid large records still use the original two durable submissions.
                var append = await AppendDurablyAsync(initial, cancellationToken).ConfigureAwait(false);
                bindingDurable = true;
                RecordPhase(DurableAppendPhase, append.Started, append.Allocated);
            }
            await PublishBoundMemberAsync(initial, started).ConfigureAwait(false);
            bindingPublished = true;
            if (!nativeDurable)
            {
                var append = await AppendDurablyAsync(native, cancellationToken).ConfigureAwait(false);
                nativeDurable = true;
                RecordPhase(DurableAppendPhase, append.Started, append.Allocated);
            }
            DatabaseCommitReceipt receipt = await PublishBoundMemberAsync(native, started).ConfigureAwait(false);
            nativePublished = true;
            try
            {
                RestoreCommittedOperationProjection(binding.OperationId, request);
            }
            catch (Exception failure)
            {
                throw new EntityOperationCommittedException<DatabaseCommitReceipt>(receipt, receipt, failure);
            }
            return receipt;
        }
        catch (Exception failure)
        {
            if (failure is DatabaseCommitLogRecoveryRequiredException ||
                (bindingDurable && !bindingPublished) || (nativeDurable && !nativePublished))
            {
                RequireRecovery("bound_commit_publication");
            }

            primary = nativeDurable && failure is not EntityOperationCommittedException<DatabaseCommitReceipt>
                ? new EntityOperationCommittedException<DatabaseCommitReceipt>(durableReceipt!, durableReceipt!, failure)
                : failure is EntityForgeDbException or OperationCanceledException or EntityOperationExpiredException
                ? failure : new EntityForgeDbCommitException("The database could not complete the bound native commit.", failure);
            if (ReferenceEquals(primary, failure))
            {
                throw;
            }

            throw primary;
        }
        finally
        {
            AbortReaderPublicationIfRequired();
            Exception? cleanup = null;
            if (!nativePublished && native?.PhysicalPlanner is { } planner)
            {
                try
                {
                    await planner.AbandonPhysicalMutationsAsync(binding.OperationId, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception failure)
                {
                    RequireRecovery("bound_commit_cleanup");
                    cleanup = failure;
                }
            }
            _commitCoordinator.Release();
            _localWriteAuthority.Release();
            if (cleanup is not null)
            {
                if (primary is null)
                {
                    throw cleanup;
                }

                primary.Data["EntityForge.BoundCommitCleanupFailure"] = cleanup;
            }
        }
    }

    private async ValueTask<DatabaseCommitReceipt> PublishBoundMemberAsync(PreparedKernelCommit prepared, long started)
    {
        var committed = RegisterDurableCommit(prepared, SHA256.HashData(prepared.Transaction.Payload.Content.Span));
        await MaterializeCommittedChangesAsync(prepared).ConfigureAwait(false);
        BeginReaderPublication();
        await PublishCommitAsync(prepared).ConfigureAwait(false);
        Interlocked.Exchange(ref _version, prepared.Version.Value);
        CompleteReaderPublication();
        _telemetry.Commit(_profile.Profile, Stopwatch.GetElapsedTime(started));
        return Receipt(prepared.Transaction.TransactionId, committed, OutcomeFor(prepared.ChangeKind));
    }
}
