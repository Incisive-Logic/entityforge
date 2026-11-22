namespace EntityForge.Db.Semantic;

/// <summary>Protects a semantic snapshot from reclamation until its expiry or explicit release.</summary>
/// <param name="LeaseId">Identity used to renew or release this lease.</param>
/// <param name="SnapshotVersion">Semantic snapshot retained by the lease.</param>
/// <param name="ExpiresAtUtc">UTC time after which retention is no longer guaranteed.</param>
/// <param name="Revision">Lease-state revision used to distinguish renewals.</param>
public sealed record SnapshotLease(
    Guid LeaseId,
    DatabaseVersion SnapshotVersion,
    DateTimeOffset ExpiresAtUtc,
    long Revision);

/// <summary>
/// Couples a retained semantic snapshot to the exact committed-change position immediately following it.
/// Consumers can enumerate the leased snapshot and then resume the change feed after this cursor without a gap.
/// </summary>
/// <param name="Lease">Retention lease for the bootstrap snapshot.</param>
/// <param name="ChangeCursor">Exact committed-change boundary represented by the snapshot; resume the feed after it.</param>
public sealed record SemanticChangeBootstrap(
    SnapshotLease Lease,
    DatabaseChangeCursor? ChangeCursor);

/// <summary>Identifies a durable snapshot-lease state transition.</summary>
public enum SnapshotLeaseChangeKind
{
    /// <summary>A lease was acquired to retain a snapshot.</summary>
    Acquire,

    /// <summary>An existing lease was renewed.</summary>
    Renew,

    /// <summary>An existing lease was released.</summary>
    Release
}

/// <summary>Describes a durable snapshot-lease state transition.</summary>
/// <param name="LeaseId">Identity of the affected lease.</param>
/// <param name="Kind">Acquire, renew or release transition.</param>
/// <param name="SnapshotVersion">Snapshot associated with the lease.</param>
/// <param name="ExpiresAtUtc">Expiry recorded by this transition.</param>
/// <param name="Revision">Lease-state revision assigned to the transition.</param>
public sealed record SnapshotLeaseChange(
    Guid LeaseId,
    SnapshotLeaseChangeKind Kind,
    DatabaseVersion SnapshotVersion,
    DateTimeOffset ExpiresAtUtc,
    long Revision);
