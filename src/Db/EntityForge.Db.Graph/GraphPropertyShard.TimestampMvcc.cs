using EntityForge.Db.Durability;
using EntityForge.Db.Kernel;
using EntityForge.Db.Mapping;
using EntityForge.Db.Graph.Contracts;
using System.Collections.Immutable;

namespace EntityForge.Db.Graph;

internal sealed partial class GraphPropertyShard
{
    internal sealed partial class Runtime : IDatabaseTimestampProfile
    {
        private sealed record TimestampView(State State, EntityMappingCatalog Mappings);

        private sealed record Effects(DatabaseMapEffects<GraphVertexId, GraphVertex> Vertices,
            DatabaseMapEffects<GraphEdgeId, GraphEdge> Edges,
            DatabaseMapEffects<BagId, System.Collections.Frozen.FrozenDictionary<string, DatabaseValue>> Bags,
            ImmutableHashSet<BagId> Retired, EntityMappingRegistration? Mapping);

        private DatabaseLogicalHistory<TimestampView, Effects>? _logicalHistory;
        private readonly DatabaseReadContext<TimestampView> _timestampRead = new();
        private Effects? _lastEffects;

        private void CaptureEffects(State before, State after, EntityMappingRegistration? mapping) =>
            _lastEffects = new(new(before.Topology.Vertices, after.Topology.Vertices),
                new(before.Topology.Edges, after.Topology.Edges),
                new(before.Bags, after.Bags), after.Retired.Except(before.Retired).ToImmutableHashSet(), mapping);

        void IDatabaseTimestampProfile.InitializeTimestampHistory(DurableTimestampState floor)
        {
            lock (_sync)
            {
                var mappings = Mappings.Snapshot();
                _logicalHistory = new(new(_state, mappings), floor, ApplyEffects);
            }
        }

        void IDatabaseTimestampProfile.ValidateTimestampMutation(Guid operation, ReadOnlyMemory<byte> payload)
        {
            if (_timestampRead.Value is not null)
            {
                throw new InvalidOperationException("Timestamp projection is read-only.");
            }

            lock (_sync)
            {
                _logicalHistory!.RequireCapacity(1);
            }
        }

        void IDatabaseTimestampProfile.ReserveTimestampMutation(Guid operation, ReadOnlyMemory<byte> payload, DatabaseProfileChangePlan plan)
        {
            lock (_sync)
            {
                _logicalHistory!.Reserve(operation, checked(payload.Length + (plan.Changes?.Content.Length ?? 0)));
            }
        }

        void IDatabaseTimestampProfile.PublishTimestampVersion(Guid operation, DurableTimestampState time, ReadOnlyMemory<byte> payload)
        {
            lock (_sync)
            {
                _logicalHistory!.Publish(operation, time, _lastEffects ?? throw new InvalidDataException("Missing split Graph effects."));
            }
        }

        object IDatabaseTimestampProfile.CaptureTimestampView(DurableTimestampState time)
        {
            lock (_sync)
            {
                return _logicalHistory!.Read(time);
            }
        }

        IDisposable IDatabaseTimestampProfile.EnterTimestampView(object view)
        {
            if (view is not TimestampView selected || _timestampRead.Value is not null)
            {
                throw new InvalidDataException("Invalid split timestamp view.");
            }

            return _timestampRead.Enter(selected);
        }

        void IDatabaseTimestampProfile.TrimTimestampVersions(DurableTimestampState floor)
        {
            lock (_sync)
            {
                _logicalHistory!.Trim(floor);
            }
        }

        void IDatabaseTimestampProfile.ValidateTimestampMappings(IReadOnlyList<EntityMappingManifest> manifests)
        {
            var view = _timestampRead.Value ?? throw new InvalidOperationException("No timestamp projection.");
            view.Mappings.RequireCurrent(manifests);
        }

        private static TimestampView ApplyEffects(TimestampView before, Effects effects)
        {
            var topology = GraphReadView.Build(effects.Vertices.Apply(before.State.Topology.Vertices.ToImmutableDictionary()),
                effects.Edges.Apply(before.State.Topology.Edges.ToImmutableDictionary()));
            var mappings = before.Mappings;
            if (effects.Mapping is { } registration)
            {
                mappings = before.Mappings.WithRegistration(registration);
            }
            return new(new(topology, effects.Bags.Apply(before.State.Bags), before.State.Retired.Union(effects.Retired)), mappings);
        }
    }
}
