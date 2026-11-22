using System.Security.Cryptography;
using System.Collections.Frozen;
using System.Text;
using System.Text.Json.Serialization;
using EntityForge.Db.Durability;

namespace EntityForge.Db.Kernel;

/// <summary>A profile pin owns a historical logical view and its physical reclamation protection.</summary>
public interface IDatabaseProfileSnapshotPin
{
    /// <summary>Enters optional profile admission while selecting a snapshot pin; the default requires no additional scope.</summary>
    ValueTask<IDisposable?> EnterSnapshotPinAsync(CancellationToken token) => ValueTask.FromResult<IDisposable?>(null);

    /// <summary>Pins the historical view at the supplied native version until the returned handle is disposed.</summary>
    IDisposable PinSnapshot(DatabaseVersion version);
}

/// <summary>Immutable participant evidence captured atomically with the pinned native view.</summary>
public sealed record DatabasePartitionSnapshotEvidence
{
    /// <summary>Creates bounded snapshot evidence from copied, distinct prepared and committed identities, validating protocol and optional scope fingerprints.</summary>
    [JsonConstructor]
    public DatabasePartitionSnapshotEvidence(DatabaseVersion version, string protocol,
        IReadOnlyList<Guid> prepared, IReadOnlyList<Guid> committed, string? scope = null)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(committed);
        if (scope is not null && (scope.Length != 64 || !scope.All(Uri.IsHexDigit)) ||
            version.Value < 0 || protocol is not { Length: 64 } || !protocol.All(Uri.IsHexDigit) ||
            prepared.Count + committed.Count > DurablePartitionTransaction.MaximumTransactions ||
            prepared.Concat(committed).Any(id => id == Guid.Empty) ||
            prepared.Concat(committed).Distinct().Count() != prepared.Count + committed.Count)
        {
            throw new ArgumentException("Invalid bounded snapshot certificate.");
        }

        Version = version;
        Protocol = protocol;
        Scope = scope;
        Prepared = Array.AsReadOnly(prepared.ToArray());
        Committed = Array.AsReadOnly(committed.ToArray());
    }

    /// <summary>Gets the native version captured with the pinned view.</summary>
    public DatabaseVersion Version
    {
        get;
    }

    /// <summary>Gets the hexadecimal fingerprint of the captured participant protocol state.</summary>
    public string Protocol
    {
        get;
    }

    /// <summary>Gets the projection scope fingerprint, or null for unrestricted evidence.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Scope
    {
        get;
    }

    /// <summary>Gets the complete prepared operation identities captured with the snapshot.</summary>
    public IReadOnlyList<Guid> Prepared
    {
        get;
    }

    /// <summary>Gets the complete retained committed identities captured with the snapshot.</summary>
    public IReadOnlyList<Guid> Committed
    {
        get;
    }

    // Keep memoization behind a stable holder so record equality/hash codes never change.
    private readonly DerivedEvidence _derived = new();

    private sealed class DerivedEvidence
    {
        internal DatabasePartitionSnapshotSummary? Summary;
        internal FrozenSet<Guid>? CommittedIndex;
    }

    /// <summary>The sample supports early rejection only; validation must probe the complete pinned evidence.</summary>
    public DatabasePartitionSnapshotSummary Summarize() => LazyInitializer.EnsureInitialized(ref _derived.Summary, () =>
        new(Scope is null ? DatabasePartitionSnapshotSummary.CurrentFormat : DatabasePartitionSnapshotSummary.ScopedFormat, Version, Protocol, Prepared,
            Committed.TakeLast(DatabasePartitionSnapshotSummary.MaximumCommitmentSample).ToArray(), Scope));

    internal bool HasCommittedAny(IReadOnlyList<Guid> prepared) => prepared.Count != 0 &&
        prepared.Any(LazyInitializer.EnsureInitialized(ref _derived.CommittedIndex, () => Committed.ToFrozenSet()).Contains);
}

/// <summary>Owns a pinned native partition view and the evidence captured atomically with it.</summary>
public sealed class DatabasePartitionSnapshot : IDatabaseTimestampProtection
{
    private readonly object _disposeGate = new();
    private IDisposable? _pin;

    /// <summary>Gets the immutable snapshot certificate associated with the pin.</summary>
    public DatabasePartitionSnapshotEvidence Evidence
    {
        get;
    }

    internal DatabaseScopedSnapshotState? ScopedState
    {
        get;
    }

    internal DatabaseKernel? Owner
    {
        get; init;
    }

    /// <summary>Gets whether the snapshot pin has been released.</summary>
    public bool IsDisposed => Volatile.Read(ref _pin) is null;

    internal DatabasePartitionSnapshot(DatabasePartitionSnapshotEvidence evidence, IDisposable pin, DatabaseScopedSnapshotState? scopedState = null)
    {
        Evidence = evidence;
        _pin = pin;
        ScopedState = scopedState;
    }

    /// <summary>Releases the native pin and its registered timestamp-history protection.</summary>
    public void Dispose()
    {
        lock (_disposeGate)
        {
            _pin?.Dispose();
            Volatile.Write(ref _pin, null);
            Owner?.ReleaseTimestampGeneration(this);
        }
    }
}

public sealed partial class DatabaseKernel
{
    private SnapshotCertificate? _snapshotCertificate;

    private sealed record SnapshotEntry(DurablePartitionTransaction Preparation, CommittedTransaction? Commitment);

    private sealed record SnapshotCertificate(long Floor, DatabasePartitionSnapshotEvidence Evidence, SnapshotEntry[] Entries);

    internal async ValueTask<DatabasePartitionSnapshot> CapturePartitionSnapshotAsync(CancellationToken token, DatabasePartitionReadScope? scope = null)
    {
        RequirePartitionTransactions();
        if (scope is not null && (scope.Profile != Profile || _profile is not IDatabaseProfileScopedReads))
        {
            throw new NotSupportedException("Read scope does not match the receiving profile.");
        }

        if (_profile is not IDatabaseProfileSnapshotPin snapshots)
        {
            throw new NotSupportedException("This profile has not qualified transient MVCC snapshot pins.");
        }

        using var trace = _telemetry.Start("db.snapshot.capture", _profile.Profile);
        IDisposable? admission;
        using (_telemetry.Start("db.snapshot.profile_wait", _profile.Profile))
        {
            admission = await snapshots.EnterSnapshotPinAsync(token).ConfigureAwait(false);
        }

        bool entered = false;
        try
        {
            using (_telemetry.Start("db.snapshot.capture_wait", _profile.Profile))
            {
                await _commitCoordinator.WaitAsync(token).ConfigureAwait(false);
            }

            entered = true;
            EnsureReady();
            await ReserveTimestampReadAsync(token).ConfigureAwait(false);
            var evidence = PartitionSnapshotEvidence(scope);
            var pin = snapshots.PinSnapshot(evidence.Version);
            var owned = ProtectTimestampSnapshot(new SnapshotPin(pin, admission));
            admission = null;
            if (scope is null)
            {
                var snapshot = new DatabasePartitionSnapshot(evidence, owned) { Owner = this };
                try
                {
                    if (RegisterTimestampGeneration(snapshot) is { } floor)
                    {
                        // Register under admission, but do not hold native write
                        // admission while waiting. The owned generation already
                        // pins the floor and is disposed on cancellation/failure.
                        _commitCoordinator.Release();
                        entered = false;
                        // The gateway's fresh upper bound must cover this floor.
                        await WaitForTimestampClockAsync(floor, token, "snapshot_protection").ConfigureAwait(false);
                    }
                    return snapshot;
                }
                catch
                {
                    snapshot.Dispose();
                    throw;
                }
            }
            var state = new DatabaseScopedSnapshotState(this, scope);
            try
            {
                return new(evidence, OwnScopedSnapshot(state, owned), state);
            }
            catch
            {
                owned.Dispose();
                throw;
            }
        }
        finally
        {
            if (entered)
            {
                _commitCoordinator.Release();
            }

            admission?.Dispose();
        }
    }

    internal async ValueTask<bool> ValidatePartitionSnapshotAsync(DatabasePartitionSnapshotEvidence evidence, CancellationToken token, IReadOnlyList<Guid>? prepared = null)
    {
        RequirePartitionTransactions();
        if (evidence.Scope is not null)
        {
            throw new ArgumentException("Scoped evidence requires its original live scoped pin.");
        }

        if (prepared is not null)
        {
            if (prepared.Count > DatabasePartitionSnapshotSummary.MaximumPreparedProbe)
            {
                throw new ArgumentException("Snapshot preparation probe exceeds its bound.");
            }

            prepared = prepared.ToArray(); // Own the probe before awaiting native admission.
            DatabasePartitionSnapshotSummary.RequireProbe(prepared);
        }
        using var trace = _telemetry.Start("db.snapshot.validate", _profile.Profile);
        using (_telemetry.Start("db.snapshot.validate_wait", _profile.Profile))
        {
            await _commitCoordinator.WaitAsync(token).ConfigureAwait(false);
        }

        try
        {
            EnsureReady();
            // A changed native position already invalidates this certificate.
            // Equal positions still need protocol evidence: prepare, abort and
            // retention can change it without advancing the native version.
            if (CurrentVersion != evidence.Version || prepared is not null && evidence.HasCommittedAny(prepared))
            {
                return false;
            }

            var current = PartitionSnapshotEvidence();
            return current.Version == evidence.Version && current.Protocol == evidence.Protocol &&
                current.Prepared.SequenceEqual(evidence.Prepared) && current.Committed.SequenceEqual(evidence.Committed);
        }
        finally
        {
            _commitCoordinator.Release();
        }
    }

    private sealed class SnapshotPin(IDisposable pin, IDisposable? admission) : IDisposable
    {
        private bool _pinReleased;
        private bool _admissionReleased;

        public void Dispose()
        {
            if (!_pinReleased)
            {
                pin.Dispose();
                _pinReleased = true;
            }
            if (!_admissionReleased)
            {
                admission?.Dispose();
                _admissionReleased = true;
            }
        }
    }

    private DatabasePartitionSnapshotEvidence PartitionSnapshotEvidence(DatabasePartitionReadScope? scope = null)
    {
        using var trace = _telemetry.Start("db.snapshot.certificate", _profile.Profile);
        trace?.SetTag("entityforge.entries", _partitionTransactions.Count);
        if (scope is not null)
        {
            return ScopedPartitionSnapshotEvidence(scope);
        }
        // All callers own commit admission. Reuse interpretation of immutable
        // evidence only; each observation still checks readiness and current state.
        if (_snapshotCertificate is { } cached && cached.Floor == _localOperationFloor &&
            cached.Evidence.Version == CurrentVersion && cached.Entries.Length == _partitionTransactions.Count &&
            cached.Entries.All(entry =>
                _partitionTransactions.TryGetValue(entry.Preparation.Operation, out var current) && ReferenceEquals(current, entry.Preparation) &&
                (_committedTransactions.TryGetValue(entry.Preparation.Operation, out var commitment) ? (CommittedTransaction?)commitment : null) == entry.Commitment))
        {
            return cached.Evidence;
        }
        // Include all transitions, including preparation/abort which do not advance native position.
        // Retention cannot produce ABA: its monotonic identity floor is part of the certificate.
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes, Encoding.UTF8, true);
        writer.Write(_localOperationFloor);
        var prepared = new List<Guid>();
        var committedOperations = new List<Guid>();
        var entries = new List<SnapshotEntry>(_partitionTransactions.Count);
        foreach (var entry in _partitionTransactions.Values.OrderBy(entry => entry.Operation))
        {
            var state = entry.State;
            var version = entry.LocalVersion;
            bool hasCommitment = _committedTransactions.TryGetValue(entry.Operation, out var committed);
            entries.Add(new(entry, hasCommitment ? committed : null));
            if (hasCommitment)
            {
                if (state == DurablePartitionTransactionState.Aborted ||
                    !PartitionPayloadHash(entry).AsSpan().SequenceEqual(committed.ContentHash))
                {
                    throw new InvalidDataException("Snapshot transaction evidence contradicts native commitment.");
                }
                // Project the effective terminal fields without copying the durable payload.
                // Native commitment can precede the partition terminal projection.
                state = DurablePartitionTransactionState.Committed;
                version = committed.Version;
            }
            writer.Write(entry.Operation.ToByteArray());
            writer.Write(entry.Descriptor);
            writer.Write((byte)state);
            writer.Write(version?.Value ?? 0);
            if (state == DurablePartitionTransactionState.Prepared)
            {
                prepared.Add(entry.Operation);
            }

            if (state == DurablePartitionTransactionState.Committed)
            {
                committedOperations.Add(entry.Operation);
            }
        }
        writer.Flush();
        var evidence = new DatabasePartitionSnapshotEvidence(CurrentVersion, Convert.ToHexString(SHA256.HashData(bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length)))),
            prepared, committedOperations);
        _snapshotCertificate = new(_localOperationFloor, evidence, entries.ToArray());
        return evidence;
    }
}
