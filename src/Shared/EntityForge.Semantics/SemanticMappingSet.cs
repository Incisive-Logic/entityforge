namespace EntityForge.Semantics;

/// <summary>Declares the directions in which a semantic mapping may be applied.</summary>
public enum SemanticMappingDirection
{
    /// <summary>Applies from the declared source term to the canonical term.</summary>
    SourceToCanonical,

    /// <summary>Permits both directions for a symmetric mapping kind.</summary>
    Bidirectional
}

/// <summary>A declared relation between same-role source and canonical semantic terms.</summary>
public sealed record SemanticMapping
{
    /// <summary>Creates a term mapping and checks role and direction compatibility.</summary>
    /// <param name="sourceTerm">The nonnull source term.</param>
    /// <param name="canonicalTerm">The nonnull canonical term with the same role.</param>
    /// <param name="kind">The semantic mapping relation.</param>
    /// <param name="direction">The permitted application direction; bidirectional requires a symmetric kind.</param>
    /// <exception cref="ArgumentException">A term is null, roles differ or a nonsymmetric mapping is bidirectional.</exception>
    public SemanticMapping(
        SemanticTerm sourceTerm,
        SemanticTerm canonicalTerm,
        SemanticMappingKind kind,
        SemanticMappingDirection direction = SemanticMappingDirection.SourceToCanonical)
    {
        SourceTerm = sourceTerm ?? throw new ArgumentNullException(nameof(sourceTerm));
        CanonicalTerm = canonicalTerm ?? throw new ArgumentNullException(nameof(canonicalTerm));

        if (sourceTerm.Role != canonicalTerm.Role)
        {
            throw new ArgumentException("Source and canonical terms must have the same semantic role.", nameof(canonicalTerm));
        }

        if (direction is SemanticMappingDirection.Bidirectional && !kind.IsSymmetric())
        {
            throw new ArgumentException("Only symmetric semantic mappings may be declared bidirectional.", nameof(direction));
        }

        Kind = kind;
        Direction = direction;
    }

    /// <summary>Gets the declared source term.</summary>
    public SemanticTerm SourceTerm
    {
        get;
    }

    /// <summary>Gets the declared canonical term.</summary>
    public SemanticTerm CanonicalTerm
    {
        get;
    }

    /// <summary>Gets the semantic relation between the terms.</summary>
    public SemanticMappingKind Kind
    {
        get;
    }

    /// <summary>Gets the permitted mapping direction.</summary>
    public SemanticMappingDirection Direction
    {
        get;
    }
}

/// <summary>A versioned collection of distinct semantic term-mapping declarations.</summary>
public sealed class SemanticMappingSet
{
    private readonly IReadOnlyList<SemanticMapping> _mappings;

    /// <summary>Captures mapping declarations without applying them or resolving conflicts.</summary>
    /// <param name="identifier">The nonnull absolute mapping-set identifier.</param>
    /// <param name="version">The nonblank version identifier.</param>
    /// <param name="mappings">The nonnull mapping sequence, copied and deduplicated.</param>
    /// <exception cref="ArgumentException">The identifier is null or relative, the version is blank or the sequence is null.</exception>
    public SemanticMappingSet(Uri identifier, string version, IEnumerable<SemanticMapping> mappings)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentNullException.ThrowIfNull(mappings);

        if (!identifier.IsAbsoluteUri)
        {
            throw new ArgumentException("A mapping-set identifier must be an absolute URI.", nameof(identifier));
        }

        Identifier = identifier;
        Version = version;
        _mappings = Array.AsReadOnly(mappings.Distinct().ToArray());
    }

    /// <summary>Gets the absolute mapping-set identifier.</summary>
    public Uri Identifier
    {
        get;
    }

    /// <summary>Gets the mapping-set version identifier.</summary>
    public string Version
    {
        get;
    }

    /// <summary>Gets the captured distinct mapping declarations.</summary>
    public IReadOnlyList<SemanticMapping> Mappings => _mappings;

    /// <summary>Enumerates mappings declared for a source term.</summary>
    /// <param name="sourceTerm">The source identifier and role to match.</param>
    /// <returns>Matching declared mappings; no transitive alignment is performed.</returns>
    public IEnumerable<SemanticMapping> FindBySource(SemanticTerm sourceTerm) =>
        _mappings.Where(mapping => mapping.SourceTerm == sourceTerm);
}
