using EntityForge.Db.Durability;

namespace EntityForge.Db.Kernel;

/// <summary>The kernel's profile-neutral local-position transaction protocol.</summary>
/// <remarks>The owning distributed receiver must supply verified decisions and hold placement admission.
/// This capability does not itself grant distributed authority.</remarks>
public sealed class DatabasePartitionTransactions
{
    /// <summary>Prepares a visible journal prefix; the final checkpoint durability barrier is still required.</summary>
    public ValueTask PrepareCheckpointChangesAsync(CancellationToken token = default) =>
        _kernel.PrepareCheckpointChangesAsync(token);

    /// <summary>Whether this store admits logical timestamp visibility.</summary>
    public bool TimestampVisibilityEnabled => _kernel.TimestampMvccEnabled;

    /// <summary>The immutable clock domain for logical visibility, or null when disabled.</summary>
    public Guid? TimestampDomain => TimestampVisibilityEnabled ? _kernel.TimestampCostOptions!.Domain : null;

    /// <summary>Reads the durable vote associated with an exact prepared operation.</summary>
    public ValueTask<DurableTimestampState?> ReadTimestampVoteAsync(Guid operation, CancellationToken token = default) =>
        _kernel.ReadTimestampVoteAsync(operation, token);

    /// <summary>Selects a timestamp beyond all same-domain participant votes. The caller must persist its authoritative decision.</summary>
    public ValueTask<DurableTimestampState> ChooseTimestampAsync(IReadOnlyList<DurableTimestampState> votes, CancellationToken token = default) =>
        _kernel.ChooseTimestampAsync(votes, token);

    /// <summary>Captures an immutable logical projection under a live complete-owner protection generation.</summary>
    /// <remarks>Resources must conservatively cover the actual projection. Null waits for all relevant partition intents.
    /// Bounded-clock reads establish a durable assignment promise instead of waiting for query clock expiry;
    /// earlier relevant preparations still require terminal application. The caller supplies a qualified read timestamp.</remarks>
    public ValueTask<object> CaptureTimestampProjectionAsync(IDatabaseTimestampProtection protection, DurableTimestampState time,
        HashSet<string>? resources, CancellationToken token = default) => _kernel.CaptureTimestampProjectionAsync(protection, time, resources, token);

    /// <summary>Registers logical-history protection independently of a physical snapshot certificate.</summary>
    public ValueTask<DatabaseTimestampProtection> ProtectTimestampHistoryAsync(CancellationToken token = default) =>
        _kernel.ProtectTimestampHistoryAsync(token);

    /// <summary>Enters a read-only profile context for a previously captured logical projection.</summary>
    public IDisposable EnterTimestampProjection(object view) => _kernel.EnterTimestampProjection(view);

    /// <summary>Captures profile-neutral index membership after closed timestamp admission, joining lower intents.</summary>
    public ValueTask<DatabaseRootIndexSnapshot> CaptureRootIndexesAsync(IDatabaseTimestampProtection protection,
        DurableTimestampState time, DatabaseRootIndexColumn[] columns, CancellationToken token = default) =>
        _kernel.CaptureRootIndexesAsync(protection, time, columns, token);

    /// <summary>Exports exact committed effects and closed timestamp progress, joining lower intents.</summary>
    public ValueTask<DatabaseTimestampServingBatch> CaptureTimestampServingAsync(IDatabaseTimestampProtection protection,
        DurableTimestampState time, DatabaseTimestampServingCursor? cursor = null, CancellationToken token = default) =>
        _kernel.CaptureTimestampServingAsync(protection, time, cursor, token);

    /// <summary>Validates mapping manifests against the active logical projection.</summary>
    public void ValidateTimestampMappings(IReadOnlyList<EntityForge.Db.Mapping.EntityMappingManifest> manifests) =>
        _kernel.ValidateTimestampMappings(manifests);

    // Raw physical-snapshot qualification only; public reads admit timestamp views.
    internal ValueTask<bool> ValidateReadEpochAsync(DatabasePartitionSnapshot snapshot, CancellationToken token = default) =>
        _kernel.ValidateReadEpochAsync(snapshot, token);

    /// <summary>Gets or sets the logical timestamp admission service used by partition operations.</summary>
    public IDatabasePartitionTimestampAdmission? TimestampAdmission
    {
        get => _kernel.TimestampAdmission; set => _kernel.TimestampAdmission = value;
    }

    /// <summary>Enables native version retention for timestamp-coordinated reads.</summary>
    public ValueTask EnableTimestampHistoryAsync(CancellationToken token = default) => _kernel.EnableTimestampHistoryAsync(token);

    /// <summary>Advances the retained native history floor without moving beyond the current database version.</summary>
    public ValueTask TrimTimestampHistoryAsync(DatabaseVersion floor, CancellationToken token = default) => _kernel.TrimTimestampHistoryAsync(floor, token);

    /// <summary>Releases version pins retained for timestamp history.</summary>
    public ValueTask ReleaseTimestampHistoryAsync(CancellationToken token = default) => _kernel.ReleaseTimestampHistoryAsync(token);

    /// <summary>Captures a pinned snapshot at a retained native version for timestamp-coordinated execution.</summary>
    public ValueTask<DatabasePartitionSnapshot> CaptureTimestampSnapshotAsync(DatabaseVersion version, CancellationToken token = default) =>
        _kernel.CaptureTimestampSnapshotAsync(version, token);

    /// <summary>Maximum identities in one bounded selection-evidence observation.</summary>
    public const int MaximumSelectionObservation = 256;
    private readonly DatabaseKernel _kernel;

    internal DatabasePartitionTransactions(DatabaseKernel kernel) => _kernel = kernel;

    /// <summary>Selects a complete home manifest and reserves its native member in one durable boundary.</summary>
    /// <remarks>The immutable application descriptor arbitrates retries; a retained winner
    /// is returned whole. Definitive fresh native rejection is retained as an abort.</remarks>
    public ValueTask<DurablePartitionTransaction> SelectCoordinatorAsync(Guid operation, string descriptor,
        DatabaseVersion readVersion, ReadOnlyMemory<byte> payload, DurablePartitionCoordinator coordinator,
        CancellationToken token = default) =>
        _kernel.SelectPartitionCoordinatorAsync(operation, descriptor, readVersion, payload, coordinator, token);

    /// <summary>Reads recovery-aware home evidence without executing or replanning.</summary>
    public ValueTask<DurablePartitionTransaction?> ReadCoordinatorAsync(Guid operation, CancellationToken token = default) =>
        _kernel.ReadPartitionCoordinatorAsync(operation, token);

    /// <summary>Durably reserves an exact command against the selected local state.</summary>
    public ValueTask PrepareAsync(Guid operation, string descriptor, DatabaseVersion readVersion,
        ReadOnlyMemory<byte> payload, CancellationToken token = default) =>
        _kernel.PreparePartitionTransactionAsync(operation, descriptor, readVersion, payload, token);

    /// <summary>Applies the verified durable decision, assigning a native local position on commitment.</summary>
    public ValueTask<DatabaseCommitReceipt?> CompleteAsync(Guid operation, string descriptor, bool commit, CancellationToken token = default) =>
        _kernel.CompletePartitionTransactionAsync(operation, descriptor, commit, token);

    /// <summary>Completes the exact prepared operation as commit or abort, carrying optional durable logical timestamp evidence.</summary>
    public ValueTask<DatabaseCommitReceipt?> CompleteTimestampedAsync(Guid operation, string descriptor, bool commit,
        DurableTimestampState? time, CancellationToken token = default) =>
        _kernel.CompletePartitionTransactionAsync(operation, descriptor, commit, token, time);

    /// <summary>Observes an exact prepared operation without applying its decision or allocating a version.</summary>
    public ValueTask<DatabaseCommitReceipt?> ResolveAsync(Guid operation, string descriptor, CancellationToken token = default) =>
        _kernel.ResolvePartitionTransactionAsync(operation, descriptor, token);

    /// <summary>Captures a pinned MVCC view without rejecting unrelated prepared transactions.</summary>
    public ValueTask<DatabasePartitionSnapshot> CaptureSnapshotAsync(CancellationToken token = default) =>
        _kernel.CapturePartitionSnapshotAsync(token);

    /// <summary>Captures only the declared projection's certificate; it must not authorize other reads.</summary>
    public ValueTask<DatabasePartitionSnapshot> CaptureScopedSnapshotAsync(DatabasePartitionReadScope scope, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return _kernel.CapturePartitionSnapshotAsync(token, scope);
    }

    /// <summary>Checks relevant local stability and cross-owner preparation evidence on the original live pin.</summary>
    public ValueTask<bool> ValidateScopedSnapshotAsync(DatabasePartitionSnapshot snapshot, IReadOnlyList<Guid> prepared, CancellationToken token = default) =>
        _kernel.ValidateScopedPartitionSnapshotAsync(snapshot, prepared, token);

    /// <summary>Waits until the specified preparations are no longer pending; grants no read or commit authority.</summary>
    public ValueTask WaitForSnapshotProgressAsync(IReadOnlyList<Guid> operations, CancellationToken token = default) =>
        _kernel.WaitForSnapshotProgressAsync(operations, token);

    /// <summary>Second collection for a complete-owner optimistic consistent-cut certificate.</summary>
    public ValueTask<bool> ValidateSnapshotAsync(DatabasePartitionSnapshotEvidence evidence, CancellationToken token = default) =>
        _kernel.ValidatePartitionSnapshotAsync(evidence, token);

    /// <summary>Validates the pinned cut and excludes commitments to any preparation captured on another owner.</summary>
    public ValueTask<bool> ValidateSnapshotAsync(DatabasePartitionSnapshotEvidence evidence, IReadOnlyList<Guid> prepared,
        CancellationToken token = default) => _kernel.ValidatePartitionSnapshotAsync(evidence, token, prepared);

    /// <summary>Freezes a resolved local state until the returned handle is disposed.</summary>
    public ValueTask<IAsyncDisposable> FreezeAsync(CancellationToken token = default) => _kernel.FreezePartitionAsync(token);

    /// <summary>Reads retained participant evidence for recovery and drainage.</summary>
    public ValueTask<IReadOnlyList<DurablePartitionTransaction>> ReadAsync(CancellationToken token = default) =>
        _kernel.ReadPartitionTransactionsAsync(token);

    /// <summary>Reads immutable descriptors already selected durably on this native owner.</summary>
    /// <remarks>This grants no distributed decision or placement permission. Missing entries do not
    /// establish an application outcome; callers must use their authority's full observation path.</remarks>
    public ValueTask<IReadOnlyDictionary<Guid, string>> ReadSelectedDescriptorsAsync(IReadOnlyList<Guid> operations,
        CancellationToken token = default) => _kernel.ReadPartitionSelectedDescriptorsAsync(operations, token);

    /// <summary>Rejects unresolved preparation and returns the minimum native position for a fresh read.</summary>
    public ValueTask<DatabaseVersion> AdmitReadAsync(CancellationToken token = default) => _kernel.AdmitPartitionReadAsync(token);

    /// <summary>Captures a sealed transfer image without releasing prepared dependencies or advancing their history floor.</summary>
    public ValueTask<DatabaseVersion> CheckpointForTransferAsync(CancellationToken token = default) =>
        _kernel.CheckpointPartitionTransferAsync(token);

    /// <summary>Checkpoints a retry cutoff while the receiver holds a drained maintenance fence.</summary>
    public ValueTask<long> RetainAsync(DateTimeOffset retainedSince, CancellationToken token = default) =>
        _kernel.RetainPartitionOperationsAsync(retainedSince, token);

    /// <summary>Captures retention under the receiver fence, then invokes captured to release
    /// that fence before durable publication. Unsupported storage retains the fence until completion.</summary>
    public ValueTask<long> RetainConcurrentlyAsync(DateTimeOffset retainedSince, Func<ValueTask> captured,
        CancellationToken token = default) =>
        _kernel.RetainPartitionOperationsAsync(retainedSince, token, captured ?? throw new ArgumentNullException(nameof(captured)));
}
