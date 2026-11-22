using EntityForge.Semantics;
using EntityForge.Values;
using EntityForge.Query.Results;

namespace EntityForge.Query.Values;

/// <summary>Base representation for typed query results, including scalars, graph elements and explicit absence states.</summary>
public abstract record QueryValue;

/// <summary>An explicitly null query value, distinct from an unbound variable.</summary>
public sealed record NullValue : QueryValue
{
    private NullValue()
    {
    }

    /// <summary>Gets the shared explicit-null value.</summary>
    public static NullValue Instance { get; } = new();
}

/// <summary>A query variable with no binding in this solution, distinct from an explicit null.</summary>
public sealed record UnboundValue : QueryValue
{
    private UnboundValue()
    {
    }

    /// <summary>Gets the shared unbound-variable value.</summary>
    public static UnboundValue Instance { get; } = new();
}

/// <summary>Base identity for a graph element, including stable identifiers and result-scoped blank nodes.</summary>
public abstract record GraphElementIdentity : QueryValue;

/// <summary>A named graph element identity whose interpretation belongs to the producing profile.</summary>
public sealed record IdentifierValue : GraphElementIdentity
{
    /// <summary>Captures a nonblank identifier without URI validation or normalization.</summary>
    /// <param name="identifier">The identifier exactly as supplied by the producer.</param>
    /// <exception cref="ArgumentException">The identifier is null, empty or whitespace.</exception>
    public IdentifierValue(string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        Identifier = identifier;
    }

    /// <summary>Gets the exact named identifier.</summary>
    public string Identifier
    {
        get;
    }
}

/// <summary>A blank-node identity qualified by the result scope in which its label is meaningful.</summary>
public sealed record BlankNodeValue : GraphElementIdentity
{
    /// <summary>Associates a blank-node label with its result scope.</summary>
    /// <param name="identifier">The nonblank label within the result scope.</param>
    /// <param name="resultScopeId">The producer's scope identity; labels from different scopes are not interchangeable.</param>
    /// <exception cref="ArgumentException">The identifier is null, empty or whitespace.</exception>
    public BlankNodeValue(string identifier, Guid resultScopeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        Identifier = identifier;
        ResultScopeId = resultScopeId;
    }

    /// <summary>Gets the blank-node label within its result scope.</summary>
    public string Identifier
    {
        get;
    }

    /// <summary>Gets the scope that distinguishes this label from labels in other results.</summary>
    public Guid ResultScopeId
    {
        get;
    }
}

/// <summary>Classifies a literal's scalar representation independently of its optional datatype URI.</summary>
public enum QueryScalarKind
{
    /// <summary>A Boolean truth value.</summary>
    Boolean,

    /// <summary>An integral numeric value.</summary>
    Integer,

    /// <summary>A floating-point numeric value.</summary>
    FloatingPoint,

    /// <summary>A decimal numeric value.</summary>
    Decimal,

    /// <summary>A character string, optionally carrying language metadata.</summary>
    String,

    /// <summary>A calendar date.</summary>
    Date,

    /// <summary>A time-of-day value in the producer's supported representation.</summary>
    Time,

    /// <summary>A date-and-time value in the producer's supported representation.</summary>
    DateTime,

    /// <summary>A duration value.</summary>
    Duration,

    /// <summary>A binary value represented lexically by the producer.</summary>
    Binary,

    /// <summary>An absolute URI value.</summary>
    AbsoluteUri,

    /// <summary>A globally unique identifier value.</summary>
    Guid,

    /// <summary>A point in a Cartesian or geographic coordinate reference system.</summary>
    Point,

    /// <summary>A time of day without a date or zone, with nanosecond precision.</summary>
    LocalTime,

    /// <summary>A Gregorian date and nanosecond local time without an offset or zone.</summary>
    LocalDateTime,

    /// <summary>A nanosecond time with a fixed UTC offset.</summary>
    ZonedTime,

    /// <summary>A nanosecond date and time with a fixed UTC offset.</summary>
    ZonedDateTime
}

/// <summary>Specifies the base writing direction associated with a text literal.</summary>
public enum TextDirection
{
    /// <summary>Text has left-to-right base direction.</summary>
    LeftToRight,

    /// <summary>Text has right-to-left base direction.</summary>
    RightToLeft
}

/// <summary>A scalar lexical value with optional datatype, language and writing-direction metadata.</summary>
/// <remarks>This transport representation does not validate datatype lexical spaces or language-tag syntax.</remarks>
public sealed record LiteralValue : QueryValue
{
    /// <summary>Captures a scalar's lexical spelling and metadata without converting its value.</summary>
    /// <param name="lexicalValue">The nonnull lexical spelling; empty strings are allowed.</param>
    /// <param name="scalarKind">The scalar representation reported by the producer.</param>
    /// <param name="datatypeIdentifier">An absolute datatype URI, or null when unspecified.</param>
    /// <param name="language">The language tag, or null when absent.</param>
    /// <param name="direction">The base text direction, or null when absent.</param>
    /// <exception cref="ArgumentNullException">The lexical value is null.</exception>
    /// <exception cref="ArgumentException">The datatype URI is relative.</exception>
    public LiteralValue(
        string lexicalValue,
        QueryScalarKind scalarKind = QueryScalarKind.String,
        Uri? datatypeIdentifier = null,
        string? language = null,
        TextDirection? direction = null)
    {
        ArgumentNullException.ThrowIfNull(lexicalValue);

        if (datatypeIdentifier is not null && !datatypeIdentifier.IsAbsoluteUri)
        {
            throw new ArgumentException("A datatype identifier must be an absolute URI.", nameof(datatypeIdentifier));
        }

        LexicalValue = lexicalValue;
        ScalarKind = scalarKind;
        DatatypeIdentifier = datatypeIdentifier;
        Language = language;
        Direction = direction;
    }

    /// <summary>Gets the unmodified lexical spelling.</summary>
    public string LexicalValue
    {
        get;
    }

    /// <summary>Gets the producer's scalar representation category.</summary>
    public QueryScalarKind ScalarKind
    {
        get;
    }

    /// <summary>Gets the datatype URI, or null when unspecified.</summary>
    public Uri? DatatypeIdentifier
    {
        get;
    }

    /// <summary>Gets the language tag as supplied, or null when absent.</summary>
    public string? Language
    {
        get;
    }

    /// <summary>Gets the base writing direction, or null when absent.</summary>
    public TextDirection? Direction
    {
        get;
    }
}

/// <summary>An ordered collection of query values, preserving repeated elements.</summary>
public sealed record ListValue : QueryValue
{
    /// <summary>Materializes the supplied sequence into a read-only list.</summary>
    /// <param name="items">The nonnull sequence to enumerate once; element references are retained.</param>
    /// <exception cref="ArgumentNullException">The sequence is null.</exception>
    public ListValue(IEnumerable<QueryValue> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        Items = Array.AsReadOnly(items.ToArray());
    }

    /// <summary>Gets the captured values in input order.</summary>
    public IReadOnlyList<QueryValue> Items
    {
        get;
    }
}

/// <summary>A structured JSON query value with an object or array root.</summary>
public sealed record JsonValue : QueryValue
{
    /// <summary>Parses and canonicalizes a structured JSON value.</summary>
    /// <param name="json">The JSON document containing an object or array root.</param>
    /// <exception cref="ArgumentException">The parsed root is a scalar rather than an object or array.</exception>
    public JsonValue(string json)
    {
        Value = CanonicalJsonValue.Parse(json);
        if (!Value.IsStructured)
        {
            throw new ArgumentException("A query JSON value must have an object or array root.", nameof(json));
        }
    }

    /// <summary>Gets the parsed canonical JSON representation.</summary>
    public CanonicalJsonValue Value
    {
        get;
    }
}

/// <summary>A property or relationship value with its source vocabulary, alignments and evidence.</summary>
public sealed record PropertyAssertion
{
    /// <summary>Captures an assertion and distinct alignment and evidence references.</summary>
    /// <param name="sourceTerm">The asserted property or relationship term; concepts are rejected.</param>
    /// <param name="value">The nonnull query value being asserted.</param>
    /// <param name="alignments">Optional alignments with the same semantic role; copied and deduplicated.</param>
    /// <param name="evidence">Optional evidence references; copied and deduplicated.</param>
    /// <exception cref="ArgumentException">The source role or an aligned term's role is incompatible.</exception>
    public PropertyAssertion(
        SemanticTerm sourceTerm,
        QueryValue value,
        IEnumerable<SemanticAlignment>? alignments = null,
        IEnumerable<EvidenceReference>? evidence = null)
    {
        SourceTerm = sourceTerm ?? throw new ArgumentNullException(nameof(sourceTerm));
        Value = value ?? throw new ArgumentNullException(nameof(value));

        if (sourceTerm.Role is SemanticTermRole.Concept)
        {
            throw new ArgumentException("A property assertion source term must be a property or relationship.", nameof(sourceTerm));
        }

        var materializedAlignments = alignments?.Distinct().ToArray() ?? Array.Empty<SemanticAlignment>();
        if (materializedAlignments.Any(alignment => alignment.AlignedTerm.Role != sourceTerm.Role))
        {
            throw new ArgumentException("Aligned and source terms must have the same semantic role.", nameof(alignments));
        }

        Alignments = Array.AsReadOnly(materializedAlignments);
        Evidence = Array.AsReadOnly((evidence ?? Array.Empty<EvidenceReference>()).Distinct().ToArray());
    }

    /// <summary>Gets the original asserted property or relationship term.</summary>
    public SemanticTerm SourceTerm
    {
        get;
    }

    /// <summary>Gets the asserted value.</summary>
    public QueryValue Value
    {
        get;
    }

    /// <summary>Gets the captured distinct vocabulary alignments.</summary>
    public IReadOnlyList<SemanticAlignment> Alignments
    {
        get;
    }

    /// <summary>Gets the captured distinct evidence references.</summary>
    public IReadOnlyList<EvidenceReference> Evidence
    {
        get;
    }
}

/// <summary>A concept membership assertion with vocabulary alignments and evidence.</summary>
public sealed record TypeAssertion
{
    /// <summary>Captures a concept assertion and its distinct alignment and evidence references.</summary>
    /// <param name="sourceTerm">The asserted concept term.</param>
    /// <param name="alignments">Optional alignments to concept terms; copied and deduplicated.</param>
    /// <param name="evidence">Optional evidence references; copied and deduplicated.</param>
    /// <exception cref="ArgumentException">The source or an aligned term is not a concept.</exception>
    public TypeAssertion(
        SemanticTerm sourceTerm,
        IEnumerable<SemanticAlignment>? alignments = null,
        IEnumerable<EvidenceReference>? evidence = null)
    {
        SourceTerm = sourceTerm ?? throw new ArgumentNullException(nameof(sourceTerm));

        if (sourceTerm.Role is not SemanticTermRole.Concept)
        {
            throw new ArgumentException("A type assertion source term must be a concept.", nameof(sourceTerm));
        }

        var materializedAlignments = alignments?.Distinct().ToArray() ?? Array.Empty<SemanticAlignment>();
        if (materializedAlignments.Any(alignment => alignment.AlignedTerm.Role is not SemanticTermRole.Concept))
        {
            throw new ArgumentException("A type alignment must target a concept.", nameof(alignments));
        }

        Alignments = Array.AsReadOnly(materializedAlignments);
        Evidence = Array.AsReadOnly((evidence ?? Array.Empty<EvidenceReference>()).Distinct().ToArray());
    }

    /// <summary>Gets the original asserted concept term.</summary>
    public SemanticTerm SourceTerm
    {
        get;
    }

    /// <summary>Gets the captured distinct concept alignments.</summary>
    public IReadOnlyList<SemanticAlignment> Alignments
    {
        get;
    }

    /// <summary>Gets the captured distinct evidence references.</summary>
    public IReadOnlyList<EvidenceReference> Evidence
    {
        get;
    }
}

/// <summary>A relationship type assertion with vocabulary alignments and evidence.</summary>
public sealed record RelationshipAssertion
{
    /// <summary>Captures a relationship assertion and distinct alignment and evidence references.</summary>
    /// <param name="sourceTerm">The asserted relationship term.</param>
    /// <param name="alignments">Optional alignments to relationship terms; copied and deduplicated.</param>
    /// <param name="evidence">Optional evidence references; copied and deduplicated.</param>
    /// <exception cref="ArgumentException">The source or an aligned term is not a relationship.</exception>
    public RelationshipAssertion(
        SemanticTerm sourceTerm,
        IEnumerable<SemanticAlignment>? alignments = null,
        IEnumerable<EvidenceReference>? evidence = null)
    {
        SourceTerm = sourceTerm ?? throw new ArgumentNullException(nameof(sourceTerm));

        if (sourceTerm.Role is not SemanticTermRole.Relationship)
        {
            throw new ArgumentException("A relationship assertion source term must be a relationship.", nameof(sourceTerm));
        }

        var materializedAlignments = alignments?.Distinct().ToArray() ?? Array.Empty<SemanticAlignment>();
        if (materializedAlignments.Any(alignment => alignment.AlignedTerm.Role is not SemanticTermRole.Relationship))
        {
            throw new ArgumentException("A relationship alignment must target a relationship.", nameof(alignments));
        }

        Alignments = Array.AsReadOnly(materializedAlignments);
        Evidence = Array.AsReadOnly((evidence ?? Array.Empty<EvidenceReference>()).Distinct().ToArray());
    }

    /// <summary>Gets the original asserted relationship term.</summary>
    public SemanticTerm SourceTerm
    {
        get;
    }

    /// <summary>Gets the captured distinct relationship alignments.</summary>
    public IReadOnlyList<SemanticAlignment> Alignments
    {
        get;
    }

    /// <summary>Gets the captured distinct evidence references.</summary>
    public IReadOnlyList<EvidenceReference> Evidence
    {
        get;
    }
}

/// <summary>A graph node result with identity, semantic assertions and property-graph labels.</summary>
public sealed record NodeValue : QueryValue
{
    /// <summary>Captures a node and copies its supplied collections without deduplicating them.</summary>
    /// <param name="identity">The nonnull node identity.</param>
    /// <param name="types">Optional type assertions, retained in input order.</param>
    /// <param name="properties">Optional property assertions, retained in input order.</param>
    /// <param name="labels">Optional property-graph labels, retained in input order.</param>
    /// <exception cref="ArgumentNullException">The identity is null.</exception>
    public NodeValue(
        GraphElementIdentity identity,
        IEnumerable<TypeAssertion>? types = null,
        IEnumerable<PropertyAssertion>? properties = null,
        IEnumerable<string>? labels = null)
    {
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        Types = ToReadOnly(types);
        Properties = ToReadOnly(properties);
        Labels = ToReadOnly(labels);
    }

    /// <summary>Gets the node identity.</summary>
    public GraphElementIdentity Identity
    {
        get;
    }

    /// <summary>Gets the captured semantic type assertions.</summary>
    public IReadOnlyList<TypeAssertion> Types
    {
        get;
    }

    /// <summary>Gets the captured property assertions.</summary>
    public IReadOnlyList<PropertyAssertion> Properties
    {
        get;
    }

    /// <summary>Gets the captured property-graph labels.</summary>
    public IReadOnlyList<string> Labels
    {
        get;
    }

    private static IReadOnlyList<T> ToReadOnly<T>(IEnumerable<T>? items) =>
        Array.AsReadOnly(items?.ToArray() ?? Array.Empty<T>());
}

/// <summary>A directed edge result with endpoints, a relationship assertion and optional edge identity.</summary>
public sealed record EdgeValue : QueryValue
{
    /// <summary>Creates an edge whose relationship has no additional alignment or evidence metadata.</summary>
    /// <param name="source">The nonnull source node identity.</param>
    /// <param name="target">The nonnull target node identity.</param>
    /// <param name="relationship">The relationship-role semantic term.</param>
    /// <param name="identity">The edge identity, or null if the producer does not supply one.</param>
    /// <param name="properties">Optional property assertions; copied in input order.</param>
    public EdgeValue(
        GraphElementIdentity source,
        GraphElementIdentity target,
        SemanticTerm relationship,
        GraphElementIdentity? identity = null,
        IEnumerable<PropertyAssertion>? properties = null)
        : this(source, target, new RelationshipAssertion(relationship), identity, properties)
    {
    }

    /// <summary>Creates an edge with its relationship assertion and captured property list.</summary>
    /// <param name="source">The nonnull source node identity.</param>
    /// <param name="target">The nonnull target node identity.</param>
    /// <param name="relationship">The nonnull relationship assertion.</param>
    /// <param name="identity">The edge identity, or null if the producer does not supply one.</param>
    /// <param name="properties">Optional property assertions; copied in input order without deduplication.</param>
    /// <exception cref="ArgumentNullException">An endpoint or relationship assertion is null.</exception>
    public EdgeValue(
        GraphElementIdentity source,
        GraphElementIdentity target,
        RelationshipAssertion relationship,
        GraphElementIdentity? identity = null,
        IEnumerable<PropertyAssertion>? properties = null)
    {
        if (relationship is null)
        {
            throw new ArgumentNullException(nameof(relationship));
        }

        Source = source ?? throw new ArgumentNullException(nameof(source));
        Target = target ?? throw new ArgumentNullException(nameof(target));
        Relationship = relationship;
        Identity = identity;
        Properties = Array.AsReadOnly(properties?.ToArray() ?? Array.Empty<PropertyAssertion>());
    }

    /// <summary>Gets the edge identity, or null when unavailable.</summary>
    public GraphElementIdentity? Identity
    {
        get;
    }

    /// <summary>Gets the source endpoint identity.</summary>
    public GraphElementIdentity Source
    {
        get;
    }

    /// <summary>Gets the target endpoint identity.</summary>
    public GraphElementIdentity Target
    {
        get;
    }

    /// <summary>Gets the relationship type and its associated semantic metadata.</summary>
    public RelationshipAssertion Relationship
    {
        get;
    }

    /// <summary>Gets the captured edge property assertions.</summary>
    public IReadOnlyList<PropertyAssertion> Properties
    {
        get;
    }
}

/// <summary>An ordered path result containing node and edge values.</summary>
/// <remarks>Construction checks element types, but does not validate alternation, connectivity or traversal direction.</remarks>
public sealed record PathValue : QueryValue
{
    /// <summary>Captures a sequence consisting only of node and edge values.</summary>
    /// <param name="elements">The sequence to copy in traversal order; an empty sequence is allowed.</param>
    /// <exception cref="ArgumentNullException">The sequence is null.</exception>
    /// <exception cref="ArgumentException">An element is neither a node nor an edge.</exception>
    public PathValue(IEnumerable<QueryValue> elements)
    {
        ArgumentNullException.ThrowIfNull(elements);
        var materializedElements = elements.ToArray();

        if (materializedElements.Any(element => element is not NodeValue and not EdgeValue))
        {
            throw new ArgumentException("A path may contain only nodes and edges.", nameof(elements));
        }

        Elements = Array.AsReadOnly(materializedElements);
    }

    /// <summary>Gets the captured node and edge sequence in input order.</summary>
    public IReadOnlyList<QueryValue> Elements
    {
        get;
    }
}
