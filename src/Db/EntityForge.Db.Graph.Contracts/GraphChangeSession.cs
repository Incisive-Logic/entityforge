using System.Text.Json;
using EntityForge.Db.Mapping;
using EntityForge.Db.Graph.Contracts;

namespace EntityForge.Db.Graph;

/// <summary>Complete vertex state in an explicitly declared scope. Null removes the vertex; incident edges must be removed explicitly.</summary>
/// <param name="Identity">Vertex identity to replace.</param>
/// <param name="Value">Complete desired vertex value, or null to remove the vertex.</param>
public sealed record GraphVertexReplacement(GraphVertexId Identity, GraphVertex? Value);

/// <summary>Complete edge state in an explicitly declared scope. Null removes the edge.</summary>
/// <param name="Identity">Edge identity to replace.</param>
/// <param name="Value">Complete desired edge value, or null to remove the edge.</param>
public sealed record GraphEdgeReplacement(GraphEdgeId Identity, GraphEdge? Value);

/// <summary>Detached native Graph changes without a POCO or entity mapping.</summary>
/// <remarks>
/// Factory methods capture a serialized request; creating a session does not contact a database or commit changes.
/// Submit it through the graph connection's change API. Retain the operation identity when retrying the same
/// request; use a new identity for a different logical operation. Database validation, concurrency checks and
/// expansion of replacement scopes occur when the request is applied.
/// </remarks>
public sealed class GraphChangeSession
{
    /// <summary>Maximum supplied or expanded mutations in a bounded native change session.</summary>
    public const int MaximumChanges = 1_000;
    private const int FormatVersion = 1;

    internal sealed record Definition(int Version, Guid OperationId, byte[] Payload, Guid[]? Vertices, Guid[]? Edges, DatabaseVersion? ExpectedVersion);

    private readonly byte[] _request;

    internal byte[] RequestBytes() => _request.ToArray();

    internal static GraphChangeSession RestoreRequest(byte[] bytes)
    {
        if (bytes.Length > EntityRecordDeliveryCodec.MaximumPayloadBytes)
        {
            throw new InvalidDataException("Oversized native request.");
        }

        var definition = JsonSerializer.Deserialize<Definition>(bytes) ?? throw new InvalidDataException("Missing native request.");
        if (definition.Version != FormatVersion || definition.OperationId == Guid.Empty)
        {
            throw new InvalidDataException("Invalid native request version or identity.");
        }

        var transaction = GraphTransactionCodec.Decode(definition.OperationId, definition.Payload);
        GraphChangeSession result;
        if (definition.Vertices is null)
        {
            if (definition.Edges is not null || definition.ExpectedVersion is not null)
            {
                throw new InvalidDataException("Unexpected incremental replacement metadata.");
            }

            result = Incremental(transaction);
        }
        else
        {
            if (definition.Edges is null || transaction.IsInitialBulkLoad || transaction.InterpretationFingerprint is not null ||
                transaction.ReadVersion is not null || transaction.ReadPredicates is { Count: > 0 })
            {
                throw new InvalidDataException("Invalid replacement transaction metadata.");
            }

            var vertices = definition.Vertices.Select(id =>
            {
                var key = new GraphVertexId(id);
                var create = transaction.Mutations.OfType<CreateVertex>().SingleOrDefault(v => v.Vertex == key);
                return new GraphVertexReplacement(key, create is null ? null : new GraphVertex(key,
                    create.Labels.ToHashSet(StringComparer.Ordinal), transaction.Mutations.OfType<SetVertexProperty>()
                        .Where(p => p.Vertex == key).ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal)));
            }).ToArray();
            var edges = definition.Edges.Select(id =>
            {
                var key = new GraphEdgeId(id);
                var create = transaction.Mutations.OfType<CreateEdge>().SingleOrDefault(e => e.Edge == key);
                return new GraphEdgeReplacement(key, create is null ? null : new GraphEdge(key, create.From, create.To, create.Label,
                    transaction.Mutations.OfType<SetEdgeProperty>().Where(p => p.Edge == key)
                        .ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal)));
            }).ToArray();
            result = Replace(definition.OperationId, vertices, edges, definition.ExpectedVersion);
        }
        if (!result._request.AsSpan().SequenceEqual(bytes))
        {
            throw new InvalidDataException("Noncanonical native request.");
        }

        return result;
    }

    internal Definition ReadDefinition() => JsonSerializer.Deserialize<Definition>(_request)!;

    private GraphChangeSession(Definition definition)
    {
        _request = JsonSerializer.SerializeToUtf8Bytes(definition);
        if (_request.Length > EntityRecordDeliveryCodec.MaximumPayloadBytes)
        {
            throw new InvalidOperationException("The Graph change session exceeds its byte limit.");
        }

        OperationId = definition.OperationId;
    }

    /// <summary>Gets the stable operation identity for replay and resolution.</summary>
    public Guid OperationId
    {
        get;
    }

    /// <summary>Captures an ordered native delta with its native concurrency predicates.</summary>
    /// <param name="transaction">The transaction to capture, including its operation identity, ordered mutations and read predicates.</param>
    /// <returns>A detached session containing a serialized copy of the transaction.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="transaction"/> is null.</exception>
    /// <exception cref="ArgumentException">The transaction requests initial bulk loading or carries a mapping interpretation fingerprint.</exception>
    /// <exception cref="InvalidOperationException">The request exceeds the mutation or serialized payload limit.</exception>
    /// <remarks>Subsequent changes to caller-owned collections do not alter the captured request. This method does not check the mutations against current database state.</remarks>
    public static GraphChangeSession Incremental(GraphTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        if (transaction.IsInitialBulkLoad || transaction.InterpretationFingerprint is not null)
        {
            throw new ArgumentException("A change session cannot impersonate a mapped or initial-load transaction.", nameof(transaction));
        }

        return new(new(FormatVersion, transaction.TransactionId, Encode(transaction), null, null, null));
    }

    /// <summary>Captures complete state of the declared vertices and edges. Undeclared identities remain unchanged.</summary>
    /// <param name="operationId">Nonempty identity of this logical replacement operation, retained for retries of the same request.</param>
    /// <param name="vertices">Unique vertex scopes and their complete desired values; a null value requests deletion.</param>
    /// <param name="edges">Unique edge scopes and their complete desired values; a null value requests deletion.</param>
    /// <param name="expectedVersion">Read version for optimistic validation when applied, or null to use the snapshot acquired during preparation.</param>
    /// <returns>A detached session containing canonical serialized replacement scopes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="vertices"/> or <paramref name="edges"/> is null.</exception>
    /// <exception cref="ArgumentException">The identity is empty, scopes are empty, repeated or oversized, or a desired value differs from its declared identity.</exception>
    /// <exception cref="InvalidOperationException">The encoded desired mutations or serialized request exceed their limits.</exception>
    /// <remarks>
    /// Omitted properties and labels are removed from declared vertices. Incident edges are not deleted implicitly:
    /// include their deletion scopes when removing a vertex. Preparation captures reads of affected elements and
    /// relevant adjacency/endpoints; conflicts are checked when applied. Expanded mutations must also fit
    /// <see cref="MaximumChanges"/>, so a session accepted here can still exceed the limit during preparation.
    /// </remarks>
    public static GraphChangeSession Replace(Guid operationId, IReadOnlyList<GraphVertexReplacement> vertices,
        IReadOnlyList<GraphEdgeReplacement> edges, DatabaseVersion? expectedVersion = null)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        ArgumentNullException.ThrowIfNull(edges);
        if (vertices.Count + (long)edges.Count is < 1 or > MaximumChanges || vertices.Any(v => v is null) || edges.Any(e => e is null) ||
            vertices.Select(v => v.Identity).Distinct().Count() != vertices.Count || edges.Select(e => e.Identity).Distinct().Count() != edges.Count)
        {
            throw new ArgumentException("Invalid, repeated or oversized Graph replacement scopes.");
        }

        if (vertices.Any(v => v.Identity.Value == Guid.Empty || v.Value is not null && v.Value.Id != v.Identity) ||
            edges.Any(e => e.Identity.Value == Guid.Empty || e.Value is not null && e.Value.Id != e.Identity))
        {
            throw new ArgumentException("A Graph replacement differs from its declared identity.");
        }

        var desired = new List<GraphMutation>();
        foreach (var vertex in vertices.OrderBy(v => v.Identity.Value).Select(v => v.Value).OfType<GraphVertex>())
        {
            desired.Add(new CreateVertex(vertex.Id, vertex.Labels.Order(StringComparer.Ordinal).ToArray()));
            desired.AddRange(vertex.Properties.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new SetVertexProperty(vertex.Id, p.Key, p.Value)));
        }
        foreach (var edge in edges.OrderBy(e => e.Identity.Value).Select(e => e.Value).OfType<GraphEdge>())
        {
            desired.Add(new CreateEdge(edge.Id, edge.From, edge.To, edge.Label));
            desired.AddRange(edge.Properties.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new SetEdgeProperty(edge.Id, p.Key, p.Value)));
        }
        return new(new(FormatVersion, operationId, Encode(new(operationId, desired)),
            vertices.Select(v => v.Identity.Value).Order().ToArray(), edges.Select(e => e.Identity.Value).Order().ToArray(), expectedVersion));
    }

    internal static byte[] Encode(GraphTransaction transaction)
    {
        if (transaction.Mutations.Count > MaximumChanges)
        {
            throw new InvalidOperationException("The Graph change session exceeds its mutation limit.");
        }

        return GraphTransactionCodec.Encode(transaction);
    }
}
