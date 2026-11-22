using EntityForge.Db.Kernel;
using System.Collections.Immutable;
using EntityForge.Db.Graph.Contracts;

namespace EntityForge.Db.Graph;

internal sealed partial class GraphProfileRuntime : IDatabaseProfileLogicalPreparation, IDatabaseProfileSnapshotPin
{
    IDisposable IDatabaseProfileSnapshotPin.PinSnapshot(DatabaseVersion version) => AcquireSnapshot(version);

    private readonly DatabaseLogicalPreparationCache<GraphTransaction> _logicalIntents = new();

    public void RetainLogicalPreparation(Guid operation, ReadOnlyMemory<byte> payload)
    {
        if (!IsMetadata(payload))
        {
            _logicalIntents.RetainCompiled(operation, payload, owned => DecodeMeasured(operation, owned.Span));
        }
    }

    private sealed record Effects(DatabaseMapEffects<GraphVertexId, GraphVertex> Vertices,
        DatabaseMapEffects<GraphEdgeId, GraphEdge> Edges, GraphCommittedEffects Changes,
        IReadOnlyDictionary<GraphVertexId, GraphVertex> BeforeVertices,
        IReadOnlyDictionary<GraphEdgeId, GraphEdge> BeforeEdges, GraphTransactionPlan Captured)
    {
        internal readonly EntityForge.Db.Storage.Pages.AppendMergeMutation<GraphAccessKey, Guid>[] Indexes = GraphPhysicalAccessPath.Compile(Changes);
    }

    private readonly DatabaseLogicalPreparationCache<Effects> _logicalEffects = new();

    public void CaptureLogicalEffects(Guid operation, ReadOnlyMemory<byte> payload)
    {
        lock (_stateGate)
        {
            if (_preparedPlans.TryGetValue(operation, out var plan))
            {
                var vertices = new HashSet<GraphVertexId>();
                var edges = new HashSet<GraphEdgeId>();
                foreach (var effect in plan.CommittedEffects.Effects)
                {
                    switch (effect)
                    {
                        case GraphVertexCreated e:
                            vertices.Add(e.Vertex.Id);
                            break;
                        case GraphVertexDeleted e:
                            vertices.Add(e.Vertex.Id);
                            break;
                        case GraphVertexPropertyChanged e:
                            vertices.Add(e.Vertex);
                            break;
                        case GraphVertexLabelsChanged e:
                            vertices.Add(e.Vertex);
                            break;
                        case GraphEdgeCreated e:
                            edges.Add(e.Edge.Id);
                            break;
                        case GraphEdgeDeleted e:
                            edges.Add(e.Edge.Id);
                            break;
                        case GraphEdgePropertyChanged e:
                            edges.Add(e.Edge);
                            break;
                        default:
                            throw new InvalidDataException("Unknown prepared Graph effect.");
                    }
                }

                _logicalEffects.Retain(operation, payload, new(new(_vertices, plan.Vertices, vertices),
                    new(_edges, plan.Edges, edges), plan.CommittedEffects, _vertices, _edges, plan));
            }
        }
    }

    private GraphTransactionPlan PlanLogical(GraphTransaction transaction, ReadOnlySpan<byte> payload,
        IReadOnlyDictionary<GraphVertexId, GraphVertex> vertices, IReadOnlyDictionary<GraphEdgeId, GraphEdge> edges)
    {
        long started = DatabaseProfileWorkTelemetry.Start();
        if (_logicalEffects.TryGet(transaction.TransactionId, payload, out var effects))
        {
            var rebased = ReferenceEquals(vertices, effects.BeforeVertices) && ReferenceEquals(edges, effects.BeforeEdges)
                ? effects.Captured
                : new GraphTransactionPlan(effects.Vertices.Apply(vertices as ImmutableDictionary<GraphVertexId, GraphVertex> ?? vertices.ToImmutableDictionary()),
                effects.Edges.Apply(edges as ImmutableDictionary<GraphEdgeId, GraphEdge> ?? edges.ToImmutableDictionary()), effects.Changes);
            DatabaseProfileWorkTelemetry.Record("graph", "reuse", "mutation", transaction.Mutations.Count, started);
            return rebased;
        }
        var plan = _planner.Plan(transaction, vertices, edges);
        DatabaseProfileWorkTelemetry.Record("graph", "compile", "mutation", transaction.Mutations.Count, started);
        return plan;
    }

    public void ReleaseLogicalPreparation(Guid operation)
    {
        _logicalIntents.Remove(operation);
        _logicalEffects.Remove(operation);
    }

    public void ClearLogicalPreparations()
    {
        _logicalIntents.Clear();
        _logicalEffects.Clear();
    }
}
