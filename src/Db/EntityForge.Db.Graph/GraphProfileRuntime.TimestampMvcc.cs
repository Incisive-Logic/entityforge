using System.Collections.Immutable;
using EntityForge.Db.Durability;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Kernel;
using EntityForge.Db.Mapping;

namespace EntityForge.Db.Graph;

internal sealed partial class GraphProfileRuntime : IDatabaseTimestampProfile
{
    private sealed record TimestampView(GraphReadView Graph, EntityMappingCatalog Mappings);

    private sealed record TimestampEffects(GraphCommittedEffect[] Effects, EntityMappingRegistration? Mapping);

    private DatabaseLogicalHistory<TimestampView, TimestampEffects>? _logicalHistory;
    private readonly DatabaseReadContext<TimestampView> _timestampRead = new();

    void IDatabaseTimestampProfile.InitializeTimestampHistory(DurableTimestampState floor)
    {
        lock (_stateGate)
        {
            var mappings = Mappings.Snapshot();
            _logicalHistory = new(new(_currentView, mappings), floor, ApplyTimestampEffects);
        }
    }

    void IDatabaseTimestampProfile.ValidateTimestampMutation(Guid operation, ReadOnlyMemory<byte> payload)
    {
        if (_timestampRead.Value is not null)
        {
            throw new InvalidOperationException("Timestamp projection is read-only.");
        }

        lock (_stateGate)
        {
            _logicalHistory!.RequireCapacity(_pendingStates.Count + (_pendingStates.ContainsKey(operation) ? 0 : 1));
        }
    }

    void IDatabaseTimestampProfile.ReserveTimestampMutation(Guid operation, ReadOnlyMemory<byte> payload, DatabaseProfileChangePlan changePlan)
    {
        lock (_stateGate)
        {
            long effects = changePlan.Changes?.Content.Length ?? 0;
            if (effects == 0 && _preparedPlans.TryGetValue(operation, out var plan) && plan.CommittedEffects.Effects.Count != 0)
            {
                effects = GraphCommittedEffectsCodec.Encode(plan.CommittedEffects).Length;
            }

            _logicalHistory!.Reserve(operation, checked(payload.Length + effects));
        }
    }

    void IDatabaseTimestampProfile.PublishTimestampVersion(Guid operation, DurableTimestampState time, ReadOnlyMemory<byte> payload)
    {
        lock (_stateGate)
        {
            var mapping = EntityMappingCodec.IsProfileCommand(payload) ? EntityMappingCodec.DecodeProfileCommand(payload) : null;
            _logicalHistory!.Publish(operation, time, new(IsMetadata(payload) ? [] : _lastCommittedEffects.Effects.ToArray(), mapping));
        }
    }

    object IDatabaseTimestampProfile.CaptureTimestampView(DurableTimestampState time)
    {
        lock (_stateGate)
        {
            return _logicalHistory!.Read(time);
        }
    }

    CommittedChangePayload? IDatabaseTimestampProfile.CaptureServingEffects(CommittedChangePayload? changes)
    {
        if (changes is null)
        {
            return null;
        }

        if (changes.CodecId != GraphCommittedEffectsCodec.CodecId || changes.CodecVersion != GraphCommittedEffectsCodec.CodecVersion)
        {
            return changes; // An unsupported reset/codec must fail the serving consumer explicitly.
        }

        lock (_stateGate)
        {
            return new(GraphServingEffectsCodec.CodecId, GraphServingEffectsCodec.CodecVersion,
            GraphServingEffectsCodec.Encode(_lastCommittedEffects), _lastCommittedEffects.Effects.Count, changes.Routing, changes.Kind);
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

    void IDatabaseTimestampProfile.TrimTimestampVersions(DurableTimestampState floor)
    {
        lock (_stateGate)
        {
            _logicalHistory!.Trim(floor);
        }
    }

    void IDatabaseTimestampProfile.ValidateTimestampMappings(IReadOnlyList<EntityMappingManifest> manifests)
    {
        var view = _timestampRead.Value ?? throw new InvalidOperationException("No timestamp projection context.");
        view.Mappings.RequireCurrent(manifests);
    }

    private static TimestampView ApplyTimestampEffects(TimestampView before, TimestampEffects change)
    {
        var mappings = before.Mappings;
        if (change.Mapping is { } registration)
        {
            mappings = before.Mappings.WithRegistration(registration);
        }
        return new(ApplyServingEffects(before.Graph, change.Effects), mappings);
    }

    internal static GraphReadView ApplyServingEffects(GraphReadView before, IReadOnlyList<GraphCommittedEffect> effects)
    {
        if (effects.Count == 0)
        {
            return before;
        }

        var vertices = before.Vertices as ImmutableDictionary<GraphVertexId, GraphVertex> ?? before.Vertices.ToImmutableDictionary();
        var edges = before.Edges as ImmutableDictionary<GraphEdgeId, GraphEdge> ?? before.Edges.ToImmutableDictionary();
        foreach (var effect in effects)
        {
            switch (effect)
            {
                case GraphVertexCreated e:
                    vertices = vertices.SetItem(e.Vertex.Id, e.Vertex);
                    break;
                case GraphVertexDeleted e:
                    vertices = vertices.Remove(e.Vertex.Id);
                    break;
                case GraphEdgeCreated e:
                    edges = edges.SetItem(e.Edge.Id, e.Edge);
                    break;
                case GraphEdgeDeleted e:
                    edges = edges.Remove(e.Edge.Id);
                    break;
                case GraphVertexPropertyChanged e:
                    vertices = vertices.SetItem(e.Vertex, vertices[e.Vertex] with
                    {
                        Properties = Property(vertices[e.Vertex].Properties, e.Property, e.After)
                    });
                    break;
                case GraphEdgePropertyChanged e:
                    edges = edges.SetItem(e.Edge, edges[e.Edge] with
                    {
                        Properties = Property(edges[e.Edge].Properties, e.Property, e.After)
                    });
                    break;
                case GraphVertexLabelsChanged e:
                    // Before/after sets describe physical execution. Apply only this
                    // operation's delta when independent labels arrive out of order.
                    var labels = vertices[e.Vertex].Labels.Except(e.Before.Except(e.After, StringComparer.Ordinal), StringComparer.Ordinal)
                        .Union(e.After.Except(e.Before, StringComparer.Ordinal), StringComparer.Ordinal).ToImmutableHashSet(StringComparer.Ordinal);
                    vertices = vertices.SetItem(e.Vertex, vertices[e.Vertex] with
                    {
                        Labels = labels
                    });
                    break;
                default:
                    throw new InvalidDataException("Unknown logical Graph effect.");
            }
        }

        var result = GraphReadView.Build(vertices, edges);
        result.CarryUnchangedIndexes(before, effects);
        return result;
    }

    private static IReadOnlyDictionary<string, DatabaseValue> Property(IReadOnlyDictionary<string, DatabaseValue> prior, string key, DatabaseValue? value)
    {
        return SmallGraphProperties.Change(prior, key, value);
    }
}
