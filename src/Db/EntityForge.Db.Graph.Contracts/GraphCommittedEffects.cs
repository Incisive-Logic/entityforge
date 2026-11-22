using EntityForge.Db;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EntityForge.Db.Graph.Contracts;

/// <summary>Identifies a net logical effect produced by a committed graph transaction.</summary>
public enum GraphCommittedEffectKind : byte
{
    /// <summary>A vertex was created.</summary>
    VertexCreated = 1,

    /// <summary>A vertex was deleted.</summary>
    VertexDeleted = 2,

    /// <summary>An edge was created.</summary>
    EdgeCreated = 3,

    /// <summary>An edge was deleted.</summary>
    EdgeDeleted = 4,

    /// <summary>A vertex property was added, removed or replaced.</summary>
    VertexPropertyChanged = 5,

    /// <summary>An edge property was added, removed or replaced.</summary>
    EdgePropertyChanged = 6,

    /// <summary>A vertex's label set changed.</summary>
    VertexLabelsChanged = 7
}

/// <summary>Base type for net logical effects emitted by graph commits.</summary>
/// <param name="Kind">The logical change category.</param>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$effect")]
[JsonDerivedType(typeof(GraphVertexCreated), "vertexCreated")]
[JsonDerivedType(typeof(GraphVertexDeleted), "vertexDeleted")]
[JsonDerivedType(typeof(GraphEdgeCreated), "edgeCreated")]
[JsonDerivedType(typeof(GraphEdgeDeleted), "edgeDeleted")]
[JsonDerivedType(typeof(GraphVertexPropertyChanged), "vertexPropertyChanged")]
[JsonDerivedType(typeof(GraphEdgePropertyChanged), "edgePropertyChanged")]
[JsonDerivedType(typeof(GraphVertexLabelsChanged), "vertexLabelsChanged")]
public abstract record GraphCommittedEffect(GraphCommittedEffectKind Kind);

/// <summary>Reports a committed vertex creation.</summary>
/// <param name="Vertex">The created vertex state.</param>
public sealed record GraphVertexCreated(GraphVertex Vertex)
    : GraphCommittedEffect(GraphCommittedEffectKind.VertexCreated);

/// <summary>Reports a committed vertex deletion.</summary>
/// <param name="Vertex">The deleted vertex's prior state.</param>
public sealed record GraphVertexDeleted(GraphVertex Vertex)
    : GraphCommittedEffect(GraphCommittedEffectKind.VertexDeleted);

/// <summary>Reports a committed edge creation.</summary>
/// <param name="Edge">The created edge state.</param>
public sealed record GraphEdgeCreated(GraphEdge Edge)
    : GraphCommittedEffect(GraphCommittedEffectKind.EdgeCreated);

/// <summary>Reports a committed edge deletion.</summary>
/// <param name="Edge">The deleted edge's prior state.</param>
public sealed record GraphEdgeDeleted(GraphEdge Edge)
    : GraphCommittedEffect(GraphCommittedEffectKind.EdgeDeleted);

/// <summary>Reports the net committed before/after value of a vertex property.</summary>
/// <param name="Vertex">The affected vertex identity.</param>
/// <param name="Property">The case-sensitive property name.</param>
/// <param name="Before">The prior value, or null when the property was absent.</param>
/// <param name="After">The new value, or null when the property was removed.</param>
public sealed record GraphVertexPropertyChanged(
    GraphVertexId Vertex, string Property, DatabaseValue? Before, DatabaseValue? After)
    : GraphCommittedEffect(GraphCommittedEffectKind.VertexPropertyChanged);

/// <summary>Reports the net committed before/after value of an edge property.</summary>
/// <param name="Edge">The affected edge identity.</param>
/// <param name="Property">The case-sensitive property name.</param>
/// <param name="Before">The prior value, or null when absent.</param>
/// <param name="After">The new value, or null when removed.</param>
public sealed record GraphEdgePropertyChanged(
    GraphEdgeId Edge, string Property, DatabaseValue? Before, DatabaseValue? After)
    : GraphCommittedEffect(GraphCommittedEffectKind.EdgePropertyChanged);

/// <summary>Reports the net committed before/after label set of a vertex.</summary>
/// <param name="Vertex">The affected vertex identity.</param>
/// <param name="Before">The prior label set.</param>
/// <param name="After">The committed label set.</param>
public sealed record GraphVertexLabelsChanged(
    GraphVertexId Vertex, IReadOnlySet<string> Before, IReadOnlySet<string> After)
    : GraphCommittedEffect(GraphCommittedEffectKind.VertexLabelsChanged);

/// <summary>Contains the ordered net logical effects of one committed graph transaction.</summary>
/// <param name="Effects">The ordered effects; the supplied collection is retained by this record.</param>
public sealed record GraphCommittedEffects(IReadOnlyList<GraphCommittedEffect> Effects);

/// <summary>Stable codec identities used by Graph committed-change consumers.</summary>
public static class GraphCommittedChangeCodecs
{
    /// <summary>The codec identifier for changes that require consumers to reload the dataset.</summary>
    public const string DatasetReset = "entityforge.graph.dataset-reset";

    /// <summary>The supported dataset-reset payload version.</summary>
    public const int DatasetResetVersion = 1;
}

/// <summary>Defines stable routing dimensions for graph committed-change subscriptions.</summary>
public static class GraphChangeRoutingKinds
{
    /// <summary>Routes by affected vertex identity.</summary>
    public const string Vertex = "graph.vertex";

    /// <summary>Routes by affected edge identity.</summary>
    public const string Edge = "graph.edge";

    /// <summary>Routes by graph label.</summary>
    public const string Label = "graph.label";

    /// <summary>Routes by property name.</summary>
    public const string Property = "graph.property";

    /// <summary>Routes by logical committed-effect category.</summary>
    public const string EffectKind = "graph.effect-kind";
}
