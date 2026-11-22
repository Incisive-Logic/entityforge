using EntityForge.Db.Mapping;

namespace EntityForge.Db.Graph;

/// <summary>Compiles scalar POCO conventions into an inspectable native Graph map.</summary>
public static class GraphEntityConventions
{
    /// <summary>Gets the compiled native map for this CLR type.</summary>
    public static GraphEntityMap<T> Map<T>() => Cache<T>.Map;

    /// <summary>Gets the exact durable mapping contract, suitable for explicit registration or upgrade.</summary>
    public static EntityMappingManifest Manifest<T>() => Cache<T>.Manifest;

    private static class Cache<T>
    {
        private static readonly EntityConvention<T> Shape = EntityConvention<T>.Instance;
        public static readonly GraphEntityMap<T> Map = new(Shape.Map, Shape.TypeBinding,
            Shape.Map.Members.ToDictionary(member => member.Name, member => Shape.Binding(member.Name), StringComparer.Ordinal), Shape.Materializer());

        public static readonly EntityMappingManifest Manifest = Map.CreateManifest(Shape.Model, Shape.EntityType,
            EntityConvention<T>.Version, Shape.MemberIdentities);
    }
}
