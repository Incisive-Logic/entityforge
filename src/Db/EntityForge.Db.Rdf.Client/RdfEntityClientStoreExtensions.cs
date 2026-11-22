using EntityForge.Db.Mapping;

namespace EntityForge.Db.Rdf;

/// <summary>Composes the existing store over application capabilities.</summary>
public static partial class RdfEntityStoreExtensions
{
    /// <summary>Creates the ordinary entity store over a named structure, without a domain CLR type.</summary>
    public static RdfEntityStore<Entity> EntityStore(this IRdfEntityClient database, RdfEntityStructure structure)
    {
        ArgumentNullException.ThrowIfNull(structure);
        return database.EntityStore(structure.Mapping);
    }

    /// <summary>Creates a scalar convention store; first use durably registers its exact map.</summary>
    public static RdfEntityStore<T> EntityStore<T>(this IRdfEntityClient database) =>
        database.EntityStore(new RdfResolvedEntityMapping<T>(RdfEntityConventions.Map<T>(),
            RdfEntityConventions.Manifest<T>()));

    /// <summary>Creates a store over one explicit RDF entity map.</summary>
    public static RdfEntityStore<T> EntityStore<T>(this IRdfEntityClient database, RdfEntityMap<T> map)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(map);
        return database.EntityStore(map.ResolveBinding());
    }

    /// <summary>Creates a store admitted under the same resolved interpretation as plain-input compilation.</summary>
    public static RdfEntityStore<T> EntityStore<T>(this IRdfEntityClient database,
        RdfResolvedEntityMapping<T> resolved)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(resolved);
        return new(database, resolved);
    }
}
