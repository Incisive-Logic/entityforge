using EntityForge.Db.Mapping;
using EntityForge.Semantics;
using EntityForge.Semantics.Protobuf;

namespace EntityForge.Db.Semantic;

using System.Reflection;

/// <summary>Creates one local Semantic entity store for scalar state, ownership, references and collections.</summary>
public static class SemanticEmbeddedEntityStoreExtensions
{
    /// <summary>Uses a named structure with Entity values through the same complete-record lifecycle as POCOs.</summary>
    public static SemanticEntityStore<Entity> EntityStore(this ISemanticDatabase database, SemanticEntityStructure structure,
        SemanticApplicationIdentity? application = null, SemanticEntityStoreLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(structure);
        return database.EntityStore(new SemanticAggregateMap<Entity>(structure.Node), application, limits);
    }

    /// <summary>Compiles declared POCO ownership and collection conventions into a bounded local store.</summary>
    public static SemanticEntityStore<T> EntityStore<T>(this ISemanticDatabase database,
        SemanticApplicationIdentity? application = null, SemanticEntityStoreLimits? limits = null) where T : class =>
        database.EntityStore(SemanticAggregateConventions.Map<T>(), application, limits);

    /// <summary>Composes an explicit writable scalar map into the same entity-store lifecycle.</summary>
    public static SemanticEntityStore<T> EntityStore<T>(this ISemanticDatabase database, SemanticEntityMap<T> map,
        SemanticApplicationIdentity? application = null) where T : class =>
        Create(database, map, application ?? SemanticApplicationIdentity.Default, null, null);

    /// <summary>Uses a validated source-neutral scalar target binding for root provenance.</summary>
    public static SemanticEntityStore<T> EntityStore<T>(this ISemanticDatabase database, SemanticEntityMap<T> map,
        SemanticTargetBinding targetBinding, IReadOnlyDictionary<string, string>? persistentMemberIdentities = null,
        SemanticApplicationIdentity? application = null) where T : class
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(targetBinding);
        if (map.CreateTargetBinding(targetBinding.Identifier, targetBinding.Version, targetBinding.TargetVocabulary,
            persistentMemberIdentities).Fingerprint != targetBinding.Fingerprint)
        {
            throw new ArgumentException("The target binding does not describe this writable POCO map.", nameof(targetBinding));
        }

        return Create(database, map, application ?? SemanticApplicationIdentity.Default, targetBinding, persistentMemberIdentities);
    }

    private static SemanticEntityStore<T> Create<T>(ISemanticDatabase database, SemanticEntityMap<T> map,
        SemanticApplicationIdentity application, SemanticTargetBinding? binding, IReadOnlyDictionary<string, string>? memberIdentities) where T : class =>
        binding is null ? database.Operations().EntityStore(map, application)
            : database.Operations().EntityStore(map, binding, memberIdentities, application);

    /// <summary>Creates an entity store from an explicit lifecycle mapping, with optional resource limits.</summary>
    public static SemanticEntityStore<T> EntityStore<T>(this ISemanticDatabase database, SemanticAggregateMap<T> map,
        SemanticApplicationIdentity? application = null, SemanticEntityStoreLimits? limits = null) where T : class =>
        new((database ?? throw new ArgumentNullException(nameof(database))).Operations(), map ?? throw new ArgumentNullException(nameof(map)),
            application ?? SemanticApplicationIdentity.Default, limits ?? new());
}
