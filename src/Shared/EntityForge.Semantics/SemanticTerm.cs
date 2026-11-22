namespace EntityForge.Semantics;

/// <summary>Distinguishes entity concepts, value-bearing properties and entity relationships.</summary>
public enum SemanticTermRole
{
    /// <summary>A category of entities.</summary>
    Concept,

    /// <summary>A property associating an entity with a value.</summary>
    Property,

    /// <summary>A relationship connecting entities.</summary>
    Relationship
}

/// <summary>A semantic identifier qualified by its role; the same URI in different roles denotes different terms.</summary>
public sealed record SemanticTerm
{
    /// <summary>Creates a term using an absolute URI and declared role.</summary>
    /// <param name="identifier">The nonnull absolute identifier; no resource is fetched.</param>
    /// <param name="role">The semantic role.</param>
    /// <exception cref="ArgumentException">The identifier is null or relative.</exception>
    /// <remarks>Equality compares the URI's AbsoluteUri representation ordinally together with the role.</remarks>
    public SemanticTerm(Uri identifier, SemanticTermRole role)
    {
        ArgumentNullException.ThrowIfNull(identifier);

        if (!identifier.IsAbsoluteUri)
        {
            throw new ArgumentException("A semantic term identifier must be an absolute URI.", nameof(identifier));
        }

        Identifier = identifier;
        Role = role;
    }

    /// <summary>Gets the absolute term identifier.</summary>
    public Uri Identifier
    {
        get;
    }

    /// <summary>Gets the term role used in identity and shape validation.</summary>
    public SemanticTermRole Role
    {
        get;
    }

    /// <summary>Compares Semantic terms by their identifier and role.</summary>
    /// <param name="other">The term to compare, or null.</param>
    /// <returns>True when both identifier and role match.</returns>
    public bool Equals(SemanticTerm? other) =>
        other is not null &&
        Role == other.Role &&
        string.Equals(Identifier.AbsoluteUri, other.Identifier.AbsoluteUri, StringComparison.Ordinal);

    /// <summary>Computes an equality hash from the term identifier and role.</summary>
    /// <returns>An in-process hash consistent with term equality.</returns>
    public override int GetHashCode() =>
        HashCode.Combine(StringComparer.Ordinal.GetHashCode(Identifier.AbsoluteUri), Role);
}
