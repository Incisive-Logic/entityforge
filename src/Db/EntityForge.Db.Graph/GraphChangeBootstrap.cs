using EntityForge.Db.Graph.Contracts;

namespace EntityForge.Db.Graph;

/// <summary>Opaque continuation within one leased logical Graph snapshot.</summary>
public readonly record struct GraphSnapshotCursor
{
    /// <summary>Creates an opaque continuation from its serialized value.</summary>
    public GraphSnapshotCursor(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    /// <summary>Gets the opaque serialized continuation.</summary>
    public string Value
    {
        get;
    }

    /// <summary>Returns the opaque serialized snapshot continuation unchanged.</summary>
    public override string ToString() => Value;
}

/// <summary>Contains one bounded page from a leased logical Graph snapshot.</summary>
/// <param name="Effects">Logical graph state represented by this page.</param>
/// <param name="NextCursor">Opaque next-page cursor, or null when the snapshot is exhausted.</param>
public sealed record GraphLogicalSnapshotPage(
    GraphCommittedEffects Effects,
    GraphSnapshotCursor? NextCursor);

/// <summary>Couples a retained Graph snapshot to the exact committed-change boundary represented by it.</summary>
/// <param name="Lease">Retention lease for the snapshot used to bootstrap the consumer.</param>
/// <param name="ChangeCursor">Committed-change boundary represented by the snapshot; resume changes after this boundary.</param>
public sealed record GraphChangeBootstrap(
    GraphSnapshotLease Lease,
    DatabaseChangeCursor? ChangeCursor);
