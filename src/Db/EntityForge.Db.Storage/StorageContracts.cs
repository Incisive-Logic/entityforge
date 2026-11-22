using EntityForge.Db.Durability;
using EntityForge.Db;

namespace EntityForge.Db.Storage;

/// <summary>Accepts transaction envelopes and recovers the log's ordered transaction history.</summary>
/// <remarks>Choose an implementation whose durability guarantees match the deployment. Disposing the
/// log releases its resources; it does not substitute for successful commit completion.</remarks>
public interface IDatabaseCommitLog : IAsyncDisposable
{
    /// <summary>Appends a transaction using the log implementation's commit protocol.</summary>
    /// <param name="transaction">The transaction envelope to record.</param>
    /// <param name="cancellationToken">Cancels the operation where supported; cancellation alone does not prove rollback.</param>
    /// <returns>A task representing completion of the append protocol.</returns>
    ValueTask CommitAsync(DurableTransactionEnvelope transaction, CancellationToken cancellationToken = default);

    /// <summary>Reads transaction envelopes retained for recovery in log order.</summary>
    /// <param name="cancellationToken">Cancels recovery.</param>
    /// <returns>The recoverable transaction history; checkpoint pairing must be validated separately.</returns>
    ValueTask<IReadOnlyList<DurableTransactionEnvelope>> RecoverAsync(CancellationToken cancellationToken = default);
}

/// <summary>Checks that a checkpoint covers history removed from a durable commit log.</summary>
public interface ICheckpointRecoveryDatabaseCommitLog
{
    /// <summary>Rejects a missing checkpoint when history was reclaimed, and checks its redo position when supplied.</summary>
    /// <remarks>Logical-only stores without position evidence require their own checkpoint/history pairing validation.</remarks>
    /// <param name="checkpoint">The checkpoint to restore, or null when none exists.</param>
    /// <param name="cancellationToken">Cancels validation before recovery proceeds.</param>
    /// <returns>A task that completes when the recovery boundary is valid.</returns>
    ValueTask ValidateRecoveryCheckpointAsync(DurableCheckpointEnvelope? checkpoint,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Indicates that a commit-log append could not complete while the live writer's ordering state was being
/// advanced. The database must reopen and recover the durable prefix before accepting another commit.
/// </summary>
public class DatabaseCommitLogRecoveryRequiredException : Exception
{
    /// <summary>Creates a failure requiring the database to reopen and recover its durable prefix.</summary>
    /// <param name="transactionId">The transaction whose append failed.</param>
    /// <param name="outcomeUnknown">Whether the caller cannot determine whether the transaction became durable.</param>
    /// <param name="message">The failure description.</param>
    /// <param name="innerException">The underlying append failure.</param>
    public DatabaseCommitLogRecoveryRequiredException(Guid transactionId, bool outcomeUnknown,
        string message, Exception innerException) : base(message, innerException)
    {
        TransactionId = transactionId;
        OutcomeUnknown = outcomeUnknown;
    }

    /// <summary>Gets the transaction whose append failed.</summary>
    public Guid TransactionId
    {
        get;
    }

    /// <summary>Gets whether recovery is needed to resolve the transaction's durability outcome.</summary>
    public bool OutcomeUnknown
    {
        get;
    }
}

/// <summary>
/// A physical page mutation whose supplied memories remain immutable until the commit operation completes.
/// The caller retains memory ownership and must not reuse or modify the buffers during that lifetime.
/// </summary>
public sealed record DatabasePhysicalPageMutation
{
    /// <summary>Describes a byte-range replacement within an existing physical page.</summary>
    /// <param name="pageId">Nonnegative physical page identifier.</param>
    /// <param name="offset">Nonnegative byte offset in the before-image.</param>
    /// <param name="bytes">Nonempty replacement bytes, borrowed until commit completes.</param>
    /// <param name="beforeImage">Nonempty previous page image covering the replaced range, borrowed until commit completes.</param>
    /// <exception cref="ArgumentOutOfRangeException">The page, offset or replacement range is invalid.</exception>
    /// <exception cref="ArgumentException">The replacement or before-image is empty.</exception>
    public DatabasePhysicalPageMutation(long pageId, int offset, ReadOnlyMemory<byte> bytes,
        ReadOnlyMemory<byte> beforeImage)
    {
        if (pageId < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageId));
        }

        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        if (bytes.IsEmpty)
        {
            throw new ArgumentException("A physical mutation cannot be empty.", nameof(bytes));
        }

        if (beforeImage.IsEmpty)
        {
            throw new ArgumentException("A physical mutation requires a before-image.", nameof(beforeImage));
        }

        if (offset > beforeImage.Length - bytes.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(bytes));
        }

        PageId = pageId;
        Offset = offset;
        Bytes = bytes;
        BeforeImage = beforeImage;
    }

    private DatabasePhysicalPageMutation(long pageId, ReadOnlyMemory<byte> bytes)
    {
        if (pageId < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageId));
        }

        if (bytes.IsEmpty)
        {
            throw new ArgumentException("A physical mutation cannot be empty.", nameof(bytes));
        }

        PageId = pageId;
        Offset = 0;
        Bytes = bytes;
        BeforeImage = ReadOnlyMemory<byte>.Empty;
    }

    /// <summary>Creates a complete mutation for a page that did not exist before this transaction.</summary>
    /// <param name="pageId">Nonnegative identifier of the new page.</param>
    /// <param name="bytes">Nonempty complete page image; ownership remains with the caller until commit completes.</param>
    /// <returns>A mutation with offset zero and no before-image.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The page identifier is negative.</exception>
    /// <exception cref="ArgumentException">The page image is empty.</exception>
    public static DatabasePhysicalPageMutation CreatePage(long pageId, ReadOnlyMemory<byte> bytes) =>
        new(pageId, bytes);

    /// <summary>Gets the physical page identifier.</summary>
    public long PageId
    {
        get;
    }

    /// <summary>Gets the byte offset where replacement begins.</summary>
    public int Offset
    {
        get;
    }

    /// <summary>Gets the borrowed replacement bytes.</summary>
    public ReadOnlyMemory<byte> Bytes
    {
        get;
    }

    /// <summary>Gets the borrowed previous page image, or empty memory for a newly allocated page.</summary>
    public ReadOnlyMemory<byte> BeforeImage
    {
        get;
    }

    /// <summary>Gets whether the page had a durable image before this transaction.</summary>
    public bool PageExisted => !BeforeImage.IsEmpty;
}

/// <summary>Records a logical transaction together with its physical recovery mutations.</summary>
public interface IPhysicalDatabaseCommitLog : IDatabaseCommitLog
{
    /// <summary>Commits the transaction and physical page mutations through the same log protocol.</summary>
    /// <param name="transaction">The logical transaction envelope.</param>
    /// <param name="mutations">Page mutations whose buffers remain immutable until completion.</param>
    /// <param name="cancellationToken">Cancels the operation where supported.</param>
    /// <returns>A task representing the physical commit operation.</returns>
    ValueTask CommitWithPhysicalMutationsAsync(DurableTransactionEnvelope transaction,
        IReadOnlyList<DatabasePhysicalPageMutation> mutations,
        CancellationToken cancellationToken = default);
}

/// <summary>Pairs one transaction with the physical mutations needed for its recovery.</summary>
/// <param name="Transaction">The logical transaction envelope.</param>
/// <param name="Mutations">Physical mutations, with caller-owned immutable buffers through commit completion.</param>
public sealed record DatabasePhysicalCommit(
    DurableTransactionEnvelope Transaction,
    IReadOnlyList<DatabasePhysicalPageMutation> Mutations);

/// <summary>Durably submits an already ordered transaction group through one atomic WAL group marker.</summary>
public interface IGroupedPhysicalDatabaseCommitLog : IPhysicalDatabaseCommitLog
{
    /// <summary>Checks configured group bounds without submitting or retaining any work.</summary>
    /// <param name="commits">The ordered group to validate.</param>
    void ValidateGroup(IReadOnlyList<DatabasePhysicalCommit> commits);

    /// <summary>Commits an ordered group under one atomic WAL group marker.</summary>
    /// <param name="commits">The ordered commits and their immutable physical buffers.</param>
    /// <param name="cancellationToken">Cancels submission where supported.</param>
    /// <returns>A task representing the group's durable submission.</returns>
    ValueTask CommitGroupAsync(IReadOnlyList<DatabasePhysicalCommit> commits,
        CancellationToken cancellationToken = default);
}

/// <summary>A point-in-time summary of storage usage.</summary>
/// <param name="WalBytes">Bytes retained by the write-ahead log.</param>
/// <param name="ChangeJournal">Change-journal diagnostics when supplied by the implementation.</param>
public sealed record DatabaseStorageStatus(long WalBytes,
    DatabaseChangeJournalStatus? ChangeJournal = null);

/// <summary>Reports storage usage without changing stored data.</summary>
public interface IDatabaseStorageDiagnostics
{
    /// <summary>Reads the current storage diagnostics.</summary>
    /// <param name="cancellationToken">Cancels the diagnostic read.</param>
    /// <returns>A storage status snapshot.</returns>
    ValueTask<DatabaseStorageStatus> GetStatusAsync(CancellationToken cancellationToken = default);
}

/// <summary>Reports the completed reclamation of checkpoint-covered log history.</summary>
/// <param name="ReclaimedThroughVersion">The version through which history was reclaimed.</param>
/// <param name="RecordsRemoved">Number of removed log records.</param>
/// <param name="BytesRemoved">Number of reclaimed bytes.</param>
public sealed record DatabaseCommitLogReclamationResult(
    DatabaseVersion ReclaimedThroughVersion, int RecordsRemoved, long BytesRemoved);

/// <summary>Removes commit-log history already covered by a durable checkpoint and retention policy.</summary>
public interface IReclaimableDatabaseCommitLog
{
    /// <summary>Reclaims eligible history through a checkpoint version.</summary>
    /// <param name="checkpointVersion">Version protected by a checkpoint durably published before reclamation.</param>
    /// <param name="cancellationToken">Cancels reclamation where supported.</param>
    /// <returns>The history actually reclaimed.</returns>
    ValueTask<DatabaseCommitLogReclamationResult> ReclaimThroughAsync(
        DatabaseVersion checkpointVersion, CancellationToken cancellationToken = default);
}

/// <summary>Reclaims a complete checkpointed WAL prefix, including records that allocate no native version.</summary>
/// <remarks>The owner must exclude writes while capturing this position and the checkpoint, persist that
/// checkpoint before reclamation, and preserve any independent history-retention boundary.</remarks>
public interface ICheckpointPositionDatabaseCommitLog : IReclaimableDatabaseCommitLog
{
    /// <summary>Captures the current physical log boundary while the owner excludes writes.</summary>
    /// <returns>An opaque log position to bind into the checkpoint before reclamation.</returns>
    long CaptureCheckpointPosition();

    /// <summary>Reclaims the prefix protected by a durably published checkpoint and its captured position.</summary>
    /// <param name="checkpointVersion">The checkpoint's logical version.</param>
    /// <param name="checkpointPosition">The log position captured for that checkpoint.</param>
    /// <param name="cancellationToken">Cancels reclamation where supported.</param>
    /// <returns>The reclaimed history and byte counts.</returns>
    ValueTask<DatabaseCommitLogReclamationResult> ReclaimCheckpointAsync(DatabaseVersion checkpointVersion,
        long checkpointPosition, CancellationToken cancellationToken = default);
}

/// <summary>Controls whether canonical commit history may be reclaimed after a durable checkpoint.</summary>
public enum DatabaseHistoryDisposition
{
    /// <summary>Keep canonical history according to the retention policy.</summary>
    Retain,

    /// <summary>Defer reclamation while the policy's required conditions remain unmet.</summary>
    Hold,

    /// <summary>Permit reclamation of history covered by the checkpoint.</summary>
    Reclaim,

    /// <summary>Permit reclamation after the policy has durably archived and verified the required evidence.</summary>
    ArchiveVerifiedAndReclaim
}

/// <summary>Identifies the checkpoint and database whose history may be reclaimed.</summary>
/// <param name="DatabaseIncarnation">Identity of this database lifetime.</param>
/// <param name="Profile">The database's storage profile.</param>
/// <param name="CheckpointVersion">Logical version protected by the checkpoint.</param>
/// <param name="Checkpoint">The checkpoint envelope supplied to the retention policy.</param>
public sealed record DatabaseHistoryReclamationRequest(
    Guid DatabaseIncarnation,
    DatabaseProfileId Profile,
    DatabaseVersion CheckpointVersion,
    DurableCheckpointEnvelope Checkpoint);

/// <summary>
/// Makes the profile-neutral retention decision. Implementations that require archival must return
/// <see cref="DatabaseHistoryDisposition.ArchiveVerifiedAndReclaim"/> only after the archive and its
/// integrity acknowledgement are durable; a backup operation alone is not such an acknowledgement.
/// </summary>
public interface IDatabaseHistoryRetentionPolicy
{
    /// <summary>Determines whether the specified checkpoint permits history reclamation.</summary>
    /// <param name="request">The database identity and checkpoint to assess.</param>
    /// <param name="cancellationToken">Cancels policy evaluation or archival work.</param>
    /// <returns>The permitted history disposition.</returns>
    ValueTask<DatabaseHistoryDisposition> DecideAsync(
        DatabaseHistoryReclamationRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Identifies a retention policy whose reclaim decision durably archives and verifies the checkpoint first.
/// Physical profile maintenance can use this capability to close the write gap around evidence reclamation.
/// </summary>
public interface IArchiveVerifiedDatabaseHistoryRetentionPolicy : IDatabaseHistoryRetentionPolicy;

/// <summary>Permits checkpoint-covered history reclamation without requiring an archive.</summary>
public sealed class CheckpointDatabaseHistoryRetentionPolicy : IDatabaseHistoryRetentionPolicy
{
    /// <inheritdoc />
    public ValueTask<DatabaseHistoryDisposition> DecideAsync(
        DatabaseHistoryReclamationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(DatabaseHistoryDisposition.Reclaim);
    }
}

/// <summary>Persists and loads the checkpoint used to begin database recovery.</summary>
public interface IDatabaseCheckpointStore : IAsyncDisposable
{
    /// <summary>Publishes a checkpoint using the store's durability protocol.</summary>
    /// <param name="checkpoint">The captured checkpoint envelope.</param>
    /// <param name="cancellationToken">Cancels saving where supported.</param>
    /// <returns>A task representing checkpoint publication.</returns>
    ValueTask SaveAsync(DurableCheckpointEnvelope checkpoint, CancellationToken cancellationToken = default);

    /// <summary>Loads the stored checkpoint, distinguishing absence from a failed or corrupt read.</summary>
    /// <param name="cancellationToken">Cancels loading.</param>
    /// <returns>The checkpoint, or null when none exists; invalid stored data must not be treated as absence.</returns>
    ValueTask<DurableCheckpointEnvelope?> LoadAsync(CancellationToken cancellationToken = default);
}

/// <summary>Reads retained, published database changes using opaque change cursors.</summary>
public interface IDatabaseCommittedChangeJournal : IAsyncDisposable
{
    /// <summary>Reads the journal's currently available cursor bounds.</summary>
    /// <param name="cancellationToken">Cancels reading the bounds.</param>
    /// <returns>The retained and published change boundaries.</returns>
    ValueTask<DatabaseChangeFeedBounds> GetBoundsAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads a bounded page of selected changes after a cursor.</summary>
    /// <param name="cursor">Exclusive starting cursor, or null to start from available history.</param>
    /// <param name="selector">Filters the requested change stream.</param>
    /// <param name="maximumCount">Maximum number of changes requested.</param>
    /// <param name="cancellationToken">Cancels the page read.</param>
    /// <returns>A page with its continuation and completion information.</returns>
    ValueTask<DatabaseChangePage> ReadPageAfterAsync(
        DatabaseChangeCursor? cursor,
        DatabaseChangeSelector selector,
        int maximumCount,
        CancellationToken cancellationToken = default);

    /// <summary>Enumerates selected published changes, waiting for later changes as configured.</summary>
    /// <param name="cursor">Exclusive starting cursor, or null to start from available history.</param>
    /// <param name="selector">Filters the requested change stream.</param>
    /// <param name="options">Subscription batching and waiting controls.</param>
    /// <param name="cancellationToken">Cancels reading or waiting.</param>
    /// <returns>An asynchronous change stream; dispose its enumerator when abandoning the subscription.</returns>
    IAsyncEnumerable<CommittedChangeSet> SubscribeAsync(
        DatabaseChangeCursor? cursor,
        DatabaseChangeSelector selector,
        DatabaseChangeSubscriptionOptions options,
        CancellationToken cancellationToken = default);
}

/// <summary>Separates change materialization, durable publication and visibility notification.</summary>
public interface IDatabaseCommittedChangeJournalWriter
{
    /// <summary>Appends a change for later publication.</summary>
    /// <param name="changeSet">The committed change to materialize.</param>
    /// <param name="cancellationToken">Cancels appending where supported.</param>
    /// <returns>A task representing materialization; this alone does not notify subscribers.</returns>
    ValueTask AppendAsync(CommittedChangeSet changeSet, CancellationToken cancellationToken = default);

    /// <summary>Advances the durable publication frontier through an appended cursor.</summary>
    /// <param name="cursor">The last change covered by publication.</param>
    /// <param name="cancellationToken">Cancels publication where supported.</param>
    /// <returns>A task representing publication before visibility notification.</returns>
    ValueTask PublishThroughAsync(DatabaseChangeCursor cursor, CancellationToken cancellationToken = default);

    /// <summary>Makes already published changes visible and wakes waiting subscribers.</summary>
    /// <param name="cursor">A cursor no later than the durable publication frontier.</param>
    /// <param name="cancellationToken">Cancels notification where supported.</param>
    /// <returns>A task representing visibility advancement.</returns>
    ValueTask NotifyPublishedAsync(DatabaseChangeCursor cursor, CancellationToken cancellationToken = default);
}

/// <summary>Combines committed-change reading, materialization and publication in one store.</summary>
public interface IDatabaseCommittedChangeStore : IDatabaseCommittedChangeJournal,
    IDatabaseCommittedChangeJournalWriter;

/// <summary>Materializes a captured visible prefix without creating a checkpoint or replacing its final durability barrier.</summary>
public interface IDatabaseCommittedChangeCheckpointPreparation
{
    /// <summary>Prepares visible changes for checkpoint capture without replacing the final durability barrier.</summary>
    /// <param name="cancellationToken">Cancels preparation.</param>
    /// <returns>A task representing preparation of the captured prefix.</returns>
    ValueTask PrepareCheckpointAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Drains a WAL-backed committed-change projection through its latest accepted change. A checkpoint must cross
/// this barrier before it permits reclamation of the WAL records from which the projection can be rebuilt.
/// </summary>
public interface IDatabaseCommittedChangeDurabilityBarrier
{
    /// <summary>Flushes the accepted projection so its supporting WAL can safely be reclaimed after checkpointing.</summary>
    /// <param name="cancellationToken">Cancels the flush where supported.</param>
    /// <returns>A task representing completion of the projection durability barrier.</returns>
    ValueTask FlushAsync(CancellationToken cancellationToken = default);
}

/// <summary>Configures change-history space pressure while preserving minimum count and age windows.</summary>
/// <remarks>The byte target is not a hard disk quota: minimum retention requirements can keep usage above it.</remarks>
public sealed record DatabaseChangeJournalRetentionOptions
{
    /// <summary>Default retained-byte target: 256 MiB.</summary>
    public const long DefaultMaximumRetainedBytes = 256L * 1024 * 1024;

    /// <summary>Default minimum retained change count: 1,000.</summary>
    public const int DefaultMinimumRetainedChangeCount = 1_000;

    /// <summary>Default minimum retention age: one day.</summary>
    public static readonly TimeSpan DefaultMinimumRetainedDuration = TimeSpan.FromDays(1);

    /// <summary>Creates validated retention limits.</summary>
    /// <param name="maximumRetainedBytes">Positive retained-byte target.</param>
    /// <param name="minimumRetainedChangeCount">Nonnegative minimum number of recent changes to retain.</param>
    /// <param name="minimumRetainedDuration">Nonnegative age window, or null for one day.</param>
    /// <exception cref="ArgumentOutOfRangeException">A target or minimum is outside its supported range.</exception>
    public DatabaseChangeJournalRetentionOptions(
        long maximumRetainedBytes = DefaultMaximumRetainedBytes,
        int minimumRetainedChangeCount = DefaultMinimumRetainedChangeCount,
        TimeSpan? minimumRetainedDuration = null)
    {
        if (maximumRetainedBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumRetainedBytes));
        }

        if (minimumRetainedChangeCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumRetainedChangeCount));
        }

        TimeSpan duration = minimumRetainedDuration ?? DefaultMinimumRetainedDuration;
        if (duration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumRetainedDuration));
        }

        MaximumRetainedBytes = maximumRetainedBytes;
        MinimumRetainedChangeCount = minimumRetainedChangeCount;
        MinimumRetainedDuration = duration;
    }

    /// <summary>Gets the retained-byte target used to report retention pressure.</summary>
    public long MaximumRetainedBytes
    {
        get;
    }

    /// <summary>Gets the minimum number of recent changes retained during compaction.</summary>
    public int MinimumRetainedChangeCount
    {
        get;
    }

    /// <summary>Gets the minimum age window protected from compaction.</summary>
    public TimeSpan MinimumRetainedDuration
    {
        get;
    }
}

/// <summary>Reports journal space, publication progress and accumulated work diagnostics.</summary>
/// <param name="RetainedBytes">Bytes currently retained by journal segments.</param>
/// <param name="RetentionPressureBytes">Retained bytes exceeding the configured byte target, or zero.</param>
/// <param name="SegmentCount">Number of retained segments.</param>
/// <param name="RetentionFloor">Exclusive continuation boundary through which old history was reclaimed, when present.</param>
/// <param name="MaterializedCurrent">Latest materialized change cursor, when present.</param>
/// <param name="PublishedCurrent">Latest published cursor reported by the implementation.</param>
/// <param name="MaterializationDebt">Changes awaiting materialization or visible publication, as reported by the implementation.</param>
/// <param name="CompactedBytes">Accumulated bytes removed by compaction.</param>
/// <param name="CompactedRecords">Accumulated records removed by compaction.</param>
/// <param name="IndexedCandidates">Accumulated candidates examined through journal indexes.</param>
/// <param name="DecodedRecords">Accumulated journal records decoded.</param>
/// <param name="SubscriptionWakeUps">Accumulated subscription wakeups.</param>
/// <param name="ExpiredCursorCount">Accumulated expired-cursor observations.</param>
/// <param name="RetentionFloorVersion">Logical version associated with the retention floor, if available.</param>
public sealed record DatabaseChangeJournalStatus(
    long RetainedBytes,
    long RetentionPressureBytes,
    int SegmentCount,
    DatabaseChangeCursor? RetentionFloor,
    DatabaseChangeCursor? MaterializedCurrent,
    DatabaseChangeCursor? PublishedCurrent,
    long MaterializationDebt,
    long CompactedBytes,
    long CompactedRecords,
    long IndexedCandidates,
    long DecodedRecords,
    long SubscriptionWakeUps,
    long ExpiredCursorCount,
    long? RetentionFloorVersion = null);

/// <summary>Exposes change-journal retention and work counters.</summary>
public interface IDatabaseChangeJournalDiagnostics
{
    /// <summary>Reads a point-in-time diagnostic snapshot.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The journal's current status.</returns>
    ValueTask<DatabaseChangeJournalStatus> GetStatusAsync(CancellationToken cancellationToken = default);
}

/// <summary>One named artifact belonging to a captured journal snapshot.</summary>
/// <param name="Name">The artifact's storage name.</param>
/// <param name="Content">The artifact bytes; callers must preserve them unchanged while saving the snapshot.</param>
public sealed record DatabaseChangeJournalSnapshotArtifact(string Name, ReadOnlyMemory<byte> Content);

/// <summary>A collection of journal artifacts captured at a publication boundary.</summary>
/// <param name="Artifacts">Artifacts needed to reconstruct the captured journal state.</param>
public sealed record DatabaseChangeJournalSnapshot(
    IReadOnlyList<DatabaseChangeJournalSnapshotArtifact> Artifacts);

/// <summary>Captures journal artifacts for a checkpoint or backup boundary.</summary>
public interface IDatabaseChangeJournalSnapshotParticipant
{
    /// <summary>Captures journal state through a supplied change cursor.</summary>
    /// <param name="cursor">The requested inclusive publication boundary, or null for an empty boundary.</param>
    /// <param name="cancellationToken">Cancels capture.</param>
    /// <returns>The artifacts representing the requested boundary.</returns>
    ValueTask<DatabaseChangeJournalSnapshot> CaptureSnapshotThroughAsync(
        DatabaseChangeCursor? cursor, CancellationToken cancellationToken = default);
}

/// <summary>Installs a new commit log in an unpublished restore directory. Successful completion
/// durably preserves the independent redo allocation floor before any restored write can be admitted.</summary>
public interface IDatabaseRestoreLogInitializer
{
    /// <summary>Initializes recovery metadata for a restored physical image before it becomes writable.</summary>
    /// <param name="path">Destination log path in an unpublished restore directory.</param>
    /// <param name="physicalImagePath">The restored physical image path.</param>
    /// <param name="allocationFloor">Redo allocation floor that must remain preserved.</param>
    /// <param name="cancellationToken">Cancels initialization.</param>
    /// <returns>A task representing durable initialization.</returns>
    ValueTask InitializeAsync(string path, string physicalImagePath, long allocationFloor, CancellationToken cancellationToken = default);
}

/// <summary>A captured checkpoint whose physical writeback is complete, with an immutable
/// recovery binding. Publication may overlap later WAL-protected commits.</summary>
public interface IDatabaseCheckpointPublication
{
    /// <summary>Durably publishes the previously captured checkpoint image.</summary>
    /// <param name="cancellationToken">Cancels publication where supported.</param>
    /// <returns>A task representing publication completion.</returns>
    ValueTask PublishAsync(CancellationToken cancellationToken = default);
}

/// <summary>Completes only the logical payload of an already captured physical epoch.</summary>
public interface IDatabaseDeferredCheckpointPublication : IDatabaseCheckpointPublication
{
    /// <summary>Publishes captured physical state with its deferred logical profile content.</summary>
    /// <param name="profileState">Logical payload belonging to the captured epoch.</param>
    /// <param name="cancellationToken">Cancels publication where supported.</param>
    /// <returns>A task representing publication of the completed checkpoint.</returns>
    ValueTask PublishAsync(DurableProfilePayload profileState, CancellationToken cancellationToken = default);
}

/// <summary>Best-effort preparation only; final capture and publication barriers remain mandatory.</summary>
public interface IDatabaseCheckpointImagePreparation
{
    /// <summary>Performs preparatory image work without completing checkpoint capture or publication.</summary>
    /// <param name="token">Cancels preparation.</param>
    /// <returns>A task representing best-effort preparation.</returns>
    ValueTask PrepareImageAsync(CancellationToken token = default);
}

/// <summary>Production storage capable of separating capture from durable publication.
/// Capture requires exclusive kernel admission and a newly rotated WAL protection epoch.</summary>
public interface IConcurrentDatabaseCheckpointStore
{
    /// <summary>Gets whether publication may overlap later WAL-protected commits after exclusive capture.</summary>
    bool SupportsConcurrentPublication
    {
        get;
    }

    /// <summary>Gets whether the logical profile payload may be supplied after physical capture; defaults to false.</summary>
    bool SupportsDeferredProfileContent => false;

    /// <summary>Captures a checkpoint for later publication while the caller holds exclusive kernel admission.</summary>
    /// <param name="checkpoint">Checkpoint metadata and content for the captured epoch.</param>
    /// <param name="cancellationToken">Cancels capture.</param>
    /// <returns>The publication handle; the caller must subsequently publish it.</returns>
    ValueTask<IDatabaseCheckpointPublication> PreparePublicationAsync(
        DurableCheckpointEnvelope checkpoint, CancellationToken cancellationToken = default);
}

/// <summary>Starts a full-page protection epoch before foreground writes resume.
/// Every first post-boundary page modification must carry independent recovery protection.</summary>
public interface IConcurrentCheckpointCommitLog : ICheckpointPositionDatabaseCommitLog
{
    /// <summary>Rotates physical recovery protection at a checkpoint boundary.</summary>
    /// <param name="cancellationToken">Cancels epoch creation.</param>
    /// <returns>The captured log position associated with the new protection epoch.</returns>
    ValueTask<long> BeginCheckpointEpochAsync(CancellationToken cancellationToken = default);
}
