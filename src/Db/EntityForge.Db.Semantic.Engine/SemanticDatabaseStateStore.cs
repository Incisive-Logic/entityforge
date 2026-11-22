using System.Diagnostics;
using System.Runtime.CompilerServices;
using EntityForge.Db.Semantic.Core;
using EntityForge.Db.Semantic;
using EntityForge.Db.Telemetry;

namespace EntityForge.Db.Semantic.Engine;

internal interface ISemanticDatabaseStateReader
{
    SemanticDatabaseState Current
    {
        get;
    }

    SemanticDatabaseState Working
    {
        get;
    }

    DatabaseReadView ReadView
    {
        get;
    }

    DatabaseVersion RetentionFloor(SemanticDatabaseState state, DateTimeOffset now) => state.SnapshotLeases.RetentionFloor(state.Version, now);
}

internal interface ISemanticDatabaseStatePublisher : ISemanticDatabaseStateReader
{
    IDisposable PinSnapshot(DatabaseVersion version) => throw new NotSupportedException("Transient snapshot pins are unavailable.");

    void Stage(SemanticDatabaseState state);

    void Publish(SemanticDatabaseState state);

    void PrunePreparationHistory(DatabaseVersion floor);
}

internal sealed class SemanticDatabaseStateStore : ISemanticDatabaseStatePublisher
{
    // Query-ready state belongs to this native owner and exact immutable MVCC
    // state. Weak ownership never extends the history/retention lifetime.
    private readonly ConditionalWeakTable<SemanticDatabaseState, Lazy<DatabaseReadView>> _timestampServingViews = new();

    internal DatabaseReadView TimestampServingView(SemanticDatabaseState state) =>
        _timestampServingViews.GetValue(state, static selected => new(() => DatabaseReadView.Create(selected, SemanticTimestampIndex.For(selected)),
            LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    private readonly AsyncLocal<SemanticDatabaseState?> _timestampRead = new();

    internal SemanticDatabaseState? TimestampRead => _timestampRead.Value;

    internal IDisposable EnterTimestampRead(SemanticDatabaseState state)
    {
        if (_timestampRead.Value is not null)
        {
            throw new InvalidOperationException("Nested Semantic timestamp read.");
        }

        _timestampRead.Value = state;
        return new TimestampReadContext(this);
    }

    private sealed class TimestampReadContext(SemanticDatabaseStateStore owner) : IDisposable
    {
        public void Dispose() => owner._timestampRead.Value = null;
    }

    private readonly SemanticCommitLogCandidateAccessPath _physicalCandidates;
    private readonly EntityForgeDbTelemetry _telemetry;
    private SemanticDatabaseState _current = SemanticDatabaseState.Empty;
    private SemanticDatabaseState _working = SemanticDatabaseState.Empty;
    private readonly object _historyGate = new();
    private readonly SortedDictionary<long, int> _snapshotPins = [];

    public DatabaseVersion RetentionFloor(SemanticDatabaseState state, DateTimeOffset now)
    {
        long floor = state.SnapshotLeases.RetentionFloor(state.Version, now).Value;
        lock (_historyGate)
        {
            return new(Math.Min(floor, _snapshotPins.Keys.DefaultIfEmpty(floor).First()));
        }
    }

    public IDisposable PinSnapshot(DatabaseVersion version)
    {
        lock (_historyGate)
        {
            if (version.Value > Current.Version.Value || version != Current.Version && !_preparationHistory.ContainsKey(version.Value))
            {
                throw new InvalidOperationException("Snapshot pin requires retained published history.");
            }

            _snapshotPins[version.Value] = _snapshotPins.GetValueOrDefault(version.Value) + 1;
            return new SnapshotPin(this, version.Value);
        }
    }

    private sealed class SnapshotPin(SemanticDatabaseStateStore owner, long version) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
            {
                return;
            }

            lock (owner._historyGate)
            {
                if (owner._snapshotPins[version] == 1)
                {
                    owner._snapshotPins.Remove(version);
                }
                else
                {
                    owner._snapshotPins[version]--;
                }
            }
        }
    }

    private readonly SortedDictionary<long, SemanticDatabaseState> _preparationHistory = [];

    internal Func<bool> IsDistributed { get; set; } = static () => false;

    internal SemanticDatabaseState CapturePreparationState(DatabaseVersion version)
    {
        lock (_historyGate)
        {
            if (_preparationHistory.TryGetValue(version.Value, out var state))
            {
                return state;
            }

            var current = PreparationSnapshot.State;
            if (current.Version == version)
            {
                return current;
            }

            throw new EntityForgeSnapshotUnavailableException(version,
                new(_preparationHistory.Keys.DefaultIfEmpty(current.Version.Value).Min()));
        }
    }

    public void PrunePreparationHistory(DatabaseVersion floor)
    {
        lock (_historyGate)
        {
            foreach (long version in _preparationHistory.Keys.Where(v => v < floor.Value && !_snapshotPins.ContainsKey(v)).ToArray())
            {
                _preparationHistory.Remove(version);
            }
        }
    }

    private DatabaseReadView _readView = DatabaseReadView.Empty;
    private DatabasePreparationSnapshot _preparationSnapshot = new(
        SemanticDatabaseState.Empty, new SemanticCatalogRegistry(SemanticDatabaseState.Empty));

    public SemanticDatabaseStateStore(
        SemanticCommitLogCandidateAccessPath physicalCandidates,
        EntityForgeDbTelemetry telemetry)
    {
        _physicalCandidates = physicalCandidates ?? throw new ArgumentNullException(nameof(physicalCandidates));
        _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
    }

    public SemanticDatabaseState Current => Volatile.Read(ref _current);

    // Only the serialized planner/publication path reads speculative predecessors.
    public SemanticDatabaseState Working => _working;

    public DatabaseReadView ReadView => Volatile.Read(ref _readView);

    public DatabasePreparationSnapshot PreparationSnapshot =>
        Volatile.Read(ref _preparationSnapshot);

    public void Stage(SemanticDatabaseState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _working = state;
    }

    public void Publish(SemanticDatabaseState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        long started = Stopwatch.GetTimestamp();
        _physicalCandidates.Capture(state.Version);
        DatabaseReadView view = DatabaseReadView.Apply(ReadView, state, _physicalCandidates);
        _working = state;
        Volatile.Write(ref _current, state);
        Volatile.Write(ref _readView, view);
        lock (_historyGate)
        {
            _preparationHistory[state.Version.Value] = state;
            int capacity = IsDistributed() ? DistributedSnapshotAcquisition.MaximumVersions : 1;
            foreach (var version in _preparationHistory.Keys.Where(version => !_snapshotPins.ContainsKey(version))
                .Take(Math.Max(0, _preparationHistory.Count - _snapshotPins.Count - capacity)).ToArray())
            {
                _preparationHistory.Remove(version);
            }

            Volatile.Write(ref _preparationSnapshot, new DatabasePreparationSnapshot(state, view.Catalog));
        }
        _telemetry.RecordPublication(Stopwatch.GetElapsedTime(started),
            state.AssertionStatistics.FieldCount, state.AssertionStatistics.RelationshipCount);
    }

    public void RestorePublishedPreparationState() =>
        _working = PreparationSnapshot.State;
}
