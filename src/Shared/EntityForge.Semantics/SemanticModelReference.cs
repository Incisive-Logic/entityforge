namespace EntityForge.Semantics;

/// <summary>Identifies the kind of versioned semantic model being referenced.</summary>
public enum SemanticModelKind
{
    /// <summary>A vocabulary defining semantic terms and shapes.</summary>
    Vocabulary,

    /// <summary>A set of mappings between semantic terms.</summary>
    MappingSet
}

/// <summary>A reference to a semantic model identified by absolute URI, version and model kind.</summary>
public sealed record SemanticModelReference
{
    /// <summary>Captures a model reference without loading or validating the model contents.</summary>
    /// <param name="identifier">The nonnull absolute model identifier.</param>
    /// <param name="version">The nonblank version identifier.</param>
    /// <param name="kind">The referenced model kind.</param>
    /// <exception cref="ArgumentException">The identifier is null or relative, or the version is null or blank.</exception>
    public SemanticModelReference(Uri identifier, string version, SemanticModelKind kind)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        if (!identifier.IsAbsoluteUri)
        {
            throw new ArgumentException("A semantic-model identifier must be absolute.", nameof(identifier));
        }

        Identifier = identifier;
        Version = version;
        Kind = kind;
    }

    /// <summary>Gets the absolute model identifier.</summary>
    public Uri Identifier
    {
        get;
    }

    /// <summary>Gets the exact model version identifier.</summary>
    public string Version
    {
        get;
    }

    /// <summary>Gets the referenced model kind.</summary>
    public SemanticModelKind Kind
    {
        get;
    }
}
