using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Rdf.Contracts;
using EntityForge.Db.Semantic;
using EntityForge.Semantics;

namespace EntityForge.Db.Semantic.Query;

internal sealed partial class SemanticNativeQueryView
{
    // Only the immediately preceding bounded view is borrowed during construction.
    // Parts never reference their owning view or a storage lease.
    private SemanticNativeQueryView? _previous;
    private List<RdfQuad>? _recordingQuads;
    private readonly Dictionary<string, Guid> _identityIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EntityPart> _entityParts = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, ValuePart> _valueParts = [];
    private readonly Dictionary<Guid, AssertionPart> _assertionParts = [];

    private sealed record EntityPart(SemanticValue Value, RdfTerm Term, GraphVertex Vertex, RdfIri Iri, RdfQuad[] Quads, long Bytes);

    private sealed record ValuePart(SemanticValue Value, RdfTerm Term, GraphVertex Vertex, long Bytes);

    private sealed record AssertionKey(GraphVertexId Subject, GraphVertexId Target, RdfTerm Value,
        string Predicate, SemanticTermRole Role, string Provenance, DatabaseVersion Begin);

    private sealed record AssertionPart(AssertionKey Key, GraphEdge Edge, RdfQuad[] Quads, long Bytes);

    private Guid RegisterIdentity(Guid id, string identity)
    {
        if (_identities.TryGetValue(id, out var other) && other != identity)
        {
            throw new InvalidDataException("A semantic query-view identity hash collision was detected.");
        }
        _identities[id] = identity;
        _identityIds[identity] = id;
        return id;
    }

    private void AddValue(SemanticFieldEvidence field, GraphVertexId id, RdfTerm term)
    {
        if (_previous is { } previous && previous._valueParts.TryGetValue(field.AssertionId, out var part) &&
            part.Term == term && part.Value.Equals(field.Value))
        {
            _vertices.Add(id, part.Vertex);
            ChargeBytes(part.Bytes);
            _valueParts.Add(field.AssertionId, part);
            return;
        }
        long bytesBefore = _bytes;
        var properties = ValueProperties(field.Value, term);
        ChargeProperties(properties);
        var vertex = new GraphVertex(id, ValueLabels, properties);
        _vertices.Add(id, vertex);
        _valueParts.Add(field.AssertionId, new(field.Value, term, vertex, _bytes - bytesBefore));
    }
}
