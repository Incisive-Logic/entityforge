namespace EntityForge.Db;

/// <summary>Explicit local database maintenance for bounded operation replay.</summary>
public interface IDatabaseOperationRetention
{
    /// <summary>Checkpoints and expires completed operations created before the UTC cutoff.</summary>
    /// <remarks>
    /// The cutoff is monotonic and uses UUIDv7 creation time, not first submission. At a positive
    /// cutoff non-UUIDv7 operation IDs also expire. Expiry does not indicate an abort. Preserve
    /// original IDs when retrying; replacing an expired ID can repeat effects. Undecided expired
    /// bindings block maintenance. Distributed participants must use authority-coordinated retention.
    /// Graph and RDF local retention also perform physical compaction and reclamation under
    /// write exclusion. This can take longer than an ordinary checkpoint and requires temporary
    /// storage for replacement pages. Existing snapshots prevent maintenance admission; release
    /// them and retry. New snapshot leases are rejected while physical maintenance is in progress.
    /// After local expiry this incarnation cannot enroll in distributed execution; provisioning a
    /// new distributed incarnation is required so an authority cannot admit already-expired work.
    /// </remarks>
    ValueTask<DatabaseVersion> RetainOperationsAsync(DateTimeOffset retainedSince,
        CancellationToken cancellationToken = default);
}
