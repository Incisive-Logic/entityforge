using EntityForge.Db.Mapping;

namespace EntityForge.Db.Graph;

/// <summary>Composes the existing store over application capabilities.</summary>
public static partial class GraphEntityStoreExtensions
{
    /// <summary>Creates the ordinary entity store over a named structure, without a domain CLR type.</summary>
    public static GraphEntityStore<Entity> EntityStore(this IGraphEntityClient database, GraphEntityStructure structure)
    {
        ArgumentNullException.ThrowIfNull(structure);
        return database.EntityStore(structure.Mapping);
    }

    /// <summary>Creates a scalar convention store; first use durably registers its exact map.</summary>
    public static GraphEntityStore<T> EntityStore<T>(this IGraphEntityClient database) =>
        database.EntityStore(new GraphResolvedEntityMapping<T>(GraphEntityConventions.Map<T>(),
            GraphEntityConventions.Manifest<T>()));

    /// <summary>Creates a store over one explicit Graph entity map.</summary>
    public static GraphEntityStore<T> EntityStore<T>(this IGraphEntityClient database, GraphEntityMap<T> map)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(map);
        return database.EntityStore(map.ResolveBinding());
    }

    /// <summary>Creates a store admitted under the same resolved interpretation as plain-input compilation.</summary>
    public static GraphEntityStore<T> EntityStore<T>(this IGraphEntityClient database,
        GraphResolvedEntityMapping<T> resolved)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(resolved);
        return new(database, resolved);
    }
}
