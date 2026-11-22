using System.Collections.Immutable;
using EntityForge.Db.Semantic;
using EntityForge.Db.Semantic.Durability;

namespace EntityForge.Db.Semantic.Core;

/// <summary>Applies durable resumable-write lifecycle mutations to retained transaction state.</summary>
public sealed class ResumableWriteStateProjector
{
    private const long InitialLeaseRevision = 1;
    private const long LeaseRevisionIncrement = 1;

    /// <summary>Projects the supplied resumable-write mutation while validating its transaction lifecycle.</summary>
    public SemanticDatabaseState Apply(SemanticDatabaseState state,
        ResumableWriteChange change, DatabaseVersion version)
    {
        ImmutableDictionary<Guid, ResumableWriteTransaction> writes =
            state.ResumableWrites as ImmutableDictionary<Guid, ResumableWriteTransaction> ??
            state.ResumableWrites.ToImmutableDictionary();
        SnapshotLeaseState leases;
        if (change.Kind is ResumableWriteChangeKind.Prepare)
        {
            if (writes.ContainsKey(change.Transaction.TransactionId))
            {
                throw new InvalidDataException("A resumable transaction ID must be unique.");
            }

            writes = writes.Add(change.Transaction.TransactionId, change.Transaction);
            leases = state.SnapshotLeases.Apply(new(change.Transaction.LeaseId,
                SnapshotLeaseChangeKind.Acquire, change.Transaction.ReadVersion,
                change.Transaction.ExpiresAtUtc, InitialLeaseRevision));
        }
        else if (change.Kind is ResumableWriteChangeKind.UpdatePredicateReads)
        {
            if (!writes.TryGetValue(change.Transaction.TransactionId,
                    out ResumableWriteTransaction? existing) ||
                existing.LeaseId != change.Transaction.LeaseId ||
                existing.ReadVersion != change.Transaction.ReadVersion ||
                existing.Session.Close.ContentHash != change.Transaction.Session.Close.ContentHash)
            {
                throw new InvalidDataException("A resumable transaction read-set update has no matching intent.");
            }

            writes = writes.SetItem(change.Transaction.TransactionId, change.Transaction);
            leases = state.SnapshotLeases;
        }
        else
        {
            if (!writes.ContainsKey(change.Transaction.TransactionId))
            {
                throw new InvalidDataException("A resumable transaction close has no prepared intent.");
            }

            writes = writes.Remove(change.Transaction.TransactionId);
            SnapshotLease lease = state.SnapshotLeases.Active[change.Transaction.LeaseId];
            leases = state.SnapshotLeases.Apply(new(change.Transaction.LeaseId,
                SnapshotLeaseChangeKind.Release, lease.SnapshotVersion, lease.ExpiresAtUtc,
                checked(lease.Revision + LeaseRevisionIncrement)));
        }
        return state with
        {
            Version = version,
            SnapshotLeases = leases,
            ResumableWrites = writes
        };
    }
}
