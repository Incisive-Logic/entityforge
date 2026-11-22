using EntityForge.Semantics;

namespace EntityForge.Query.Projection;

/// <summary>Selects which assertions contribute values to a projected field.</summary>
public enum EntityFieldValueSelection
{
    /// <summary>Includes every matching assertion in source order.</summary>
    AllCandidates,

    /// <summary>Includes only the first matching assertion in source order.</summary>
    FirstCandidate,

    /// <summary>Includes the first matching assertion from the first available preferred provider; no match yields no value.</summary>
    PreferredProvider
}

/// <summary>Specifies which endpoint of a directed relationship is the current entity.</summary>
public enum EntityRelationshipDirection
{
    /// <summary>Traverses from the current source entity to the target.</summary>
    Outgoing,

    /// <summary>Traverses from the current target entity to the source.</summary>
    Incoming
}

/// <summary>Controls expansion when projection traversal encounters an already visited entity.</summary>
public enum RevisitedEntityPolicy
{
    /// <summary>Emits an identity-only reference when the entity has already been visited.</summary>
    Reference,

    /// <summary>Expands repeated entities until the configured depth limit requires a reference.</summary>
    DuplicateUntilDepthLimit
}

/// <summary>Controls source properties not selected by the explicit field mapping.</summary>
public enum UnmappedPropertyPolicy
{
    /// <summary>Omits properties absent from the field mapping.</summary>
    Omit,

    /// <summary>Includes unmatched properties under names derived from their source term identifiers.</summary>
    IncludeBySourceIdentifier
}

/// <summary>Shared default and upper-bound constants used by entity traversal APIs.</summary>
/// <remarks>Individual execution APIs validate their own options; these constants do not themselves enforce limits.</remarks>
public static class EntityTraversalLimits
{
    /// <summary>The shared relationship-depth bound and default projection depth.</summary>
    public const int MaximumRelationshipDepth = 4;

    /// <summary>The default budget for expanded relationship items.</summary>
    public const int DefaultMaximumExpandedEntities = 10_000;

    /// <summary>The shared upper bound used by traversal APIs that enforce a maximum expansion budget.</summary>
    public const int MaximumExpandedEntities = 100_000;
}

/// <summary>Indicates that projection traversal exceeded its configured relationship-item budget.</summary>
public sealed class EntityProjectionExpansionLimitException : Exception
{
    /// <summary>Reports the expansion budget exceeded by the projection.</summary>
    /// <param name="maximumExpandedEntities">The configured maximum expansion count.</param>
    public EntityProjectionExpansionLimitException(int maximumExpandedEntities)
        : base($"The document projection expanded more than {maximumExpandedEntities} relationship items.") =>
        MaximumExpandedEntities = maximumExpandedEntities;

    /// <summary>Gets the configured expansion budget that was exceeded.</summary>
    public int MaximumExpandedEntities
    {
        get;
    }
}

/// <summary>Maps a semantic property term to an output field and assertion-selection policy.</summary>
public sealed record EntityFieldProjection
{
    /// <summary>Defines a projected field, capturing preferred providers in priority order.</summary>
    /// <param name="property">The nonnull property-role term to select.</param>
    /// <param name="name">The nonblank output field name.</param>
    /// <param name="valueSelection">The policy for selecting matching assertions.</param>
    /// <param name="preferredProviders">Absolute provider URIs in preference order; copied and deduplicated.</param>
    /// <exception cref="ArgumentException">An input is invalid, or preferred-provider selection has no providers.</exception>
    public EntityFieldProjection(
        SemanticTerm property,
        string name,
        EntityFieldValueSelection valueSelection = EntityFieldValueSelection.AllCandidates,
        IEnumerable<Uri>? preferredProviders = null)
    {
        Property = property ?? throw new ArgumentNullException(nameof(property));
        if (property.Role is not SemanticTermRole.Property)
        {
            throw new ArgumentException("An entity field projection requires a property term.", nameof(property));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        ValueSelection = valueSelection;
        PreferredProviders = Array.AsReadOnly(
            (preferredProviders ?? Array.Empty<Uri>()).Distinct().ToArray());

        if (PreferredProviders.Any(provider => provider is null || !provider.IsAbsoluteUri))
        {
            throw new ArgumentException("Preferred provider identifiers must be absolute URIs.", nameof(preferredProviders));
        }

        if (valueSelection is EntityFieldValueSelection.PreferredProvider && PreferredProviders.Count == 0)
        {
            throw new ArgumentException("Preferred-provider selection requires at least one provider.", nameof(preferredProviders));
        }
    }

    /// <summary>Gets the property-role term selected directly or through supported alignments.</summary>
    public SemanticTerm Property
    {
        get;
    }

    /// <summary>Gets the output field name.</summary>
    public string Name
    {
        get;
    }

    /// <summary>Gets the policy for choosing among matching assertions.</summary>
    public EntityFieldValueSelection ValueSelection
    {
        get;
    }

    /// <summary>Gets the captured distinct preferred providers in priority order.</summary>
    public IReadOnlyList<Uri> PreferredProviders
    {
        get;
    }
}

/// <summary>Defines a named relationship traversal and the projection of its related entities.</summary>
public sealed record EntityRelationshipProjection
{
    /// <summary>Defines a relationship expansion without executing it.</summary>
    /// <param name="relationship">The nonnull relationship-role term to select.</param>
    /// <param name="name">The nonblank output relationship name.</param>
    /// <param name="target">The nonnull projection to apply to related entities.</param>
    /// <param name="direction">The direction to traverse relative to the current entity.</param>
    /// <exception cref="ArgumentException">A required argument is null, the name is blank or the term is not a relationship.</exception>
    public EntityRelationshipProjection(
        SemanticTerm relationship,
        string name,
        EntityProjectionDefinition target,
        EntityRelationshipDirection direction = EntityRelationshipDirection.Outgoing)
    {
        Relationship = relationship ?? throw new ArgumentNullException(nameof(relationship));
        if (relationship.Role is not SemanticTermRole.Relationship)
        {
            throw new ArgumentException("An entity relationship projection requires a relationship term.", nameof(relationship));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        Target = target ?? throw new ArgumentNullException(nameof(target));
        Direction = direction;
    }

    /// <summary>Gets the relationship-role term to traverse.</summary>
    public SemanticTerm Relationship
    {
        get;
    }

    /// <summary>Gets the output relationship name.</summary>
    public string Name
    {
        get;
    }

    /// <summary>Gets the projection applied to related entities.</summary>
    public EntityProjectionDefinition Target
    {
        get;
    }

    /// <summary>Gets the traversal direction relative to the current entity.</summary>
    public EntityRelationshipDirection Direction
    {
        get;
    }
}

/// <summary>Selects a concept and a uniquely named set of fields and relationships for an entity view.</summary>
public sealed class EntityProjectionDefinition
{
    /// <summary>Captures a projection definition, requiring unique names across fields and relationships.</summary>
    /// <param name="concept">The nonnull concept-role term identifying the entity type.</param>
    /// <param name="fields">Optional selected fields, copied in input order.</param>
    /// <param name="relationships">Optional selected relationships, copied in input order.</param>
    /// <exception cref="ArgumentException">The concept is null or has the wrong role, or output names repeat.</exception>
    public EntityProjectionDefinition(
        SemanticTerm concept,
        IEnumerable<EntityFieldProjection>? fields = null,
        IEnumerable<EntityRelationshipProjection>? relationships = null)
    {
        Concept = concept ?? throw new ArgumentNullException(nameof(concept));
        if (concept.Role is not SemanticTermRole.Concept)
        {
            throw new ArgumentException("An entity projection requires a concept term.", nameof(concept));
        }

        Fields = Array.AsReadOnly((fields ?? Array.Empty<EntityFieldProjection>()).ToArray());
        Relationships = Array.AsReadOnly(
            (relationships ?? Array.Empty<EntityRelationshipProjection>()).ToArray());
        EnsureUniqueNames(Fields.Select(field => field.Name).Concat(Relationships.Select(relationship => relationship.Name)));
    }

    /// <summary>Gets the concept identifying entities to project.</summary>
    public SemanticTerm Concept
    {
        get;
    }

    /// <summary>Gets the captured field selections in declaration order.</summary>
    public IReadOnlyList<EntityFieldProjection> Fields
    {
        get;
    }

    /// <summary>Gets the captured relationship selections in declaration order.</summary>
    public IReadOnlyList<EntityRelationshipProjection> Relationships
    {
        get;
    }

    private static void EnsureUniqueNames(IEnumerable<string> names)
    {
        var materialized = names.ToArray();
        if (materialized.Distinct(StringComparer.Ordinal).Count() != materialized.Length)
        {
            throw new ArgumentException("Projection field and relationship names must be unique.");
        }
    }
}

/// <summary>Controls bounded entity projection, cycle handling and provenance inclusion.</summary>
/// <param name="MaximumRelationshipDepth">Maximum expansion depth, with roots at zero and deeper entities returned as references.</param>
/// <param name="MaximumExpandedEntities">The positive traversal budget; exceeding it fails rather than returning a complete-looking prefix.</param>
/// <param name="RevisitedEntityPolicy">How to represent entities encountered again during traversal.</param>
/// <param name="UnmappedPropertyPolicy">How to handle properties absent from the explicit field mapping.</param>
/// <param name="IncludeEvidence">Whether to retain evidence references and envelope evidence records in the projection.</param>
public sealed record EntityProjectionProjectionOptions(
    int MaximumRelationshipDepth = EntityTraversalLimits.MaximumRelationshipDepth,
    int MaximumExpandedEntities = EntityTraversalLimits.DefaultMaximumExpandedEntities,
    RevisitedEntityPolicy RevisitedEntityPolicy = RevisitedEntityPolicy.Reference,
    UnmappedPropertyPolicy UnmappedPropertyPolicy = UnmappedPropertyPolicy.Omit,
    bool IncludeEvidence = false);
