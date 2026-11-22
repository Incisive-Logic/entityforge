using EntityForge.Db.Mapping;

namespace EntityForge.Db.Rdf;

/// <summary>Compiles scalar POCO conventions into an inspectable native RDF map.</summary>
public static class RdfEntityConventions
{
    /// <summary>Gets the compiled native map for this CLR type.</summary>
    public static RdfEntityMap<T> Map<T>() => Cache<T>.Map;

    /// <summary>Gets the exact durable mapping contract, suitable for explicit registration or upgrade.</summary>
    public static EntityMappingManifest Manifest<T>() => Cache<T>.Manifest;

    private static class Cache<T>
    {
        private static readonly EntityConvention<T> Shape = EntityConvention<T>.Instance;
        public static readonly RdfEntityMap<T> Map = new(Shape.Map, new Uri(Shape.TypeBinding),
            Shape.Map.Members.ToDictionary(member => member.Name, member => new Uri(Shape.Binding(member.Name) +
                (member.Role == EntityMemberRole.Identity ? "/" : string.Empty)), StringComparer.Ordinal), Shape.Materializer());

        public static readonly EntityMappingManifest Manifest = Map.CreateManifest(Shape.Model, Shape.EntityType,
            EntityConvention<T>.Version, Shape.MemberIdentities);
    }
}
