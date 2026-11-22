using EntityForge.Query.Values;

namespace EntityForge.Query.Results;

/// <summary>Identifies the ordered roots in an entity page and the provider's continuation metadata.</summary>
public sealed record EntityPageResult
{
    /// <summary>Captures root identities in page order, without deduplicating them.</summary>
    /// <param name="rootIdentities">The nonnull identity sequence, containing no null entries.</param>
    /// <param name="hasNextPage">Whether the provider reports more results.</param>
    /// <param name="endCursor">The opaque continuation token, or null when unavailable.</param>
    /// <exception cref="ArgumentException">The identity sequence or an entry is null, or a supplied cursor is blank.</exception>
    /// <remarks>The constructor does not require a cursor when hasNextPage is true; the provider is responsible for a usable paging contract.</remarks>
    public EntityPageResult(
        IEnumerable<GraphElementIdentity> rootIdentities,
        bool hasNextPage,
        string? endCursor)
    {
        ArgumentNullException.ThrowIfNull(rootIdentities);
        RootIdentities = Array.AsReadOnly(rootIdentities.ToArray());
        if (RootIdentities.Any(identity => identity is null))
        {
            throw new ArgumentException("Page root identities cannot contain null entries.", nameof(rootIdentities));
        }

        if (endCursor is not null && string.IsNullOrWhiteSpace(endCursor))
        {
            throw new ArgumentException("An end cursor cannot be empty.", nameof(endCursor));
        }

        HasNextPage = hasNextPage;
        EndCursor = endCursor;
    }

    /// <summary>Gets the captured root identities in page order.</summary>
    public IReadOnlyList<GraphElementIdentity> RootIdentities
    {
        get;
    }

    /// <summary>Gets whether the provider reports further results.</summary>
    public bool HasNextPage
    {
        get;
    }

    /// <summary>Gets the provider-issued continuation token, or null when unavailable.</summary>
    public string? EndCursor
    {
        get;
    }
}
