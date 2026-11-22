using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace EntityForge.Db.Rdf.Contracts;

/// <summary>An RDF IRI, blank node, literal, or supported quoted triple.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$term")]
[JsonDerivedType(typeof(RdfIri), "iri")]
[JsonDerivedType(typeof(RdfBlankNode), "blank")]
[JsonDerivedType(typeof(RdfLiteral), "literal")]
[JsonDerivedType(typeof(RdfTripleTerm), "triple")]
public abstract record RdfTerm;

/// <summary>An absolute RDF IRI represented as decoded identifier text.</summary>
public sealed record RdfIri : RdfTerm
{
    /// <summary>Creates an IRI after validating that it is absolute.</summary>
    /// <param name="value">The absolute IRI text.</param>
    /// <exception cref="ArgumentException">The value is not a supported absolute IRI.</exception>
    public RdfIri(string value)
    {
        if (!RdfTermValidation.IsAbsoluteIri(value))
        {
            throw new ArgumentException("An RDF IRI must be a well-formed absolute IRI.", nameof(value));
        }

        Value = DatabaseDecodedIdentifiers.Text(value);
    }

    /// <summary>Gets the decoded absolute IRI text used for term equality.</summary>
    public string Value
    {
        get;
    }
}

/// <summary>A blank-node identity; its identifier is not a globally resolvable IRI.</summary>
public sealed record RdfBlankNode : RdfTerm
{
    /// <summary>Creates a blank node with the supplied local identifier.</summary>
    /// <param name="identifier">A nonempty, non-whitespace identifier without a query syntax prefix.</param>
    /// <exception cref="ArgumentNullException">The identifier is null.</exception>
    /// <exception cref="ArgumentException">The identifier is empty or consists only of whitespace.</exception>
    public RdfBlankNode(string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        Identifier = identifier;
    }

    /// <summary>Gets the local identifier, preserving its original case.</summary>
    public string Identifier
    {
        get;
    }
}

/// <summary>An RDF literal with a lexical form and either a datatype or a language tag.</summary>
public sealed record RdfLiteral : RdfTerm
{
    /// <summary>The datatype assigned to plain string literals.</summary>
    public const string StringDatatype = "http://www.w3.org/2001/XMLSchema#string";

    /// <summary>The datatype assigned to language-tagged strings.</summary>
    public const string LanguageStringDatatype = "http://www.w3.org/1999/02/22-rdf-syntax-ns#langString";

    /// <summary>Creates a literal without normalizing or interpreting its lexical form.</summary>
    /// <param name="lexicalForm">The literal text; an empty string is permitted.</param>
    /// <param name="datatype">An absolute datatype IRI, or null to infer the string datatype.</param>
    /// <param name="language">An optional tag following SPARQL LANGTAG syntax, stored in lowercase. Registry validity is not checked.</param>
    /// <exception cref="ArgumentNullException"><paramref name="lexicalForm"/> is null.</exception>
    /// <exception cref="ArgumentException">The datatype is not absolute, the language syntax is invalid, or the datatype and language are inconsistent.</exception>
    public RdfLiteral(string lexicalForm, string? datatype = null, string? language = null)
    {
        ArgumentNullException.ThrowIfNull(lexicalForm);
        if (datatype is not null && !RdfTermValidation.IsAbsoluteIri(datatype))
        {
            throw new ArgumentException("An RDF datatype IRI must be a well-formed absolute IRI.", nameof(datatype));
        }

        if (datatype is not null && language is not null &&
            !StringComparer.Ordinal.Equals(datatype, LanguageStringDatatype))
        {
            throw new ArgumentException("An RDF literal cannot specify both datatype and language.");
        }

        if (language is null && StringComparer.Ordinal.Equals(datatype, LanguageStringDatatype))
        {
            throw new ArgumentException("An rdf:langString literal requires a language tag.", nameof(language));
        }

        if (language is not null && !RdfTermValidation.IsLanguageTag(language))
        {
            throw new ArgumentException("An RDF language tag is invalid.", nameof(language));
        }

        LexicalForm = lexicalForm;
        Datatype = datatype ?? (language is null ? StringDatatype : LanguageStringDatatype);
        Language = language?.ToLowerInvariant();
    }

    /// <summary>Gets the original literal text, including any significant whitespace.</summary>
    public string LexicalForm
    {
        get;
    }

    /// <summary>Gets the explicit or inferred datatype IRI.</summary>
    public string? Datatype
    {
        get;
    }

    /// <summary>Gets the lowercase language tag, or null for a literal without a language.</summary>
    public string? Language
    {
        get;
    }
}

/// <summary>An RDF-star quoted triple used as an RDF subject or object term.</summary>
public sealed record RdfTripleTerm : RdfTerm
{
    /// <summary>Maximum supported nesting depth for untrusted quoted-triple input.</summary>
    public const int MaximumNestingDepth = 8;

    /// <summary>Creates a quoted triple after checking term placement and nesting limits.</summary>
    /// <param name="subject">An IRI, blank node or supported quoted triple; never a literal.</param>
    /// <param name="predicate">The predicate IRI.</param>
    /// <param name="object">The object term.</param>
    /// <exception cref="ArgumentNullException">A required term is null.</exception>
    /// <exception cref="ArgumentException">A term violates subject placement or nesting limits.</exception>
    [JsonConstructor]
    public RdfTripleTerm(RdfTerm subject, RdfIri predicate, RdfTerm @object)
    {
        Subject = subject ?? throw new ArgumentNullException(nameof(subject));
        Predicate = predicate ?? throw new ArgumentNullException(nameof(predicate));
        Object = @object ?? throw new ArgumentNullException(nameof(@object));
        RdfTermValidation.Validate(Subject, subject: true);
        RdfTermValidation.Validate(Object, subject: false);
    }

    /// <summary>Gets the subject term.</summary>
    public RdfTerm Subject
    {
        get;
    }

    /// <summary>Gets the predicate IRI.</summary>
    public RdfIri Predicate
    {
        get;
    }

    /// <summary>Gets the object term.</summary>
    public RdfTerm Object
    {
        get;
    }
}

/// <summary>An RDF statement in the default graph or a named graph.</summary>
public sealed record RdfQuad
{
    private RdfTerm? _graph;

    /// <summary>Creates and validates a statement and its optional graph name.</summary>
    /// <param name="subject">An IRI, blank node or supported quoted triple.</param>
    /// <param name="predicate">The predicate IRI.</param>
    /// <param name="object">The object term.</param>
    /// <param name="graph">An IRI or blank node naming the graph; null selects the default graph.</param>
    /// <exception cref="ArgumentNullException">A required term is null.</exception>
    /// <exception cref="ArgumentException">Term placement, graph naming or quoted-triple nesting is invalid.</exception>
    [JsonConstructor]
    public RdfQuad(RdfTerm subject, RdfIri predicate, RdfTerm @object, RdfTerm? graph = null)
    {
        Subject = subject;
        Predicate = predicate ?? throw new ArgumentNullException(nameof(predicate));
        Object = @object;
        Graph = graph;
        Validate();
    }

    /// <summary>Gets the subject term.</summary>
    public RdfTerm Subject
    {
        get;
    }

    /// <summary>Gets the predicate IRI.</summary>
    public RdfIri Predicate
    {
        get;
    }

    /// <summary>Gets the object term.</summary>
    public RdfTerm Object
    {
        get;
    }

    /// <summary>Gets or sets the named graph; null denotes the default graph.</summary>
    /// <exception cref="ArgumentException">The assigned graph is neither an IRI nor a blank node.</exception>
    public RdfTerm? Graph
    {
        get => _graph;
        init
        {
            if (value is not null and not RdfIri and not RdfBlankNode)
            {
                throw new ArgumentException("An RDF graph name must be an IRI or blank node.", nameof(value));
            }

            _graph = value;
        }
    }

    /// <summary>Validates RDF term placement, including subject and graph-name restrictions.</summary>
    /// <returns>This quad after validation.</returns>
    /// <exception cref="ArgumentException">A subject, object or graph term violates the supported RDF term rules.</exception>
    public RdfQuad Validate()
    {
        RdfTermValidation.Validate(Subject, subject: true);
        RdfTermValidation.Validate(Object, subject: false);
        if (Graph is not null and not RdfIri and not RdfBlankNode)
        {
            throw new ArgumentException("An RDF graph name must be an IRI or blank node.");
        }

        return this;
    }

}

internal static partial class RdfTermValidation
{
    // SPARQL LANGTAG syntax without the leading '@'; no registry validation.
    [GeneratedRegex(@"\A[a-zA-Z]+(?:-[a-zA-Z0-9]+)*\z")]
    private static partial Regex LanguageTagSyntax();

    internal static bool IsLanguageTag(string language) => LanguageTagSyntax().IsMatch(language);

    internal static void Validate(RdfTerm term, bool subject, int depth = 0)
    {
        ArgumentNullException.ThrowIfNull(term);
        if (subject && term is RdfLiteral)
        {
            throw new ArgumentException("An RDF subject cannot be a literal.");
        }

        if (term is not RdfTripleTerm triple)
        {
            return;
        }

        if (depth >= RdfTripleTerm.MaximumNestingDepth)
        {
            throw new ArgumentException($"An RDF-star quoted triple cannot exceed {RdfTripleTerm.MaximumNestingDepth} levels.");
        }

        Validate(triple.Subject, subject: true, depth + 1);
        Validate(triple.Object, subject: false, depth + 1);
    }
}

/// <summary>A statement assertion or retraction applied as part of an RDF transaction.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$mutation")]
[JsonDerivedType(typeof(AssertRdfQuad), "assert")]
[JsonDerivedType(typeof(RetractRdfQuad), "retract")]
public abstract record RdfMutation
{
    /// <summary>Captures the statement affected by this mutation.</summary>
    /// <param name="quad">The statement to assert or retract.</param>
    /// <exception cref="ArgumentNullException">The statement is null.</exception>
    protected RdfMutation(RdfQuad quad) => Quad = quad ?? throw new ArgumentNullException(nameof(quad));

    /// <summary>Gets the statement affected by this mutation.</summary>
    public RdfQuad Quad
    {
        get;
    }
}

/// <summary>Ensures a statement is present in its graph.</summary>
public sealed record AssertRdfQuad : RdfMutation
{
    /// <summary>Creates an assertion for the supplied statement.</summary>
    /// <param name="quad">The statement to assert.</param>
    /// <exception cref="ArgumentNullException">The statement is null.</exception>
    public AssertRdfQuad(RdfQuad quad) : base(quad)
    {
    }
}

/// <summary>Removes a statement from its graph if it is present.</summary>
public sealed record RetractRdfQuad : RdfMutation
{
    /// <summary>Creates a retraction for the supplied statement.</summary>
    /// <param name="quad">The statement to retract.</param>
    /// <exception cref="ArgumentNullException">The statement is null.</exception>
    public RetractRdfQuad(RdfQuad quad) : base(quad)
    {
    }
}

/// <summary>Base type for an RDF predicate read tracked by optimistic validation.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$read")]
[JsonDerivedType(typeof(RdfSubjectStateRead), "subject-state")]
public abstract record RdfReadPredicate;

/// <summary>Tracks the complete state of one RDF subject in a selected graph.</summary>
public sealed record RdfSubjectStateRead : RdfReadPredicate
{
    private const string FingerprintPrefix = "rdf-subject-state:v1:";

    /// <summary>Creates an optimistic read predicate with a previously captured state fingerprint.</summary>
    /// <param name="subject">The subject whose statements were read.</param>
    /// <param name="graph">The selected named graph, or null for graph selection controlled by <paramref name="matchDefaultGraphOnly"/>.</param>
    /// <param name="matchDefaultGraphOnly">Whether only default-graph statements participate.</param>
    /// <param name="expectedFingerprint">The canonical v1 fingerprint returned by <see cref="Capture"/>.</param>
    /// <exception cref="ArgumentNullException">The subject or fingerprint is null.</exception>
    /// <exception cref="ArgumentException">The graph name or fingerprint format is invalid.</exception>
    public RdfSubjectStateRead(RdfTerm subject, RdfTerm? graph, bool matchDefaultGraphOnly,
        string expectedFingerprint)
    {
        Subject = subject ?? throw new ArgumentNullException(nameof(subject));
        if (graph is not null and not RdfIri and not RdfBlankNode)
        {
            throw new ArgumentException("An RDF graph name must be an IRI or blank node.", nameof(graph));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(expectedFingerprint);
        string digest = expectedFingerprint.StartsWith(FingerprintPrefix, StringComparison.Ordinal)
            ? expectedFingerprint[FingerprintPrefix.Length..] : String.Empty;
        if (digest.Length != 64 || !digest.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("An RDF subject-state fingerprint must use the canonical v1 format.",
                nameof(expectedFingerprint));
        }

        Subject = subject;
        Graph = graph;
        MatchDefaultGraphOnly = matchDefaultGraphOnly;
        ExpectedFingerprint = expectedFingerprint;
    }

    /// <summary>Gets the subject term.</summary>
    public RdfTerm Subject
    {
        get;
    }

    /// <summary>Gets the named-graph selection; null leaves selection to the default-graph-only flag.</summary>
    public RdfTerm? Graph
    {
        get;
    }

    /// <summary>Gets whether selection is restricted to the default graph; false with a null graph includes all graphs.</summary>
    public bool MatchDefaultGraphOnly
    {
        get;
    }

    /// <summary>Gets the canonical fingerprint against which current subject state is checked.</summary>
    public string ExpectedFingerprint
    {
        get;
    }

    /// <summary>Captures a deterministic fingerprint of the subject state read by a caller.</summary>
    /// <param name="subject">The subject whose state was read.</param>
    /// <param name="graph">The graph selection used for the read.</param>
    /// <param name="matchDefaultGraphOnly">Whether the read was restricted to the default graph.</param>
    /// <param name="quads">The complete set of statements returned by that read. The caller must supply the matching statements.</param>
    /// <returns>A read predicate containing an order-independent fingerprint of the supplied statements.</returns>
    public static RdfSubjectStateRead Capture(RdfTerm subject, RdfTerm? graph,
        bool matchDefaultGraphOnly, IEnumerable<RdfQuad> quads) =>
        new(subject, graph, matchDefaultGraphOnly, Fingerprint(quads));

    internal static string Fingerprint(IEnumerable<RdfQuad> quads)
    {
        ArgumentNullException.ThrowIfNull(quads);
        var canonical = quads.Select(quad => CanonicalQuad(
                quad ?? throw new ArgumentException("An RDF quad cannot be null.", nameof(quads))))
            .Order(StringComparer.Ordinal);
        return FingerprintPrefix + Convert.ToHexString(DatabaseUtf8Hash.HashJoined(canonical, "\n"));
    }

    private static string CanonicalQuad(RdfQuad quad) =>
        $"{CanonicalTerm(quad.Subject)}{CanonicalTerm(quad.Predicate)}{CanonicalTerm(quad.Object)}" +
        (quad.Graph is null ? "G0:" : $"G1:{CanonicalTerm(quad.Graph)}");

    private static string CanonicalTerm(RdfTerm term) => term switch
    {
        RdfIri iri => Field('I', iri.Value),
        RdfBlankNode blank => Field('B', blank.Identifier),
        RdfLiteral literal => Field('L', literal.LexicalForm) + Field('D', literal.Datatype ?? String.Empty) +
            Field('A', literal.Language ?? String.Empty),
        RdfTripleTerm triple => $"T{CanonicalTerm(triple.Subject)}{CanonicalTerm(triple.Predicate)}" +
            CanonicalTerm(triple.Object),
        _ => throw new ArgumentOutOfRangeException(nameof(term))
    };

    private static string Field(char kind, string value) => $"{kind}{value.Length}:{value}";
}

/// <summary>An atomic RDF mutation batch with an identity for retry detection and optional optimistic reads.</summary>
/// <param name="TransactionId">A nonempty identity reused when retrying the same logical transaction.</param>
/// <param name="Mutations">The ordered mutations; copied into an array, with null entries rejected.</param>
/// <param name="IsInitialBulkLoad">Whether the caller requests initial bulk-load handling.</param>
/// <param name="SourceCommandHash">An optional command fingerprint used to validate retries.</param>
/// <param name="ReadVersion">The snapshot version on which optimistic reads were based.</param>
/// <param name="ReadPredicates">Optional predicates to validate before commit; copied into an array.</param>
/// <param name="InterpretationFingerprint">An optional fingerprint identifying the command interpretation used for replay.</param>
public sealed record RdfTransaction(Guid TransactionId, IReadOnlyList<RdfMutation> Mutations,
    bool IsInitialBulkLoad = false, string? SourceCommandHash = null,
    DatabaseVersion? ReadVersion = null, IReadOnlyList<RdfReadPredicate>? ReadPredicates = null, string? InterpretationFingerprint = null)
{
    /// <summary>Gets the identity used to detect retries of this transaction.</summary>
    public Guid TransactionId
    {
        get;
    }
    = TransactionId != Guid.Empty ? TransactionId
        : throw new ArgumentException("A transaction identity is required.", nameof(TransactionId));

    /// <summary>Gets the captured mutation batch in application order.</summary>
    public IReadOnlyList<RdfMutation> Mutations
    {
        get;
    }
    = Capture(Mutations, nameof(Mutations));

    /// <summary>Gets the captured optimistic read predicates, or null when none were supplied.</summary>
    public IReadOnlyList<RdfReadPredicate>? ReadPredicates
    {
        get;
    }
    = ReadPredicates is null ? null : Capture(ReadPredicates, nameof(ReadPredicates));

    private static T[] Capture<T>(IReadOnlyList<T> source, string parameter) where T : class
    {
        ArgumentNullException.ThrowIfNull(source, parameter);
        if (source.Count is < 0 or > RdfTransactionCodec.MaximumItems)
        {
            throw new ArgumentOutOfRangeException(parameter, "RDF transaction collection exceeds its item bound.");
        }

        var captured = new List<T>();
        foreach (T item in source)
        {
            if (captured.Count == RdfTransactionCodec.MaximumItems)
            {
                throw new ArgumentOutOfRangeException(parameter, "RDF transaction collection exceeds its item bound.");
            }

            captured.Add(item ?? throw new ArgumentException("An RDF transaction item cannot be null.", parameter));
        }

        return captured.ToArray();
    }
}

/// <summary>Thrown when an RDF transaction's optimistic subject read no longer matches current state.</summary>
/// <param name="readVersion">The snapshot version used by the rejected transaction.</param>
/// <param name="subject">The subject whose state conflicted.</param>
public sealed class RdfWriteConflictException(DatabaseVersion readVersion, RdfTerm subject)
    : EntityForgeDbException($"RDF transaction based on version {readVersion.Value} conflicts at subject '{subject}'.")
{
    /// <summary>Gets the snapshot version used by the conflicting transaction.</summary>
    public DatabaseVersion ReadVersion { get; } = readVersion;

    /// <summary>Gets the subject whose state changed.</summary>
    public RdfTerm Subject { get; } = subject;
}

/// <summary>Matches RDF statements by optional term constraints and graph selection.</summary>
public sealed record RdfQuadPattern
{
    /// <summary>Creates a pattern; null term constraints match any term in that position.</summary>
    /// <param name="Subject">The required subject, or null for any subject.</param>
    /// <param name="Predicate">The required predicate, or null for any predicate.</param>
    /// <param name="Object">The required object, or null for any object.</param>
    /// <param name="Graph">A required named graph, or null to leave graph selection to <paramref name="MatchDefaultGraphOnly"/>.</param>
    /// <param name="MatchDefaultGraphOnly">True to restrict to the default graph; false with a null graph matches all graphs.</param>
    /// <exception cref="ArgumentException">A constrained term is invalid, or a named graph is combined with default-only selection.</exception>
    public RdfQuadPattern(RdfTerm? Subject = null, RdfIri? Predicate = null, RdfTerm? Object = null,
        RdfTerm? Graph = null, bool MatchDefaultGraphOnly = false)
    {
        if (Subject is not null)
        {
            RdfTermValidation.Validate(Subject, subject: true);
        }

        if (Object is not null)
        {
            RdfTermValidation.Validate(Object, subject: false);
        }

        if (Graph is not null and not RdfIri and not RdfBlankNode)
        {
            throw new ArgumentException("An RDF graph name must be an IRI or blank node.", nameof(Graph));
        }

        if (MatchDefaultGraphOnly && Graph is not null)
        {
            throw new ArgumentException("A named graph and default-graph-only matching are mutually exclusive.", nameof(MatchDefaultGraphOnly));
        }

        this.Subject = Subject;
        this.Predicate = Predicate;
        this.Object = Object;
        this.Graph = Graph;
        this.MatchDefaultGraphOnly = MatchDefaultGraphOnly;
    }

    /// <summary>Gets the subject constraint, or null for any subject.</summary>
    public RdfTerm? Subject
    {
        get;
    }

    /// <summary>Gets the predicate constraint, or null for any predicate.</summary>
    public RdfIri? Predicate
    {
        get;
    }

    /// <summary>Gets the object constraint, or null for any object.</summary>
    public RdfTerm? Object
    {
        get;
    }

    /// <summary>Gets the named-graph selection; null leaves selection to the default-graph-only flag.</summary>
    public RdfTerm? Graph
    {
        get;
    }

    /// <summary>Gets whether selection is restricted to the default graph; false with a null graph includes all graphs.</summary>
    public bool MatchDefaultGraphOnly
    {
        get;
    }
}
