using System.Runtime.CompilerServices;
using EntityForge.Db.Durability;

namespace EntityForge.Db.Kernel;

internal sealed class DatabaseScopedSnapshotState(DatabaseKernel owner, DatabasePartitionReadScope scope)
{
    internal DatabaseKernel Owner { get; } = owner;

    internal DatabasePartitionReadScope Scope { get; } = scope;

    internal bool Invalid;
}

public sealed partial class DatabaseKernel
{
    private const int MaximumScopedSnapshots = 128;
    private readonly object _scopedSnapshotSync = new();
    private readonly HashSet<DatabaseScopedSnapshotState> _scopedSnapshots = [];
    private int _scopedSnapshotCount;
    private readonly ConditionalWeakTable<DurablePartitionTransaction, DatabaseTransactionFootprint> _scopedFootprints = new();

    private DatabaseTransactionFootprint SnapshotFootprint(DurablePartitionTransaction entry) =>
        _scopedFootprints.GetValue(entry, value =>
            (_profile as IDatabaseProfilePartitionReservations)?.DescribeTransaction(value.Operation, value.Payload)
            ?? DatabaseTransactionFootprint.WholePartition);

    private DatabasePartitionSnapshotEvidence ScopedPartitionSnapshotEvidence(DatabasePartitionReadScope scope)
    {
        // Scoped certification requires no relevant unfinished write on any
        // queried owner. Committed history is unnecessary for that stronger
        // condition; native invalidation protects changes since this capture.
        var prepared = _partitionTransactions.Values.Where(entry =>
            entry.State == DurablePartitionTransactionState.Prepared && !_committedTransactions.ContainsKey(entry.Operation) &&
            SnapshotFootprint(entry).WritesAny(scope.Resources)).Select(entry => entry.Operation).Order().ToArray();
        using var hash = new DatabaseBinaryFingerprint("partition-scoped-snapshot/v2");
        hash.Long(_localOperationFloor);
        hash.Text(scope.Fingerprint);
        hash.Long(prepared.Length);
        foreach (var operation in prepared)
        {
            hash.Guid(operation);
        }

        return new(CurrentVersion, hash.Finish(), prepared, [], scope.Fingerprint);
    }

    // Every successful native mutation, including ordinary/local/grouped writes,
    // passes PublishCommitAsync under commit admission. No persistent history is
    // needed: only live pins are invalidated, before their relevant state changes.
    private void InvalidateScopedSnapshots(Guid operation, ReadOnlyMemory<byte> payload)
    {
        if (Volatile.Read(ref _scopedSnapshotCount) == 0)
        {
            return;
        }

        var writes = (_profile as IDatabaseProfilePartitionReservations)?.DescribeTransaction(operation, payload)
            ?? DatabaseTransactionFootprint.WholePartition;
        lock (_scopedSnapshotSync)
        {
            foreach (var pin in _scopedSnapshots)
            {
                if (writes.WritesAny(pin.Scope.Resources))
                {
                    pin.Invalid = true;
                }
            }
        }
    }

    private void InvalidateScopedSnapshots()
    {
        lock (_scopedSnapshotSync)
        {
            foreach (var pin in _scopedSnapshots)
            {
                pin.Invalid = true;
            }
        }
    }

    private IDisposable OwnScopedSnapshot(DatabaseScopedSnapshotState state, IDisposable pin)
    {
        lock (_scopedSnapshotSync)
        {
            if (_scopedSnapshots.Count >= MaximumScopedSnapshots)
            {
                throw new InvalidOperationException("Scoped snapshot capacity is exhausted.");
            }

            _scopedSnapshots.Add(state);
            Volatile.Write(ref _scopedSnapshotCount, _scopedSnapshots.Count);
        }
        return new ScopedSnapshotPin(this, state, pin);
    }

    private sealed class ScopedSnapshotPin(DatabaseKernel owner, DatabaseScopedSnapshotState state, IDisposable pin) : IDisposable
    {
        public void Dispose()
        {
            lock (owner._scopedSnapshotSync)
            {
                state.Invalid = true;
                owner._scopedSnapshots.Remove(state);
                Volatile.Write(ref owner._scopedSnapshotCount, owner._scopedSnapshots.Count);
            }
            pin.Dispose();
        }
    }

    internal async ValueTask<bool> ValidateScopedPartitionSnapshotAsync(DatabasePartitionSnapshot snapshot,
        IReadOnlyList<Guid> prepared, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(prepared);
        if (prepared.Count > DatabasePartitionSnapshotSummary.MaximumPreparedProbe)
        {
            throw new ArgumentException("Snapshot preparation probe exceeds its bound.");
        }

        prepared = prepared.ToArray();
        DatabasePartitionSnapshotSummary.RequireProbe(prepared);
        if (snapshot.ScopedState is not { } state || state.Owner != this || snapshot.Evidence.Scope != state.Scope.Fingerprint)
        {
            throw new ArgumentException("A live scoped pin owned by this kernel is required.");
        }

        await _commitCoordinator.WaitAsync(token).ConfigureAwait(false);
        try
        {
            EnsureReady();
            lock (_scopedSnapshotSync)
            {
                if (state.Invalid || !_scopedSnapshots.Contains(state))
                {
                    return false;
                }
            }

            var evidence = snapshot.Evidence;
            // A projection with any unapplied relevant write is not certified,
            // even if that transaction is absent from another owner's filtered
            // commitments. That owner may be read-only or outside this scope.
            // Combined with local stability, this establishes a common interval
            // with no outstanding write to any requested resource.
            if (evidence.Prepared.Count != 0 || prepared.Count != 0)
            {
                return false;
            }

            var current = PartitionSnapshotEvidence(state.Scope);
            return current.Protocol == evidence.Protocol && current.Prepared.SequenceEqual(evidence.Prepared) &&
                current.Committed.SequenceEqual(evidence.Committed);
        }
        finally
        {
            _commitCoordinator.Release();
        }
    }
}
