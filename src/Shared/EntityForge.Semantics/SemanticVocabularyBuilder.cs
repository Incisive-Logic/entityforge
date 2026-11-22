namespace EntityForge.Semantics;

/// <summary>
/// Builds immutable logical vocabulary definitions. It deliberately contains no physical storage
/// choices; those belong to the database property-storage catalogue.
/// </summary>
public sealed class SemanticVocabularyBuilder
{
    private readonly IReadOnlyDictionary<SemanticTerm, SemanticTermDefinition> _terms;
    private readonly IReadOnlyDictionary<Uri, SemanticIdentityScheme> _identitySchemes;

    private SemanticVocabularyBuilder(
        Uri identifier,
        string version,
        IReadOnlyDictionary<SemanticTerm, SemanticTermDefinition> terms,
        IReadOnlyDictionary<Uri, SemanticIdentityScheme> identitySchemes)
    {
        Identifier = identifier;
        Version = version;
        _terms = terms;
        _identitySchemes = identitySchemes;
    }

    /// <summary>Gets the vocabulary identifier.</summary>
    public Uri Identifier
    {
        get;
    }

    /// <summary>Gets the version being authored.</summary>
    public string Version
    {
        get;
    }

    /// <summary>Starts a new empty vocabulary definition.</summary>
    public static SemanticVocabularyBuilder Define(Uri identifier, string version)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        if (!identifier.IsAbsoluteUri)
        {
            throw new ArgumentException("A vocabulary identifier must be absolute.", nameof(identifier));
        }

        return new(identifier, version,
            new Dictionary<SemanticTerm, SemanticTermDefinition>(),
            new Dictionary<Uri, SemanticIdentityScheme>());
    }

    /// <summary>Starts a new version from an existing immutable vocabulary.</summary>
    public static SemanticVocabularyBuilder Evolve(SemanticVocabulary vocabulary, string version)
    {
        ArgumentNullException.ThrowIfNull(vocabulary);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        return new(vocabulary.Identifier, version,
            vocabulary.Terms.ToDictionary(item => item.Term),
            vocabulary.IdentitySchemes.ToDictionary(item => item.Identifier));
    }

    /// <summary>Adds or replaces a concept definition.</summary>
    public SemanticVocabularyBuilder Concept(
        SemanticTerm concept,
        string name,
        string? description = null,
        IEnumerable<string>? aliases = null) =>
        With(new SemanticTermDefinition(RequireRole(concept, SemanticTermRole.Concept), name,
            description, aliases: aliases));

    /// <summary>Adds or replaces a property and declares the concepts to which it applies.</summary>
    public SemanticVocabularyBuilder Property(
        SemanticTerm property,
        string name,
        SemanticValueKind valueKind,
        IEnumerable<SemanticTerm> applicableConcepts,
        SemanticCardinality? cardinality = null,
        string? description = null,
        Uri? normalization = null,
        IEnumerable<string>? aliases = null) =>
        With(new SemanticTermDefinition(RequireRole(property, SemanticTermRole.Property), name,
            description, aliases: aliases,
            field: new SemanticFieldShape(valueKind, cardinality, normalization, applicableConcepts)));

    /// <summary>Adds or replaces a relationship between declared source and target concepts.</summary>
    public SemanticVocabularyBuilder Relationship(
        SemanticTerm relationship,
        string name,
        IEnumerable<SemanticTerm> sourceConcepts,
        IEnumerable<SemanticTerm> targetConcepts,
        SemanticCardinality? cardinality = null,
        SemanticTerm? inverse = null,
        string? description = null,
        IEnumerable<string>? aliases = null) =>
        With(new SemanticTermDefinition(RequireRole(relationship, SemanticTermRole.Relationship), name,
            description, aliases: aliases,
            relationship: new SemanticRelationshipShape(sourceConcepts, targetConcepts, cardinality, inverse)));

    /// <summary>Adds or replaces an identity scheme.</summary>
    public SemanticVocabularyBuilder IdentityScheme(SemanticIdentityScheme scheme)
    {
        ArgumentNullException.ThrowIfNull(scheme);
        var schemes = new Dictionary<Uri, SemanticIdentityScheme>(_identitySchemes)
        {
            [scheme.Identifier] = scheme
        };
        return new(Identifier, Version, _terms, schemes);
    }

    /// <summary>Removes a term from the new version.</summary>
    public SemanticVocabularyBuilder Remove(SemanticTerm term)
    {
        ArgumentNullException.ThrowIfNull(term);
        var terms = new Dictionary<SemanticTerm, SemanticTermDefinition>(_terms);
        terms.Remove(term);
        return new(Identifier, Version, terms, _identitySchemes);
    }

    /// <summary>Marks a term deprecated and optionally points to its replacement.</summary>
    public SemanticVocabularyBuilder Deprecate(SemanticTerm term, SemanticTerm? replacedBy = null)
    {
        ArgumentNullException.ThrowIfNull(term);
        if (!_terms.TryGetValue(term, out SemanticTermDefinition? existing))
        {
            throw new InvalidOperationException($"Term '{term.Identifier}' is not defined.");
        }

        return With(new SemanticTermDefinition(existing.Term, existing.Name, existing.Description,
            true, existing.Aliases, replacedBy, existing.Field, existing.Relationship));
    }

    /// <summary>Creates the validated immutable vocabulary.</summary>
    public SemanticVocabulary Build() => new(Identifier, Version, _terms.Values, _identitySchemes.Values);

    private SemanticVocabularyBuilder With(SemanticTermDefinition definition)
    {
        var terms = new Dictionary<SemanticTerm, SemanticTermDefinition>(_terms)
        {
            [definition.Term] = definition
        };
        return new(Identifier, Version, terms, _identitySchemes);
    }

    private static SemanticTerm RequireRole(SemanticTerm term, SemanticTermRole role)
    {
        ArgumentNullException.ThrowIfNull(term);
        return term.Role == role
            ? term
            : throw new ArgumentException($"Expected a semantic {role} term.", nameof(term));
    }
}
