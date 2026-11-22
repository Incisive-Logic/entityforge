using EntityForge.Db.Semantic;
using EntityForge.Db.Semantic.Core;

namespace EntityForge.Db.Query;

/// <summary>Binds a semantic equality request to its physical property catalog entry.</summary>
/// <param name="Query">The requested property equality query.</param>
/// <param name="Storage">The resolved property-storage binding.</param>
/// <param name="CatalogVersion">The catalog version used to bind storage.</param>
public sealed record BoundSemanticEqualityQuery(SemanticFieldEqualityQuery Query,
    SemanticPropertyStorage Storage, SemanticPropertyStorageCatalogVersion CatalogVersion);

/// <summary>Represents the bound semantic equality operation before physical access selection.</summary>
/// <param name="BoundQuery">The catalog-bound equality query.</param>
/// <param name="Operation">The logical operation identifier.</param>
public sealed record SemanticEqualityLogicalPlan(BoundSemanticEqualityQuery BoundQuery,
    string Operation);

/// <summary>Describes an equality access strategy, its candidate estimate and current availability.</summary>
/// <param name="StorageKind">The physical property-storage strategy.</param>
/// <param name="Operation">The physical operation identifier.</param>
/// <param name="EstimatedCandidates">The estimated number of candidates examined by this alternative.</param>
/// <param name="Available">Whether the property is currently bound to this storage strategy.</param>
public sealed record SemanticEqualityPhysicalAlternative(SemanticPropertyStorageKind StorageKind,
    string Operation, long EstimatedCandidates, bool Available);

/// <summary>Records the selected equality access strategy and considered alternatives.</summary>
/// <param name="LogicalPlan">The bound logical equality operation.</param>
/// <param name="Selected">The strategy available for the property's current storage binding.</param>
/// <param name="Alternatives">All reported strategies with estimates and availability.</param>
public sealed record SemanticEqualityPhysicalPlan(SemanticEqualityLogicalPlan LogicalPlan,
    SemanticEqualityPhysicalAlternative Selected,
    IReadOnlyList<SemanticEqualityPhysicalAlternative> Alternatives);

/// <summary>Binds property equality requests to a captured storage catalog.</summary>
public sealed class SemanticEqualityQueryBinder
{
    /// <summary>Resolves a property's storage binding and records the exact catalog version used.</summary>
    public BoundSemanticEqualityQuery Bind(PropertyStorageCatalogState catalog,
        SemanticFieldEqualityQuery query)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(query);
        return new(query, catalog.Resolve(query.Field), catalog.Version);
    }
}

/// <summary>Creates logical plans for bound semantic equality requests.</summary>
public sealed class SemanticEqualityLogicalPlanner
{
    /// <summary>The logical operation identifier for semantic field equality.</summary>
    public const string EqualityOperation = "semantic-field-equality";

    /// <summary>Creates a logical equality plan from the bound query.</summary>
    public SemanticEqualityLogicalPlan Plan(BoundSemanticEqualityQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return new(query, EqualityOperation);
    }
}

/// <summary>Selects the physical strategy supported by the current property-storage binding.</summary>
public sealed class SemanticEqualityPhysicalOptimizer
{
    /// <summary>Reports candidate estimates for storage alternatives and selects the one available under the bound catalog.</summary>
    public SemanticEqualityPhysicalPlan Optimize(SemanticEqualityLogicalPlan logical,
        Func<SemanticPropertyStorageKind, long> estimateCandidates)
    {
        ArgumentNullException.ThrowIfNull(logical);
        ArgumentNullException.ThrowIfNull(estimateCandidates);
        SemanticPropertyStorageKind selectedKind = logical.BoundQuery.Storage.StorageKind;
        SemanticEqualityPhysicalAlternative[] alternatives = Enum.GetValues<SemanticPropertyStorageKind>()
            .Select(kind => new SemanticEqualityPhysicalAlternative(kind, Operation(kind),
                estimateCandidates(kind), kind == selectedKind)).ToArray();
        return new(logical, alternatives.Single(item => item.Available), alternatives);
    }

    /// <summary>Maps a supported property-storage kind to its physical equality operation identifier.</summary>
    public static string Operation(SemanticPropertyStorageKind storageKind) => storageKind switch
    {
        SemanticPropertyStorageKind.HotScalar => "hot-scalar-equality-seek",
        SemanticPropertyStorageKind.IndexedJson => "indexed-json-equality-seek",
        SemanticPropertyStorageKind.ColdJson => "cold-json-bucket-scan",
        _ => throw new ArgumentOutOfRangeException(nameof(storageKind), storageKind,
            "Unsupported property storage kind.")
    };
}
