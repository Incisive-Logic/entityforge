using EntityForge.Db.Durability;

namespace EntityForge.Db.Kernel;

/// <summary>Reconstructible logical snapshots; physical append order is not logical visibility order.</summary>
public sealed class DatabaseLogicalHistory<TView, TEffect> where TView : class
{
    internal const int MaximumVersions = 8192;
    private const long MaximumRetainedBytes = 256L * 1024 * 1024;

    private sealed record Entry(Guid Operation, DurableTimestampState Time, TEffect Effect, TView View, long Bytes);

    private readonly List<Entry> _entries = [];
    private readonly Dictionary<Guid, long> _reservations = [];
    private long _retainedBytes;
    private long _reservedBytes;
    private readonly Func<TView, TEffect, TView> _apply;
    private TView _basis;

    /// <summary>Gets the oldest logical timestamp still represented by the retained history.</summary>
    public DurableTimestampState Floor
    {
        get; private set;
    }

    /// <summary>Gets the number of retained logical versions above the history floor.</summary>
    public int Count => _entries.Count;

    /// <summary>Encoded logical evidence retained above the history floor; not managed heap size.</summary>
    public long RetainedBytes => _retainedBytes;

    /// <summary>Reserves bounded evidence capacity before native durability. Replanning the same operation replaces its charge.</summary>
    public void Reserve(Guid operation, long bytes)
    {
        if (operation == Guid.Empty || bytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bytes));
        }

        long previous = _reservations.GetValueOrDefault(operation);
        if (_entries.Count + _reservations.Count + (_reservations.ContainsKey(operation) ? 0 : 1) > MaximumVersions ||
            bytes > MaximumRetainedBytes - _retainedBytes - (_reservedBytes - previous))
        {
            throw new InvalidOperationException("Logical history evidence capacity requires reader drainage/reclamation.");
        }

        _reservations[operation] = bytes;
        _reservedBytes = checked(_reservedBytes - previous + bytes);
    }

    /// <summary>Releases capacity belonging to an unsubmitted or abandoned candidate.</summary>
    public void Abandon(Guid operation)
    {
        if (_reservations.Remove(operation, out long bytes))
        {
            _reservedBytes -= bytes;
        }
    }

    /// <summary>Rejects admission when the requested number of additional versions exceeds the bounded history capacity.</summary>
    public void RequireCapacity(int additional)
    {
        if (additional < 1 || additional > MaximumVersions - _entries.Count)
        {
            throw new InvalidOperationException("Logical history requires reader drainage/reclamation before admission.");
        }
    }

    /// <summary>Creates logical history from an immutable basis, its retention floor and a function that applies one logical effect.</summary>
    public DatabaseLogicalHistory(TView basis, DurableTimestampState floor, Func<TView, TEffect, TView> apply)
    {
        _basis = basis;
        Floor = floor;
        _apply = apply;
    }

    /// <summary>Returns the reconstructed view at the requested logical time; throws when the time predates the retained floor.</summary>
    public TView Read(DurableTimestampState time)
    {
        if (time.CompareTo(Floor) < 0)
        {
            throw new InvalidOperationException("Timestamp history is no longer protected.");
        }

        int low = 0, high = _entries.Count;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (_entries[middle].Time.CompareTo(time) <= 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }
        return low == 0 ? _basis : _entries[low - 1].View;
    }

    /// <summary>Publishes a unique operation's logical effect in timestamp order and reconstructs affected later views.</summary>
    public void Publish(Guid operation, DurableTimestampState time, TEffect effect)
    {
        if (_entries.Any(entry => entry.Operation == operation))
        {
            throw new InvalidDataException("Duplicate logical publication.");
        }

        long bytes = _reservations.GetValueOrDefault(operation);
        Abandon(operation);
        if (time.CompareTo(Floor) <= 0)
        {
            // A recovered prepared transaction can predate the new recovery base.
            // Admission waits for relevant intents before exposing that base.
            _basis = _apply(_basis, effect);
            Rebuild(0);
            return;
        }
        if (_entries.Count >= MaximumVersions)
        {
            throw new InvalidOperationException("Logical history requires reader drainage/reclamation.");
        }

        int index = _entries.FindIndex(entry => entry.Time.CompareTo(time) > 0 ||
            entry.Time.CompareTo(time) == 0 && entry.Operation.CompareTo(operation) > 0);
        if (index < 0)
        {
            index = _entries.Count;
        }

        _entries.Insert(index, new(operation, time, effect, _basis, bytes));
        _retainedBytes = checked(_retainedBytes + bytes);
        Rebuild(index);
    }

    private void Rebuild(int index)
    {
        var view = index == 0 ? _basis : _entries[index - 1].View;
        for (; index < _entries.Count; index++)
        {
            var entry = _entries[index];
            view = _apply(view, entry.Effect);
            _entries[index] = entry with
            {
                View = view
            };
        }
    }

    /// <summary>Advances the history floor, retaining its reconstructed basis and discarding versions at or below that floor.</summary>
    public void Trim(DurableTimestampState floor)
    {
        if (floor.CompareTo(Floor) < 0)
        {
            throw new InvalidOperationException("Logical history floor regressed.");
        }

        _basis = Read(floor);
        _entries.RemoveAll(entry => entry.Time.CompareTo(floor) <= 0);
        _retainedBytes = _entries.Sum(entry => entry.Bytes);
        Floor = floor;
    }
}

/// <summary>Profile-specific logical effects and immutable read context, owned by common native admission.</summary>
public interface IDatabaseTimestampProfile
{
    /// <summary>Builds bounded, reconstructible routing membership from the exact admitted MVCC view.</summary>
    DatabaseRootIndexSnapshot CaptureRootIndexes(object view, DatabaseRootIndexColumn[] columns) =>
        throw new NotSupportedException("This native profile does not provide root-index membership.");

    /// <summary>Exact serving evidence, including profile-specific physical associations where required.</summary>
    CommittedChangePayload? CaptureServingEffects(CommittedChangePayload? changes) => changes;

    /// <summary>Initializes profile logical history at the supplied retention floor.</summary>
    void InitializeTimestampHistory(DurableTimestampState floor);

    /// <summary>Initializes profile logical history after checking cancellation; implementations may perform asynchronous restoration.</summary>
    ValueTask InitializeTimestampHistoryAsync(DurableTimestampState floor, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        InitializeTimestampHistory(floor);
        return ValueTask.CompletedTask;
    }

    /// <summary>Publishes the logical effects of a committed profile payload at its assigned timestamp.</summary>
    void PublishTimestampVersion(Guid operation, DurableTimestampState time, ReadOnlyMemory<byte> payload);

    /// <summary>Captures the immutable profile view visible at a retained logical timestamp.</summary>
    object CaptureTimestampView(DurableTimestampState time);

    /// <summary>Installs a captured view for reads in the returned disposable scope.</summary>
    IDisposable EnterTimestampView(object view);

    /// <summary>Discards logical versions below the newly protected retention floor.</summary>
    void TrimTimestampVersions(DurableTimestampState floor);

    /// <summary>Validates whether a profile payload can be admitted to logical timestamp execution.</summary>
    void ValidateTimestampMutation(Guid operation, ReadOnlyMemory<byte> payload);

    /// <summary>Reserves logical evidence capacity after planning and before native durability.</summary>
    void ReserveTimestampMutation(Guid operation, ReadOnlyMemory<byte> payload, DatabaseProfileChangePlan changePlan);

    /// <summary>Validates mapping manifests for logical timestamp execution in this profile.</summary>
    void ValidateTimestampMappings(IReadOnlyList<EntityForge.Db.Mapping.EntityMappingManifest> manifests);
}
