namespace EntityForge.Semantics;

/// <summary>Classifies the semantic relationship from a source term to a canonical term.</summary>
public enum SemanticMappingKind
{
    /// <summary>Source and canonical terms have the same meaning for this mapping.</summary>
    Exact,

    /// <summary>The terms are sufficiently similar for an application-selected alignment, without exact equivalence.</summary>
    Close,

    /// <summary>The mapping relates the source to a broader canonical meaning.</summary>
    Broader,

    /// <summary>The mapping relates the source to a narrower canonical meaning.</summary>
    Narrower,

    /// <summary>The meanings are associated without equivalence or hierarchy.</summary>
    Related,

    /// <summary>Alignment requires an explicit value transformation rather than term substitution.</summary>
    Transforming
}

/// <summary>Describes the symmetry, transitivity and inverse relation supported by each mapping kind.</summary>
public static class SemanticMappingKindExtensions
{
    /// <summary>Tests whether reversing the mapping preserves its kind.</summary>
    /// <param name="kind">Mapping relation to inspect.</param>
    /// <returns>True for Exact, Close and Related mappings.</returns>
    public static bool IsSymmetric(this SemanticMappingKind kind) => kind is
        SemanticMappingKind.Exact or
        SemanticMappingKind.Close or
        SemanticMappingKind.Related;

    /// <summary>Tests whether this mapping kind supports transitive chaining.</summary>
    /// <param name="kind">Mapping relation to inspect.</param>
    /// <returns>True only for Exact mappings.</returns>
    public static bool IsTransitive(this SemanticMappingKind kind) =>
        kind is SemanticMappingKind.Exact;

    /// <summary>Obtains the relation used when source and canonical terms are reversed.</summary>
    /// <param name="kind">Forward mapping relation.</param>
    /// <returns>The inverse relation, or null when no inverse is defined for this kind.</returns>
    public static SemanticMappingKind? Inverse(this SemanticMappingKind kind) => kind switch
    {
        SemanticMappingKind.Exact => SemanticMappingKind.Exact,
        SemanticMappingKind.Close => SemanticMappingKind.Close,
        SemanticMappingKind.Broader => SemanticMappingKind.Narrower,
        SemanticMappingKind.Narrower => SemanticMappingKind.Broader,
        SemanticMappingKind.Related => SemanticMappingKind.Related,
        _ => null
    };
}

/// <summary>Identifies the versioned mapping set and relation kind supporting an alignment.</summary>
public sealed record MappingReference
{
    /// <summary>Creates a mapping reference without loading or applying its mapping set.</summary>
    /// <param name="mappingSetIdentifier">The nonnull absolute mapping-set identifier.</param>
    /// <param name="version">The nonblank mapping-set version.</param>
    /// <param name="kind">The mapping relation used by the alignment.</param>
    /// <exception cref="ArgumentException">The identifier is null or relative, or the version is null or blank.</exception>
    public MappingReference(Uri mappingSetIdentifier, string version, SemanticMappingKind kind)
    {
        ArgumentNullException.ThrowIfNull(mappingSetIdentifier);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        if (!mappingSetIdentifier.IsAbsoluteUri)
        {
            throw new ArgumentException("A mapping-set identifier must be an absolute URI.", nameof(mappingSetIdentifier));
        }

        MappingSetIdentifier = mappingSetIdentifier;
        Version = version;
        Kind = kind;
    }

    /// <summary>Gets the absolute mapping-set identifier.</summary>
    public Uri MappingSetIdentifier
    {
        get;
    }

    /// <summary>Gets the mapping-set version.</summary>
    public string Version
    {
        get;
    }

    /// <summary>Gets the alignment relation kind.</summary>
    public SemanticMappingKind Kind
    {
        get;
    }
}

/// <summary>Associates an aligned term with the mapping declaration supporting it.</summary>
public sealed record SemanticAlignment
{
    /// <summary>Creates alignment metadata without transforming a value.</summary>
    /// <param name="alignedTerm">The nonnull aligned term.</param>
    /// <param name="mapping">The nonnull mapping-set reference and relation kind.</param>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public SemanticAlignment(SemanticTerm alignedTerm, MappingReference mapping)
    {
        AlignedTerm = alignedTerm ?? throw new ArgumentNullException(nameof(alignedTerm));
        Mapping = mapping ?? throw new ArgumentNullException(nameof(mapping));
    }

    /// <summary>Gets the aligned term.</summary>
    public SemanticTerm AlignedTerm
    {
        get;
    }

    /// <summary>Gets the mapping declaration reference supporting this alignment.</summary>
    public MappingReference Mapping
    {
        get;
    }
}
