using EntityForge.Db.Durability;
using EntityForge.Db.Kernel;
using EntityForge.Db.Mapping;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Rdf;

internal sealed partial class RdfProfileRuntime : IDatabaseTimestampProfile
{
    private sealed record TimestampView(RdfReadView Data, EntityMappingCatalog Mappings);

    private sealed record TimestampEffects(RdfMutation[] Mutations, EntityMappingRegistration? Mapping);

    private DatabaseLogicalHistory<TimestampView, TimestampEffects>? _timestampHistory;
    private readonly DatabaseReadContext<TimestampView> _timestampRead = new();

    void IDatabaseTimestampProfile.InitializeTimestampHistory(DurableTimestampState floor)
    {
        lock (_gate)
        {
            var mappings = new EntityMappingCatalog(Profile);
            mappings.Restore(Mappings.Capture());
            _timestampHistory = new(new(_view, mappings), floor, ApplyTimestampEffects);
        }
    }

    void IDatabaseTimestampProfile.ValidateTimestampMutation(Guid operation, ReadOnlyMemory<byte> payload)
    {
        if (_timestampRead.Value is not null)
        {
            throw new InvalidOperationException("Timestamp projection is read-only.");
        }

        lock (_gate)
        {
            _timestampHistory!.RequireCapacity(_pendingStates.Count + (_pendingStates.ContainsKey(operation) ? 0 : 1));
        }
    }

    void IDatabaseTimestampProfile.ReserveTimestampMutation(Guid operation, ReadOnlyMemory<byte> payload, DatabaseProfileChangePlan changePlan)
    {
        lock (_gate)
        {
            _timestampHistory!.Reserve(operation, payload.Length);
        }
    }

    void IDatabaseTimestampProfile.PublishTimestampVersion(Guid operation, DurableTimestampState time,
        ReadOnlyMemory<byte> payload)
    {
        lock (_gate)
        {
            var mapping = EntityMappingCodec.IsProfileCommand(payload)
                ? EntityMappingCodec.DecodeProfileCommand(payload) : null;
            // Preserve individual quad changes, never a physical after-state containing
            // independent transactions that may have a later logical timestamp.
            var mutations = IsMetadata(payload) ? [] : DecodeTransaction(operation, payload.Span).Mutations.ToArray();
            _timestampHistory!.Publish(operation, time, new(mutations, mapping));
        }
    }

    object IDatabaseTimestampProfile.CaptureTimestampView(DurableTimestampState time)
    {
        lock (_gate)
        {
            return _timestampHistory!.Read(time);
        }
    }

    IDisposable IDatabaseTimestampProfile.EnterTimestampView(object view)
    {
        if (view is not TimestampView selected || _timestampRead.Value is not null)
        {
            throw new InvalidOperationException("Invalid or nested timestamp projection.");
        }

        return _timestampRead.Enter(selected);
    }

    internal RdfSnapshotLease AcquireTimestampProjectionLease(DatabaseVersion association)
    {
        var valid = _timestampRead.CaptureValidation();
        // The surrounding timestamp context owns the immutable data. This lease
        // is only an adapter and cannot outlive or escape that exact context.
        return new(this, association, static () =>
{

}, valid);
    }

    void IDatabaseTimestampProfile.TrimTimestampVersions(DurableTimestampState floor)
    {
        lock (_gate)
        {
            _timestampHistory!.Trim(floor);
        }
    }

    void IDatabaseTimestampProfile.ValidateTimestampMappings(IReadOnlyList<EntityMappingManifest> manifests)
    {
        var view = _timestampRead.Value ?? throw new InvalidOperationException("No timestamp projection context.");
        view.Mappings.RequireCurrent(manifests);
    }

    private static TimestampView ApplyTimestampEffects(TimestampView before, TimestampEffects effects)
    {
        var mappings = before.Mappings;
        if (effects.Mapping is { } registration)
        {
            mappings = before.Mappings.WithRegistration(registration);
        }
        return new(effects.Mutations.Length == 0 ? before.Data : before.Data.Apply(effects.Mutations), mappings);
    }
}
