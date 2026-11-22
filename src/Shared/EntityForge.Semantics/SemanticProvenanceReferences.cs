namespace EntityForge.Semantics;

/// <summary>Identifies a source entity participating in the provenance of a semantic assertion.</summary>
public sealed record SemanticProvenanceEntity
{
    /// <summary>Creates an absolute provenance entity reference without fetching the referenced resource.</summary>
    /// <param name="identifier">The nonnull absolute entity identifier.</param>
    /// <param name="description">Optional human-readable description.</param>
    /// <exception cref="ArgumentException">The identifier is null or relative.</exception>
    public SemanticProvenanceEntity(Uri identifier, string? description = null)
    {
        Identifier = RequireAbsolute(identifier, nameof(identifier));
        Description = description;
    }

    /// <summary>Gets the absolute provenance entity identifier.</summary>
    public Uri Identifier
    {
        get;
    }

    /// <summary>Gets the optional human-readable description.</summary>
    public string? Description
    {
        get;
    }

    private static Uri RequireAbsolute(Uri identifier, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(identifier, parameterName);
        return identifier.IsAbsoluteUri
            ? identifier
            : throw new ArgumentException("A provenance entity identifier must be absolute.", parameterName);
    }
}

/// <summary>Identifies a provenance activity and its optional start and end times.</summary>
public sealed record SemanticProvenanceActivity
{
    /// <summary>Creates an activity reference, validating the interval when both times are supplied.</summary>
    /// <param name="identifier">The nonnull absolute activity identifier.</param>
    /// <param name="startedAt">The activity start, or null when unknown.</param>
    /// <param name="endedAt">The activity end, or null when unknown; must follow a supplied start.</param>
    /// <exception cref="ArgumentException">The identifier is null or relative, or the interval is empty or reversed.</exception>
    public SemanticProvenanceActivity(
        Uri identifier,
        DateTimeOffset? startedAt = null,
        DateTimeOffset? endedAt = null)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        if (!identifier.IsAbsoluteUri)
        {
            throw new ArgumentException("A provenance activity identifier must be absolute.", nameof(identifier));
        }

        if (startedAt >= endedAt)
        {
            throw new ArgumentException("A provenance activity start must precede its end.", nameof(endedAt));
        }

        Identifier = identifier;
        StartedAt = startedAt;
        EndedAt = endedAt;
    }

    /// <summary>Gets the absolute activity identifier.</summary>
    public Uri Identifier
    {
        get;
    }

    /// <summary>Gets the activity start, or null when unknown.</summary>
    public DateTimeOffset? StartedAt
    {
        get;
    }

    /// <summary>Gets the activity end, or null when unknown.</summary>
    public DateTimeOffset? EndedAt
    {
        get;
    }
}

/// <summary>Identifies an agent responsible for an activity or assertion in its provenance.</summary>
public sealed record SemanticProvenanceAgent
{
    /// <summary>Creates an agent reference without resolving or authenticating it.</summary>
    /// <param name="identifier">The nonnull absolute agent identifier.</param>
    /// <param name="name">An optional human-readable agent name.</param>
    /// <exception cref="ArgumentException">The identifier is null or relative.</exception>
    public SemanticProvenanceAgent(Uri identifier, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        Identifier = identifier.IsAbsoluteUri
            ? identifier
            : throw new ArgumentException("A provenance agent identifier must be absolute.", nameof(identifier));
        Name = name;
    }

    /// <summary>Gets the absolute agent identifier.</summary>
    public Uri Identifier
    {
        get;
    }

    /// <summary>Gets the optional human-readable agent name.</summary>
    public string? Name
    {
        get;
    }
}

/// <summary>Identifies a versioned semantic artifact, optionally pinned to exact SHA-256 content.</summary>
public sealed record SemanticArtifactReference
{
    /// <summary>Creates an artifact reference and normalizes a supplied content fingerprint to lowercase.</summary>
    /// <param name="identifier">The nonnull absolute artifact identifier.</param>
    /// <param name="version">The nonblank version identifier.</param>
    /// <param name="contentHash">An optional 64-digit hexadecimal SHA-256 fingerprint.</param>
    /// <exception cref="ArgumentException">The URI, version or supplied fingerprint is invalid.</exception>
    /// <remarks>Construction validates fingerprint syntax; it does not fetch or verify artifact content.</remarks>
    public SemanticArtifactReference(Uri identifier, string version, string? contentHash = null)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        if (contentHash is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(contentHash);
            if (contentHash.Length != 64 || contentHash.Any(character => !Uri.IsHexDigit(character)))
            {
                throw new ArgumentException("An artifact content hash must be a SHA-256 hexadecimal value.",
                    nameof(contentHash));
            }

            contentHash = contentHash.ToLowerInvariant();
        }
        Identifier = identifier.IsAbsoluteUri
            ? identifier
            : throw new ArgumentException("A semantic artifact identifier must be absolute.", nameof(identifier));
        Version = version;
        ContentHash = contentHash;
    }

    /// <summary>Gets the absolute artifact identifier.</summary>
    public Uri Identifier
    {
        get;
    }

    /// <summary>Gets the exact artifact version identifier.</summary>
    public string Version
    {
        get;
    }

    /// <summary>Gets the exact content fingerprint when replay must retain one interpretation.</summary>
    public string? ContentHash
    {
        get;
    }
}
