namespace EntityForge.Db.Hosting;

internal sealed partial class EntityForgeDbRuntime
{
    internal async Task<GqlManagedCatalog.Compaction> CompactManagedGqlCatalogAsync(long generation, long version,
        CancellationToken cancellationToken = default)
    {
        await _gqlMutation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_gqlGraphGate)
            {
                RequireManagedGraphHost();
                var catalog = _managedCatalog ?? throw new InvalidOperationException("No managed graph catalog is open.");
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    return catalog.Compact(generation, version);
                }
                catch
                {
                    _managedCatalogFaulted = !catalog.IsAvailable;
                    throw;
                }
            }
        }
        finally
        {
            _gqlMutation.Release();
        }
    }
}
