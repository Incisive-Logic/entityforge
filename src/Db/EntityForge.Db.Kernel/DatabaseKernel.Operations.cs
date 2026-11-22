using EntityForge.Db.Mapping;

namespace EntityForge.Db.Kernel;

public sealed partial class DatabaseKernel
{
    async ValueTask<EntityOperationBinding?> IEntityOperationCompactionAuthority.ReadOperationAsync(
        Guid operationId, CancellationToken cancellationToken)
    {
        await _commitCoordinator.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureReady();
            return ((IEntityOperationCompactionProfile)_profile).OperationCatalog.Get(operationId);
        }
        finally
        {
            _commitCoordinator.Release();
        }
    }

    async ValueTask IEntityOperationCompactionAuthority.CompactCommittedAsync(Guid operationId,
        CancellationToken cancellationToken)
    {
        await _commitCoordinator.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureReady();
            // Evidence is derived and installed under the same admission. No caller-supplied
            // projection or intervening state change requires a second reconstruction/validation.
            RestoreCommittedOperationProjection(operationId);
        }
        finally
        {
            _commitCoordinator.Release();
        }
    }

    async ValueTask<EntityOperationBinding?> IEntityOperationCompactionAuthority.PrepareCommittedCompactionAsync(
        Guid operationId, CancellationToken cancellationToken)
    {
        await _commitCoordinator.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureReady();
            return PrepareCommittedCompactionCore(operationId);
        }
        finally
        {
            _commitCoordinator.Release();
        }
    }

    private EntityOperationBinding? PrepareCommittedCompactionCore(Guid operationId)
    {
        var compact = CreateCommittedProjection(operationId);
        if (compact is null)
        {
            return null;
        }

        var catalog = ((IEntityOperationCompactionProfile)_profile).OperationCatalog;
        if (EntityOperationCatalog.ReservedBytes(compact) > EntityOperationCatalog.ReservedBytes(catalog.Get(operationId)!))
        {
            return null;
        }

        catalog.Validate(compact);
        return compact;
    }

    private EntityOperationBinding? CreateCommittedProjection(Guid operationId, EntityOperationBoundCommit? owned = null)
    {
        if (_profile is not IEntityOperationCompactionProfile profile)
        {
            return null;
        }

        var binding = profile.OperationCatalog.Get(operationId);
        if (binding is null || binding.Aborted || binding.Committed is not null ||
            !_committedTransactions.TryGetValue(operationId, out var committed))
        {
            return null;
        }

        var receipt = Receipt(operationId, committed, OutcomeFor(committed.ChangeKind));
        EntityOperationCommitted terminal;
        if (owned is not null)
        {
            if (owned.Binding.OperationId != operationId || owned.Binding.RequestFingerprint != binding.RequestFingerprint ||
                owned.Binding.Submission != binding.Submission || !owned.Binding.Plan.AsSpan().SequenceEqual(binding.Plan))
            {
                throw new EntityForgeDbIdempotencyException(operationId);
            }

            terminal = ((IEntityOperationAtomicCommitProfile)profile).DescribeBoundCommitted(owned, receipt);
        }
        else
        {
            terminal = profile.DescribeCommitted(binding, receipt);
        }

        VerifyCommittedEvidence(terminal);
        return binding with
        {
            Plan = [],
            Committed = terminal
        };
    }

    async ValueTask IEntityOperationCompactionAuthority.CompleteCommittedCompactionAsync(
        EntityOperationBinding compact, CancellationToken cancellationToken)
    {
        await _commitCoordinator.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureReady();
            if (compact.Committed is null)
            {
                throw new ArgumentException("A committed projection is required.", nameof(compact));
            }

            ValidateCommittedTransition(compact);
            ((IEntityOperationCompactionProfile)_profile).OperationCatalog.Apply(compact);
        }
        finally
        {
            _commitCoordinator.Release();
        }
    }

    // Recreate the same compact projection after WAL/checkpoint recovery, including a crash before
    // in-memory compaction. The binding plus exact native receipt remain the durable authority.
    private void RestoreCommittedOperationProjections()
    {
        if (_profile is not IEntityOperationCompactionProfile profile)
        {
            return;
        }

        foreach (Guid id in profile.OperationCatalog.UncompactedIds())
        {
            RestoreCommittedOperationProjection(id);
        }
    }

    private void RestoreCommittedOperationProjection(Guid id, EntityOperationBoundCommit? owned = null)
    {
        if (CreateCommittedProjection(id, owned) is { } compact)
        {
            ((IEntityOperationCompactionProfile)_profile).OperationCatalog.TryApplyCommittedProjection(compact);
        }
    }

    async ValueTask IEntityOperationCompactionAuthority.VerifyReadableAsync(EntityOperationCommitted terminal,
        CancellationToken cancellationToken)
    {
        await _commitCoordinator.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureReady();
            VerifyCommittedEvidence(terminal);
        }
        finally
        {
            _commitCoordinator.Release();
        }
    }

    private void VerifyCommittedEvidence(EntityOperationCommitted terminal)
    {
        if (terminal.Receipt is null)
        {
            throw new InvalidDataException("Missing committed operation receipt.");
        }

        Guid id = terminal.Receipt.TransactionId;
        EntityOperationIds.RequireRetained(id, _localOperationFloor);
        if (!_committedTransactions.TryGetValue(id, out var committed) ||
            terminal.NativeCodecId != _profile.CodecId || terminal.NativeCodecVersion != _profile.CodecVersion ||
            terminal.NativeHash != Convert.ToHexString(committed.ContentHash) ||
            terminal.Receipt != Receipt(id, committed, OutcomeFor(committed.ChangeKind)))
        {
            throw new EntityForgeDbIdempotencyException(id);
        }
    }

    private void ValidateCommittedTransition(EntityOperationBinding transition)
    {
        var profile = (IEntityOperationCompactionProfile)_profile;
        VerifyCommittedEvidence(transition.Committed!);
        var prior = profile.OperationCatalog.Get(transition.OperationId)
            ?? throw new EntityForgeDbIdempotencyException(transition.OperationId);
        profile.OperationCatalog.Validate(transition);
        var expected = prior.Committed ?? profile.DescribeCommitted(prior, transition.Committed!.Receipt);
        if (!expected.Equivalent(transition.Committed!))
        {
            throw new EntityForgeDbIdempotencyException(transition.OperationId);
        }
    }
}
