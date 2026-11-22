using EntityForge.Db.Semantic;

namespace EntityForge.Db.Hosting;

/// <summary>Requests a snapshot lease with a duration measured in seconds.</summary>
internal sealed record SnapshotLeaseRequest(int DurationSeconds);

/// <summary>Requests optimistic renewal of an existing snapshot lease.</summary>
internal sealed record SnapshotLeaseRenewalRequest(long ExpectedRevision, int DurationSeconds);

/// <summary>Describes a hosted semantic snapshot lease.</summary>
internal sealed record SnapshotLeaseResponse(
    Guid LeaseId,
    long SnapshotVersion,
    DateTimeOffset ExpiresAtUtc,
    long Revision);

internal static class SnapshotLeaseResponseMapper
{
    public static SnapshotLeaseResponse Map(SnapshotLease lease) => new(
        lease.LeaseId, lease.SnapshotVersion.Value, lease.ExpiresAtUtc, lease.Revision);

    public static TimeSpan Duration(int durationSeconds)
    {
        if (durationSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(durationSeconds),
                "A snapshot lease duration must be positive.");
        }

        return TimeSpan.FromSeconds(durationSeconds);
    }
}
