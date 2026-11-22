using EntityForge.Db.Mapping;

namespace EntityForge.Db.Graph;

/// <summary>One resolved native map and exact durable interpretation shared by object and plain-input producers.</summary>
public sealed class GraphResolvedEntityMapping<T>
{
    internal GraphResolvedEntityMapping(GraphEntityMap<T> map, EntityMappingManifest binding)
    {
        Map = map;
        Manifest = binding;
    }

    /// <summary>Gets the native POCO input and materialization map.</summary>
    public GraphEntityMap<T> Map
    {
        get;
    }

    /// <summary>Gets the exact interpretation admitted by both producers.</summary>
    public EntityMappingManifest Manifest
    {
        get;
    }
}

/// <summary>Resolves native maps once for reuse by every mapped producer.</summary>
public static class GraphResolvedEntityMappingExtensions
{
    /// <summary>Resolves stable identities explicitly; omitted identities use the native type's explicit-map namespace.</summary>
    public static GraphResolvedEntityMapping<T> ResolveBinding<T>(this GraphEntityMap<T> map,
        EntityModelIdentity? model = null, PersistentEntityTypeIdentity? entityType = null,
        string convention = "explicit-v1", IReadOnlyDictionary<string, string>? persistentMemberIdentities = null)
    {
        ArgumentNullException.ThrowIfNull(map);
        return new(map, map.CreateManifest(model ?? new("entityforge/explicit-graph"),
            entityType ?? new(map.VertexLabel), convention, persistentMemberIdentities));
    }
}
