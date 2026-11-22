using EntityForge.Query.Projection;
using EntityForge.Query.Values;

namespace EntityForge.Query.Querying;

/// <summary>Identifies a filter form in the profile-neutral entity query model.</summary>
public enum EntityQueryFilterKind
{
    /// <summary>Selects every candidate without a property or relationship predicate.</summary>
    MatchAll,

    /// <summary>Compares a property value with a supplied operand.</summary>
    PropertyComparison,

    /// <summary>Tests whether a property is present.</summary>
    PropertyExists,

    /// <summary>Tests for a relationship to an entity satisfying a target filter.</summary>
    RelationshipExists,

    /// <summary>Requires all child filters to match.</summary>
    And,

    /// <summary>Requires at least one child filter to match.</summary>
    Or,

    /// <summary>Negates a child filter.</summary>
    Not
}

/// <summary>Identifies a supported category of property-comparison operand.</summary>
public enum EntityQueryOperandKind
{
    /// <summary>A typed lexical scalar value.</summary>
    Literal,

    /// <summary>A named graph element identity.</summary>
    Identifier
}

/// <summary>Identifies a provider-supported restriction on the entity query's data scope.</summary>
public enum EntityQueryScopeKind
{
    /// <summary>A named data partition.</summary>
    DataPartition,

    /// <summary>A named tenant context.</summary>
    Tenant,

    /// <summary>A specified snapshot.</summary>
    Snapshot,

    /// <summary>A temporal validity selection.</summary>
    Temporal
}

/// <summary>Classifies the spelling of an entity identity used in an explicit selection.</summary>
public enum EntityQueryIdentityKind
{
    /// <summary>An identifier accepted as an absolute URI.</summary>
    AbsoluteUri,

    /// <summary>An opaque string that is not an absolute URI.</summary>
    OpaqueString
}

/// <summary>Declares the entity-query forms a provider supports for client-side capability validation.</summary>
/// <remarks>These declarations describe the typed entity-query API, not complete GQL or SPARQL language conformance.</remarks>
public sealed record EntityQueryProviderCapabilities
{
    /// <summary>Captures supported feature categories as distinct sets.</summary>
    /// <param name="providerKind">The nonblank provider identifier used in diagnostics.</param>
    /// <param name="filterKinds">The supported filter forms.</param>
    /// <param name="comparisonOperators">The supported property comparison operators.</param>
    /// <param name="operandKinds">The supported comparison operand categories.</param>
    /// <param name="literalKinds">The supported literal scalar kinds.</param>
    /// <param name="orderDirections">Supported ordering directions; null declares none.</param>
    /// <param name="orderValueSelections">Supported ways to choose an ordering value from a multivalued property; null declares none.</param>
    /// <param name="missingValuePlacements">Supported positions for missing ordering values; null declares none.</param>
    /// <param name="maximumPageSize">The positive advertised page-size limit, or null when unspecified.</param>
    /// <param name="supportsKeysetPagination">Whether the provider supports entity query pages and continuation keys.</param>
    /// <param name="relationshipDirections">Supported relationship traversal directions; null declares none.</param>
    /// <param name="scopeKinds">Supported query scopes; null declares none.</param>
    /// <param name="identityKinds">Supported forms of explicitly selected identities; null declares none.</param>
    /// <exception cref="ArgumentException">The provider identifier is blank or a required collection is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A supplied maximum page size is not positive.</exception>
    public EntityQueryProviderCapabilities(
        string providerKind,
        IEnumerable<EntityQueryFilterKind> filterKinds,
        IEnumerable<EntityQueryComparisonOperator> comparisonOperators,
        IEnumerable<EntityQueryOperandKind> operandKinds,
        IEnumerable<QueryScalarKind> literalKinds,
        IEnumerable<EntityQueryOrderDirection>? orderDirections = null,
        IEnumerable<EntityQueryOrderValueSelection>? orderValueSelections = null,
        IEnumerable<EntityQueryMissingValuePlacement>? missingValuePlacements = null,
        int? maximumPageSize = null,
        bool supportsKeysetPagination = false,
        IEnumerable<EntityRelationshipDirection>? relationshipDirections = null,
        IEnumerable<EntityQueryScopeKind>? scopeKinds = null,
        IEnumerable<EntityQueryIdentityKind>? identityKinds = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKind);
        ArgumentNullException.ThrowIfNull(filterKinds);
        ArgumentNullException.ThrowIfNull(comparisonOperators);
        ArgumentNullException.ThrowIfNull(operandKinds);
        ArgumentNullException.ThrowIfNull(literalKinds);
        ProviderKind = providerKind;
        FilterKinds = AsSet(filterKinds);
        ComparisonOperators = AsSet(comparisonOperators);
        OperandKinds = AsSet(operandKinds);
        LiteralKinds = AsSet(literalKinds);
        OrderDirections = AsSet(orderDirections ?? Array.Empty<EntityQueryOrderDirection>());
        OrderValueSelections = AsSet(orderValueSelections ?? Array.Empty<EntityQueryOrderValueSelection>());
        MissingValuePlacements = AsSet(missingValuePlacements ?? Array.Empty<EntityQueryMissingValuePlacement>());
        if (maximumPageSize is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumPageSize));
        }

        MaximumPageSize = maximumPageSize;
        SupportsKeysetPagination = supportsKeysetPagination;
        RelationshipDirections = AsSet(relationshipDirections ?? Array.Empty<EntityRelationshipDirection>());
        ScopeKinds = AsSet(scopeKinds ?? Array.Empty<EntityQueryScopeKind>());
        IdentityKinds = AsSet(identityKinds ?? Array.Empty<EntityQueryIdentityKind>());
    }

    /// <summary>Gets the provider identifier used in capability diagnostics.</summary>
    public string ProviderKind
    {
        get;
    }

    /// <summary>Gets the supported filter forms.</summary>
    public IReadOnlySet<EntityQueryFilterKind> FilterKinds
    {
        get;
    }

    /// <summary>Gets the supported property comparison operators.</summary>
    public IReadOnlySet<EntityQueryComparisonOperator> ComparisonOperators
    {
        get;
    }

    /// <summary>Gets the supported comparison operand categories.</summary>
    public IReadOnlySet<EntityQueryOperandKind> OperandKinds
    {
        get;
    }

    /// <summary>Gets the supported literal scalar kinds.</summary>
    public IReadOnlySet<QueryScalarKind> LiteralKinds
    {
        get;
    }

    /// <summary>Gets the supported ordering directions.</summary>
    public IReadOnlySet<EntityQueryOrderDirection> OrderDirections
    {
        get;
    }

    /// <summary>Gets the supported methods of selecting an ordering value from a multivalued property.</summary>
    public IReadOnlySet<EntityQueryOrderValueSelection> OrderValueSelections
    {
        get;
    }

    /// <summary>Gets the supported placements of missing ordering values.</summary>
    public IReadOnlySet<EntityQueryMissingValuePlacement> MissingValuePlacements
    {
        get;
    }

    /// <summary>Gets the advertised page-size bound, or null when no bound is declared here.</summary>
    public int? MaximumPageSize
    {
        get;
    }

    /// <summary>Gets whether entity query paging with continuation keys is supported.</summary>
    public bool SupportsKeysetPagination
    {
        get;
    }

    /// <summary>Gets the supported relationship traversal directions.</summary>
    public IReadOnlySet<EntityRelationshipDirection> RelationshipDirections
    {
        get;
    }

    /// <summary>Gets the supported data, tenant, snapshot and temporal scope categories.</summary>
    public IReadOnlySet<EntityQueryScopeKind> ScopeKinds
    {
        get;
    }

    /// <summary>Gets the supported spelling categories for explicitly selected entity identities.</summary>
    public IReadOnlySet<EntityQueryIdentityKind> IdentityKinds
    {
        get;
    }

    private static IReadOnlySet<T> AsSet<T>(IEnumerable<T> values) where T : notnull =>
        new HashSet<T>(values);
}

/// <summary>Identifies one requested entity-query feature absent from a provider's declarations.</summary>
/// <param name="Path">The location within the query model, such as filter or page.first.</param>
/// <param name="Message">The human-readable capability mismatch.</param>
public sealed record EntityQueryCapabilityIssue(string Path, string Message);

/// <summary>The result of checking a typed entity query against declared provider capabilities.</summary>
/// <param name="Issues">The capability mismatches; an empty list means no mismatch was found.</param>
public sealed record EntityQueryCapabilityValidation(
    IReadOnlyList<EntityQueryCapabilityIssue> Issues)
{
    /// <summary>Gets whether the declared capabilities cover the checked query; this does not execute or authorize it.</summary>
    public bool IsSupported => Issues.Count == 0;
}

/// <summary>Checks query features against a provider's declared execution capabilities.</summary>
public sealed class EntityQueryCapabilityValidator
{
    /// <summary>Collects unsupported filters, orderings, paging, scopes and identity forms without executing a query.</summary>
    /// <param name="query">Query whose requested features are checked.</param>
    /// <param name="capabilities">Capabilities advertised by the selected provider.</param>
    /// <returns>A validation result containing path-specific issues; an empty issue list means supported by these declarations.</returns>
    /// <exception cref="ArgumentNullException">The query or capabilities are null.</exception>
    public EntityQueryCapabilityValidation Validate(
        EntityQueryDefinition query,
        EntityQueryProviderCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(capabilities);
        var issues = new List<EntityQueryCapabilityIssue>();
        ValidateFilter(query.Filter, "filter", capabilities, issues);
        for (var index = 0; index < query.Orderings.Count; index++)
        {
            ValidateOrdering(query.Orderings[index], $"orderings[{index}]", capabilities, issues);
        }
        ValidatePage(query.Page, capabilities, issues);
        for (var index = 0; index < query.Scopes.Count; index++)
        {
            var kind = ScopeKind(query.Scopes[index]);
            if (!capabilities.ScopeKinds.Contains(kind))
            {
                issues.Add(new EntityQueryCapabilityIssue(
                    $"scopes[{index}]",
                    $"Provider '{capabilities.ProviderKind}' does not support scope kind '{kind}'."));
            }
        }
        ValidateIdentities(query.Identities, capabilities, issues);
        return new EntityQueryCapabilityValidation(Array.AsReadOnly(issues.ToArray()));
    }

    private static void ValidateIdentities(
        EntityIdentitySelection? selection,
        EntityQueryProviderCapabilities capabilities,
        ICollection<EntityQueryCapabilityIssue> issues)
    {
        if (selection is null)
        {
            return;
        }

        for (var index = 0; index < selection.Identifiers.Count; index++)
        {
            var identifier = selection.Identifiers[index].Identifier;
            var kind = Uri.TryCreate(identifier, UriKind.Absolute, out _)
                ? EntityQueryIdentityKind.AbsoluteUri
                : EntityQueryIdentityKind.OpaqueString;
            if (!capabilities.IdentityKinds.Contains(kind))
            {
                issues.Add(new EntityQueryCapabilityIssue(
                    $"identities.identifiers[{index}]",
                    $"Provider '{capabilities.ProviderKind}' does not support identity kind '{kind}'."));
            }
        }
    }

    private static EntityQueryScopeKind ScopeKind(EntityQueryScope scope) => scope switch
    {
        DataPartitionEntityQueryScope => EntityQueryScopeKind.DataPartition,
        TenantEntityQueryScope => EntityQueryScopeKind.Tenant,
        SnapshotEntityQueryScope => EntityQueryScopeKind.Snapshot,
        TemporalEntityQueryScope => EntityQueryScopeKind.Temporal,
        _ => throw new NotSupportedException($"Unknown entity query scope '{scope.GetType().Name}'.")
    };

    private static void ValidatePage(
        EntityQueryPage? page,
        EntityQueryProviderCapabilities capabilities,
        ICollection<EntityQueryCapabilityIssue> issues)
    {
        if (page is null)
        {
            return;
        }

        if (!capabilities.SupportsKeysetPagination)
        {
            issues.Add(new EntityQueryCapabilityIssue(
                "page",
                $"Provider '{capabilities.ProviderKind}' does not support keyset pagination."));
        }

        if (capabilities.MaximumPageSize is int maximum && page.First > maximum)
        {
            issues.Add(new EntityQueryCapabilityIssue(
                "page.first",
                $"Requested page size '{page.First}' exceeds provider maximum '{maximum}'."));
        }
    }

    private static void ValidateOrdering(
        EntityQueryOrdering ordering,
        string path,
        EntityQueryProviderCapabilities capabilities,
        ICollection<EntityQueryCapabilityIssue> issues)
    {
        if (!capabilities.OrderDirections.Contains(ordering.Direction))
        {
            issues.Add(new EntityQueryCapabilityIssue(
                $"{path}.direction",
                $"Provider '{capabilities.ProviderKind}' does not support order direction '{ordering.Direction}'."));
        }

        if (!capabilities.OrderValueSelections.Contains(ordering.ValueSelection))
        {
            issues.Add(new EntityQueryCapabilityIssue(
                $"{path}.valueSelection",
                $"Provider '{capabilities.ProviderKind}' does not support order value selection '{ordering.ValueSelection}'."));
        }

        if (!capabilities.MissingValuePlacements.Contains(ordering.MissingValuePlacement))
        {
            issues.Add(new EntityQueryCapabilityIssue(
                $"{path}.missingValuePlacement",
                $"Provider '{capabilities.ProviderKind}' does not support missing-value placement '{ordering.MissingValuePlacement}'."));
        }
    }

    private static void ValidateFilter(
        EntityQueryFilter filter,
        string path,
        EntityQueryProviderCapabilities capabilities,
        ICollection<EntityQueryCapabilityIssue> issues)
    {
        var kind = Kind(filter);
        if (!capabilities.FilterKinds.Contains(kind))
        {
            issues.Add(new EntityQueryCapabilityIssue(
                path,
                $"Provider '{capabilities.ProviderKind}' does not support filter kind '{kind}'."));
            return;
        }

        switch (filter)
        {
            case PropertyComparisonEntityQueryFilter comparison:
                ValidateComparison(comparison, path, capabilities, issues);
                break;
            case RelationshipExistsEntityQueryFilter relationship:
                if (!capabilities.RelationshipDirections.Contains(relationship.Direction))
                {
                    issues.Add(new EntityQueryCapabilityIssue(
                        $"{path}.direction",
                        $"Provider '{capabilities.ProviderKind}' does not support relationship direction '{relationship.Direction}'."));
                }

                ValidateFilter(relationship.TargetFilter, $"{path}.targetFilter", capabilities, issues);
                break;
            case AndEntityQueryFilter and:
                ValidateChildren(and.Operands, path, capabilities, issues);
                break;
            case OrEntityQueryFilter or:
                ValidateChildren(or.Operands, path, capabilities, issues);
                break;
            case NotEntityQueryFilter not:
                ValidateFilter(not.Operand, $"{path}.operand", capabilities, issues);
                break;
        }
    }

    private static void ValidateComparison(
        PropertyComparisonEntityQueryFilter comparison,
        string path,
        EntityQueryProviderCapabilities capabilities,
        ICollection<EntityQueryCapabilityIssue> issues)
    {
        if (!capabilities.ComparisonOperators.Contains(comparison.Operator))
        {
            issues.Add(new EntityQueryCapabilityIssue(
                $"{path}.operator",
                $"Provider '{capabilities.ProviderKind}' does not support comparison operator '{comparison.Operator}'."));
        }

        var operandKind = comparison.Value switch
        {
            LiteralValue => EntityQueryOperandKind.Literal,
            IdentifierValue => EntityQueryOperandKind.Identifier,
            _ => (EntityQueryOperandKind?)null
        };
        if (operandKind is null || !capabilities.OperandKinds.Contains(operandKind.Value))
        {
            issues.Add(new EntityQueryCapabilityIssue(
                $"{path}.value",
                $"Provider '{capabilities.ProviderKind}' does not support operand '{comparison.Value.GetType().Name}'."));
        }

        if (comparison.Value is LiteralValue literal && !capabilities.LiteralKinds.Contains(literal.ScalarKind))
        {
            issues.Add(new EntityQueryCapabilityIssue(
                $"{path}.value",
                $"Provider '{capabilities.ProviderKind}' does not support literal kind '{literal.ScalarKind}'."));
        }
    }

    private static void ValidateChildren(
        IReadOnlyList<EntityQueryFilter> children,
        string path,
        EntityQueryProviderCapabilities capabilities,
        ICollection<EntityQueryCapabilityIssue> issues)
    {
        for (var index = 0; index < children.Count; index++)
        {
            ValidateFilter(children[index], $"{path}.operands[{index}]", capabilities, issues);
        }
    }

    private static EntityQueryFilterKind Kind(EntityQueryFilter filter) => filter switch
    {
        MatchAllEntityQueryFilter => EntityQueryFilterKind.MatchAll,
        PropertyComparisonEntityQueryFilter => EntityQueryFilterKind.PropertyComparison,
        PropertyExistsEntityQueryFilter => EntityQueryFilterKind.PropertyExists,
        RelationshipExistsEntityQueryFilter => EntityQueryFilterKind.RelationshipExists,
        AndEntityQueryFilter => EntityQueryFilterKind.And,
        OrEntityQueryFilter => EntityQueryFilterKind.Or,
        NotEntityQueryFilter => EntityQueryFilterKind.Not,
        _ => throw new NotSupportedException($"Unknown entity query filter '{filter.GetType().Name}'.")
    };
}
