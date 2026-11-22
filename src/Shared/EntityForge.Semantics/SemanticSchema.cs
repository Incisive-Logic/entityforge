namespace EntityForge.Semantics;

/// <summary>Declares a minimum count and an optional finite maximum for a semantic member.</summary>
public sealed record SemanticCardinality
{
    /// <summary>Creates a cardinality with a nonnegative minimum and a positive or unbounded maximum.</summary>
    /// <param name="minimum">The minimum count, including zero.</param>
    /// <param name="maximum">A positive count at least equal to minimum, or null for no upper bound.</param>
    /// <exception cref="ArgumentOutOfRangeException">The bounds are invalid.</exception>
    public SemanticCardinality(int minimum, int? maximum)
    {
        if (minimum < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minimum));
        }

        if (maximum is <= 0 || maximum < minimum)
        {
            throw new ArgumentOutOfRangeException(nameof(maximum));
        }

        Minimum = minimum;
        Maximum = maximum;
    }

    /// <summary>Gets the minimum permitted count.</summary>
    public int Minimum
    {
        get;
    }

    /// <summary>Gets the maximum permitted count, or null when unbounded.</summary>
    public int? Maximum
    {
        get;
    }

    /// <summary>Gets zero-or-one cardinality.</summary>
    public static SemanticCardinality OptionalScalar { get; } = new(0, 1);

    /// <summary>Gets exactly-one cardinality.</summary>
    public static SemanticCardinality RequiredScalar { get; } = new(1, 1);

    /// <summary>Gets zero-or-more cardinality.</summary>
    public static SemanticCardinality OptionalMany { get; } = new(0, null);

    /// <summary>Gets one-or-more cardinality.</summary>
    public static SemanticCardinality RequiredMany { get; } = new(1, null);
}

/// <summary>Declares a property's value representation, cardinality, normalization identifier and concept applicability.</summary>
public sealed record SemanticFieldShape
{
    /// <summary>Captures a property shape and its distinct applicable concepts.</summary>
    /// <param name="valueKind">The value representation; Null is a value state and cannot be a field type.</param>
    /// <param name="cardinality">The cardinality, or null for optional scalar.</param>
    /// <param name="normalization">An optional absolute normalization identifier; construction does not execute it.</param>
    /// <param name="applicableConcepts">Optional concept-role terms, copied and deduplicated; empty means globally applicable.</param>
    /// <exception cref="ArgumentException">The value kind is Null, the normalization URI is relative or an applicability term has the wrong role.</exception>
    public SemanticFieldShape(
        SemanticValueKind valueKind,
        SemanticCardinality? cardinality = null,
        Uri? normalization = null,
        IEnumerable<SemanticTerm>? applicableConcepts = null)
    {
        if (valueKind is SemanticValueKind.Null)
        {
            throw new ArgumentException("Null is a value state, not a semantic field type.", nameof(valueKind));
        }

        if (normalization is not null && !normalization.IsAbsoluteUri)
        {
            throw new ArgumentException("A normalization identifier must be absolute.", nameof(normalization));
        }

        ValueKind = valueKind;
        Cardinality = cardinality ?? SemanticCardinality.OptionalScalar;
        Normalization = normalization;
        ApplicableConcepts = Array.AsReadOnly((applicableConcepts ?? []).Distinct().ToArray());
        if (ApplicableConcepts.Any(term => term.Role is not SemanticTermRole.Concept))
        {
            throw new ArgumentException("Field applicability can contain only concept terms.",
                nameof(applicableConcepts));
        }
    }

    /// <summary>Gets the declared value representation.</summary>
    public SemanticValueKind ValueKind
    {
        get;
    }

    /// <summary>Gets the declared cardinality.</summary>
    public SemanticCardinality Cardinality
    {
        get;
    }

    /// <summary>Gets the optional normalization identifier.</summary>
    public Uri? Normalization
    {
        get;
    }

    /// <summary>
    /// Gets the concepts for which this property is defined. An empty set preserves an explicitly
    /// open/global property definition; guided application metadata should prefer scoped fields.
    /// </summary>
    public IReadOnlyList<SemanticTerm> ApplicableConcepts
    {
        get;
    }
}

/// <summary>Declares the permitted source and target concepts, cardinality and optional inverse of a relationship.</summary>
public sealed record SemanticRelationshipShape
{
    /// <summary>Captures nonempty source and target concept sets and the relationship's shape metadata.</summary>
    /// <param name="sourceConcepts">The nonnull source concept sequence, copied and deduplicated.</param>
    /// <param name="targetConcepts">The nonnull target concept sequence, copied and deduplicated.</param>
    /// <param name="cardinality">The cardinality, or null for optional many.</param>
    /// <param name="inverse">An optional relationship-role term naming the inverse.</param>
    /// <exception cref="ArgumentException">A required sequence is null or empty, or a term has an incompatible role.</exception>
    public SemanticRelationshipShape(
        IEnumerable<SemanticTerm> sourceConcepts,
        IEnumerable<SemanticTerm> targetConcepts,
        SemanticCardinality? cardinality = null,
        SemanticTerm? inverse = null)
    {
        ArgumentNullException.ThrowIfNull(sourceConcepts);
        ArgumentNullException.ThrowIfNull(targetConcepts);
        SourceConcepts = Array.AsReadOnly(sourceConcepts.Distinct().ToArray());
        TargetConcepts = Array.AsReadOnly(targetConcepts.Distinct().ToArray());
        if (SourceConcepts.Count == 0 || SourceConcepts.Any(term => term.Role is not SemanticTermRole.Concept))
        {
            throw new ArgumentException("Relationship sources must contain at least one concept.", nameof(sourceConcepts));
        }

        if (TargetConcepts.Count == 0 || TargetConcepts.Any(term => term.Role is not SemanticTermRole.Concept))
        {
            throw new ArgumentException("Relationship targets must contain at least one concept.", nameof(targetConcepts));
        }

        if (inverse is not null && inverse.Role is not SemanticTermRole.Relationship)
        {
            throw new ArgumentException("An inverse must be a relationship term.", nameof(inverse));
        }

        Cardinality = cardinality ?? SemanticCardinality.OptionalMany;
        Inverse = inverse;
    }

    /// <summary>Gets the captured distinct permitted source concepts.</summary>
    public IReadOnlyList<SemanticTerm> SourceConcepts
    {
        get;
    }

    /// <summary>Gets the captured distinct permitted target concepts.</summary>
    public IReadOnlyList<SemanticTerm> TargetConcepts
    {
        get;
    }

    /// <summary>Gets the declared relationship cardinality.</summary>
    public SemanticCardinality Cardinality
    {
        get;
    }

    /// <summary>Gets the declared inverse relationship term, or null when unspecified.</summary>
    public SemanticTerm? Inverse
    {
        get;
    }
}

/// <summary>Defines a named identity namespace and the value representation it accepts.</summary>
public sealed record SemanticIdentityScheme
{
    /// <summary>Creates an identity scheme without registering it in a vocabulary.</summary>
    /// <param name="identifier">The nonnull absolute scheme identifier.</param>
    /// <param name="name">The nonblank human-readable scheme name.</param>
    /// <param name="valueKind">The identity value kind; Null, Json and Binary are rejected.</param>
    /// <param name="description">An optional description.</param>
    /// <exception cref="ArgumentException">The identifier, name or identity value kind is invalid.</exception>
    public SemanticIdentityScheme(Uri identifier, string name, SemanticValueKind valueKind, string? description = null)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!identifier.IsAbsoluteUri)
        {
            throw new ArgumentException("An identity-scheme identifier must be absolute.", nameof(identifier));
        }

        if (valueKind is SemanticValueKind.Null or SemanticValueKind.Json or SemanticValueKind.Binary)
        {
            throw new ArgumentException($"'{valueKind}' cannot be used as an entity identity.", nameof(valueKind));
        }

        Identifier = identifier;
        Name = name;
        ValueKind = valueKind;
        Description = description;
    }

    /// <summary>Gets the absolute identity-scheme identifier.</summary>
    public Uri Identifier
    {
        get;
    }

    /// <summary>Gets the human-readable scheme name.</summary>
    public string Name
    {
        get;
    }

    /// <summary>Gets the value representation required of identities in this scheme.</summary>
    public SemanticValueKind ValueKind
    {
        get;
    }

    /// <summary>Gets the optional scheme description.</summary>
    public string? Description
    {
        get;
    }
}
