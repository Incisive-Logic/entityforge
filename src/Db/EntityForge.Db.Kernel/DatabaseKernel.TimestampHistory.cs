namespace EntityForge.Db.Kernel;

/// <summary>Coordinates partition operation admission with an external logical timestamp authority.</summary>
public interface IDatabasePartitionTimestampAdmission
{
    /// <summary>Admits an exact operation identity and descriptor and returns authority-owned admission state.</summary>
    ValueTask<object> EnterAsync(Guid operation, string descriptor, CancellationToken token);

    /// <summary>Reports the native completion receipt and position for a previously admitted operation.</summary>
    ValueTask AppliedAsync(object admission, DatabaseCommitReceipt? receipt, DatabaseVersion position, CancellationToken token);
}

public sealed partial class DatabaseKernel
{
    private const int MaximumTimestampHistory = 8192;
    private readonly SortedDictionary<long, IDisposable> _timestampPins = [];
    private bool _timestampHistory;

    internal IDatabasePartitionTimestampAdmission? TimestampAdmission
    {
        get; set;
    }

    internal async ValueTask EnableTimestampHistoryAsync(CancellationToken token)
    {
        await _commitCoordinator.WaitAsync(token);
        try
        {
            EnsureReady();
            _timestampHistory = true;
            PinTimestampVersion(CurrentVersion);
        }
        finally
        {
            _commitCoordinator.Release();
        }
    }

    private void PinTimestampVersion(DatabaseVersion version)
    {
        if (!_timestampHistory || _timestampPins.ContainsKey(version.Value))
        {
            return;
        }

        if (_timestampPins.Count >= MaximumTimestampHistory)
        {
            throw new InvalidOperationException("Timestamp history capacity requires closed-frontier reclamation.");
        }

        _timestampPins.Add(version.Value, ((IDatabaseProfileSnapshotPin)_profile).PinSnapshot(version));
    }

    internal async ValueTask TrimTimestampHistoryAsync(DatabaseVersion floor, CancellationToken token)
    {
        await _commitCoordinator.WaitAsync(token);
        try
        {
            EnsureReady();
            if (floor.Value > CurrentVersion.Value)
            {
                throw new InvalidDataException("Timestamp floor is ahead of native application.");
            }

            foreach (long old in _timestampPins.Keys.TakeWhile(v => v < floor.Value).ToArray())
            {
                _timestampPins[old].Dispose();
                _timestampPins.Remove(old);
            }
        }
        finally
        {
            _commitCoordinator.Release();
        }
    }

    internal async ValueTask<DatabasePartitionSnapshot> CaptureTimestampSnapshotAsync(DatabaseVersion version, CancellationToken token)
    {
        var snapshots = (IDatabaseProfileSnapshotPin)_profile;
        IDisposable? admission = await snapshots.EnterSnapshotPinAsync(token);
        bool entered = false;
        try
        {
            await _commitCoordinator.WaitAsync(token);
            entered = true;
            EnsureReady();
            if (!_timestampHistory || version.Value > CurrentVersion.Value || !_timestampPins.ContainsKey(version.Value))
            {
                throw new InvalidOperationException("The logical timestamp's native history is not retained.");
            }

            var pin = snapshots.PinSnapshot(version);
            var owned = new SnapshotPin(pin, admission);
            admission = null;
            return new(new(version, new string('0', 64), [], []), owned);
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

    internal async ValueTask ReleaseTimestampHistoryAsync(CancellationToken token)
    {
        await _commitCoordinator.WaitAsync(token);
        try
        {
            foreach (var pin in _timestampPins.Values)
            {
                pin.Dispose();
            }

            _timestampPins.Clear();
            _timestampHistory = false;
        }
        finally
        {
            _commitCoordinator.Release();
        }
    }
}
