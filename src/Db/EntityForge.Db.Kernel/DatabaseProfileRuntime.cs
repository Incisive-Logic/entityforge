using EntityForge.Db.Durability;
using EntityForge.Db.Storage;

namespace EntityForge.Db.Kernel;

/// <summary>Applies one profile's durable payloads under the common database kernel.</summary>
public interface IDatabaseProfileRuntime
{
    /// <summary>Gets the physical ownership layout expected by the profile.</summary>
    DatabaseStorageLayout StorageLayout => DatabaseStorageLayout.Current;

    /// <summary>Gets the database profile identifier.</summary>
    DatabaseProfileId Profile
    {
        get;
    }

    /// <summary>Gets the stable codec identifier for transaction payloads.</summary>
    string CodecId
    {
        get;
    }

    /// <summary>Gets the transaction payload codec version.</summary>
    int CodecVersion
    {
        get;
    }

    /// <summary>Validates a transaction payload before application; the default implementation performs no additional validation.</summary>
    ValueTask ValidateAsync(Guid transactionId, ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    /// <summary>Applies a transaction payload at the kernel-assigned database version.</summary>
    ValueTask ApplyAsync(DatabaseVersion version, Guid transactionId, ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default);
}

/// <summary>Produces committed-change evidence for a profile transaction before publication.</summary>
public interface IDatabaseProfileChangePlanner
{
    /// <summary>Plans the user-data classification and optional change-feed payload for the transaction.</summary>
    ValueTask<DatabaseProfileChangePlan> PlanCommittedChangesAsync(
        DatabaseVersion version,
        Guid transactionId,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default);
}

/// <summary>Publishes profile state that was staged separately from durable transaction recording.</summary>
public interface IDatabaseProfilePublicationRuntime
{
    /// <summary>Makes the staged state for the specified transaction visible after its durability requirements have been met.</summary>
    ValueTask PublishStagedStateAsync(Guid transactionId,
        CancellationToken cancellationToken = default);
}

/// <summary>Forces profile-owned physical state before a checkpoint can make older redo reclaimable.</summary>
public interface IDatabaseProfileDurabilityBarrier
{
    /// <summary>Flushes profile-owned physical state before a checkpoint permits older redo records to be reclaimed.</summary>
    ValueTask FlushPhysicalStateAsync(CancellationToken cancellationToken = default);
}

/// <summary>Stages profile metadata required by a checkpoint before its physical flush.
/// The kernel records these equivalent-state mutations through physical redo at the captured version.</summary>
public interface IDatabaseProfileCheckpointPreparation
{
    /// <summary>Stages checkpoint metadata and returns its equivalent-state physical mutations at the captured version.</summary>
    ValueTask<IReadOnlyList<DatabasePhysicalPageMutation>> PrepareCheckpointAsync(Guid id,
        DatabaseVersion version, CancellationToken cancellationToken);

    /// <summary>Publishes the checkpoint metadata staged under the supplied preparation identifier.</summary>
    ValueTask PublishCheckpointPreparationAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Discards unpublished checkpoint metadata owned by the preparation.</summary>
    ValueTask AbandonCheckpointPreparationAsync(Guid id);
}

/// <summary>Separates private physical mutation planning from publication or abandonment.</summary>
public interface IDatabaseProfilePhysicalMutationPlanner
{
    /// <summary>Plans physical page mutations for a transaction without publishing its staged state.</summary>
    ValueTask<IReadOnlyList<DatabasePhysicalPageMutation>> PreparePhysicalMutationsAsync(
        DatabaseVersion version, Guid transactionId, ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default);

    /// <summary>Publishes the physical state prepared for a transaction after durability succeeds.</summary>
    ValueTask PublishPhysicalMutationsAsync(Guid transactionId,
        CancellationToken cancellationToken = default);

    /// <summary>Abandons the transaction's staged physical state according to the profile's reclamation and quarantine rules.</summary>
    ValueTask AbandonPhysicalMutationsAsync(Guid transactionId,
        CancellationToken cancellationToken = default);

    /// <summary>Discards an admission preview before any durability submission.
    /// Profiles may reclaim reservations that have never been written. The default
    /// retains normal abandonment and quarantine semantics.</summary>
    ValueTask AbandonUnsubmittedPhysicalMutationsAsync(Guid transactionId,
        CancellationToken cancellationToken = default) =>
        AbandonPhysicalMutationsAsync(transactionId, cancellationToken);
}

/// <summary>Opts into retaining one unsubmitted native preparation across partition selection.
/// Planned state and page memories must remain privately owned and immutable until publication
/// or abandonment. Reuse is permitted only at the identical native predecessor; the kernel
/// abandons the preparation before any other native planning, checkpoint, recovery or disposal.
/// Timestamp assignment is performed at completion and must not be embedded in staged pages.</summary>
public interface IDatabaseProfileRetainedPhysicalPreparation : IDatabaseProfilePhysicalMutationPlanner
{
}

/// <summary>Describes one versioned transaction in an ordered physical preparation group.</summary>
/// <param name="Version">The database version assigned to this preparation.</param>
/// <param name="TransactionId">The stable transaction identity.</param>
/// <param name="Payload">The encoded profile transaction payload.</param>
public sealed record DatabaseProfilePhysicalPreparation(
    DatabaseVersion Version, Guid TransactionId, ReadOnlyMemory<byte> Payload);

/// <summary>Prepares a group of ordered physical transactions while retaining per-transaction mutation ownership.</summary>
public interface IDatabaseProfileGroupedPhysicalMutationPlanner : IDatabaseProfilePhysicalMutationPlanner
{
    /// <summary>Opts into publication behind one reader epoch covering the complete physical group.</summary>
    bool SupportsAtomicGroupPublication => false;

    /// <summary>Prepares the supplied transaction sequence and returns physical mutations keyed by transaction identifier.</summary>
    ValueTask<IReadOnlyDictionary<Guid, IReadOnlyList<DatabasePhysicalPageMutation>>>
        PreparePhysicalMutationGroupAsync(IReadOnlyList<DatabaseProfilePhysicalPreparation> preparations,
            CancellationToken cancellationToken = default);
}

/// <summary>Stages an invisible prospective state chain while an ordered commit group is prepared.</summary>
public interface IDatabaseProfileOrderedPreparationRuntime
{
    /// <summary>Adds a transaction to the invisible prospective state chain used to prepare an ordered group.</summary>
    ValueTask StagePreparedStateAsync(DatabaseVersion version, Guid transactionId,
        ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default);

    /// <summary>Discards the unpublished prospective states for the supplied transactions.</summary>
    ValueTask AbandonPreparedStatesAsync(IReadOnlyList<Guid> transactionIds,
        CancellationToken cancellationToken = default);

    /// <summary>Transitions the prepared state chain into its publication phase.</summary>
    ValueTask BeginPreparedPublicationAsync(CancellationToken cancellationToken = default);
}

/// <summary>Adds profile checkpoint capture and restoration to the kernel runtime contract.</summary>
public interface ICheckpointableDatabaseProfileRuntime : IDatabaseProfileRuntime
{
    /// <summary>Gets the stable checkpoint codec identifier.</summary>
    string CheckpointCodecId
    {
        get;
    }

    /// <summary>Gets the codec version written by newly captured checkpoints.</summary>
    int CheckpointCodecVersion
    {
        get;
    }

    /// <summary>Admits only checkpoint versions for which this profile has an explicit reader.</summary>
    bool CanReadCheckpointVersion(int codecVersion) => codecVersion == CheckpointCodecVersion;

    /// <summary>Captures the profile's current recovery state as an encoded checkpoint payload.</summary>
    ValueTask<ReadOnlyMemory<byte>> CaptureCheckpointAsync(CancellationToken cancellationToken = default);

    /// <summary>Restores profile state from an admitted checkpoint payload at its recorded database version.</summary>
    ValueTask RestoreCheckpointAsync(DatabaseVersion version, ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default);

    /// <summary>Restores a checkpoint using its admitted durable codec version.</summary>
    ValueTask RestoreCheckpointAsync(DatabaseVersion version, int codecVersion, ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default) => RestoreCheckpointAsync(version, payload, cancellationToken);
}

/// <summary>Captures current recovery state without advancing live snapshot or conflict retention.
/// The kernel must not publish retention notifications, run maintenance, or reclaim WAL for this capture.</summary>
public interface IDatabaseProfileSnapshotPreservingCheckpoint
{
    /// <summary>Captures recovery state while preserving live snapshots and conflict-retention boundaries.</summary>
    ValueTask<ReadOnlyMemory<byte>> CaptureSnapshotPreservingCheckpointAsync(CancellationToken cancellationToken = default);
}

/// <summary>Notifies a profile only after its captured checkpoint has been durably published.</summary>
public interface IDatabaseProfileCheckpointPublicationObserver
{
    /// <summary>Notifies the profile that the checkpoint at the supplied version has been durably published.</summary>
    ValueTask CheckpointPublishedAsync(DatabaseVersion version,
        CancellationToken cancellationToken = default);
}

/// <summary>Prunes profile replay evidence only for identities protected by the durable operation floor.</summary>
public interface IDatabaseProfileOperationRetention
{
    /// <summary>Captures a checkpoint omitting replay evidence for the supplied safely expired operation identities.</summary>
    ValueTask<ReadOnlyMemory<byte>> CaptureRetentionCheckpointAsync(IReadOnlySet<Guid> expired,
        CancellationToken cancellationToken);

    /// <summary>Notifies the profile that the retention checkpoint is durable and the specified replay evidence may be pruned.</summary>
    void RetentionCheckpointPublished(IReadOnlySet<Guid> expired);
}

/// <summary>Equivalent physical rewrites after a durable checkpoint, without allocating a logical version.</summary>
public interface IDatabaseProfileCheckpointMaintenance
{
    /// <summary>Whether local write authority is sufficient to enter physical maintenance without an external reader-drain fence.</summary>
    bool SupportsLocalRetentionMaintenance => false;

    /// <summary>Requests exclusive checkpoint maintenance when physical free-space debt is substantial.
    /// Local checkpoints also require local maintenance and checkpoint page-reuse support.</summary>
    bool RequiresCheckpointMaintenance => false;

    /// <summary>Acquires the profile's maintenance exclusion scope, if one is required.</summary>
    IDisposable? EnterMaintenance();

    /// <summary>Stages equivalent-state maintenance page mutations at the unchanged logical version, optionally compacting storage.</summary>
    ValueTask<IReadOnlyList<DatabasePhysicalPageMutation>> PrepareMaintenanceAsync(Guid id, DatabaseVersion version,
        bool compact, CancellationToken cancellationToken);

    /// <summary>Publishes the staged physical maintenance identified by the supplied identifier.</summary>
    ValueTask PublishMaintenanceAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Discards unpublished physical maintenance state.</summary>
    ValueTask AbandonMaintenanceAsync(Guid id);

    /// <summary>Completes profile cleanup after maintenance publication.</summary>
    ValueTask CompleteMaintenanceAsync(CancellationToken cancellationToken);
}

/// <summary>Allows ordinary writes to consume free pages covered by a durable checkpoint.
/// Called only after physical maintenance and replacement checkpoint publication while writers are excluded.</summary>
public interface IDatabaseProfileCheckpointPageReuse : IDatabaseProfileCheckpointMaintenance
{
    /// <summary>Evaluated after reclamation-only maintenance; retained-data growth may require a full rewrite.</summary>
    bool RequiresCheckpointCompaction => false;

    /// <summary>Authorizes reuse of checkpoint-covered free pages after replacement publication, indicating whether compaction occurred.</summary>
    void AuthorizeCheckpointPageReuse(bool compacted);
}

/// <summary>Builds a private compaction over bounded checkpoint slices. The journal
/// callback commits complete physical batches at the unchanged logical version.
/// Returning true means the next maintenance pass can atomically publish its roots.</summary>
public interface IDatabaseProfileIncrementalCheckpointCompaction
{
    // Private base construction may run between checkpoints under kernel commit
    // exclusion and non-blocking profile admission. Final catch-up/publication
    // remains exclusively owned by the ordinary checkpoint path.
    /// <summary>Gets whether private compaction may advance between checkpoints.</summary>
    bool CanAdvancePrivateCheckpointCompaction => false;

    /// <summary>Attempts nonblocking profile admission for a private compaction slice; returns null when admission is unavailable.</summary>
    IDisposable? TryEnterPrivateCheckpointCompaction() => null;

    /// <summary>Advances bounded compaction work and journals complete physical batches; returns true when the next maintenance pass can publish its roots.</summary>
    ValueTask<bool> AdvanceCheckpointCompactionAsync(DatabaseVersion version,
        Func<IReadOnlyList<DatabasePhysicalPageMutation>, CancellationToken, ValueTask> journal,
        CancellationToken cancellationToken);

    /// <summary>Stops private compaction and releases its retained preparation state.</summary>
    ValueTask StopCheckpointCompactionAsync();
}

/// <summary>Classifies a transaction as user data or system work and carries its optional committed-change payload.</summary>
/// <param name="IsUserData">Whether this transaction represents user-data changes.</param>
/// <param name="Changes">The committed-change payload, if the transaction exposes one.</param>
public sealed record DatabaseProfileChangePlan(bool IsUserData, CommittedChangePayload? Changes)
{
    /// <summary>Creates a user-data change plan with optional change-feed evidence.</summary>
    public static DatabaseProfileChangePlan UserData(CommittedChangePayload? changes) => new(true, changes);

    /// <summary>Creates a system-work plan without user change-feed evidence.</summary>
    public static DatabaseProfileChangePlan System() => new(false, null);
}

/// <summary>Opts into detached checkpoint publication. Captured payloads are privately owned;
/// post-capture writes retain the captured physical roots until publication, and publication
/// observers preserve newer versions, conflict evidence, and active snapshots.</summary>
public interface IDatabaseProfileConcurrentCheckpoint : ICheckpointableDatabaseProfileRuntime
{
}

/// <summary>Captures an immutable logical checkpoint while admission is excluded.
/// The returned encoder may run after later commits; it must not read live profile state
/// or the caller's expiration set. Operation-retention profiles filter that set during capture.</summary>
public interface IDatabaseProfileDeferredCheckpoint : IDatabaseProfileConcurrentCheckpoint
{
    /// <summary>Captures immutable state and returns a detached encoder that does not read later live state; optionally preserves snapshots or filters expired operations.</summary>
    ValueTask<Func<CancellationToken, ReadOnlyMemory<byte>>> CaptureCheckpointEncoderAsync(bool preserveSnapshots,
        IReadOnlySet<Guid>? expired, CancellationToken cancellationToken = default);
}

/// <summary>Suspends opportunistic profile maintenance while a foreground checkpoint runs.</summary>
public interface IDatabaseProfileCheckpointPriority
{
    /// <summary>Requests preemption before kernel gates are acquired. Disposal permits later work.</summary>
    IDisposable EnterCheckpointPriority();
}
