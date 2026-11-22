namespace EntityForge.Db.Mapping;

internal sealed class EntityMappingStoreContext(EntityMappingRegistry registry, EntityMappingManifest manifest) : IEntityMappingStoreContext
{
    public EntityMappingManifest Manifest => manifest;

    public ValueTask<TResult> ExecuteAsync<TResult>(Func<ValueTask<TResult>> operation, CancellationToken cancellationToken, Func<ValueTask<(bool Found, TResult Value)>>? resolveReplay = null) => registry.ExecuteAsync([manifest], operation, cancellationToken, resolveReplay);
}
