using EntityForge.Query.Projection;
using EntityForge.Semantics;
using EntityForge.Query.Values;

namespace EntityForge.Query.Querying;

/// <summary>A profile-neutral entity predicate whose supported forms are declared by the query provider.</summary>
public abstract record EntityQueryFilter;

/// <summary>A predicate that imposes no additional restriction on candidate entities.</summary>
public sealed record MatchAllEntityQueryFilter : EntityQueryFilter
{
    private MatchAllEntityQueryFilter()
    {
    }

    /// <summary>Gets the shared unrestricted predicate.</summary>
    public static MatchAllEntityQueryFilter Instance { get; } = new();
}

/// <summary>Comparison operations available in the typed entity-query model, subject to provider capabilities.</summary>
public enum EntityQueryComparisonOperator
{
    /// <summary>Tests value equality.</summary>
    Equal,

    /// <summary>Tests value inequality.</summary>
    NotEqual,

    /// <summary>Tests whether a string contains the supplied text.</summary>
    Contains,

    /// <summary>Tests whether a string starts with the supplied text.</summary>
    StartsWith,

    /// <summary>Tests whether a value sorts after the operand.</summary>
    GreaterThan,

    /// <summary>Tests whether a value equals or sorts after the operand.</summary>
    GreaterThanOrEqual,

    /// <summary>Tests whether a value sorts before the operand.</summary>
    LessThan,

    /// <summary>Tests whether a value equals or sorts before the operand.</summary>
    LessThanOrEqual
}

/// <summary>Compares a selected semantic property with a non-null, bound query operand.</summary>
public sealed record PropertyComparisonEntityQueryFilter : EntityQueryFilter
{
    /// <summary>Defines a property comparison without executing or coercing the operand.</summary>
    /// <param name="property">The nonnull property-role term.</param>
    /// <param name="operator">The comparison requested from the provider.</param>
    /// <param name="value">The operand; null, explicit null and unbound values are rejected.</param>
    /// <exception cref="ArgumentException">A required argument is null, the term has the wrong role or the operand represents absence.</exception>
    public PropertyComparisonEntityQueryFilter(
        SemanticTerm property,
        EntityQueryComparisonOperator @operator,
        QueryValue value)
    {
        Property = RequireProperty(property);
        Operator = @operator;
        Value = value ?? throw new ArgumentNullException(nameof(value));
        if (value is NullValue or UnboundValue)
        {
            throw new ArgumentException(
                "Null and unbound are states, not property-comparison operands.", nameof(value));
        }
    }

    /// <summary>Gets the property-role term to compare.</summary>
    public SemanticTerm Property
    {
        get;
    }

    /// <summary>Gets the requested comparison operation.</summary>
    public EntityQueryComparisonOperator Operator
    {
        get;
    }

    /// <summary>Gets the retained comparison operand.</summary>
    public QueryValue Value
    {
        get;
    }

    private static SemanticTerm RequireProperty(SemanticTerm property)
    {
        ArgumentNullException.ThrowIfNull(property);
        return property.Role is SemanticTermRole.Property
            ? property
            : throw new ArgumentException("A property filter requires a property term.", nameof(property));
    }
}

/// <summary>Tests whether a selected semantic property is present.</summary>
public sealed record PropertyExistsEntityQueryFilter : EntityQueryFilter
{
    /// <summary>Defines a property-presence predicate.</summary>
    /// <param name="property">The nonnull property-role term whose presence is tested.</param>
    /// <exception cref="ArgumentException">The term is null or does not have the property role.</exception>
    public PropertyExistsEntityQueryFilter(SemanticTerm property)
    {
        ArgumentNullException.ThrowIfNull(property);
        Property = property.Role is SemanticTermRole.Property
            ? property
            : throw new ArgumentException("A property-exists filter requires a property term.", nameof(property));
    }

    /// <summary>Gets the property-role term whose presence is tested.</summary>
    public SemanticTerm Property
    {
        get;
    }
}

/// <summary>Tests for a directed relationship to an entity of a selected concept satisfying a nested predicate.</summary>
public sealed record RelationshipExistsEntityQueryFilter : EntityQueryFilter
{
    /// <summary>Defines a relationship-existence predicate and its target restriction.</summary>
    /// <param name="relationship">The nonnull relationship-role term.</param>
    /// <param name="direction">The traversal direction relative to the candidate entity.</param>
    /// <param name="targetConcept">The nonnull concept-role term required of the related entity.</param>
    /// <param name="targetFilter">An additional target predicate, or null to accept every candidate target.</param>
    /// <exception cref="ArgumentException">A required term is null or has the wrong role.</exception>
    public RelationshipExistsEntityQueryFilter(
        SemanticTerm relationship,
        EntityRelationshipDirection direction,
        SemanticTerm targetConcept,
        EntityQueryFilter? targetFilter = null)
    {
        ArgumentNullException.ThrowIfNull(relationship);
        Relationship = relationship.Role is SemanticTermRole.Relationship
            ? relationship
            : throw new ArgumentException("A relationship filter requires a relationship term.", nameof(relationship));
        ArgumentNullException.ThrowIfNull(targetConcept);
        TargetConcept = targetConcept.Role is SemanticTermRole.Concept
            ? targetConcept
            : throw new ArgumentException("A relationship filter target requires a concept term.", nameof(targetConcept));
        Direction = direction;
        TargetFilter = targetFilter ?? MatchAllEntityQueryFilter.Instance;
    }

    /// <summary>Gets the relationship-role term to traverse.</summary>
    public SemanticTerm Relationship
    {
        get;
    }

    /// <summary>Gets the direction relative to the candidate entity.</summary>
    public EntityRelationshipDirection Direction
    {
        get;
    }

    /// <summary>Gets the concept required of related entities.</summary>
    public SemanticTerm TargetConcept
    {
        get;
    }

    /// <summary>Gets the target predicate; omitted input becomes the match-all predicate.</summary>
    public EntityQueryFilter TargetFilter
    {
        get;
    }
}

/// <summary>Requires every child entity predicate to match.</summary>
public sealed record AndEntityQueryFilter : EntityQueryFilter
{
    /// <summary>Captures at least two nonnull predicates in declaration order.</summary>
    /// <param name="operands">The predicate sequence to copy.</param>
    /// <exception cref="ArgumentException">The sequence is null, contains null or has fewer than two predicates.</exception>
    public AndEntityQueryFilter(IEnumerable<EntityQueryFilter> operands)
    {
        ArgumentNullException.ThrowIfNull(operands);
        Operands = Array.AsReadOnly(operands.ToArray());
        if (Operands.Count < 2 || Operands.Any(operand => operand is null))
        {
            throw new ArgumentException("And requires at least two non-null operands.", nameof(operands));
        }
    }

    /// <summary>Gets the captured predicates that must all match.</summary>
    public IReadOnlyList<EntityQueryFilter> Operands
    {
        get;
    }
}

/// <summary>Requires at least one child entity predicate to match.</summary>
public sealed record OrEntityQueryFilter : EntityQueryFilter
{
    /// <summary>Captures at least two nonnull alternative predicates in declaration order.</summary>
    /// <param name="operands">The predicate sequence to copy.</param>
    /// <exception cref="ArgumentException">The sequence is null, contains null or has fewer than two predicates.</exception>
    public OrEntityQueryFilter(IEnumerable<EntityQueryFilter> operands)
    {
        ArgumentNullException.ThrowIfNull(operands);
        Operands = Array.AsReadOnly(operands.ToArray());
        if (Operands.Count < 2 || Operands.Any(operand => operand is null))
        {
            throw new ArgumentException("Or requires at least two non-null operands.", nameof(operands));
        }
    }

    /// <summary>Gets the captured alternative predicates.</summary>
    public IReadOnlyList<EntityQueryFilter> Operands
    {
        get;
    }
}

/// <summary>Negates one entity predicate.</summary>
public sealed record NotEntityQueryFilter : EntityQueryFilter
{
    /// <summary>Defines the negation of a nonnull predicate.</summary>
    /// <param name="operand">The predicate to negate.</param>
    /// <exception cref="ArgumentNullException">The operand is null.</exception>
    public NotEntityQueryFilter(EntityQueryFilter operand)
    {
        Operand = operand ?? throw new ArgumentNullException(nameof(operand));
    }

    /// <summary>Gets the predicate to negate.</summary>
    public EntityQueryFilter Operand
    {
        get;
    }
}

/// <summary>Specifies the direction of a selected entity ordering key.</summary>
public enum EntityQueryOrderDirection
{
    /// <summary>Orders lesser values before greater values.</summary>
    Ascending,

    /// <summary>Orders greater values before lesser values.</summary>
    Descending
}

/// <summary>Selects one ordering key when a property has multiple values.</summary>
public enum EntityQueryOrderValueSelection
{
    /// <summary>Uses the least property value as the ordering key.</summary>
    Minimum,

    /// <summary>Uses the greatest property value as the ordering key.</summary>
    Maximum
}

/// <summary>Positions entities lacking an ordering value relative to entities with values.</summary>
public enum EntityQueryMissingValuePlacement
{
    /// <summary>Places missing values before present values.</summary>
    First,

    /// <summary>Places missing values after present values.</summary>
    Last
}

/// <summary>Defines a property-based ordering key, including multivalue selection and missing-value placement.</summary>
public sealed record EntityQueryOrdering
{
    /// <summary>Defines one ordering key without sorting or executing a query.</summary>
    /// <param name="property">The nonnull property-role term supplying the key.</param>
    /// <param name="direction">The requested ordering direction.</param>
    /// <param name="valueSelection">How to select a key from multiple property values.</param>
    /// <param name="missingValuePlacement">Where to place entities without a key.</param>
    /// <exception cref="ArgumentException">The term is null or has the wrong role.</exception>
    public EntityQueryOrdering(
        SemanticTerm property,
        EntityQueryOrderDirection direction = EntityQueryOrderDirection.Ascending,
        EntityQueryOrderValueSelection valueSelection = EntityQueryOrderValueSelection.Minimum,
        EntityQueryMissingValuePlacement missingValuePlacement = EntityQueryMissingValuePlacement.Last)
    {
        ArgumentNullException.ThrowIfNull(property);
        Property = property.Role is SemanticTermRole.Property
            ? property
            : throw new ArgumentException("Entity ordering requires a property term.", nameof(property));
        Direction = direction;
        ValueSelection = valueSelection;
        MissingValuePlacement = missingValuePlacement;
    }

    /// <summary>Gets the property supplying the ordering key.</summary>
    public SemanticTerm Property
    {
        get;
    }

    /// <summary>Gets the requested ordering direction.</summary>
    public EntityQueryOrderDirection Direction
    {
        get;
    }

    /// <summary>Gets how to choose a key from multiple property values.</summary>
    public EntityQueryOrderValueSelection ValueSelection
    {
        get;
    }

    /// <summary>Gets where entities without an ordering key are placed.</summary>
    public EntityQueryMissingValuePlacement MissingValuePlacement
    {
        get;
    }
}

/// <summary>Requests a bounded keyset page, optionally after an opaque continuation token.</summary>
public sealed record EntityQueryPage
{
    /// <summary>Defines a positive page size and optional continuation.</summary>
    /// <param name="first">The positive maximum number of requested entities, subject to provider limits.</param>
    /// <param name="after">A provider-issued token, or null for the first page; retain the query and scope required by that provider.</param>
    /// <exception cref="ArgumentOutOfRangeException">The page size is not positive.</exception>
    /// <exception cref="ArgumentException">A supplied token is empty or whitespace.</exception>
    public EntityQueryPage(int first, string? after = null)
    {
        if (first <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(first), "Page size must be greater than zero.");
        }

        if (after is not null && string.IsNullOrWhiteSpace(after))
        {
            throw new ArgumentException("A continuation token cannot be empty.", nameof(after));
        }

        First = first;
        After = after;
    }

    /// <summary>Gets the maximum requested page size.</summary>
    public int First
    {
        get;
    }

    /// <summary>Gets the opaque continuation token, or null for the first page.</summary>
    public string? After
    {
        get;
    }
}

/// <summary>A provider-interpreted restriction on the data or temporal context of an entity query.</summary>
public abstract record EntityQueryScope;

/// <summary>Selects a named data partition using an absolute URI.</summary>
public sealed record DataPartitionEntityQueryScope : EntityQueryScope
{
    /// <summary>Defines a data-partition scope without resolving or opening it.</summary>
    /// <param name="identifier">The nonnull absolute partition identifier.</param>
    /// <exception cref="ArgumentException">The identifier is null or relative.</exception>
    public DataPartitionEntityQueryScope(Uri identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        Identifier = identifier.IsAbsoluteUri
            ? identifier
            : throw new ArgumentException("A data-partition identifier must be an absolute URI.", nameof(identifier));
    }

    /// <summary>Gets the absolute data-partition identifier.</summary>
    public Uri Identifier
    {
        get;
    }
}

/// <summary>Selects a tenant context understood by the query provider.</summary>
public sealed record TenantEntityQueryScope : EntityQueryScope
{
    /// <summary>Captures a tenant identifier without normalizing or authorizing it.</summary>
    /// <param name="identifier">The nonblank tenant identifier.</param>
    /// <exception cref="ArgumentException">The identifier is null, empty or whitespace.</exception>
    public TenantEntityQueryScope(string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        Identifier = identifier;
    }

    /// <summary>Gets the exact tenant identifier.</summary>
    public string Identifier
    {
        get;
    }
}

/// <summary>Selects a snapshot by its provider-issued identifier.</summary>
public sealed record SnapshotEntityQueryScope : EntityQueryScope
{
    /// <summary>Captures a snapshot identifier without acquiring or extending snapshot retention.</summary>
    /// <param name="identifier">The nonblank snapshot identifier.</param>
    /// <exception cref="ArgumentException">The identifier is null, empty or whitespace.</exception>
    public SnapshotEntityQueryScope(string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        Identifier = identifier;
    }

    /// <summary>Gets the exact snapshot identifier.</summary>
    public string Identifier
    {
        get;
    }
}

/// <summary>Selects the time dimension constrained by a temporal entity-query scope.</summary>
public enum EntityQueryTemporalDimension
{
    /// <summary>The time for which an assertion is valid.</summary>
    ValidTime,

    /// <summary>The time at which an assertion was observed.</summary>
    ObservationTime
}

/// <summary>Constrains one time dimension using an interval with at least one specified boundary.</summary>
public sealed record TemporalEntityQueryScope : EntityQueryScope
{
    /// <summary>Defines an inclusive-start, exclusive-end temporal scope.</summary>
    /// <param name="dimension">The time dimension to constrain.</param>
    /// <param name="fromInclusive">The inclusive lower bound, or null for no lower bound.</param>
    /// <param name="toExclusive">The exclusive upper bound, or null for no upper bound.</param>
    /// <exception cref="ArgumentException">Both bounds are absent, or the start is not earlier than the end.</exception>
    public TemporalEntityQueryScope(
        EntityQueryTemporalDimension dimension,
        DateTimeOffset? fromInclusive = null,
        DateTimeOffset? toExclusive = null)
    {
        if (fromInclusive is null && toExclusive is null)
        {
            throw new ArgumentException("A temporal scope requires at least one boundary.");
        }

        if (fromInclusive >= toExclusive)
        {
            throw new ArgumentException("A temporal scope start must precede its end.");
        }

        Dimension = dimension;
        FromInclusive = fromInclusive;
        ToExclusive = toExclusive;
    }

    /// <summary>Gets the constrained time dimension.</summary>
    public EntityQueryTemporalDimension Dimension
    {
        get;
    }

    /// <summary>Gets the inclusive lower bound, or null when unbounded below.</summary>
    public DateTimeOffset? FromInclusive
    {
        get;
    }

    /// <summary>Gets the exclusive upper bound, or null when unbounded above.</summary>
    public DateTimeOffset? ToExclusive
    {
        get;
    }
}

/// <summary>Restricts candidate entities to a nonempty set of named identities.</summary>
public sealed record EntityIdentitySelection
{
    /// <summary>Captures distinct entity identities without resolving them.</summary>
    /// <param name="identifiers">The nonnull identity sequence to copy and deduplicate.</param>
    /// <exception cref="ArgumentException">The sequence is null, empty or contains null.</exception>
    public EntityIdentitySelection(IEnumerable<IdentifierValue> identifiers)
    {
        ArgumentNullException.ThrowIfNull(identifiers);
        Identifiers = Array.AsReadOnly(identifiers.Distinct().ToArray());
        if (Identifiers.Count == 0 || Identifiers.Any(identifier => identifier is null))
        {
            throw new ArgumentException(
                "Identity selection requires at least one non-null identifier.", nameof(identifiers));
        }
    }

    /// <summary>Gets the captured distinct identities.</summary>
    public IReadOnlyList<IdentifierValue> Identifiers
    {
        get;
    }
}

/// <summary>
/// Restricts the provenance-bearing assertions that may support a Semantic entity query.
/// </summary>
public sealed record SemanticAssertionPolicy
{
    /// <summary>Captures provenance restrictions without evaluating assertions or granting access.</summary>
    /// <param name="allowedProviders">Allowed absolute provider URIs; null or empty imposes no allowlist restriction.</param>
    /// <param name="excludedProviders">Absolute provider URIs to exclude.</param>
    /// <param name="allowedSourceVocabularies">Allowed absolute source vocabulary URIs; null or empty imposes no allowlist restriction.</param>
    /// <param name="allowedTargetVocabularies">Allowed absolute target vocabulary URIs; null or empty imposes no allowlist restriction.</param>
    /// <param name="requireValidityInterval">Whether eligible provenance must carry at least one validity boundary.</param>
    /// <param name="requireResponsibleAgent">Whether eligible provenance must identify a responsible agent.</param>
    /// <exception cref="ArgumentException">An identifier is null or relative, or a provider is both allowed and excluded.</exception>
    /// <remarks>Identifier collections are copied and deduplicated.</remarks>
    public SemanticAssertionPolicy(
        IEnumerable<Uri>? allowedProviders = null,
        IEnumerable<Uri>? excludedProviders = null,
        IEnumerable<Uri>? allowedSourceVocabularies = null,
        IEnumerable<Uri>? allowedTargetVocabularies = null,
        bool requireValidityInterval = false,
        bool requireResponsibleAgent = false)
    {
        AllowedProviders = AbsoluteIdentifiers(allowedProviders, nameof(allowedProviders));
        ExcludedProviders = AbsoluteIdentifiers(excludedProviders, nameof(excludedProviders));
        AllowedSourceVocabularies = AbsoluteIdentifiers(allowedSourceVocabularies,
            nameof(allowedSourceVocabularies));
        AllowedTargetVocabularies = AbsoluteIdentifiers(allowedTargetVocabularies,
            nameof(allowedTargetVocabularies));
        if (AllowedProviders.Intersect(ExcludedProviders).Any())
        {
            throw new ArgumentException("A provider cannot be both allowed and excluded.");
        }

        RequireValidityInterval = requireValidityInterval;
        RequireResponsibleAgent = requireResponsibleAgent;
    }

    /// <summary>Gets the provider allowlist; an empty list imposes no allowlist restriction.</summary>
    public IReadOnlyList<Uri> AllowedProviders
    {
        get;
    }

    /// <summary>Gets the providers whose assertions are excluded.</summary>
    public IReadOnlyList<Uri> ExcludedProviders
    {
        get;
    }

    /// <summary>Gets the source vocabulary allowlist, or an empty list when unrestricted.</summary>
    public IReadOnlyList<Uri> AllowedSourceVocabularies
    {
        get;
    }

    /// <summary>Gets the target vocabulary allowlist, or an empty list when unrestricted.</summary>
    public IReadOnlyList<Uri> AllowedTargetVocabularies
    {
        get;
    }

    /// <summary>Gets whether eligible provenance must carry at least one validity boundary.</summary>
    public bool RequireValidityInterval
    {
        get;
    }

    /// <summary>Gets whether eligible provenance must identify a responsible agent.</summary>
    public bool RequireResponsibleAgent
    {
        get;
    }

    private static IReadOnlyList<Uri> AbsoluteIdentifiers(IEnumerable<Uri>? identifiers, string parameterName)
    {
        Uri[] values = (identifiers ?? []).Distinct().ToArray();
        if (values.Any(value => value is null || !value.IsAbsoluteUri))
        {
            throw new ArgumentException("Semantic assertion-policy identifiers must be absolute URIs.", parameterName);
        }

        return Array.AsReadOnly(values);
    }
}

/// <summary>Controls whether concept matching uses direct assertions or also supported entailments.</summary>
public enum EntityConceptMatchMode
{
    /// <summary>Matches directly asserted concept membership.</summary>
    Direct,

    /// <summary>Also permits concept membership derived by the provider's supported entailment rules.</summary>
    Entailed
}

/// <summary>A typed entity query combining root selection, projection, predicates, ordering, paging and provenance restrictions.</summary>
public sealed record EntityQueryDefinition
{
    /// <summary>Captures a query definition for later capability validation and execution.</summary>
    /// <param name="rootConcept">The nonnull concept-role term selecting candidate roots.</param>
    /// <param name="projection">The nonnull projection whose concept must equal the root concept.</param>
    /// <param name="filter">An additional predicate, or null for match-all.</param>
    /// <param name="orderings">Optional ordering keys, copied in precedence order.</param>
    /// <param name="page">Optional keyset paging request.</param>
    /// <param name="scopes">Optional provider-interpreted scopes, copied in declaration order.</param>
    /// <param name="identities">Optional explicit candidate identities.</param>
    /// <param name="assertionPolicy">Optional restrictions on supporting semantic assertions.</param>
    /// <param name="conceptMatchMode">Whether concept membership includes supported entailments.</param>
    /// <exception cref="ArgumentException">Required inputs are null, concept roles or identities disagree, or ordering/scope entries are null.</exception>
    /// <remarks>Construction does not execute or authorize a query, nor guarantee provider support for every requested feature.</remarks>
    public EntityQueryDefinition(
        SemanticTerm rootConcept,
        EntityProjectionDefinition projection,
        EntityQueryFilter? filter = null,
        IEnumerable<EntityQueryOrdering>? orderings = null,
        EntityQueryPage? page = null,
        IEnumerable<EntityQueryScope>? scopes = null,
        EntityIdentitySelection? identities = null,
        SemanticAssertionPolicy? assertionPolicy = null,
        EntityConceptMatchMode conceptMatchMode = EntityConceptMatchMode.Entailed)
    {
        RootConcept = rootConcept ?? throw new ArgumentNullException(nameof(rootConcept));
        if (rootConcept.Role is not SemanticTermRole.Concept)
        {
            throw new ArgumentException("An entity query root must be a concept term.", nameof(rootConcept));
        }

        Projection = projection ?? throw new ArgumentNullException(nameof(projection));
        if (projection.Concept != rootConcept)
        {
            throw new ArgumentException(
                "The root projection concept must match the entity query root concept.",
                nameof(projection));
        }

        Filter = filter ?? MatchAllEntityQueryFilter.Instance;
        Orderings = Array.AsReadOnly((orderings ?? Array.Empty<EntityQueryOrdering>()).ToArray());
        if (Orderings.Any(ordering => ordering is null))
        {
            throw new ArgumentException("Entity query orderings cannot contain null entries.", nameof(orderings));
        }


        Page = page;
        Scopes = Array.AsReadOnly((scopes ?? Array.Empty<EntityQueryScope>()).ToArray());
        if (Scopes.Any(scope => scope is null))
        {
            throw new ArgumentException("Entity query scopes cannot contain null entries.", nameof(scopes));
        }


        Identities = identities;
        AssertionPolicy = assertionPolicy;
        ConceptMatchMode = conceptMatchMode;
    }

    /// <summary>Gets the concept selecting candidate root entities.</summary>
    public SemanticTerm RootConcept
    {
        get;
    }

    /// <summary>Gets the candidate predicate, including the match-all default.</summary>
    public EntityQueryFilter Filter
    {
        get;
    }

    /// <summary>Gets the requested partial entity view.</summary>
    public EntityProjectionDefinition Projection
    {
        get;
    }

    /// <summary>Gets the captured ordering keys in precedence order.</summary>
    public IReadOnlyList<EntityQueryOrdering> Orderings
    {
        get;
    }

    /// <summary>Gets the paging request, or null when no page is requested.</summary>
    public EntityQueryPage? Page
    {
        get;
    }

    /// <summary>Gets the captured provider-interpreted query scopes.</summary>
    public IReadOnlyList<EntityQueryScope> Scopes
    {
        get;
    }

    /// <summary>Gets the explicit identity restriction, or null when candidates are not selected by identity.</summary>
    public EntityIdentitySelection? Identities
    {
        get;
    }

    /// <summary>Gets the optional provenance restrictions on supporting assertions.</summary>
    public SemanticAssertionPolicy? AssertionPolicy
    {
        get;
    }

    /// <summary>Gets whether concept membership may include supported entailments.</summary>
    public EntityConceptMatchMode ConceptMatchMode
    {
        get;
    }
}
