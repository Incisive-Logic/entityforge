using System.Collections.Frozen;
using EntityForge.Db;

namespace EntityForge.Db.Graph.Contracts;

/// <summary>Strongly typed identity of a native graph vertex.</summary>
public readonly record struct GraphVertexId
{
    /// <summary>Wraps a nonempty GUID as a vertex identity.</summary>
    /// <param name="value">The vertex identifier; this is distinct from any stored property named <c>id</c>.</param>
    /// <exception cref="ArgumentException">The GUID is empty.</exception>
    [System.Text.Json.Serialization.JsonConstructor]
    public GraphVertexId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A graph vertex identity is required.", nameof(value));
        }

        Value = value;
    }

    /// <summary>Gets the underlying GUID. A default-initialized struct has an empty value and is not a valid vertex identity.</summary>
    public Guid Value
    {
        get;
    }

    /// <summary>Extracts the underlying graph-element identifier.</summary>
    /// <param name="value">Receives the identifier GUID.</param>
    public void Deconstruct(out Guid value) => value = Value;

    /// <summary>Creates a new non-empty vertex identity.</summary>
    public static GraphVertexId New() => new(Guid.NewGuid());
}

/// <summary>Strongly typed identity of a native graph edge.</summary>
public readonly record struct GraphEdgeId
{
    /// <summary>Wraps a nonempty GUID as an edge identity.</summary>
    /// <param name="value">The edge identifier; it does not identify either endpoint.</param>
    /// <exception cref="ArgumentException">The GUID is empty.</exception>
    [System.Text.Json.Serialization.JsonConstructor]
    public GraphEdgeId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A graph edge identity is required.", nameof(value));
        }

        Value = value;
    }

    /// <summary>Gets the underlying GUID. A default-initialized struct is not a valid edge identity.</summary>
    public Guid Value
    {
        get;
    }

    /// <summary>Extracts the underlying graph-element identifier.</summary>
    /// <param name="value">Receives the identifier GUID.</param>
    public void Deconstruct(out Guid value) => value = Value;

    /// <summary>Creates a new non-empty edge identity.</summary>
    public static GraphEdgeId New() => new(Guid.NewGuid());
}

/// <summary>Classifies a placement-neutral logical dependency used for Graph participant routing.</summary>
public enum GraphWriteDependencyKind
{
    /// <summary>A dependency on the graph database as a whole.</summary>
    Database,

    /// <summary>A dependency routed by vertex identity.</summary>
    Vertex,

    /// <summary>A dependency routed by edge identity.</summary>
    Edge
}

/// <summary>Identifies one placement-neutral logical owner touched by a Graph transaction.</summary>
/// <param name="Kind">The logical dependency category.</param>
/// <param name="Identity">The vertex or edge GUID when the category identifies an element.</param>
public readonly record struct GraphWriteDependency(GraphWriteDependencyKind Kind, Guid Identity);

/// <summary>Base type for an atomic mutation within a native graph transaction.</summary>
public abstract record GraphMutation;

[Flags]
internal enum GraphEdgeStorageRole : byte
{
    None = 0,
    Authoritative = 1,
    OutgoingAdjacency = 2,
    IncomingAdjacency = 4,
    Complete = Authoritative | OutgoingAdjacency | IncomingAdjacency,
    // Endpoints/label were installed by one coordinated topology decision.
    // This certifies reference projection only, never replicated edge properties.
    ReferenceProjection = 8,
    Supported = Complete | ReferenceProjection
}

/// <summary>Creates a labeled vertex.</summary>
public sealed record CreateVertex : GraphMutation
{
    /// <summary>Captures a vertex creation and a copy of its initial labels.</summary>
    /// <param name="vertex">The new vertex identity.</param>
    /// <param name="labels">The initial labels; the collection is copied rather than retained.</param>
    /// <exception cref="ArgumentNullException">The labels collection is null.</exception>
    public CreateVertex(GraphVertexId vertex, IReadOnlyCollection<string> labels)
    {
        Vertex = vertex;
        Labels = labels?.ToArray() ?? throw new ArgumentNullException(nameof(labels));
    }

    /// <summary>Gets the identity to create.</summary>
    public GraphVertexId Vertex
    {
        get;
    }

    /// <summary>Gets the captured initial labels.</summary>
    public IReadOnlyCollection<string> Labels
    {
        get;
    }
}

/// <summary>Deletes a vertex.</summary>
/// <param name="Vertex">The identity to delete; incident-edge handling is governed by transaction validation.</param>
public sealed record DeleteVertex(GraphVertexId Vertex) : GraphMutation;

/// <summary>Creates a labeled directed edge.</summary>
/// <param name="Edge">The new edge identity.</param>
/// <param name="From">The source vertex identity.</param>
/// <param name="To">The target vertex identity.</param>
/// <param name="Label">The edge's relationship label.</param>
public sealed record CreateEdge(GraphEdgeId Edge, GraphVertexId From, GraphVertexId To, string Label) : GraphMutation;

/// <summary>Creates one distributed physical copy of an edge. Ordinary local callers use <see cref="CreateEdge"/>.</summary>
internal sealed record CreateEdgeReplica(GraphEdgeId Edge, GraphVertexId From, GraphVertexId To, string Label,
    GraphEdgeStorageRole StorageRole) : GraphMutation;

/// <summary>Deletes an edge.</summary>
/// <param name="Edge">The identity of the edge to remove.</param>
public sealed record DeleteEdge(GraphEdgeId Edge) : GraphMutation;

/// <summary>Deletes one exact distributed physical copy of an edge.</summary>
internal sealed record DeleteEdgeReplica(GraphEdgeId Edge, GraphVertexId From, GraphVertexId To, string Label,
    GraphEdgeStorageRole StorageRole) : GraphMutation;

/// <summary>Sets a typed vertex property.</summary>
/// <param name="Vertex">The vertex to modify.</param>
/// <param name="Name">The case-sensitive property name.</param>
/// <param name="Value">The typed value to assign, replacing any existing value.</param>
public sealed record SetVertexProperty(GraphVertexId Vertex, string Name, DatabaseValue Value) : GraphMutation;

/// <summary>Removes a vertex property.</summary>
/// <param name="Vertex">The vertex to modify.</param>
/// <param name="Name">The case-sensitive name of the property to remove.</param>
public sealed record RemoveVertexProperty(GraphVertexId Vertex, string Name) : GraphMutation;

/// <summary>Sets a typed edge property.</summary>
/// <param name="Edge">The edge to modify.</param>
/// <param name="Name">The case-sensitive property name.</param>
/// <param name="Value">The typed value to assign, replacing any existing value.</param>
public sealed record SetEdgeProperty(GraphEdgeId Edge, string Name, DatabaseValue Value) : GraphMutation;

/// <summary>Removes an edge property.</summary>
/// <param name="Edge">The edge to modify.</param>
/// <param name="Name">The case-sensitive name of the property to remove.</param>
public sealed record RemoveEdgeProperty(GraphEdgeId Edge, string Name) : GraphMutation;

/// <summary>Adds a label to an existing vertex.</summary>
/// <param name="Vertex">The vertex to modify.</param>
/// <param name="Label">The case-sensitive label to add.</param>
public sealed record AddVertexLabel(GraphVertexId Vertex, string Label) : GraphMutation;

/// <summary>Removes a label from an existing vertex.</summary>
/// <param name="Vertex">The vertex to modify.</param>
/// <param name="Label">The case-sensitive label to remove.</param>
public sealed record RemoveVertexLabel(GraphVertexId Vertex, string Label) : GraphMutation;

/// <summary>Base type for a graph predicate read tracked by optimistic validation.</summary>
public abstract record GraphReadPredicate;

/// <summary>Tracks the complete graph state used to select a matched mutation set.</summary>
public sealed record DatabaseRead : GraphReadPredicate;

/// <summary>Tracks vertex existence and label state.</summary>
/// <param name="Vertex">The vertex whose existence and labels were read.</param>
public sealed record VertexRead(GraphVertexId Vertex) : GraphReadPredicate;

/// <summary>Tracks vertex existence, all labels and all properties, including newly added properties. Excludes edges.</summary>
/// <param name="Vertex">The vertex whose complete state was read.</param>
public sealed record WholeVertexRead(GraphVertexId Vertex) : GraphReadPredicate;

/// <summary>Tracks complete edge state, including newly added properties.</summary>
/// <param name="Edge">The edge whose complete state was read.</param>
public sealed record WholeEdgeRead(GraphEdgeId Edge) : GraphReadPredicate;

/// <summary>Tracks an outgoing-adjacency predicate read.</summary>
/// <param name="Vertex">The source vertex of the adjacency read.</param>
/// <param name="Label">The relationship filter, or null to track all outgoing labels.</param>
public sealed record OutgoingAdjacencyRead(GraphVertexId Vertex, string? Label = null) : GraphReadPredicate;

/// <summary>Tracks an incoming-adjacency predicate read.</summary>
/// <param name="Vertex">The target vertex of the adjacency read.</param>
/// <param name="Label">The relationship filter, or null to track all incoming labels.</param>
public sealed record IncomingAdjacencyRead(GraphVertexId Vertex, string? Label = null) : GraphReadPredicate;

/// <summary>Tracks a vertex-property predicate read.</summary>
/// <param name="Vertex">The vertex whose property was read.</param>
/// <param name="Property">The case-sensitive property name, including a read that found it absent.</param>
public sealed record VertexPropertyRead(GraphVertexId Vertex, string Property) : GraphReadPredicate;

/// <summary>Tracks edge existence and one property, including absence.</summary>
/// <param name="Edge">The edge whose property was read.</param>
/// <param name="Property">The case-sensitive property name.</param>
public sealed record EdgePropertyRead(GraphEdgeId Edge, string Property) : GraphReadPredicate;

/// <summary>Groups graph mutations and optional snapshot dependencies into one atomic transaction.</summary>
/// <param name="TransactionId">A nonempty identity reused when retrying the same logical transaction.</param>
/// <param name="Mutations">The ordered mutation batch; copied to an array with null entries rejected.</param>
/// <param name="ReadVersion">The snapshot version used to select the mutations, when optimistic validation is required.</param>
/// <param name="ReadPredicates">Optional dependencies to validate against intervening commits; copied to an array.</param>
/// <param name="IsInitialBulkLoad">Whether the transaction requests initial bulk-load handling.</param>
/// <param name="InterpretationFingerprint">An optional fingerprint identifying the command interpretation used for replay.</param>
public sealed record GraphTransaction(
    Guid TransactionId, IReadOnlyList<GraphMutation> Mutations, DatabaseVersion? ReadVersion = null,
    IReadOnlyList<GraphReadPredicate>? ReadPredicates = null, bool IsInitialBulkLoad = false, string? InterpretationFingerprint = null)
{
    // Profile-owned, bounded result captured before admission. It is part of the
    // native identity and retained in the operation receipt for exact replay.
    internal ReadOnlyMemory<byte> QueryResult
    {
        get; init;
    }

    /// <summary>Gets the transaction identity used for retry detection.</summary>
    public Guid TransactionId
    {
        get;
    }
    = TransactionId != Guid.Empty ? TransactionId
        : throw new ArgumentException("A transaction identity is required.", nameof(TransactionId));

    /// <summary>Gets the captured mutations in application order.</summary>
    public IReadOnlyList<GraphMutation> Mutations
    {
        get;
    }
    = Mutations?.Select(item =>
        item ?? throw new ArgumentException("A Graph mutation cannot be null.", nameof(Mutations))).ToArray()
        ?? throw new ArgumentNullException(nameof(Mutations));

    /// <summary>Gets the captured optimistic dependencies, or null when none were supplied.</summary>
    public IReadOnlyList<GraphReadPredicate>? ReadPredicates
    {
        get;
    }
    = ReadPredicates?.Select(item =>
        item ?? throw new ArgumentException("A Graph read predicate cannot be null.", nameof(ReadPredicates))).ToArray();
}

/// <summary>Represents a native graph vertex and its current labels and properties.</summary>
public sealed record GraphVertex
{
    private IReadOnlySet<string> _labels = Array.Empty<string>().ToFrozenSet(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, DatabaseValue> _properties =
        Array.Empty<KeyValuePair<string, DatabaseValue>>().ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>Creates a vertex value with immutable, ordinally compared labels and properties.</summary>
    /// <param name="id">The vertex identity.</param>
    /// <param name="labels">The vertex labels; compatible frozen sets may be reused, otherwise values are copied.</param>
    /// <param name="properties">Typed property values; compatible immutable collections may be reused, otherwise entries are copied.</param>
    /// <exception cref="ArgumentNullException">A collection or label/property name is null.</exception>
    /// <exception cref="ArgumentException">A name is empty or whitespace, a property value is null, or ordinal property names collide.</exception>
    public GraphVertex(GraphVertexId id, IReadOnlySet<string> labels,
        IReadOnlyDictionary<string, DatabaseValue> properties)
    {
        ArgumentNullException.ThrowIfNull(labels);
        ArgumentNullException.ThrowIfNull(properties);
        Id = id;
        Labels = labels;
        Properties = properties;
    }

    /// <summary>Gets the vertex identity.</summary>
    public GraphVertexId Id
    {
        get;
    }

    /// <summary>Gets or initializes the immutable label set using ordinal, case-sensitive comparison.</summary>
    public IReadOnlySet<string> Labels
    {
        get => _labels;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value is FrozenSet<string> frozen &&
                (frozen.Count == 0 || ReferenceEquals(frozen.Comparer, StringComparer.Ordinal)))
            {
                foreach (string label in frozen)
                {
                    GraphContractNames.Required(label);
                }

                _labels = frozen;
            }
            else
            {
                _labels = value.Select(GraphContractNames.Required).ToFrozenSet(StringComparer.Ordinal);
            }
        }
    }

    /// <summary>Gets or initializes immutable typed properties with ordinal, case-sensitive names.</summary>
    public IReadOnlyDictionary<string, DatabaseValue> Properties
    {
        get => _properties;
        init => _properties = GraphContractNames.CopyProperties(value);
    }
}

/// <summary>Identifies a directed edge and its endpoints and label, without edge properties or vertex state.</summary>
/// <param name="Id">The edge identity.</param>
/// <param name="From">The source vertex identity.</param>
/// <param name="To">The target vertex identity.</param>
/// <param name="Label">The relationship label.</param>
public sealed record GraphEdgeReference(GraphEdgeId Id, GraphVertexId From, GraphVertexId To, string Label);

/// <summary>Represents a labeled directed native graph edge and its properties.</summary>
public sealed record GraphEdge
{
    private IReadOnlyDictionary<string, DatabaseValue> _properties =
        Array.Empty<KeyValuePair<string, DatabaseValue>>().ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>Creates a directed edge value with an immutable property dictionary.</summary>
    /// <param name="id">The edge identity.</param>
    /// <param name="from">The source vertex identity.</param>
    /// <param name="to">The target vertex identity.</param>
    /// <param name="label">The nonempty relationship label.</param>
    /// <param name="properties">Typed properties, copied unless already held in a compatible immutable collection with ordinal comparison.</param>
    /// <exception cref="ArgumentNullException">The label, property collection or a property name is null.</exception>
    /// <exception cref="ArgumentException">A name is empty or whitespace, a property value is null, or ordinal property names collide.</exception>
    public GraphEdge(GraphEdgeId id, GraphVertexId from, GraphVertexId to, string label,
        IReadOnlyDictionary<string, DatabaseValue> properties)
        : this(id, from, to, label, properties, GraphEdgeStorageRole.Complete)
    {
    }

    internal GraphEdge(GraphEdgeId id, GraphVertexId from, GraphVertexId to, string label,
        IReadOnlyDictionary<string, DatabaseValue> properties, GraphEdgeStorageRole storageRole)
    {
        ArgumentNullException.ThrowIfNull(properties);
        Id = id;
        From = from;
        To = to;
        Label = GraphContractNames.Required(label);
        if ((storageRole & GraphEdgeStorageRole.Complete) == GraphEdgeStorageRole.None ||
            (storageRole & ~GraphEdgeStorageRole.Supported) != GraphEdgeStorageRole.None)
        {
            throw new ArgumentOutOfRangeException(nameof(storageRole));
        }

        StorageRole = storageRole;
        Properties = properties;
    }

    /// <summary>Gets the edge identity.</summary>
    public GraphEdgeId Id
    {
        get;
    }

    /// <summary>Gets the source vertex identity.</summary>
    public GraphVertexId From
    {
        get;
    }

    /// <summary>Gets the target vertex identity.</summary>
    public GraphVertexId To
    {
        get;
    }

    /// <summary>Gets the case-sensitive relationship label.</summary>
    public string Label
    {
        get;
    }

    internal GraphEdgeStorageRole StorageRole
    {
        get;
    }

    /// <summary>Gets or initializes immutable typed properties with ordinal, case-sensitive names.</summary>
    public IReadOnlyDictionary<string, DatabaseValue> Properties
    {
        get => _properties;
        init => _properties = GraphContractNames.CopyProperties(value);
    }
}

internal static class GraphContractNames
{
    public static string Required(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value;
    }

    public static IReadOnlyDictionary<string, DatabaseValue> CopyProperties(
        IReadOnlyDictionary<string, DatabaseValue> properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        if (properties is SmallGraphProperties)
        {
            return properties;
        }
        if (properties is FrozenDictionary<string, DatabaseValue> frozen &&
            (frozen.Count == 0 || ReferenceEquals(frozen.Comparer, StringComparer.Ordinal)))
        {
            foreach ((string name, DatabaseValue value) in frozen)
            {
                Required(name);
                if (value is null)
                {
                    throw new ArgumentException("A graph property value cannot be null.", nameof(properties));
                }
            }
            return frozen;
        }
        if (properties.Count is > 0 and <= SmallGraphProperties.MaximumCount)
        {
            return new SmallGraphProperties(properties);
        }
        return properties.ToFrozenDictionary(item => Required(item.Key), item => item.Value ??
            throw new ArgumentException("A graph property value cannot be null.", nameof(properties)),
            StringComparer.Ordinal);
    }
}

/// <summary>Thrown when a requested graph snapshot has been reclaimed.</summary>
/// <param name="requested">The version requested by the caller.</param>
/// <param name="oldest">The oldest version still retained by the engine.</param>
public sealed class GraphSnapshotTooOldException(DatabaseVersion requested, DatabaseVersion oldest)
    : EntityForgeDbException($"Graph snapshot {requested.Value} is older than retained version {oldest.Value}.")
{
    /// <summary>Gets the unavailable requested version.</summary>
    public DatabaseVersion Requested { get; } = requested;

    /// <summary>Gets the oldest retained version reported when the request failed.</summary>
    public DatabaseVersion OldestRetained { get; } = oldest;
}

/// <summary>Thrown when a graph snapshot lease cannot be acquired or maintained.</summary>
/// <param name="message">A diagnostic describing why the lease failed.</param>
public sealed class GraphSnapshotLeaseException(string message) : EntityForgeDbException(message);

/// <summary>Thrown when optimistic graph transaction dependencies conflict with intervening commits.</summary>
public sealed class GraphWriteConflictException : EntityForgeDbException
{
    /// <summary>Describes an optimistic dependency invalidated after the transaction's read version.</summary>
    /// <param name="readVersion">The version used by the rejected transaction.</param>
    /// <param name="conflictKey">The diagnostic key identifying the conflicting dependency.</param>
    public GraphWriteConflictException(DatabaseVersion readVersion, string conflictKey)
        : base($"Graph transaction based on version {readVersion.Value} conflicts at '{conflictKey}'.")
    {
        ReadVersion = readVersion;
        ConflictKey = conflictKey;
    }

    /// <summary>Gets the snapshot version used by the rejected transaction.</summary>
    public DatabaseVersion ReadVersion
    {
        get;
    }

    /// <summary>Gets the diagnostic key for the conflicting dependency.</summary>
    public string ConflictKey
    {
        get;
    }
}
