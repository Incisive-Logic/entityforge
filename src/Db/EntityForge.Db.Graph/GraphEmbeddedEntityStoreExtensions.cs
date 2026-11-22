using EntityForge.Db.Mapping;
using EntityForge.Db.Graph.Contracts;

namespace EntityForge.Db.Graph;

/// <summary>Creates conventional Graph mapped-entity stores.</summary>
public static class GraphEmbeddedEntityStoreExtensions
{
    /// <summary>Creates the ordinary entity store over a named structure, without a domain CLR type.</summary>
    public static GraphEntityStore<Entity> EntityStore(this IGraphDatabase database, GraphEntityStructure structure)
    {
        ArgumentNullException.ThrowIfNull(structure);
        return database.EntityStore(structure.Mapping);
    }

    /// <summary>Creates a scalar convention store; first use durably registers its exact map.</summary>
    public static GraphEntityStore<T> EntityStore<T>(this IGraphDatabase database) =>
        database.EntityStore(new GraphResolvedEntityMapping<T>(GraphEntityConventions.Map<T>(),
            GraphEntityConventions.Manifest<T>()));

    /// <summary>Creates a store over one explicit Graph entity map.</summary>
    public static GraphEntityStore<T> EntityStore<T>(this IGraphDatabase database, GraphEntityMap<T> map)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(map);
        return database.EntityStore(map.ResolveBinding());
    }

    /// <summary>Creates a store admitted under the same resolved interpretation as plain-input compilation.</summary>
    public static GraphEntityStore<T> EntityStore<T>(this IGraphDatabase database,
        GraphResolvedEntityMapping<T> resolved)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(resolved);
        return database.Operations().EntityStore(resolved);
    }

    /// <summary>Creates the advanced embedded input compiler for this mapping.</summary>
    public static GraphEntityInputCompiler CreateInputCompiler<T>(this GraphEntityStore<T> value) => GraphEntityInputCompiler.FromBinding(value.Mapping);

    /// <summary>Creates the advanced embedded input compiler for this mapping.</summary>
    public static GraphEntityInputCompiler CreateInputCompiler<T>(this GraphResolvedEntityMapping<T> value) => GraphEntityInputCompiler.FromBinding(value);

    /// <summary>Creates the advanced embedded input compiler for this mapping.</summary>
    public static GraphEntityInputCompiler CreateInputCompiler(this GraphEntityStructure value) => GraphEntityInputCompiler.FromBinding(value.Mapping);
}
