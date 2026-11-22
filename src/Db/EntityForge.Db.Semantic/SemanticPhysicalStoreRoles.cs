using EntityForge.Db.Storage;
using EntityForge.Db.Storage.Pages;
using EntityForge.Db.Semantic.Durability;
using EntityForge.Semantics;

namespace EntityForge.Db.Semantic;

internal sealed class SemanticPhysicalProjectionStore(SemanticPhysicalAccessPath store)
{
    public SemanticSourceMetadataReference SourceMetadataReference(DatabaseVersion version, int memberships, int progress) =>
        store.SourceMetadataReference(version, memberships, progress);

    public void ValidateSourceMetadataReference(SemanticSourceMetadataReference reference) =>
        store.ValidateSourceMetadataReference(reference);

    public ValueTask<SemanticSourceMetadataImage> ReadSourceMetadataAsync(long maximumVersion, CancellationToken token) =>
        store.ReadSourceMetadataAsync(maximumVersion, token);

    public ValueTask<OrderedPageTreeStage?> PrepareAsync(Guid transactionId, long version,
        SemanticCommittedEffects effects, PropertyStorageCatalogCheckpoint storage,
        CancellationToken cancellationToken, IReadOnlyList<(long Version, SemanticCommittedEffects Effects)>? group = null,
        SemanticSourceMetadataChanges? sourceMetadata = null,
        IReadOnlyList<(long Version, SemanticSourceMetadataChanges Changes)>? sourceMetadataGroup = null) =>
        store.PrepareAsync(transactionId, version, effects, storage, cancellationToken, group, sourceMetadata, sourceMetadataGroup);

    public ValueTask<OrderedPageTreeStage?> PreparePropertyStorageChangeAsync(Guid transactionId,
        long version, SemanticPropertyStorageChange change, CancellationToken cancellationToken) =>
        store.PreparePropertyStorageChangeAsync(transactionId, version, change, cancellationToken);

    public ValueTask<OrderedPageTreeStage?> PrepareMaintenanceAsync(Guid transactionId,
        DatabaseVersion retentionFloor, CancellationToken cancellationToken) => store.PrepareMaintenanceAsync(transactionId, retentionFloor, cancellationToken);

    public ValueTask<OrderedPageTreeStage?> PrepareVacuumAsync(Guid transactionId,
        PhysicalMaintenanceTarget target, DatabaseVersion retentionFloor, CancellationToken cancellationToken) =>
        store.PrepareVacuumAsync(transactionId, target, retentionFloor, cancellationToken);

    public ValueTask BuildOrderedReplacementAsync(Guid replacementId, DatabaseVersion retentionFloor,
        CancellationToken cancellationToken) =>
        store.BuildOrderedReplacementAsync(replacementId, retentionFloor, cancellationToken);

    public ValueTask<OrderedPageTreeStage> PrepareOrderedReplacementAsync(Guid transactionId,
        Guid replacementId, CancellationToken cancellationToken) =>
        store.PrepareOrderedReplacementAsync(transactionId, replacementId, cancellationToken);

    public void AbandonOrderedReplacement(Guid replacementId) => store.AbandonOrderedReplacement(replacementId);

    public ValueTask BuildAppendReplacementAsync(PhysicalMaintenanceTarget target, Guid replacementId,
        DatabaseVersion retentionFloor, CancellationToken cancellationToken) =>
        store.BuildAppendReplacementAsync(target, replacementId, retentionFloor, cancellationToken);

    public ValueTask CatchUpReplacementAsync(PhysicalMaintenanceTarget target, Guid replacementId,
        DatabaseVersion retentionFloor, CancellationToken cancellationToken) =>
        store.CatchUpReplacementAsync(target, replacementId, retentionFloor, cancellationToken);

    public ValueTask<OrderedPageTreeStage> PrepareAppendReplacementAsync(Guid transactionId,
        Guid replacementId, CancellationToken cancellationToken) =>
        store.PrepareAppendReplacementAsync(transactionId, replacementId, cancellationToken);

    public void AbandonAppendReplacement(Guid replacementId) => store.AbandonAppendReplacement(replacementId);

    public ValueTask<OrderedPageTreeStage> PrepareReplacementBatchAsync(Guid transactionId,
        IReadOnlyList<(PhysicalMaintenanceTarget Target, Guid ReplacementId)> replacements,
        CancellationToken cancellationToken) =>
        store.PrepareReplacementBatchAsync(transactionId, replacements, cancellationToken);

    public void AbandonReplacement(PhysicalMaintenanceTarget target, Guid replacementId) =>
        store.AbandonReplacement(target, replacementId);

    public ValueTask FlushReplacementPagesAsync(CancellationToken cancellationToken) =>
        store.FlushReplacementPagesAsync(cancellationToken);

    public ValueTask PersistAsync(Guid transactionId, CancellationToken cancellationToken) =>
        store.PersistAsync(transactionId, cancellationToken);

    public void Publish(Guid transactionId) => store.Publish(transactionId);

    public void Abandon(Guid transactionId, bool reclaimUnsubmittedTail = false) =>
        store.Abandon(transactionId, reclaimUnsubmittedTail);

    public bool RequiresCheckpointMaintenance => store.RequiresCheckpointMaintenance;

    public void AbandonCheckpointMaintenance(Guid id) => store.AbandonCheckpointMaintenance(id);

    public bool RequiresCheckpointCompaction => store.RequiresCheckpointCompaction;

    public bool CanAdvancePrivateCheckpointCompaction => store.CanAdvancePrivateCheckpointCompaction;

    public ValueTask<bool> AdvanceCheckpointCompactionAsync(DatabaseVersion version,
        Func<IReadOnlyList<DatabasePhysicalPageMutation>, CancellationToken, ValueTask> journal, CancellationToken token) =>
        store.AdvanceCheckpointCompactionAsync(version, journal, token);

    public ValueTask StopCheckpointCompactionAsync() => store.StopCheckpointCompactionAsync();

    public void AuthorizeCheckpointPageReuse(bool compacted) => store.AuthorizeCheckpointPageReuse(compacted);

    public ValueTask<OrderedPageTreeStage?> PrepareCheckpointMaintenanceAsync(Guid id, DatabaseVersion version, bool compact, CancellationToken token) =>
        store.PrepareCheckpointMaintenanceAsync(id, version, compact, token);

    public void ClearMaintenanceCaches() => store.ClearMaintenanceCaches();

    public async ValueTask TrimAsync(CancellationToken token)
    {
        if (store.HasPendingCheckpointCompaction)
        {
            return;
        }

        if (!await store.TryTrimUnownedTailAsync(token).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Checkpoint maintenance could not drain physical page retention for trimming.");
        }
    }
}

internal sealed class SemanticPhysicalQueryStore(SemanticPhysicalAccessPath store)
{
    public ISemanticCandidateSnapshot CaptureSnapshot(Guid? stagedTransactionId = null,
        DatabaseVersion? maximumVersion = null) => store.CaptureSnapshot(stagedTransactionId, maximumVersion);
}
