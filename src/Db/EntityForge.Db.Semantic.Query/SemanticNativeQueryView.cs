using System.Collections.Frozen;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using EntityForge.Db.Semantic;
using EntityForge.Semantics;

namespace EntityForge.Db.Semantic.Query;

// An ephemeral interpretation of retained semantic evidence, never a second database.
internal sealed partial class SemanticNativeQueryView
{
    internal const string Vocabulary = "urn:entityforge:semantic:view:";
    internal const string Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    internal const string Xsd = "http://www.w3.org/2001/XMLSchema#";
    internal const int MaximumEvidence = 10_000;
    internal const long MaximumBytes = 32 * 1024 * 1024;
    private static readonly FrozenSet<string> ValueLabels = new[] { Vocabulary + "Value" }.ToFrozenSet(StringComparer.Ordinal);
    private readonly Dictionary<GraphVertexId, GraphVertex> _vertices = [];
    private readonly Dictionary<GraphEdgeId, GraphEdge> _edges = [];
    private readonly Dictionary<Guid, string> _identities = [];
    private readonly Dictionary<string, (GraphVertexId Id, RdfIri Iri)> _entities = new(StringComparer.Ordinal);
    private readonly HashSet<RdfQuad> _quads = [];
    // Construction-only interning avoids validating and allocating the same immutable
    // predicate and metadata IRIs for every assertion. Never retain a global cache.
    private Dictionary<string, RdfIri>? _iris = new(StringComparer.Ordinal);
    private ConstructionMemo? _construction;

    private long _bytes;
    private GraphReadView? _graph;
    private RdfQuad[]? _relation;
    private IndexedRdfRelation? _rdfIndex;

    internal GraphReadView Graph => LazyInitializer.EnsureInitialized(ref _graph, () => GraphReadView.Build(_vertices, _edges));

    internal IReadOnlyList<RdfQuad> Quads => LazyInitializer.EnsureInitialized(ref _relation, () => _quads.ToArray());

    internal RdfSparqlDatasetSnapshot SparqlDataset(DatabaseVersion version) =>
        LazyInitializer.EnsureInitialized(ref _rdfIndex, () => new IndexedRdfRelation(Quads)).Snapshot(version);

    internal long PayloadBytes => _bytes;

    private sealed class ConstructionMemo : IDisposable
    {
        internal readonly SemanticCanonicalAtomCodec Codec = new();
        internal readonly Dictionary<SemanticEntityIdentity, (GraphVertexId Id, RdfIri Iri)> Entities = new(ReferenceEqualityComparer.Instance);
        internal readonly Dictionary<SemanticProvenance, string> Provenance = new(ReferenceEqualityComparer.Instance);
        internal readonly Dictionary<string, FrozenSet<string>> Labels = new(StringComparer.Ordinal);
        internal readonly IncrementalHash IdentityHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        public void Dispose() => IdentityHash.Dispose();
    }

    internal static SemanticNativeQueryView Create(IReadOnlyList<SemanticFieldEvidence> fields,
        IReadOnlyList<SemanticRelationshipEvidence> relationships, CancellationToken cancellation,
        SemanticNativeQueryView? previous = null)
    {
        if ((long)fields.Count + relationships.Count > MaximumEvidence)
        {
            throw new NotSupportedException("The semantic native-query evidence limit is 10,000 assertions.");
        }
        using var construction = new ConstructionMemo();
        var view = new SemanticNativeQueryView { _construction = construction, _previous = previous };
        foreach (var field in fields)
        {
            cancellation.ThrowIfCancellationRequested();
            var subject = view.Entity(field.Subject);
            var valueId = new GraphVertexId(view.Id("value:" + field.AssertionId.ToString("D")));
            var term = Term(field.Value);
            view.AddValue(field, valueId, term);
            view.Assertion(field.AssertionId, subject, valueId, term, field.Field,
                field.Provenance, field.BeginVersion);
        }
        foreach (var relationship in relationships)
        {
            cancellation.ThrowIfCancellationRequested();
            var subject = view.Entity(relationship.Subject);
            var target = view.Entity(relationship.Target);
            view.Assertion(relationship.AssertionId, subject, target.Id, target.Iri, relationship.Relationship,
                relationship.Provenance, relationship.BeginVersion);
        }
        view._iris = null;
        view._construction = null;
        view._previous = null;
        return view;
    }

    private (GraphVertexId Id, RdfIri Iri) Entity(SemanticEntityIdentity entity)
    {
        // Repeated references in one captured relation have the same canonical bytes.
        // Distinct objects still pass through durable encoding, never CLR value equality.
        var memo = _construction!;
        if (memo.Entities.TryGetValue(entity, out var cached))
        {
            return cached;
        }
        // Versioned canonical identity includes concept, scheme and typed value. Never equate
        // a URI-valued identity with that URI, or with an identity in another concept/scheme.
        var atom = memo.Codec.Encode(entity);
        var iri = new RdfIri($"urn:entityforge:semantic:entity:atom{SemanticCanonicalAtomCodec.FormatVersion}:" +
            Convert.ToHexStringLower(atom.FullHash.Span));
        // Deduplicate using exactly the durable encoding from which the view identifier
        // is derived, rather than a separate CLR equality rule.
        if (_entities.TryGetValue(iri.Value, out var existing))
        {
            memo.Entities.Add(entity, existing);
            return existing;
        }
        var term = Term(entity.Value);
        if (_previous is { } previous && previous._entityParts.TryGetValue(iri.Value, out var part) &&
            part.Term == term && part.Value.Equals(entity.Value) &&
            part.Vertex.Properties["concept"] is DatabaseValue.String c && c.Value == entity.Concept.Identifier.AbsoluteUri &&
            part.Vertex.Properties["identityScheme"] is DatabaseValue.String s && s.Value == entity.Scheme.Identifier.AbsoluteUri)
        {
            var reused = (part.Vertex.Id, part.Iri);
            RegisterIdentity(part.Vertex.Id.Value, iri.Value);
            _vertices.Add(part.Vertex.Id, part.Vertex);
            _quads.UnionWith(part.Quads);
            ChargeBytes(part.Bytes);
            _entityParts.Add(iri.Value, part);
            _entities.Add(iri.Value, reused);
            memo.Entities.Add(entity, reused);
            return reused;
        }
        long bytesBefore = _bytes;
        _recordingQuads = [];
        var id = new GraphVertexId(Id(iri.Value));
        var properties = ValueProperties(entity.Value, term, additionalProperties: 3);
        properties["iri"] = new DatabaseValue.String(iri.Value);
        properties["concept"] = new DatabaseValue.String(entity.Concept.Identifier.AbsoluteUri);
        properties["identityScheme"] = new DatabaseValue.String(entity.Scheme.Identifier.AbsoluteUri);
        ChargeProperties(properties);
        string concept = entity.Concept.Identifier.AbsoluteUri;
        if (!memo.Labels.TryGetValue(concept, out var labels))
        {
            labels = new[] { concept, Vocabulary + "Entity" }.ToFrozenSet(StringComparer.Ordinal);
            memo.Labels.Add(concept, labels);
        }
        _vertices.Add(id, new(id, labels, properties));
        Add(iri, Rdf + "type", Iri(entity.Concept.Identifier.AbsoluteUri));
        // Identity metadata occupies a separate graph so it cannot be mistaken for source assertions.
        var metadata = Iri(Vocabulary + "identities");
        Add(iri, Vocabulary + "identityScheme", Iri(entity.Scheme.Identifier.AbsoluteUri), metadata);
        Add(iri, Vocabulary + "identityValue", term, metadata);
        Add(iri, Vocabulary + "valueKind", new RdfLiteral(entity.Value.Kind.ToString()), metadata);
        _entityParts.Add(iri.Value, new(entity.Value, term, _vertices[id], iri, _recordingQuads.ToArray(), _bytes - bytesBefore));
        _recordingQuads = null;
        _entities.Add(iri.Value, (id, iri));
        memo.Entities.Add(entity, (id, iri));
        return (id, iri);
    }

    private void Assertion(Guid assertionId, (GraphVertexId Id, RdfIri Iri) subject,
        GraphVertexId target, RdfTerm value, SemanticTerm predicate, SemanticProvenance provenance, DatabaseVersion begin)
    {
        var provenanceMemo = _construction!.Provenance;
        if (!provenanceMemo.TryGetValue(provenance, out var provenanceJson))
        {
            provenanceJson = JsonSerializer.Serialize(provenance);
            provenanceMemo.Add(provenance, provenanceJson);
        }
        var key = new AssertionKey(subject.Id, target, value, predicate.Identifier.AbsoluteUri,
            predicate.Role, provenanceJson, begin);
        if (_previous is { } previous && previous._assertionParts.TryGetValue(assertionId, out var part) && part.Key == key)
        {
            RegisterIdentity(part.Edge.Id.Value, "assertion:" + assertionId.ToString("D"));
            _edges.Add(part.Edge.Id, part.Edge);
            _quads.UnionWith(part.Quads);
            ChargeBytes(part.Bytes);
            _assertionParts.Add(assertionId, part);
            return;
        }
        long bytesBefore = _bytes;
        _recordingQuads = [];
        var assertion = new RdfIri(Vocabulary + "assertion:" + assertionId.ToString("D"));
        // Charge each assertion's full logical payload even when objects are shared.
        Charge(provenanceJson);
        Charge(predicate.Identifier.AbsoluteUri);
        var properties = new Dictionary<string, DatabaseValue>(7 +
            (provenance.ValidFrom.HasValue ? 1 : 0) + (provenance.ValidTo.HasValue ? 1 : 0))
        {
            ["assertionId"] = new DatabaseValue.Uuid(assertionId),
            ["termRole"] = new DatabaseValue.String(predicate.Role.ToString()),
            ["sourceSystem"] = new DatabaseValue.String(provenance.SourceSystem.AbsoluteUri),
            ["sourceRecord"] = new DatabaseValue.String(provenance.SourceRecord),
            ["observedAt"] = new DatabaseValue.DateTimeOffset(provenance.ObservedAt),
            ["beginVersion"] = new DatabaseValue.Int64(begin.Value),
            ["provenanceJson"] = new DatabaseValue.String(provenanceJson)
        };
        if (provenance.ValidFrom is { } from)
        {
            properties["validFrom"] = new DatabaseValue.DateTimeOffset(from);
        }
        if (provenance.ValidTo is { } to)
        {
            properties["validTo"] = new DatabaseValue.DateTimeOffset(to);
        }
        var edge = new GraphEdgeId(Id("assertion:" + assertionId.ToString("D")));
        _edges.Add(edge, new(edge, subject.Id, target, predicate.Identifier.AbsoluteUri, properties));
        Add(subject.Iri, predicate.Identifier.AbsoluteUri, value);
        Add(subject.Iri, predicate.Identifier.AbsoluteUri, value, assertion);
        var metadata = Iri(Vocabulary + "provenance");
        Add(assertion, Rdf + "type", Iri(Rdf + "Statement"), metadata);
        Add(assertion, Rdf + "subject", subject.Iri, metadata);
        Add(assertion, Rdf + "predicate", Iri(predicate.Identifier.AbsoluteUri), metadata);
        Add(assertion, Rdf + "object", value, metadata);
        Add(assertion, Vocabulary + "termRole", new RdfLiteral(predicate.Role.ToString()), metadata);
        Add(assertion, Vocabulary + "sourceSystem", Iri(provenance.SourceSystem.AbsoluteUri), metadata);
        Add(assertion, Vocabulary + "sourceRecord", new RdfLiteral(provenance.SourceRecord), metadata);
        Add(assertion, Vocabulary + "observedAt", Term(new DateTimeSemanticValue(provenance.ObservedAt)), metadata);
        Add(assertion, Vocabulary + "beginVersion", Term(new IntegerSemanticValue(begin.Value)), metadata);
        Add(assertion, Vocabulary + "provenanceJson", new RdfLiteral(provenanceJson), metadata);
        if (provenance.ValidFrom is { } f)
        {
            Add(assertion, Vocabulary + "validFrom", Term(new DateTimeSemanticValue(f)), metadata);
        }
        if (provenance.ValidTo is { } t)
        {
            Add(assertion, Vocabulary + "validTo", Term(new DateTimeSemanticValue(t)), metadata);
        }
        _assertionParts.Add(assertionId, new(key, _edges[edge], _recordingQuads.ToArray(), _bytes - bytesBefore));
        _recordingQuads = null;
    }

    private void Add(RdfTerm subject, string predicate, RdfTerm value, RdfIri? graph = null)
    {
        var quad = new RdfQuad(subject, Iri(predicate), value, graph);
        _quads.Add(quad);
        _recordingQuads?.Add(quad);
    }

    private RdfIri Iri(string value)
    {
        if (!_iris!.TryGetValue(value, out var iri))
        {
            iri = new RdfIri(value);
            _iris.Add(value, iri);
        }
        return iri;
    }

    private Guid Id(string identity)
    {
        if (_previous is { } previous && previous._identityIds.TryGetValue(identity, out var existing))
        {
            return RegisterIdentity(existing, identity);
        }
        // Construction is single-threaded. Reuse the cryptographic provider but
        // reset between identities; the wire identity remains SHA256(UTF8(text)).
        int capacity = Encoding.UTF8.GetMaxByteCount(identity.Length);
        Span<byte> input = capacity <= 512 ? stackalloc byte[capacity] : new byte[capacity];
        int length = Encoding.UTF8.GetBytes(identity.AsSpan(), input);
        var hash = _construction!.IdentityHash;
        hash.AppendData(input[..length]);
        Span<byte> digest = stackalloc byte[32];
        if (!hash.TryGetHashAndReset(digest, out int written) || written != digest.Length)
        {
            throw new InvalidOperationException("A semantic query-view identity requires a complete SHA-256 hash.");
        }
        var id = new Guid(digest[..16]);
        return RegisterIdentity(id, identity);
    }

    private void Charge(string value)
    {
        ChargeBytes(Encoding.UTF8.GetByteCount(value));
    }

    private void ChargeBytes(long bytes)
    {
        _bytes += bytes;
        if (_bytes > MaximumBytes)
        {
            throw new NotSupportedException("The semantic native-query payload limit is 32 MiB.");
        }
    }

    private void ChargeProperties(IReadOnlyDictionary<string, DatabaseValue> properties)
    {
        foreach (var property in properties)
        {
            Charge(property.Key);
            // Lexical representations already account for scalar data, including nonfinite
            // floats which cannot be serialized as JSON numbers for budget accounting.
            if (property.Value is DatabaseValue.String text)
            {
                Charge(text.Value);
            }
        }
    }

    internal static RdfTerm Term(SemanticValue value) => value switch
    {
        NullSemanticValue => new RdfLiteral("", Vocabulary + "Null"),
        BooleanSemanticValue v => new RdfLiteral(XmlConvert.ToString(v.Value), Xsd + "boolean"),
        IntegerSemanticValue v => new RdfLiteral(XmlConvert.ToString(v.Value), Xsd + "integer"),
        FloatingPointSemanticValue v => new RdfLiteral(XmlConvert.ToString(v.Value), Xsd + "double"),
        DecimalSemanticValue v => new RdfLiteral(XmlConvert.ToString(v.Value), Xsd + "decimal"),
        StringSemanticValue v => new RdfLiteral(v.Value),
        LanguageStringSemanticValue v => new RdfLiteral(v.Value, language: v.Language),
        DateSemanticValue v => new RdfLiteral(v.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Xsd + "date"),
        TimeSemanticValue v => new RdfLiteral(v.Value.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture), Xsd + "time"),
        DateTimeSemanticValue v => new RdfLiteral(XmlConvert.ToString(v.Value), Xsd + "dateTime"),
        DurationSemanticValue v => new RdfLiteral(XmlConvert.ToString(v.Value), Xsd + "dayTimeDuration"),
        BinarySemanticValue v => new RdfLiteral(Convert.ToBase64String(v.Value.Span), Xsd + "base64Binary"),
        AbsoluteUriSemanticValue v => new RdfIri(v.Value.AbsoluteUri),
        GuidSemanticValue v => new RdfLiteral(v.Value.ToString("D"), Vocabulary + "Guid"),
        JsonSemanticValue v => new RdfLiteral(v.Utf8Json, Vocabulary + "Json"),
        TypedLiteralSemanticValue v => new RdfLiteral(v.LexicalValue, v.Datatype.AbsoluteUri),
        _ => throw new NotSupportedException("Unsupported semantic value representation.")
    };

    private static Dictionary<string, DatabaseValue> ValueProperties(SemanticValue value, RdfTerm term, int additionalProperties = 0)
    {
        string lexical = term is RdfLiteral literal ? literal.LexicalForm : ((RdfIri)term).Value;
        // Non-native kinds remain lexical plus explicit kind/datatype/language; never silently
        // treat a language-tagged or user-typed value as an unqualified scalar.
        DatabaseValue? scalar = value switch
        {
            BooleanSemanticValue v => new DatabaseValue.Boolean(v.Value),
            IntegerSemanticValue v => new DatabaseValue.Int64(v.Value),
            FloatingPointSemanticValue v => new DatabaseValue.Double(v.Value),
            DecimalSemanticValue v => new DatabaseValue.Decimal(v.Value),
            StringSemanticValue v => new DatabaseValue.String(v.Value),
            GuidSemanticValue v => new DatabaseValue.Uuid(v.Value),
            DateSemanticValue v => new DatabaseValue.Date(new(v.Value.Year, v.Value.Month, v.Value.Day)),
            TimeSemanticValue v => new DatabaseValue.LocalTime(new(v.Value.Ticks * 100)),
            DateTimeSemanticValue v => new DatabaseValue.DateTimeOffset(v.Value),
            DurationSemanticValue v => Duration(v.Value),
            _ => null
        };
        // Include the identity metadata added by the caller to avoid growing the same
        // small property bag several times during a cold view build.
        int capacity = 2 + additionalProperties + (scalar is not null ? 1 : 0) +
            (term is RdfLiteral { Datatype: not null } ? 1 : 0) +
            (term is RdfLiteral { Language: not null } ? 1 : 0);
        var result = new Dictionary<string, DatabaseValue>(capacity)
        {
            ["kind"] = new DatabaseValue.String(value.Kind.ToString()),
            ["lexical"] = new DatabaseValue.String(lexical)
        };
        if (term is RdfLiteral { Datatype: { } datatype })
        {
            result["datatype"] = new DatabaseValue.String(datatype);
        }
        if (term is RdfLiteral { Language: { } language })
        {
            result["language"] = new DatabaseValue.String(language);
        }
        if (scalar is not null)
        {
            result["scalar"] = scalar;
        }
        return result;
    }

    private static DatabaseValue.Duration Duration(TimeSpan value)
    {
        long seconds = Math.DivRem(value.Ticks, TimeSpan.TicksPerSecond, out long fractionalTicks);
        if (fractionalTicks < 0)
        {
            seconds--;
            fractionalTicks += TimeSpan.TicksPerSecond;
        }
        return new(new(0, 0, seconds, checked((int)(fractionalTicks * 100))));
    }
}
