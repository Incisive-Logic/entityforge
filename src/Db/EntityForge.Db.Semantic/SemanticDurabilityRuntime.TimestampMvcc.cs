using EntityForge.Db.Semantic.Core;
using EntityForge.Db.Durability;
using EntityForge.Db.Semantic.Engine;
using EntityForge.Db.Kernel;
using EntityForge.Db.Mapping;
using EntityForge.Db.Semantic.Durability;

namespace EntityForge.Db.Semantic;

internal sealed partial class SemanticDurabilityRuntime : IDatabaseTimestampProfile
{
    private sealed record TimestampView(SemanticDatabaseState State, EntityMappingCatalog Mappings);

    private sealed record TimestampChange(SemanticTimestampEffects Effects, EntityMappingRegistration? Mapping);

    private readonly object _timestampGate = new();
    private DatabaseLogicalHistory<TimestampView, TimestampChange>? _timestampHistory;
    private readonly Dictionary<Guid, TimestampChange> _timestampPublications = [];
    private readonly DatabaseReadContext<TimestampView> _timestampRead = new();

    private SemanticProfileStateCoordinator TimestampState => Participant() as SemanticProfileStateCoordinator ??
        throw new NotSupportedException("Timestamp visibility requires the native Semantic state coordinator.");

    void IDatabaseTimestampProfile.InitializeTimestampHistory(DurableTimestampState floor) =>
        throw new NotSupportedException("Semantic timestamp recovery requires asynchronous evidence hydration.");

    async ValueTask IDatabaseTimestampProfile.InitializeTimestampHistoryAsync(DurableTimestampState floor, CancellationToken token)
    {
        var state = await TimestampState.CaptureTimestampBaseAsync(token).ConfigureAwait(false);
        var mappings = Mappings.Snapshot();
        lock (_timestampGate)
        {
            _timestampHistory = new(new(state, mappings), floor, ApplyTimestampChange);
        }
    }

    void IDatabaseTimestampProfile.ValidateTimestampMutation(Guid operation, ReadOnlyMemory<byte> payload)
    {
        if (_timestampRead.Value is not null)
        {
            throw new InvalidOperationException("Timestamp projection is read-only.");
        }

        lock (_timestampGate)
        {
            _timestampHistory!.RequireCapacity(_plannedCommits.Count + (_plannedCommits.ContainsKey(operation) ? 0 : 1));
        }
    }

    void IDatabaseTimestampProfile.ReserveTimestampMutation(Guid operation, ReadOnlyMemory<byte> payload, DatabaseProfileChangePlan changePlan)
    {
        lock (_timestampGate)
        {
            long effects = changePlan.Changes?.Content.Length ?? 0;
            if (effects == 0 && _plannedEffects.TryGetValue(operation, out var planned) && planned.Effects.Count != 0)
            {
                effects = SemanticCommittedEffectsCodec.Encode(planned).Length;
            }

            _timestampHistory!.Reserve(operation, checked(payload.Length + effects));
        }
    }

    void IDatabaseTimestampProfile.PublishTimestampVersion(Guid operation, DurableTimestampState time, ReadOnlyMemory<byte> payload)
    {
        lock (_timestampGate)
        {
            if (!_timestampPublications.Remove(operation, out var change))
            {
                throw new InvalidDataException("Missing exact Semantic timestamp effects.");
            }

            _timestampHistory!.Publish(operation, time, change);
            TimestampState.PublishPlanningEvidence(change.Effects);
        }
    }

    object IDatabaseTimestampProfile.CaptureTimestampView(DurableTimestampState time)
    {
        lock (_timestampGate)
        {
            return _timestampHistory!.Read(time);
        }
    }

    IDisposable IDatabaseTimestampProfile.EnterTimestampView(object view)
    {
        if (view is not TimestampView selected || _timestampRead.Value is not null)
        {
            throw new InvalidOperationException("Invalid or nested Semantic timestamp view.");
        }

        return _timestampRead.Enter(selected, () => TimestampState.EnterTimestampRead(selected.State));
    }

    void IDatabaseTimestampProfile.TrimTimestampVersions(DurableTimestampState floor)
    {
        lock (_timestampGate)
        {
            _timestampHistory!.Trim(floor);
        }
    }

    void IDatabaseTimestampProfile.ValidateTimestampMappings(IReadOnlyList<EntityMappingManifest> manifests)
    {
        var view = _timestampRead.Value ?? throw new InvalidOperationException("No Semantic timestamp read context.");
        view.Mappings.RequireCurrent(manifests);
    }

    private static TimestampView ApplyTimestampChange(TimestampView prior, TimestampChange change)
    {
        var mappings = prior.Mappings;
        if (change.Mapping is { } registration)
        {
            mappings = prior.Mappings.WithRegistration(registration);
        }
        return new(change.Effects.Apply(prior.State), mappings);
    }
}
