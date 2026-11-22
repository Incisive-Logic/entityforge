using System.Security.Cryptography;

namespace EntityForge.Db.Mapping;

internal sealed class EntityOperationRegistry(EntityOperationCatalog catalog, Func<EntityOperationBinding, CancellationToken, ValueTask> persist, IEntityOperationCompactionAuthority? authority = null)
{
    internal Func<ValueTask>? AfterBindingPersisted
    {
        get; set;
    }

    internal Func<ValueTask>? AfterAbortPersisted
    {
        get; set;
    }

    internal Func<ValueTask>? AfterCompactionCompleted
    {
        get; set;
    }

    internal Func<ValueTask>? BeforeCompactionCompleted
    {
        get; set;
    }

    private async ValueTask PersistMetadataAsync(EntityOperationBinding binding, CancellationToken token)
    {
        try
        {
            await persist(binding, token).ConfigureAwait(false);
        }
        catch (EntityOperationCommittedException<DatabaseCommitReceipt> failure)
        {
            // Durable binding/abort metadata does not establish execution of the application write.
            throw new EntityForgeDbCommitException("Operation metadata is durable but its publication failed; resolve the original operation after recovery.", failure);
        }
    }

    private readonly object _coordination = new();
    private readonly Dictionary<Guid, OperationGate> _gates = [];

    private sealed class OperationGate
    {
        internal readonly SemaphoreSlim Semaphore = new(1, 1);
        internal int Users;
    }

    private async ValueTask<OperationGate> EnterAsync(Guid id, CancellationToken cancellationToken)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("An operation identity is required.", nameof(id));
        }
        OperationGate gate;
        lock (_coordination)
        {
            if (!_gates.TryGetValue(id, out gate!))
            {
                _gates.Add(id, gate = new());
            }            // Count waiting callers too: the semaphore must outlive every admitted reference.
            gate.Users++;
        }

        try
        {
            await gate.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            return gate;
        }
        catch
        {
            ReleaseReference(id, gate);
            throw;
        }
    }

    private void ReleaseReference(Guid id, OperationGate gate)
    {
        lock (_coordination)
        {
            if (--gate.Users != 0)
            {
                return;
            }
            _gates.Remove(id);
            gate.Semaphore.Dispose();
        }
    }

    private void Exit(Guid id, OperationGate gate)
    {
        gate.Semaphore.Release();
        ReleaseReference(id, gate);
    }

    internal int ActiveGateCount
    {
        get
        {
            lock (_coordination)
            {
                return _gates.Count;
            }
        }
    }

    internal async ValueTask<DatabaseCommitReceipt?> TryExecuteCapturedAsync(Guid operationId, ReadOnlyMemory<byte> request, Func<ValueTask<byte[]>> prepare, Func<EntityOperationBinding, CancellationToken, ValueTask<DatabaseCommitReceipt?>> commit, CancellationToken cancellationToken, Guid databaseIncarnation)
        => (await TryExecuteCapturedAsync(operationId, request, prepare, commit,
            terminal => terminal.ReplayReceipt(), (receipt, _) => receipt, cancellationToken, databaseIncarnation).ConfigureAwait(false))?.Value;

    internal sealed record CapturedExecution<T>(T Value);

    internal async ValueTask<CapturedExecution<T>?> TryExecuteCapturedAsync<T>(Guid operationId, ReadOnlyMemory<byte> request,
        Func<ValueTask<byte[]>> prepare, Func<EntityOperationBinding, CancellationToken, ValueTask<DatabaseCommitReceipt?>> commit,
        Func<EntityOperationCommitted, T> replay, Func<DatabaseCommitReceipt, byte[], T> result,
        CancellationToken cancellationToken, Guid databaseIncarnation)
    {
        // Fault observers require the independently observable binding/compaction boundaries.
        // Atomic-group failures are qualified at the WAL group boundary instead.
        if (AfterBindingPersisted is not null || BeforeCompactionCompleted is not null || AfterCompactionCompleted is not null)
        {
            return null;
        }
        string fingerprint = Convert.ToHexString(SHA256.HashData(request.Span));
        var gate = await EnterAsync(operationId, cancellationToken).ConfigureAwait(false);
        try
        {
            if (catalog.Get(operationId) is { } existing)
            {
                if (existing.Submission != EntityOperationSubmission.Captured || existing.RequestFingerprint != fingerprint)
                {
                    throw new EntityForgeDbIdempotencyException(operationId);
                }
                if (existing.Aborted)
                {
                    throw new EntityOperationAbortedException(operationId);
                }
                return existing.Committed is { } terminal
                    ? new(await ReadCommittedAsync(terminal, replay, cancellationToken).ConfigureAwait(false)) : null;
            }

            EntityOperationIds.RequireOwnerAdmissionTime(operationId);
            var binding = new EntityOperationBinding(operationId, fingerprint, await prepare().ConfigureAwait(false));
            catalog.Validate(binding);
            DatabaseCommitReceipt? receipt;
            try
            {
                receipt = await commit(binding, cancellationToken).ConfigureAwait(false);
                if (receipt is null)
                {
                    // Preserve the selected plan even when this provider/admission needs the ordinary path.
                    await PersistMetadataAsync(binding, cancellationToken).ConfigureAwait(false);
                    if (AfterBindingPersisted is not null)
                    {
                        await AfterBindingPersisted().ConfigureAwait(false);
                    }
                    return null;
                }
            }
            catch (Exception failure)
            {
                bool aborted = await TerminaliseOwnerFailureUnderGateAsync(operationId, failure).ConfigureAwait(false);
                EntityOperationSubmissionFailure.Attach(failure, operationId, databaseIncarnation);
                if (aborted)
                {
                    throw new EntityOperationAbortedException(operationId, failure);
                }
                throw;
            }

            T value = result(receipt, binding.Plan);
            try
            {
                await CompactUnderGateAsync(operationId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                throw new EntityOperationCommittedException<T>(value, receipt, failure);
            }

            return new(value);
        }
        finally
        {
            Exit(operationId, gate);
        }
    }

    internal async ValueTask<T> ExecutePreparedAsync<T>(Guid operationId, Func<byte[], bool> matches, Func<ValueTask<T>> execute, CancellationToken cancellationToken, byte[] preparedEnvelope, byte[] preparedIdentity, Func<EntityOperationCommitted, T>? replay = null, Func<T, DatabaseCommitReceipt>? nativeReceipt = null, Guid databaseIncarnation = default)
    {
        if (authority is not null && nativeReceipt is null)
        {
            throw new ArgumentException("Compacting execution requires a native receipt reader.", nameof(nativeReceipt));
        }
        var gate = await EnterAsync(operationId, cancellationToken).ConfigureAwait(false);
        bool submissionAttempted = false;
        bool nativeReturned = false;
        try
        {
            var binding = catalog.Get(operationId);
            if (binding?.Aborted == true)
            {
                throw new EntityOperationAbortedException(operationId);
            }
            if (binding?.Committed is { } terminal)
            {
                if (terminal.PreparedFingerprint != EntityOperationCommitted.Digest(preparedIdentity))
                {
                    throw new EntityForgeDbIdempotencyException(operationId);
                }
                return await ReadCommittedAsync(terminal, replay ?? throw new InvalidOperationException("Missing profile replay reader."), cancellationToken).ConfigureAwait(false);
            }

            if (binding is not null && (!matches(binding.Plan) || (binding.Submission == EntityOperationSubmission.Prepared && binding.RequestFingerprint != Convert.ToHexString(SHA256.HashData(preparedIdentity)))))
            {
                throw new EntityForgeDbIdempotencyException(operationId);
            }
            if (binding is null)
            {
                EntityOperationIds.RequireOwnerAdmissionTime(operationId);
                binding = new(operationId, Convert.ToHexString(SHA256.HashData(preparedIdentity!)), preparedEnvelope, EntityOperationSubmission.Prepared);
                catalog.Validate(binding);
                submissionAttempted = true;
                await PersistMetadataAsync(binding, cancellationToken).ConfigureAwait(false);
                if (AfterBindingPersisted is not null)
                {
                    await AfterBindingPersisted().ConfigureAwait(false);
                }
            }

            submissionAttempted = true;
            T result = await execute().ConfigureAwait(false);
            nativeReturned = true;
            try
            {
                await CompactUnderGateAsync(operationId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception failure) when (nativeReceipt is not null)
            {
                // Cancellation here cannot undo the result already returned by native execution.
                throw new EntityOperationCommittedException<T>(result, nativeReceipt(result), failure);
            }

            return result;
        }
        catch (Exception failure) when (submissionAttempted && !nativeReturned)
        {
            bool aborted = await TerminaliseOwnerFailureUnderGateAsync(operationId, failure).ConfigureAwait(false);
            EntityOperationSubmissionFailure.Attach(failure, operationId, databaseIncarnation);
            if (aborted)
            {
                throw new EntityOperationAbortedException(operationId, failure);
            }
            throw;
        }
        finally
        {
            Exit(operationId, gate);
        }
    }

    internal async ValueTask AbortAsync(Guid operationId, CancellationToken cancellationToken)
    {
        var gate = await EnterAsync(operationId, cancellationToken).ConfigureAwait(false);
        try
        {
            await AbortUnderGateAsync(operationId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Exit(operationId, gate);
        }
    }

    private async ValueTask<bool> TerminaliseOwnerFailureUnderGateAsync(Guid operationId, Exception failure)
    {
        if (!EntityOperationIds.IsOwnerAddressed(operationId) || authority is null)
        {
            return false;
        }
        try
        {
            // Kernel admission distinguishes definite noncommit from a catalogue
            // which may lag uncertain durable evidence. Never abort the latter.
            var observed = await authority.ReadOperationAsync(operationId, CancellationToken.None).ConfigureAwait(false);
            if (observed is { Aborted: false, Committed: null })
            {
                await AbortUnderGateAsync(operationId, CancellationToken.None).ConfigureAwait(false);
            }
            return catalog.Get(operationId)?.Aborted == true;
        }
        catch (Exception cleanup)
        {
            // The original failure and retained identity remain authoritative.
            // Recovery/maintenance owns any still-pending selection.
            failure.Data["EntityForge.OwnerTerminalisationFailure"] = cleanup;
            return false;
        }
    }

    internal async ValueTask ReconcileAbandonedOwnersAsync(long floor, CancellationToken token)
    {
        foreach (Guid id in catalog.UncompactedIds())
        {
            if (EntityOperationIds.IsOwnerAddressed(id) && EntityOperationIds.IsBefore(id, floor))
            {
                await AbortAsync(id, token).ConfigureAwait(false);
            }
        }
    }

    private async ValueTask AbortUnderGateAsync(Guid operationId, CancellationToken cancellationToken)
    {
        var binding = authority is null ? catalog.Get(operationId) : await authority.ReadOperationAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (binding is null || binding.Aborted || binding.Committed is not null)
        {
            return;
        }
        var aborted = binding with
        {
            Aborted = true
        };
        var compact = aborted with
        {
            Plan = [],
            AbortedPlanDigest = Convert.ToHexString(SHA256.HashData(binding.Plan))
        };
        // Compaction is part of the same durable abort decision and must fit its reservation.
        if (EntityOperationCodec.Encode(compact).Length < EntityOperationCodec.Encode(aborted).Length)
        {
            aborted = compact;
        }
        try
        {
            await PersistMetadataAsync(aborted, cancellationToken).ConfigureAwait(false);
        }
        catch (EntityForgeDbIdempotencyException exception) when (exception.TransactionId == operationId)
        {
            // The kernel observed a prior native commit. Resolution returns that winner.
            return;
        }

        if (AfterAbortPersisted is not null)
        {
            await AfterAbortPersisted().ConfigureAwait(false);
        }
    }

    internal async ValueTask<EntityOperationBinding?> FindAsync(Guid operationId, CancellationToken cancellationToken)
    {
        var gate = await EnterAsync(operationId, cancellationToken).ConfigureAwait(false);
        try
        {
            return authority is null ? catalog.Get(operationId) : await authority.ReadOperationAsync(operationId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Exit(operationId, gate);
        }
    }

    internal async ValueTask<T> ReadCommittedAsync<T>(EntityOperationCommitted terminal, Func<EntityOperationCommitted, T> read, CancellationToken cancellationToken)
    {
        if (authority is null)
        {
            throw new NotSupportedException("Committed operation authority is unavailable.");
        }
        await authority.VerifyReadableAsync(terminal, cancellationToken).ConfigureAwait(false);
        return read(terminal);
    }

    internal async ValueTask CompactAsync(Guid operationId, CancellationToken cancellationToken)
    {
        var gate = await EnterAsync(operationId, cancellationToken).ConfigureAwait(false);
        try
        {
            await CompactUnderGateAsync(operationId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Exit(operationId, gate);
        }
    }

    private async ValueTask CompactUnderGateAsync(Guid operationId, CancellationToken cancellationToken)
    {
        if (authority is null)
        {
            return;
        }
        if (BeforeCompactionCompleted is null && AfterCompactionCompleted is null)
        {
            // Bound commit completion already projected this record under kernel admission.
            if (catalog.Get(operationId)?.Committed is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return;
            }

            await authority.CompactCommittedAsync(operationId, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Fault observers explicitly request the separable preparation/completion boundary.
        var compact = await authority.PrepareCommittedCompactionAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (compact is null)
        {
            return;
        }
        if (BeforeCompactionCompleted is not null)
        {
            await BeforeCompactionCompleted().ConfigureAwait(false);
        }
        await authority.CompleteCommittedCompactionAsync(compact, cancellationToken).ConfigureAwait(false);
        if (AfterCompactionCompleted is not null)
        {
            await AfterCompactionCompleted().ConfigureAwait(false);
        }
    }

    internal ValueTask<EntityOperationBinding> GetOrBindOperationAsync(Guid operationId, ReadOnlyMemory<byte> request, Func<byte[]> prepare, CancellationToken cancellationToken, Guid databaseIncarnation = default) => GetOrBindOperationAsync(operationId, request, () => ValueTask.FromResult(prepare()), cancellationToken, databaseIncarnation);

    internal async ValueTask<EntityOperationBinding> GetOrBindOperationAsync(Guid operationId, ReadOnlyMemory<byte> request, Func<ValueTask<byte[]>> prepare, CancellationToken cancellationToken, Guid databaseIncarnation = default)
    {
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("An operation identity is required.", nameof(operationId));
        }
        string fingerprint = Convert.ToHexString(SHA256.HashData(request.Span));
        var gate = await EnterAsync(operationId, cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = catalog.Get(operationId);
            if (existing is not null)
            {
                if (existing.Submission != EntityOperationSubmission.Captured || existing.RequestFingerprint != fingerprint)
                {
                    throw new EntityForgeDbIdempotencyException(operationId);
                }
                if (existing.Aborted)
                {
                    throw new EntityOperationAbortedException(operationId);
                }
                return existing;
            }

            EntityOperationIds.RequireOwnerAdmissionTime(operationId);
            var binding = new EntityOperationBinding(operationId, fingerprint, await prepare().ConfigureAwait(false));
            catalog.Validate(binding);
            // The owning database must durably commit this binding before native execution begins.
            try
            {
                await PersistMetadataAsync(binding, cancellationToken).ConfigureAwait(false);
                if (AfterBindingPersisted is not null)
                {
                    await AfterBindingPersisted().ConfigureAwait(false);
                }
            }
            catch (Exception failure)
            {
                EntityOperationSubmissionFailure.Attach(failure, operationId, databaseIncarnation);
                throw;
            }

            return binding;
        }
        finally
        {
            Exit(operationId, gate);
        }
    }
}

// The kernel rejects binding an application identity already consumed by a native commit.
internal interface IEntityOperationBindingAdmission
{
    Guid? BindingIdentity(ReadOnlyMemory<byte> payload);

    Guid? OperationIdentity(Guid transactionId, ReadOnlyMemory<byte> payload);
}

internal interface IEntityOperationCatalogProvider
{
    EntityOperationCatalog OperationCatalog
    {
        get;
    }
}

internal interface IEntityOperationHost
{
    EntityOperationRegistry Operations
    {
        get;
    }
}

internal sealed class EntityOperationCatalog(int maximumRetainedBytes = EntityOperationCatalog.MaximumRetainedBytes)
{
    internal const int MaximumRetainedBytes = 64 * 1024 * 1024;
    private readonly int _maximumRetainedBytes = maximumRetainedBytes is > 0 and <= MaximumRetainedBytes ? maximumRetainedBytes : throw new ArgumentOutOfRangeException(nameof(maximumRetainedBytes));

    // Catalog-owned detached records have stable serialized size. Cache only that owned size,
    // never a caller-owned mutable binding or an unvalidated transition.
    private sealed record RetainedOperation(EntityOperationBinding Binding, long ReservedBytes);

    private readonly Dictionary<Guid, RetainedOperation> _operations = [];
    private readonly object _gate = new();
    private long _bytes;
    private long _floor;
    private bool _retentionFailed;

    internal void InstallFloor(long floor)
    {
        lock (_gate)
        {
            if (floor < _floor)
            {
                throw new InvalidDataException("Operation retention cannot move backwards.");
            }
            if (_operations.Keys.Any(id => EntityOperationIds.IsBefore(id, floor)))
            {
                throw new InvalidDataException("The retained operation catalog contradicts its expiry floor.");
            }
            _floor = floor;
        }
    }

    internal void RetentionFailed()
    {
        lock (_gate)
        {
            _retentionFailed = true;
        }
    }

    internal void PublishRetention(byte[] retained, long floor)
    {
        lock (_gate)
        {
            Restore(retained);
            InstallFloor(floor);
        }
    }

    private void RequireRetained(Guid id)
    {
        if (_retentionFailed)
        {
            throw new InvalidOperationException("Operation retention requires database recovery.");
        }
        EntityOperationIds.RequireRetained(id, _floor);
    }

    internal EntityOperationCatalog RetainedCopy(long floor)
    {
        lock (_gate)
        {
            var copy = new EntityOperationCatalog(_maximumRetainedBytes);
            var retained = new List<EntityOperationBinding>();
            foreach (var entry in _operations.Values)
            {
                var binding = entry.Binding;
                if (!EntityOperationIds.IsBefore(binding.OperationId, floor))
                {
                    retained.Add(binding);
                }
                else if (!binding.Aborted && binding.Committed is null)
                {
                    throw new InvalidOperationException("Undecided expired operations must be resolved before retention.");
                }
            }

            copy.Restore(EntityOperationCodec.EncodeCatalog(retained));
            return copy;
        }
    }

    internal long RetainedBytes
    {
        get
        {
            lock (_gate)
            {
                return _bytes;
            }
        }
    }

    // Reserve bounded terminal framing/receipt overhead while the full plan is still retained.
    internal const int TerminalReservationBytes = 4 * 1024;

    internal static long ReservedBytes(EntityOperationBinding value) => value.Committed is not null || value.AbortedPlanDigest is not null ? SerializedLength(value) : SerializedLength(value with { Aborted = false }) + TerminalReservationBytes;

    private static long SerializedLength(EntityOperationBinding value)
    {
        using var counter = new SerializedByteCounter();
        EntityOperationCodec.Write(counter, value);
        return counter.Length;
    }

    private sealed class SerializedByteCounter : Stream
    {
        private long _length;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => _length;

        public override long Position
        {
            get => _length; set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer) => _length = checked(_length + buffer.Length);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private static EntityOperationBinding Detach(EntityOperationBinding value) => value with
    {
        Plan = value.Plan.ToArray(),
        Committed = value.Committed is null ? null : value.Committed with
        {
            ProfileReceipt = value.Committed.ProfileReceipt.ToArray()
        }
    };

    internal EntityOperationBinding? Get(Guid id, bool operationIdentity = true)
    {
        lock (_gate)
        {
            // Internal metadata uses content-derived transaction IDs. Its actual operation
            // identity is checked separately during binding admission; preserve collision checks.
            if (operationIdentity)
            {
                RequireRetained(id);
            }
            else if (_retentionFailed)
            {
                throw new InvalidOperationException("Operation retention requires database recovery.");
            }
            return _operations.TryGetValue(id, out var value) ? Detach(value.Binding) : null;
        }
    }

    internal Guid[] UncompactedIds()
    {
        lock (_gate)
        {
            return _operations.Values.Select(entry => entry.Binding).Where(value => !value.Aborted && value.Committed is null).Select(value => value.OperationId).ToArray();
        }
    }

    private static bool Hash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private void ValidateShape(EntityOperationBinding value)
    {
        if (!Enum.IsDefined(value.Submission) || value.OperationId == Guid.Empty || !Hash(value.RequestFingerprint) || value.Plan is null)
        {
            throw new InvalidDataException("Invalid application operation binding.");
        }
        if (value.Committed is { } terminal)
        {
            if (value.Aborted || value.AbortedPlanDigest is not null || value.Plan.Length != 0 || terminal.FormatVersion != EntityOperationCommitted.CurrentFormat || !Hash(terminal.EnvelopeDigest) || !Hash(terminal.PreparedFingerprint) || !Hash(terminal.NativeHash) || string.IsNullOrWhiteSpace(terminal.NativeCodecId) || terminal.NativeCodecVersion <= 0 || terminal.Receipt is null || terminal.Receipt.TransactionId != value.OperationId || terminal.Receipt.Outcome == DatabaseCommitOutcome.IdempotentReplay || terminal.ProfileReceipt is null)
            {
                throw new InvalidDataException("Invalid committed operation record.");
            }
        }
        else if (value.AbortedPlanDigest is null ? value.Plan.Length == 0 : !value.Aborted || value.Plan.Length != 0 || !Hash(value.AbortedPlanDigest))
        {
            throw new InvalidDataException("Invalid application operation plan.");
        }
    }

    internal void Validate(EntityOperationBinding value)
    {
        ValidateShape(value);
        Validate(value, ReservedBytes(value));
    }

    private void Validate(EntityOperationBinding value, long reservedBytes)
    {
        lock (_gate)
        {
            RequireRetained(value.OperationId);
            if (_operations.TryGetValue(value.OperationId, out var retainedOperation))
            {
                var prior = retainedOperation.Binding;
                if (prior.Aborted && !value.Aborted)
                {
                    throw new EntityOperationAbortedException(value.OperationId);
                }
                if (prior.Submission != value.Submission || prior.RequestFingerprint != value.RequestFingerprint)
                {
                    throw new EntityForgeDbIdempotencyException(value.OperationId);
                }
                if (prior.Committed is { } retained)
                {
                    if (value.Committed is null || !retained.Equivalent(value.Committed))
                    {
                        throw new EntityForgeDbIdempotencyException(value.OperationId);
                    }
                }
                else if (value.Committed is { } committed)
                {
                    if (prior.Aborted || committed.EnvelopeDigest != EntityOperationCommitted.Digest(prior.Plan))
                    {
                        throw new EntityForgeDbIdempotencyException(value.OperationId);
                    }
                }
                else if (value.AbortedPlanDigest is not null ? value.AbortedPlanDigest != (prior.AbortedPlanDigest ?? EntityOperationCommitted.Digest(prior.Plan)) : prior.AbortedPlanDigest is not null || !prior.Plan.AsSpan().SequenceEqual(value.Plan))
                {
                    throw new EntityForgeDbIdempotencyException(value.OperationId);
                }
                if (reservedBytes > retainedOperation.ReservedBytes)
                {
                    throw new InvalidOperationException("Terminal transition must fit retained capacity.");
                }
            }
            else if (value.Aborted || value.Committed is not null)
            {
                throw new InvalidOperationException("Terminal transition requires an existing operation binding.");
            }
            else if (_bytes + reservedBytes > _maximumRetainedBytes)
            {
                throw new InvalidOperationException("The retained application operation capacity is exhausted.");
            }
        }
    }

    // Called under kernel commit admission; pending additions must fit together before WAL submission.
    internal void ValidateInitialGroup(IReadOnlyList<EntityOperationBinding> values) => ValidateInitialGroup(values.Select(value => (value, ReservedBytes(value))));

    internal void ValidateInitialGroup(IReadOnlyList<EntityOperationBoundCommit> values) => ValidateInitialGroup(values.Select(value => (value.Binding, value.ReservedBytes)));

    private void ValidateInitialGroup(IEnumerable<(EntityOperationBinding Binding, long Reserved)> values)
    {
        lock (_gate)
        {
            long bytes = _bytes;
            var ids = new HashSet<Guid>();
            foreach (var (value, reservedBytes) in values)
            {
                ValidateShape(value);
                Validate(value, reservedBytes);
                if (value.Aborted || value.Committed is not null || value.AbortedPlanDigest is not null || !ids.Add(value.OperationId) || _operations.ContainsKey(value.OperationId))
                {
                    throw new EntityForgeDbIdempotencyException(value.OperationId);
                }
                bytes = checked(bytes + reservedBytes);
                if (bytes > _maximumRetainedBytes)
                {
                    throw new InvalidOperationException("The retained application operation capacity is exhausted.");
                }
            }
        }
    }

    // The kernel has already verified native commitment. Size this detached projection once;
    // catalog-owned initial reservations remain the authority for the bounded transition.
    internal bool TryApplyCommittedProjection(EntityOperationBinding value)
    {
        if (value.Committed is null)
        {
            throw new ArgumentException("A committed projection is required.", nameof(value));
        }
        var detached = Detach(value);
        long reserved = ReservedBytes(detached);
        lock (_gate)
        {
            RequireRetained(value.OperationId);
            if (!_operations.TryGetValue(value.OperationId, out var prior))
            {
                throw new InvalidOperationException("A projection requires an existing binding.");
            }
            if (reserved > prior.ReservedBytes)
            {
                return false;
            }
            ValidateShape(detached);
            Validate(detached, reserved);
            _operations[value.OperationId] = new(detached, reserved);
            _bytes = checked(_bytes - prior.ReservedBytes + reserved);
            return true;
        }
    }

    internal void Apply(EntityOperationBinding value)
    {
        lock (_gate)
        {
            ValidateShape(value);
            var detached = Detach(value);
            long reservedBytes = ReservedBytes(detached);
            Validate(detached, reservedBytes);
            if (_operations.TryGetValue(value.OperationId, out var prior))
            {
                _bytes -= prior.ReservedBytes;
            }
            _operations[value.OperationId] = new(detached, reservedBytes);
            _bytes += reservedBytes;
        }
    }

    internal byte[] Capture()
    {
        lock (_gate)
        {
            return EntityOperationCodec.EncodeCatalog(_operations.Values.Select(entry => entry.Binding));
        }
    }

    internal void Restore(ReadOnlyMemory<byte> bytes)
    {
        var values = EntityOperationCodec.DecodeCatalog(bytes.Span);
        var restored = new EntityOperationCatalog(_maximumRetainedBytes);
        foreach (var value in values)
        {
            if (value is null || restored._operations.ContainsKey(value.OperationId))
            {
                throw new InvalidDataException("Duplicate or null operation binding.");
            }
            if (value.AbortedPlanDigest is not null || value.Committed is not null)
            {
                // Checkpoints contain published terminal evidence; transition validation still
                // requires the predecessor on the live/WAL path.
                restored.ValidateShape(value);
                var detached = Detach(value);
                long reservedBytes = ReservedBytes(detached);
                restored._operations.Add(value.OperationId, new(detached, reservedBytes));
                restored._bytes += reservedBytes;
                if (restored._bytes > _maximumRetainedBytes)
                {
                    throw new InvalidDataException("Oversized operation checkpoint.");
                }
            }
            else
            {
                restored.Apply(value with
                {
                    Aborted = false
                });
                if (value.Aborted)
                {
                    restored.Apply(value);
                }
            }
        }

        lock (_gate)
        {
            _operations.Clear();
            foreach (var pair in restored._operations)
            {
                _operations.Add(pair.Key, pair.Value);
            }
            _bytes = restored._bytes;
        }
    }
}
