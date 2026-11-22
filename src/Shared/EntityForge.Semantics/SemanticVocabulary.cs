namespace EntityForge.Semantics;

/// <summary>Describes a named semantic term, its lifecycle metadata and any role-specific shape.</summary>
public sealed record SemanticTermDefinition
{
    /// <summary>Creates a term definition and captures normalized aliases.</summary>
    /// <param name="term">The nonnull semantic term.</param>
    /// <param name="name">The nonblank display name.</param>
    /// <param name="description">An optional description.</param>
    /// <param name="isDeprecated">Whether applications should migrate away from this term.</param>
    /// <param name="aliases">Optional aliases; trimmed, blank entries discarded and duplicates removed ignoring ordinal case.</param>
    /// <param name="replacedBy">An optional same-role replacement, permitted only for deprecated terms.</param>
    /// <param name="field">An optional shape, permitted only for property terms.</param>
    /// <param name="relationship">An optional shape, permitted only for relationship terms.</param>
    /// <exception cref="ArgumentException">Required inputs are invalid or replacement/shape metadata conflicts with the term role or deprecation state.</exception>
    public SemanticTermDefinition(
        SemanticTerm term,
        string name,
        string? description = null,
        bool isDeprecated = false,
        IEnumerable<string>? aliases = null,
        SemanticTerm? replacedBy = null,
        SemanticFieldShape? field = null,
        SemanticRelationshipShape? relationship = null)
    {
        Term = term ?? throw new ArgumentNullException(nameof(term));
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        Description = description;
        IsDeprecated = isDeprecated;
        Aliases = Array.AsReadOnly((aliases ?? Array.Empty<string>())
            .Select(alias => alias?.Trim())
            .Where(alias => !string.IsNullOrWhiteSpace(alias))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray());

        if (replacedBy is not null && replacedBy.Role != term.Role)
        {
            throw new ArgumentException("A replacement term must have the same semantic role.", nameof(replacedBy));
        }

        if (replacedBy is not null && !isDeprecated)
        {
            throw new ArgumentException("A replacement can only be declared for a deprecated term.", nameof(replacedBy));
        }

        if (field is not null && term.Role is not SemanticTermRole.Property)
        {
            throw new ArgumentException("Only a semantic property can declare a field shape.", nameof(field));
        }

        if (relationship is not null && term.Role is not SemanticTermRole.Relationship)
        {
            throw new ArgumentException("Only a relationship term can declare a relationship shape.", nameof(relationship));
        }

        ReplacedBy = replacedBy;
        Field = field;
        Relationship = relationship;
    }

    /// <summary>Gets the term's identifier and role.</summary>
    public SemanticTerm Term
    {
        get;
    }

    /// <summary>Gets the display name.</summary>
    public string Name
    {
        get;
    }

    /// <summary>Gets the optional description.</summary>
    public string? Description
    {
        get;
    }

    /// <summary>Gets whether the term is deprecated.</summary>
    public bool IsDeprecated
    {
        get;
    }

    /// <summary>Gets the captured trimmed, nonblank, case-insensitively distinct aliases.</summary>
    public IReadOnlyList<string> Aliases
    {
        get;
    }

    /// <summary>Gets the replacement for a deprecated term, or null when unspecified.</summary>
    public SemanticTerm? ReplacedBy
    {
        get;
    }

    /// <summary>Gets the optional property shape.</summary>
    public SemanticFieldShape? Field
    {
        get;
    }

    /// <summary>Gets the optional relationship shape.</summary>
    public SemanticRelationshipShape? Relationship
    {
        get;
    }
}

/// <summary>A versioned vocabulary of term definitions, identity schemes and named-term axioms.</summary>
public sealed class SemanticVocabulary
{
    private readonly IReadOnlyDictionary<SemanticTerm, SemanticTermDefinition> _terms;
    private readonly IReadOnlyList<SemanticTermDefinition> _definitions;

    /// <summary>Captures a vocabulary, rejecting duplicate term/scheme identities and unresolved shape or replacement references.</summary>
    /// <param name="identifier">The nonnull absolute vocabulary identifier.</param>
    /// <param name="version">The nonblank version identifier.</param>
    /// <param name="terms">The nonnull term-definition sequence, copied in input order.</param>
    /// <param name="identitySchemes">Optional identity schemes with unique identifiers, copied in input order.</param>
    /// <param name="axioms">Optional named-term declarations, copied and deduplicated.</param>
    /// <exception cref="ArgumentException">An input identifier, name, duplicate definition or referenced term is invalid.</exception>
    /// <remarks>Construction does not infer axioms or check the complete axiom theory for consistency.</remarks>
    public SemanticVocabulary(
        Uri identifier,
        string version,
        IEnumerable<SemanticTermDefinition> terms,
        IEnumerable<SemanticIdentityScheme>? identitySchemes = null,
        IEnumerable<SemanticAxiom>? axioms = null)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentNullException.ThrowIfNull(terms);

        if (!identifier.IsAbsoluteUri)
        {
            throw new ArgumentException("A vocabulary identifier must be an absolute URI.", nameof(identifier));
        }

        Identifier = identifier;
        Version = version;
        _definitions = Array.AsReadOnly(terms.ToArray());
        _terms = _definitions.ToDictionary(definition => definition.Term);
        IdentitySchemes = Array.AsReadOnly((identitySchemes ?? Array.Empty<SemanticIdentityScheme>()).ToArray());
        Axioms = Array.AsReadOnly((axioms ?? Array.Empty<SemanticAxiom>()).Distinct().ToArray());
        if (IdentitySchemes.Select(scheme => scheme.Identifier).Distinct().Count() != IdentitySchemes.Count)
        {
            throw new ArgumentException("A vocabulary cannot contain duplicate identity schemes.", nameof(identitySchemes));
        }

        ValidateReferences();
    }

    /// <summary>Gets the absolute vocabulary identifier.</summary>
    public Uri Identifier
    {
        get;
    }

    /// <summary>Gets the vocabulary version identifier.</summary>
    public string Version
    {
        get;
    }

    /// <summary>Gets the captured term definitions in input order.</summary>
    public IReadOnlyList<SemanticTermDefinition> Terms => _definitions;

    /// <summary>Gets the captured identity schemes in input order.</summary>
    public IReadOnlyList<SemanticIdentityScheme> IdentitySchemes
    {
        get;
    }

    /// <summary>Gets the named-term standards axioms published with this vocabulary.</summary>
    public IReadOnlyList<SemanticAxiom> Axioms
    {
        get;
    }

    /// <summary>Looks up a term definition by semantic identifier and role.</summary>
    /// <param name="term">The term to resolve.</param>
    /// <param name="definition">Receives its definition when present.</param>
    /// <returns>True when this vocabulary contains the requested term.</returns>
    public bool TryGetTerm(SemanticTerm term, out SemanticTermDefinition? definition) =>
        _terms.TryGetValue(term, out definition);

    /// <summary>Looks up an identity scheme by identifier.</summary>
    /// <param name="identifier">The scheme identifier.</param>
    /// <param name="scheme">Receives the scheme when present.</param>
    /// <returns>True when this vocabulary contains the identity scheme.</returns>
    public bool TryGetIdentityScheme(Uri identifier, out SemanticIdentityScheme? scheme)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        scheme = IdentitySchemes.SingleOrDefault(candidate => candidate.Identifier == identifier);
        return scheme is not null;
    }

    private void ValidateReferences()
    {
        foreach (var definition in _definitions)
        {
            if (definition.ReplacedBy is not null && !_terms.ContainsKey(definition.ReplacedBy))
            {
                throw new ArgumentException(
                    $"Replacement term '{definition.ReplacedBy.Identifier}' is not defined by this vocabulary.",
                    nameof(_definitions));
            }

            var relationship = definition.Relationship;
            if (relationship is null)
            {
                continue;
            }

            var referencedTerms = relationship.SourceConcepts
                .Concat(relationship.TargetConcepts)
                .Concat(relationship.Inverse is null ? Array.Empty<SemanticTerm>() : new[] { relationship.Inverse });
            var missing = referencedTerms.FirstOrDefault(term => !_terms.ContainsKey(term));
            if (missing is not null)
            {
                throw new ArgumentException(
                    $"Relationship term '{definition.Term.Identifier}' references undefined term '{missing.Identifier}'.",
                    nameof(_definitions));
            }
        }
        foreach (SemanticTermDefinition definition in _definitions.Where(item => item.Field is not null))
        {
            SemanticTerm? missing = definition.Field!.ApplicableConcepts
                .FirstOrDefault(term => !_terms.ContainsKey(term));
            if (missing is not null)
            {
                throw new ArgumentException(
                    $"Property term '{definition.Term.Identifier}' references undefined concept '{missing.Identifier}'.",
                    nameof(_definitions));
            }
        }
    }
}
