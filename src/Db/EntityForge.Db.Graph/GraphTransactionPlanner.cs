using System.Collections.Immutable;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db;
using EntityForge.Values;

namespace EntityForge.Db.Graph;

internal sealed record GraphTransactionPlan(
    ImmutableDictionary<GraphVertexId, GraphVertex> Vertices,
    ImmutableDictionary<GraphEdgeId, GraphEdge> Edges,
    GraphCommittedEffects CommittedEffects)
{
    internal GraphQueryStatistics? Statistics
    {
        get; init;
    }
}

internal sealed class GraphTransactionPlanner(JsonValueAdmissionPolicy jsonAdmissionPolicy,
    int maximumEntryCount = GraphCheckpointCodec.MaximumEntryCount)
{
    private readonly JsonValueAdmissionPolicy _jsonAdmissionPolicy = jsonAdmissionPolicy ??
        throw new ArgumentNullException(nameof(jsonAdmissionPolicy));

    public GraphTransactionPlan Plan(GraphTransaction transaction,
        IReadOnlyDictionary<GraphVertexId, GraphVertex> currentVertices,
        IReadOnlyDictionary<GraphEdgeId, GraphEdge> currentEdges)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ImmutableDictionary<GraphVertexId, GraphVertex>.Builder vertices =
            (currentVertices as ImmutableDictionary<GraphVertexId, GraphVertex> ??
             currentVertices.ToImmutableDictionary()).ToBuilder();
        ImmutableDictionary<GraphEdgeId, GraphEdge>.Builder edges =
            (currentEdges as ImmutableDictionary<GraphEdgeId, GraphEdge> ??
             currentEdges.ToImmutableDictionary()).ToBuilder();
        var effects = new List<GraphCommittedEffect>();
        foreach (GraphMutation mutation in transaction.Mutations)
        {
            Apply(mutation, vertices, edges, effects);
        }

        ValidateGrowth(vertices.Count, currentVertices.Count);
        ValidateGrowth(edges.Count, currentEdges.Count);
        foreach (GraphMutation mutation in transaction.Mutations)
        {
            GraphVertexId? vertexId = mutation switch
            {
                CreateVertex value => value.Vertex,
                SetVertexProperty value => value.Vertex,
                AddVertexLabel value => value.Vertex,
                _ => null
            };
            if (vertexId is { } id && vertices.TryGetValue(id, out GraphVertex? vertex))
            {
                currentVertices.TryGetValue(id, out GraphVertex? previous);
                ValidateGrowth(vertex.Properties.Count, previous?.Properties.Count ?? 0);
                ValidateGrowth(vertex.Labels.Count, previous?.Labels.Count ?? 0);
            }
            if (mutation is SetEdgeProperty property && edges.TryGetValue(property.Edge, out GraphEdge? edge))
            {
                currentEdges.TryGetValue(property.Edge, out GraphEdge? previous);
                ValidateGrowth(edge.Properties.Count, previous?.Properties.Count ?? 0);
            }
        }
        var plan = new GraphTransactionPlan(vertices.ToImmutable(), edges.ToImmutable(), new(effects));
        GraphCheckpointSize.Remember(plan, currentVertices, currentEdges);
        GraphCheckpointSize.Require(GraphCheckpointSize.Graph(plan.Vertices, plan.Edges));
        return plan;
    }

    private void ValidateGrowth(int projected, int previous)
    {
        if (maximumEntryCount < 0 || maximumEntryCount > GraphCheckpointCodec.MaximumEntryCount)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumEntryCount));
        }

        if (projected > maximumEntryCount && projected > previous)
        {
            throw new InvalidOperationException("The Graph mutation exceeds the checkpoint collection limit.");
        }
    }

    private void Apply(GraphMutation mutation, ImmutableDictionary<GraphVertexId, GraphVertex>.Builder vertices,
        ImmutableDictionary<GraphEdgeId, GraphEdge>.Builder edges, List<GraphCommittedEffect> effects)
    {
        switch (mutation)
        {
            case CreateVertex create when !vertices.ContainsKey(create.Vertex):
                ValidateIdentifier(create.Vertex.Value, nameof(create.Vertex));
                if (create.Labels.Any(String.IsNullOrWhiteSpace))
                {
                    throw new InvalidDataException("Graph labels cannot be empty.");
                }

                var createdVertex = new GraphVertex(create.Vertex,
                    create.Labels.ToHashSet(StringComparer.Ordinal),
                    new Dictionary<string, DatabaseValue>(StringComparer.Ordinal));
                vertices.Add(create.Vertex, createdVertex);
                effects.Add(new GraphVertexCreated(createdVertex));
                break;
            case DeleteVertex delete when vertices.ContainsKey(delete.Vertex) &&
                edges.Values.All(edge => edge.From != delete.Vertex && edge.To != delete.Vertex):
                GraphVertex deletedVertex = vertices[delete.Vertex];
                vertices.Remove(delete.Vertex);
                effects.Add(new GraphVertexDeleted(deletedVertex));
                break;
            case CreateEdge create when vertices.ContainsKey(create.From) && vertices.ContainsKey(create.To) &&
                !edges.ContainsKey(create.Edge):
                ValidateIdentifier(create.Edge.Value, nameof(create.Edge));
                if (String.IsNullOrWhiteSpace(create.Label))
                {
                    throw new InvalidDataException("An edge label is required.");
                }

                var createdEdge = new GraphEdge(create.Edge, create.From, create.To, create.Label,
                    new Dictionary<string, DatabaseValue>(StringComparer.Ordinal));
                edges.Add(create.Edge, createdEdge);
                effects.Add(new GraphEdgeCreated(createdEdge));
                break;
            case CreateEdgeReplica create when !edges.ContainsKey(create.Edge) &&
                ValidReplica(create, vertices):
                ValidateIdentifier(create.Edge.Value, nameof(create.Edge));
                if (String.IsNullOrWhiteSpace(create.Label))
                {
                    throw new InvalidDataException("An edge label is required.");
                }

                var replica = new GraphEdge(create.Edge, create.From, create.To, create.Label,
                    new Dictionary<string, DatabaseValue>(StringComparer.Ordinal), create.StorageRole);
                edges.Add(create.Edge, replica);
                effects.Add(new GraphEdgeCreated(replica));
                break;
            case DeleteEdge delete when edges.TryGetValue(delete.Edge, out GraphEdge? deletedEdge):
                edges.Remove(delete.Edge);
                effects.Add(new GraphEdgeDeleted(deletedEdge));
                break;
            case DeleteEdgeReplica delete when edges.TryGetValue(delete.Edge, out GraphEdge? deletedReplica) &&
                deletedReplica.From == delete.From && deletedReplica.To == delete.To &&
                String.Equals(deletedReplica.Label, delete.Label, StringComparison.Ordinal) &&
                (delete.StorageRole & ~GraphEdgeStorageRole.Supported) == 0 &&
                (deletedReplica.StorageRole & GraphEdgeStorageRole.Complete) == (delete.StorageRole & GraphEdgeStorageRole.Complete):
                edges.Remove(delete.Edge);
                effects.Add(new GraphEdgeDeleted(deletedReplica));
                break;
            case SetVertexProperty set when vertices.TryGetValue(set.Vertex, out GraphVertex? vertex):
                ValidateProperty(set.Name, set.Value);
                vertex.Properties.TryGetValue(set.Name, out DatabaseValue? priorVertexValue);
                if (priorVertexValue == set.Value)
                {
                    break;
                }

                vertices[set.Vertex] = vertex with
                {
                    Properties = SmallGraphProperties.Change(vertex.Properties, set.Name, set.Value)
                };
                effects.Add(new GraphVertexPropertyChanged(set.Vertex, set.Name, priorVertexValue, set.Value));
                break;
            case RemoveVertexProperty remove when vertices.TryGetValue(remove.Vertex, out GraphVertex? vertex):
                ValidatePropertyName(remove.Name);
                if (!vertex.Properties.TryGetValue(remove.Name, out DatabaseValue? removedVertexValue))
                {
                    break;
                }

                vertices[remove.Vertex] = vertex with
                {
                    Properties = SmallGraphProperties.Change(vertex.Properties, remove.Name, null)
                };
                effects.Add(new GraphVertexPropertyChanged(remove.Vertex, remove.Name, removedVertexValue, null));
                break;
            case SetEdgeProperty set when edges.TryGetValue(set.Edge, out GraphEdge? edge):
                ValidateProperty(set.Name, set.Value);
                edge.Properties.TryGetValue(set.Name, out DatabaseValue? priorEdgeValue);
                if (priorEdgeValue == set.Value)
                {
                    break;
                }

                edges[set.Edge] = edge with
                {
                    Properties = SmallGraphProperties.Change(edge.Properties, set.Name, set.Value)
                };
                effects.Add(new GraphEdgePropertyChanged(set.Edge, set.Name, priorEdgeValue, set.Value));
                break;
            case RemoveEdgeProperty remove when edges.TryGetValue(remove.Edge, out GraphEdge? edge):
                ValidatePropertyName(remove.Name);
                if (!edge.Properties.TryGetValue(remove.Name, out DatabaseValue? removedEdgeValue))
                {
                    break;
                }

                edges[remove.Edge] = edge with
                {
                    Properties = SmallGraphProperties.Change(edge.Properties, remove.Name, null)
                };
                effects.Add(new GraphEdgePropertyChanged(remove.Edge, remove.Name, removedEdgeValue, null));
                break;
            case AddVertexLabel add when vertices.TryGetValue(add.Vertex, out GraphVertex? vertex):
                ValidateLabel(add.Label);
                if (vertex.Labels.Contains(add.Label))
                {
                    break;
                }

                IReadOnlySet<string> addedLabels = vertex.Labels.Append(add.Label)
                    .ToHashSet(StringComparer.Ordinal);
                vertices[add.Vertex] = vertex with
                {
                    Labels = addedLabels
                };
                effects.Add(new GraphVertexLabelsChanged(add.Vertex, vertex.Labels, addedLabels));
                break;
            case RemoveVertexLabel remove when vertices.TryGetValue(remove.Vertex, out GraphVertex? vertex):
                ValidateLabel(remove.Label);
                if (!vertex.Labels.Contains(remove.Label))
                {
                    break;
                }

                IReadOnlySet<string> remainingLabels = vertex.Labels.Where(label =>
                    !String.Equals(label, remove.Label, StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
                vertices[remove.Vertex] = vertex with
                {
                    Labels = remainingLabels
                };
                effects.Add(new GraphVertexLabelsChanged(remove.Vertex, vertex.Labels, remainingLabels));
                break;
            default:
                throw new InvalidDataException(
                    $"Graph mutation '{mutation.GetType().Name}' is invalid for the current graph state.");
        }
    }

    private static void ValidateIdentifier(Guid value, string field)
    {
        if (value == Guid.Empty)
        {
            throw new InvalidDataException($"Graph {field} cannot be empty.");
        }
    }

    private void ValidateProperty(string name, DatabaseValue value)
    {
        ValidatePropertyName(name);
        ArgumentNullException.ThrowIfNull(value);
        if (value is DatabaseValue.Json json)
        {
            _jsonAdmissionPolicy.Validate(json.Value);
        }
    }

    private static void ValidatePropertyName(string name)
    {
        if (String.IsNullOrWhiteSpace(name))
        {
            throw new InvalidDataException("A graph property name is required.");
        }
    }

    private static void ValidateLabel(string label)
    {
        if (String.IsNullOrWhiteSpace(label))
        {
            throw new InvalidDataException("A graph label is required.");
        }
    }

    private static bool ValidReplica(CreateEdgeReplica create,
        IReadOnlyDictionary<GraphVertexId, GraphVertex> vertices)
    {
        GraphEdgeStorageRole role = create.StorageRole;
        if ((role & GraphEdgeStorageRole.Complete) == GraphEdgeStorageRole.None || (role & ~GraphEdgeStorageRole.Supported) != 0)
        {
            return false;
        }

        if (role.HasFlag(GraphEdgeStorageRole.OutgoingAdjacency) && !vertices.ContainsKey(create.From))
        {
            return false;
        }

        if (role.HasFlag(GraphEdgeStorageRole.IncomingAdjacency) && !vertices.ContainsKey(create.To))
        {
            return false;
        }

        return role.HasFlag(GraphEdgeStorageRole.Authoritative) ||
               role.HasFlag(GraphEdgeStorageRole.OutgoingAdjacency) ||
               role.HasFlag(GraphEdgeStorageRole.IncomingAdjacency);
    }
}
