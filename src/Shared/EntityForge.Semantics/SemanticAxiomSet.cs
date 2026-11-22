namespace EntityForge.Semantics;

/// <summary>Named-term axiom forms represented by the semantic model; execution support depends on the consuming engine.</summary>
public enum SemanticAxiomKind
{
    /// <summary>Declares the subject concept a specialization of the related concept.</summary>
    SubConceptOf,

    /// <summary>Declares the subject property or relationship a specialization of the related term.</summary>
    SubPropertyOf,

    /// <summary>Declares two same-role terms equivalent.</summary>
    Equivalent,

    /// <summary>Declares two relationships inverses of each other.</summary>
    Inverse,

    /// <summary>Declares two same-role terms disjoint.</summary>
    Disjoint,

    /// <summary>Declares a property or relationship functional, with at most one value for a subject.</summary>
    Functional,

    /// <summary>Declares a relationship inverse-functional, with at most one subject for a target.</summary>
    InverseFunctional,

    /// <summary>Declares that a relationship also holds in the reverse direction.</summary>
    Symmetric,

    /// <summary>Declares that a relationship cannot also hold in the reverse direction.</summary>
    Asymmetric,

    /// <summary>Declares that composing two instances of the relationship implies the same relationship.</summary>
    Transitive,

    /// <summary>Declares the relationship reflexive.</summary>
    Reflexive,

    /// <summary>Declares that the relationship cannot link an entity to itself.</summary>
    Irreflexive
}

/// <summary>Identifies the vocabulary and source predicate from which an axiom was obtained.</summary>
public sealed record SemanticAxiomProvenance
{
    /// <summary>Creates an axiom provenance reference without retrieving its vocabulary.</summary>
    /// <param name="sourceVocabulary">The nonnull vocabulary-kind model reference.</param>
    /// <param name="sourcePredicate">The nonnull absolute predicate URI.</param>
    /// <exception cref="ArgumentException">An input is null, the model is not a vocabulary or the predicate URI is relative.</exception>
    public SemanticAxiomProvenance(SemanticModelReference sourceVocabulary, Uri sourcePredicate)
    {
        SourceVocabulary = sourceVocabulary ?? throw new ArgumentNullException(nameof(sourceVocabulary));
        ArgumentNullException.ThrowIfNull(sourcePredicate);
        if (sourceVocabulary.Kind != SemanticModelKind.Vocabulary)
        {
            throw new ArgumentException("A semantic axiom must originate in a vocabulary.", nameof(sourceVocabulary));
        }

        if (!sourcePredicate.IsAbsoluteUri)
        {
            throw new ArgumentException("An axiom source predicate must be absolute.", nameof(sourcePredicate));
        }

        SourcePredicate = sourcePredicate;
    }

    /// <summary>Gets the originating vocabulary reference.</summary>
    public SemanticModelReference SourceVocabulary
    {
        get;
    }

    /// <summary>Gets the absolute source predicate URI.</summary>
    public Uri SourcePredicate
    {
        get;
    }
}

/// <summary>A named-term semantic statement. Anonymous OWL expressions are intentionally outside this 0.1 model.</summary>
public sealed record SemanticAxiom
{
    private static readonly HashSet<SemanticAxiomKind> UnaryKinds = new()
    {
        SemanticAxiomKind.Functional, SemanticAxiomKind.InverseFunctional,
        SemanticAxiomKind.Symmetric, SemanticAxiomKind.Asymmetric, SemanticAxiomKind.Transitive,
        SemanticAxiomKind.Reflexive, SemanticAxiomKind.Irreflexive
    };

    /// <summary>Creates a named-term axiom, checking unary/binary shape and applicable term roles.</summary>
    /// <param name="subject">The nonnull subject term.</param>
    /// <param name="kind">The represented axiom form.</param>
    /// <param name="provenance">The nonnull source vocabulary and predicate reference.</param>
    /// <param name="relatedTerm">Required for binary axioms and absent for unary property characteristics.</param>
    /// <exception cref="ArgumentException">A required argument is null or the term roles and arity do not fit the axiom form.</exception>
    /// <remarks>Construction records a declaration; it does not perform inference or check a complete theory for consistency.</remarks>
    public SemanticAxiom(SemanticTerm subject, SemanticAxiomKind kind,
        SemanticAxiomProvenance provenance, SemanticTerm? relatedTerm = null)
    {
        Subject = subject ?? throw new ArgumentNullException(nameof(subject));
        Provenance = provenance ?? throw new ArgumentNullException(nameof(provenance));
        bool unary = UnaryKinds.Contains(kind);
        if (unary != (relatedTerm is null))
        {
            throw new ArgumentException(unary ? "A property characteristic is unary." : "A binary axiom requires a related term.", nameof(relatedTerm));
        }

        if (unary && subject.Role == SemanticTermRole.Concept)
        {
            throw new ArgumentException("Property characteristics require a property or relationship term.", nameof(subject));
        }

        if (unary && kind != SemanticAxiomKind.Functional && subject.Role != SemanticTermRole.Relationship)
        {
            throw new ArgumentException("This property characteristic requires a relationship term.", nameof(subject));
        }

        if (kind == SemanticAxiomKind.SubConceptOf &&
            (subject.Role != SemanticTermRole.Concept || relatedTerm!.Role != SemanticTermRole.Concept))
        {
            throw new ArgumentException("Concept subsumption requires concept terms.", nameof(relatedTerm));
        }

        if (kind == SemanticAxiomKind.SubPropertyOf &&
            (subject.Role == SemanticTermRole.Concept || relatedTerm!.Role == SemanticTermRole.Concept))
        {
            throw new ArgumentException("Property subsumption requires property or relationship terms.", nameof(relatedTerm));
        }

        if (kind == SemanticAxiomKind.Inverse &&
            (subject.Role != SemanticTermRole.Relationship || relatedTerm!.Role != SemanticTermRole.Relationship))
        {
            throw new ArgumentException("An inverse axiom requires relationship terms.", nameof(relatedTerm));
        }

        if ((kind is SemanticAxiomKind.Equivalent or SemanticAxiomKind.Disjoint) && subject.Role != relatedTerm!.Role)
        {
            throw new ArgumentException("Equivalent or disjoint terms must have the same role.", nameof(relatedTerm));
        }

        Kind = kind;
        RelatedTerm = relatedTerm;
    }

    /// <summary>Gets the subject term.</summary>
    public SemanticTerm Subject
    {
        get;
    }

    /// <summary>Gets the axiom form.</summary>
    public SemanticAxiomKind Kind
    {
        get;
    }

    /// <summary>Gets the second term of a binary axiom, or null for a unary characteristic.</summary>
    public SemanticTerm? RelatedTerm
    {
        get;
    }

    /// <summary>Gets the source vocabulary and predicate provenance.</summary>
    public SemanticAxiomProvenance Provenance
    {
        get;
    }
}

/// <summary>A versioned collection of distinct named-term axiom declarations.</summary>
public sealed class SemanticAxiomSet
{
    /// <summary>Captures and deduplicates axiom declarations without deriving their consequences.</summary>
    /// <param name="identifier">The nonnull absolute axiom-set identifier.</param>
    /// <param name="version">The nonblank version identifier.</param>
    /// <param name="axioms">The nonnull declaration sequence to copy and deduplicate.</param>
    /// <exception cref="ArgumentException">The identifier is null or relative, the version is blank or the sequence is null.</exception>
    public SemanticAxiomSet(Uri identifier, string version, IEnumerable<SemanticAxiom> axioms)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentNullException.ThrowIfNull(axioms);
        if (!identifier.IsAbsoluteUri)
        {
            throw new ArgumentException("An axiom-set identifier must be absolute.", nameof(identifier));
        }

        Identifier = identifier;
        Version = version;
        Axioms = Array.AsReadOnly(axioms.Distinct().ToArray());
    }

    /// <summary>Gets the absolute axiom-set identifier.</summary>
    public Uri Identifier
    {
        get;
    }

    /// <summary>Gets the axiom-set version identifier.</summary>
    public string Version
    {
        get;
    }

    /// <summary>Gets the captured distinct declarations.</summary>
    public IReadOnlyList<SemanticAxiom> Axioms
    {
        get;
    }

    /// <summary>Enumerates axioms whose subject is the supplied term.</summary>
    /// <param name="term">Subject identifier and role to match.</param>
    /// <returns>The matching axioms, without inference or hierarchy expansion.</returns>
    public IEnumerable<SemanticAxiom> FindBySubject(SemanticTerm term) => Axioms.Where(axiom => axiom.Subject == term);
}
