namespace EntityForge.Db.Mapping;

internal interface IEntityMappingStoreContext
{
    EntityMappingManifest Manifest
    {
        get;
    }

    ValueTask<TResult> ExecuteAsync<TResult>(Func<ValueTask<TResult>> operation, CancellationToken cancellationToken,
        Func<ValueTask<(bool Found, TResult Value)>>? resolveReplay = null);
}
