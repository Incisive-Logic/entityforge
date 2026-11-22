namespace EntityForge.Db.Mapping;

// Only an owned, whole-result materialization may defer fragment-level validation.
// RunAsync below always includes the final validation inside this boundary.
internal interface IEntityReadMaterializationScope
{
    ValueTask<T> MaterializeAsync<T>(Func<ValueTask<T>> readAndValidate);
}

// A trusted provider validates its retained view after each public read, before returning its
// result. The gateway need not repeat that same boundary; client final validation still applies.
internal interface IEntityReadValidatesResults
{
}

internal static class EntityReadScopeLifetime
{
    internal static async ValueTask<T> RunAsync<T>(IAsyncDisposable scope, Func<ValueTask<T>> read,
        Func<ValueTask> validate)
    {
        T result;
        async ValueTask<T> ReadAndValidate()
        {
            T value = await read().ConfigureAwait(false);
            await validate().ConfigureAwait(false);
            return value;
        }
        try
        {
            result = scope is IEntityReadMaterializationScope materialization
            ? await materialization.MaterializeAsync(ReadAndValidate).ConfigureAwait(false)
            : await ReadAndValidate().ConfigureAwait(false);
        }
        catch (Exception primary)
        {
            try
            {
                await scope.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception cleanup)
            {
                EntityFailureDiagnostics.Attach(primary, cleanup);
            }
            throw;
        }
        await scope.DisposeAsync().ConfigureAwait(false);
        return result;
    }
}
